using System.Diagnostics;
using System.Windows.Automation;

namespace DynamicCapsule.Services;

// Opt-in, local-only UI Automation probe. Never reads prompts, reply text or history.
internal sealed class AiActivityMonitor : IDisposable
{
    internal sealed record Signal(string Key, string Source, bool Stop, int Copies, int Errors);
    internal sealed record Row(string Source, string State)
    {
        public string Badge => (State switch { "Completed" => "✓", "Error" => "!", "Cancelled" => "×", _ => "◌" }) + "  " + State;
    }
    private sealed record Entry(string Source, int Copies, int Errors, string State, DateTimeOffset Until);
    internal sealed class Tracker
    {
        private readonly Dictionary<string, Entry> _entries = [];
        internal Row[] Step(IEnumerable<Signal> signals, DateTimeOffset now)
        {
            var seen = new HashSet<string>();
            foreach (var s in signals)
            {
                seen.Add(s.Key);
                _entries.TryGetValue(s.Key, out var before);
                if (s.Stop)
                {
                    if (before?.State != "Running")
                        _entries[s.Key] = new(s.Source, s.Copies, s.Errors, "Running", DateTimeOffset.MaxValue);
                    else if (before.Until != DateTimeOffset.MaxValue)
                        _entries[s.Key] = before with { Until = DateTimeOffset.MaxValue };
                }
                else if (before?.State == "Running")
                {
                    var state = s.Errors > before.Errors ? "Error" : s.Copies > before.Copies ? "Completed" : null;
                    if (state != null) _entries[s.Key] = before with { State = state, Until = now.AddSeconds(6) };
                    else if (before.Until == DateTimeOffset.MaxValue) _entries[s.Key] = before with { Until = now.AddSeconds(3) };
                }
            }
            foreach (var (key, item) in _entries.ToArray())
                if (item.Until <= now || (!seen.Contains(key) && item.State == "Running")) _entries.Remove(key);
            return _entries.Values.OrderByDescending(x => x.State == "Running").ThenBy(x => x.Source)
                .Select(x => new Row(x.Source, x.State)).ToArray();
        }
    }

    private readonly CancellationTokenSource _stop = new();
    internal AiActivityMonitor(Action<Row[]> report)
    {
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            var tracker = new Tracker();
            Row[] previous = [];
            while (!token.IsCancellationRequested)
            {
                var observations = new List<Signal>();
                foreach (var name in new[] { "ChatGPT", "Doubao", "豆包", "msedge", "chrome", "firefox" })
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    using (p)
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            if (p.MainWindowHandle == IntPtr.Zero) continue;
                            var s = Observe(p.MainWindowHandle, name);
                            if (s != null) observations.Add(s);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Window closed or provider unavailable. */ }
                    }
                }
                var rows = tracker.Step(observations, DateTimeOffset.UtcNow);
                if (!token.IsCancellationRequested && !rows.SequenceEqual(previous)) report(rows);
                previous = rows;
                await Task.Delay(1500, token).ConfigureAwait(false);
            }
        }, token);
    }

    internal static Signal? Observe(IntPtr hwnd, string process)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var browser = process is "msedge" or "chrome" or "firefox";
        var doubao = process is "Doubao" or "豆包";
        string key = hwnd.ToString();
        if (browser)
        {
            // Only inspect page controls after validating the browser's own location bar.
            var addressCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new OrCondition(Any(AutomationElement.AutomationIdProperty, "addressEditBox", "urlbar-input", "omnibox"),
                    Any(AutomationElement.NameProperty, "Address and search bar", "地址栏和搜索栏", "地址和搜索栏")),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            var address = root.FindFirst(TreeScope.Descendants, addressCondition);
            if (address == null || !address.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return null;
            for (var parent = TreeWalker.ControlViewWalker.GetParent(address); parent != null && !parent.Equals(root);
                 parent = TreeWalker.ControlViewWalker.GetParent(parent))
                if (parent.Current.ControlType == ControlType.Document) return null;
            var raw = ((ValuePattern)pattern).Current.Value;
            if (!Uri.TryCreate(raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? raw : "https://" + raw,
                    UriKind.Absolute, out var url)) return null;
            doubao = url.Host is "www.doubao.com" or "doubao.com";
            if (url.Host != "chatgpt.com" && !doubao) return null;
            key += ":" + url.AbsolutePath;
            root = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            if (root == null) return null;
        }
        int Buttons(params string[] labels) => root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            Any(AutomationElement.NameProperty, labels))).Count;
        return new(key, doubao ? (browser ? "豆包 Web" : "豆包") : browser ? "ChatGPT Web" : "ChatGPT",
            ((browser || doubao) && Buttons("停止", "Stop", "停止流式传输", "停止回复") > 0) ||
            Buttons("Stop generating", "Stop streaming", "Stop response", "停止生成", "停止回答", "停止响应", "停止输出", "停止回應") > 0,
            Buttons("Copy response", "Copy message", "复制回复", "复制回答", "Copy", "复制"),
            Buttons("Retry", "Try again", "重试", "重試"));
    }

    private static Condition Any(AutomationProperty property, params string[] values) =>
        new OrCondition(values.Select(v => new PropertyCondition(property, v, PropertyConditionFlags.IgnoreCase)).ToArray());
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
