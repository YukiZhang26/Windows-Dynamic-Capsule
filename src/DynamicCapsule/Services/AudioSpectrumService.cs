using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace DynamicCapsule.Services;

// Audio is analyzed in memory only; no microphone, file writer, or network path.
internal sealed class AudioSpectrumService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WasapiRecorder? _recorder;
    private volatile bool _disposed;
    private string? _requested;
    private long _retryAfter;
    private float[] _bands = new float[16];
    private long _lastFrame;
    internal string Status { get; private set; } = "未采样";
    internal static string? ResolveProcessName(string source)
    {
        // Exact observed desktop AUMID; do not infer executables from arbitrary app IDs.
        if (string.Equals(source, "com.bilibili.bilibiliPC", StringComparison.OrdinalIgnoreCase))
            return "哔哩哔哩";
        return source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(source) : null;
    }
    internal float[] ReadBands() => Environment.TickCount64 - Interlocked.Read(ref _lastFrame) < 250
        ? Volatile.Read(ref _bands) : new float[16];

    internal async Task SetSourceAsync(string? source)
    {
        if (_disposed || (_requested == source && Environment.TickCount64 < _retryAfter)) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (_disposed) return;
            if (_requested == source && _recorder is not null) return;
            _requested = source;
            _retryAfter = Environment.TickCount64 + 15000;
            if (_recorder is not null) { await _recorder.DisposeAsync(); _recorder = null; }
            _requested = source;
            Interlocked.Exchange(ref _lastFrame, 0);
            if (source is null) { Status = "已暂停"; return; }
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            { Status = "此系统不支持进程音频采样"; return; }
            var processName = ResolveProcessName(source);
            if (processName is null)
            { Status = "无法明确匹配播放器进程"; return; }
            var processes = Process.GetProcessesByName(processName);
            int pid;
            try
            {
                var candidates = processes.Where(p => p.MainWindowHandle != IntPtr.Zero).ToArray();
                if (processes.Length == 1) pid = processes[0].Id;
                else if (candidates.Length == 1) pid = candidates[0].Id;
                else { Status = "播放器进程不唯一，未采样"; return; }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            var analyzer = new SpectrumAnalyzer();
            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)pid)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
                .BuildAsync();
            if (_disposed) { await recorder.DisposeAsync(); return; }
            recorder.DataAvailable += (buffer, flags, _, _) =>
            {
                if ((flags & NAudio.CoreAudioApi.AudioClientBufferFlags.Silent) != 0)
                {
                    analyzer = new SpectrumAnalyzer();
                    Interlocked.Exchange(ref _lastFrame, 0);
                    return;
                }
                var levels = analyzer.Push(MemoryMarshal.Cast<byte, float>(buffer));
                if (levels is null) return;
                Volatile.Write(ref _bands, levels);
                Interlocked.Exchange(ref _lastFrame, Environment.TickCount64);
            };
            recorder.RecordingStopped += (_, _) =>
            {
                Interlocked.Exchange(ref _lastFrame, 0);
                Status = "采样已停止";
                _requested = null;
            };
            _recorder = recorder;
            recorder.StartRecording();
            Status = "真实播放器频谱";
        }
        catch (Exception ex)
        {
            Status = $"频谱不可用：{ex.GetType().Name}";
            if (_recorder is not null) { try { await _recorder.DisposeAsync(); } catch { } _recorder = null; }
            Interlocked.Exchange(ref _lastFrame, 0);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _ = StopAsync();
    }
    private async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { if (_recorder is not null) await _recorder.DisposeAsync(); }
        catch { /* Shutdown must not crash the application. */ }
        finally { _recorder = null; _gate.Release(); }
    }
}

internal sealed class SpectrumAnalyzer
{
    private const int Size = 2048;
    private readonly Complex[] _left = new Complex[Size];
    private readonly Complex[] _right = new Complex[Size];
    private int _count;
    internal float[]? Push(ReadOnlySpan<float> stereo)
    {
        float[]? result = null;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * _count / (Size - 1));
            _left[_count] = (float.IsFinite(stereo[i]) ? stereo[i] : 0) * window;
            _right[_count] = (float.IsFinite(stereo[i + 1]) ? stereo[i + 1] : 0) * window;
            if (++_count != Size) continue;
            Transform(_left); Transform(_right);
            result = new float[16];
            for (var band = 0; band < 16; band++)
            {
                var low = Math.Max(1, (int)(50 * Math.Pow(320, band / 16.0) * Size / 44100));
                var high = Math.Min(Size / 2, Math.Max(low + 1, (int)(50 * Math.Pow(320, (band + 1) / 16.0) * Size / 44100)));
                double energy = 0;
                for (var bin = low; bin < high; bin++)
                    energy += (_left[bin].Magnitude * _left[bin].Magnitude + _right[bin].Magnitude * _right[bin].Magnitude) / 2;
                var amplitude = Math.Sqrt(energy) * 4 / Size;
                result[band] = (float)Math.Clamp((20 * Math.Log10(Math.Max(amplitude, 1e-9)) + 60) / 60, 0, 1);
            }
            _count = 0;
        }
        return result;
    }
    private static void Transform(Complex[] data)
    {
        for (int i = 1, j = 0; i < Size; i++)
        {
            var bit = Size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var length = 2; length <= Size; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < Size; start += length)
            {
                var weight = Complex.One;
                for (var i = 0; i < length / 2; i++, weight *= step)
                {
                    var even = data[start + i];
                    var odd = weight * data[start + i + length / 2];
                    data[start + i] = even + odd;
                    data[start + i + length / 2] = even - odd;
                }
            }
        }
    }
}
