using DynamicCapsule.Models;
using Windows.Devices.Enumeration;
using Windows.Networking.Connectivity;

namespace DynamicCapsule.Services;

internal sealed class ConnectivityEventService : IDisposable
{
    private const string BluetoothClassicSelector =
        "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    private const string BluetoothLowEnergySelector =
        "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
    private const string IsConnectedProperty =
        "System.Devices.Aep.IsConnected";
    private const string IsPairedProperty =
        "System.Devices.Aep.IsPaired";
    private const string ContainerIdProperty =
        "System.Devices.Aep.ContainerId";
    private static readonly string[] BluetoothProperties =
    [
        IsConnectedProperty,
        IsPairedProperty,
        ContainerIdProperty,
        "System.ItemNameDisplay"
    ];

    private readonly Lock _bluetoothGate = new();
    private readonly Dictionary<string, BluetoothEndpointState>
        _bluetoothEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DeviceWatcher> _completedBluetoothWatchers = [];
    private readonly Dictionary<string, DateTimeOffset>
        _recentBluetoothEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _networkRefreshGate = new(1, 1);
    private readonly System.Threading.Timer _networkDebounceTimer;

    private DeviceWatcher? _classicBluetoothWatcher;
    private DeviceWatcher? _lowEnergyBluetoothWatcher;
    private NetworkState? _networkState;
    private bool _started;
    private bool _bluetoothEnumerationReady;
    private bool _disposed;

    internal ConnectivityEventService()
    {
        _networkDebounceTimer = new System.Threading.Timer(
            _ => _ = RefreshNetworkAsync(emitChanges: true),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal event Action<CapsuleEvent>? EventReceived;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _ = RefreshNetworkAsync(emitChanges: false);

        try
        {
            _classicBluetoothWatcher = CreateBluetoothWatcher(
                BluetoothClassicSelector);
            _lowEnergyBluetoothWatcher = CreateBluetoothWatcher(
                BluetoothLowEnergySelector);
            StartBluetoothWatcher(_classicBluetoothWatcher);
            StartBluetoothWatcher(_lowEnergyBluetoothWatcher);
        }
        catch
        {
            StopBluetoothWatchers();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }

        _networkDebounceTimer.Dispose();
        StopBluetoothWatchers();
        _networkRefreshGate.Dispose();

        lock (_bluetoothGate)
        {
            _bluetoothEndpoints.Clear();
            _completedBluetoothWatchers.Clear();
            _recentBluetoothEvents.Clear();
        }
    }

    private static DeviceWatcher CreateBluetoothWatcher(string selector)
    {
        return DeviceInformation.CreateWatcher(
            selector,
            BluetoothProperties,
            DeviceInformationKind.AssociationEndpoint);
    }

    private void StartBluetoothWatcher(DeviceWatcher watcher)
    {
        watcher.Added += OnBluetoothAdded;
        watcher.Updated += OnBluetoothUpdated;
        watcher.Removed += OnBluetoothRemoved;
        watcher.EnumerationCompleted += OnBluetoothEnumerationCompleted;
        watcher.Start();
    }

    private void StopBluetoothWatchers()
    {
        StopBluetoothWatcher(_classicBluetoothWatcher);
        StopBluetoothWatcher(_lowEnergyBluetoothWatcher);
        _classicBluetoothWatcher = null;
        _lowEnergyBluetoothWatcher = null;
    }

    private void StopBluetoothWatcher(DeviceWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.Added -= OnBluetoothAdded;
        watcher.Updated -= OnBluetoothUpdated;
        watcher.Removed -= OnBluetoothRemoved;
        watcher.EnumerationCompleted -= OnBluetoothEnumerationCompleted;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started
                or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch
        {
            // A watcher can race with shutdown; there is nothing left to do.
        }
    }

    private void OnBluetoothEnumerationCompleted(
        DeviceWatcher sender,
        object args)
    {
        lock (_bluetoothGate)
        {
            _completedBluetoothWatchers.Add(sender);
            _bluetoothEnumerationReady =
                _completedBluetoothWatchers.Count >= 2;
        }
    }

    private void OnBluetoothAdded(
        DeviceWatcher sender,
        DeviceInformation information)
    {
        if (_disposed || !IsPaired(information))
        {
            return;
        }

        BluetoothTransition? transition = null;
        lock (_bluetoothGate)
        {
            var state = CreateEndpointState(information);
            var wasConnected = IsContainerConnected(state.ContainerKey);
            _bluetoothEndpoints[state.EndpointId] = state;
            var isConnected = IsContainerConnected(state.ContainerKey);
            if (_bluetoothEnumerationReady && wasConnected != isConnected)
            {
                transition = new BluetoothTransition(
                    state.ContainerKey,
                    state.Name,
                    isConnected);
            }
        }

        PublishBluetoothTransition(transition);
    }

    private void OnBluetoothUpdated(
        DeviceWatcher sender,
        DeviceInformationUpdate update)
    {
        if (_disposed)
        {
            return;
        }

        BluetoothTransition? transition = null;
        lock (_bluetoothGate)
        {
            if (!_bluetoothEndpoints.TryGetValue(
                    update.Id,
                    out var previous))
            {
                return;
            }

            var wasConnected = IsContainerConnected(previous.ContainerKey);
            if (TryGetBoolean(
                    update.Properties,
                    IsPairedProperty,
                    out var isPaired)
                && !isPaired)
            {
                _bluetoothEndpoints.Remove(update.Id);
            }
            else
            {
                var isConnected = TryGetBoolean(
                    update.Properties,
                    IsConnectedProperty,
                    out var updatedConnection)
                    ? updatedConnection
                    : previous.IsConnected;
                _bluetoothEndpoints[update.Id] = previous with
                {
                    IsConnected = isConnected
                };
            }

            var isContainerConnected =
                IsContainerConnected(previous.ContainerKey);
            if (_bluetoothEnumerationReady
                && wasConnected != isContainerConnected)
            {
                transition = new BluetoothTransition(
                    previous.ContainerKey,
                    previous.Name,
                    isContainerConnected);
            }
        }

        PublishBluetoothTransition(transition);
    }

    private void OnBluetoothRemoved(
        DeviceWatcher sender,
        DeviceInformationUpdate update)
    {
        if (_disposed)
        {
            return;
        }

        BluetoothTransition? transition = null;
        lock (_bluetoothGate)
        {
            if (!_bluetoothEndpoints.TryGetValue(update.Id, out var previous))
            {
                return;
            }

            var wasConnected = IsContainerConnected(previous.ContainerKey);
            _bluetoothEndpoints.Remove(update.Id);
            var isConnected = IsContainerConnected(previous.ContainerKey);
            if (_bluetoothEnumerationReady && wasConnected != isConnected)
            {
                transition = new BluetoothTransition(
                    previous.ContainerKey,
                    previous.Name,
                    isConnected);
            }
        }

        PublishBluetoothTransition(transition);
    }

    private void PublishBluetoothTransition(BluetoothTransition? transition)
    {
        if (_disposed || transition is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var deduplicationKey =
            $"{transition.ContainerKey}:{transition.IsConnected}";
        lock (_bluetoothGate)
        {
            if (_recentBluetoothEvents.TryGetValue(
                    deduplicationKey,
                    out var lastPublished)
                && now - lastPublished < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _recentBluetoothEvents[deduplicationKey] = now;
        }

        EventReceived?.Invoke(new CapsuleEvent(
            $"connectivity:bluetooth:{transition.ContainerKey}",
            CapsuleEventKind.Bluetooth,
            CapsuleEventPriority.High,
            "system.bluetooth",
            "蓝牙",
            transition.IsConnected
                ? "蓝牙设备已连接"
                : "蓝牙设备已断开",
            transition.Name,
            null,
            null,
            EventPrivacyLevel.Full,
            now,
            now.AddSeconds(5)));
    }

    private BluetoothEndpointState CreateEndpointState(
        DeviceInformation information)
    {
        var containerKey = TryGetContainerKey(information.Properties)
            ?? information.Id;
        var name = string.IsNullOrWhiteSpace(information.Name)
            ? "蓝牙设备"
            : information.Name.Trim();
        var isConnected = TryGetBoolean(
            information.Properties,
            IsConnectedProperty,
            out var connected) && connected;

        return new BluetoothEndpointState(
            information.Id,
            containerKey,
            name,
            isConnected);
    }

    private static bool IsPaired(DeviceInformation information)
    {
        return information.Pairing.IsPaired
               || TryGetBoolean(
                   information.Properties,
                   IsPairedProperty,
                   out var paired) && paired;
    }

    private bool IsContainerConnected(string containerKey)
    {
        return _bluetoothEndpoints.Values.Any(state =>
            string.Equals(
                state.ContainerKey,
                containerKey,
                StringComparison.OrdinalIgnoreCase)
            && state.IsConnected);
    }

    private static string? TryGetContainerKey(
        IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(ContainerIdProperty, out var value)
            || value is null)
        {
            return null;
        }

        return value switch
        {
            Guid guid => guid.ToString("D"),
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => value.ToString()
        };
    }

    private static bool TryGetBoolean(
        IReadOnlyDictionary<string, object> properties,
        string propertyName,
        out bool value)
    {
        if (properties.TryGetValue(propertyName, out var rawValue)
            && rawValue is bool boolean)
        {
            value = boolean;
            return true;
        }

        value = false;
        return false;
    }

    private void OnNetworkStatusChanged(object sender)
    {
        if (_disposed)
        {
            return;
        }

        _networkDebounceTimer.Change(
            TimeSpan.FromMilliseconds(700),
            Timeout.InfiniteTimeSpan);
    }

    private async Task RefreshNetworkAsync(bool emitChanges)
    {
        if (_disposed)
        {
            return;
        }

        await _networkRefreshGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            var next = ReadNetworkState();
            var previous = _networkState;
            _networkState = next;
            if (!emitChanges || previous is null || previous == next)
            {
                return;
            }

            PublishNetworkTransition(previous, next);
        }
        finally
        {
            _networkRefreshGate.Release();
        }
    }

    private static NetworkState ReadNetworkState()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return NetworkState.Disconnected;
            }

            var connectivityLevel = profile.GetNetworkConnectivityLevel();
            var isConnected =
                connectivityLevel is not NetworkConnectivityLevel.None;
            var isWiFi = profile.IsWlanConnectionProfile;
            var name = profile.ProfileName;
            if (isWiFi)
            {
                try
                {
                    name = profile.WlanConnectionProfileDetails?
                               .GetConnectedSsid()
                           ?? name;
                }
                catch
                {
                    // ProfileName remains a useful fallback when SSID access
                    // is unavailable under the current privacy policy.
                }
            }

            return new NetworkState(
                isConnected,
                isWiFi,
                string.IsNullOrWhiteSpace(name) ? "Wi-Fi" : name.Trim());
        }
        catch
        {
            return NetworkState.Disconnected;
        }
    }

    private void PublishNetworkTransition(
        NetworkState previous,
        NetworkState next)
    {
        var wasOnWiFi = previous.IsConnected && previous.IsWiFi;
        var isOnWiFi = next.IsConnected && next.IsWiFi;
        if (!wasOnWiFi && !isOnWiFi)
        {
            return;
        }

        string title;
        string message;
        if (!wasOnWiFi && isOnWiFi)
        {
            title = "已连接 Wi-Fi";
            message = next.Name;
        }
        else if (wasOnWiFi && !isOnWiFi)
        {
            title = "Wi-Fi 已断开";
            message = previous.Name;
        }
        else if (!string.Equals(
                     previous.Name,
                     next.Name,
                     StringComparison.OrdinalIgnoreCase))
        {
            title = "Wi-Fi 已切换";
            message = next.Name;
        }
        else
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        EventReceived?.Invoke(new CapsuleEvent(
            "connectivity:wifi",
            CapsuleEventKind.WiFi,
            CapsuleEventPriority.High,
            "system.wifi",
            "Wi-Fi",
            title,
            message,
            null,
            null,
            EventPrivacyLevel.Full,
            now,
            now.AddSeconds(5)));
    }

    private sealed record BluetoothEndpointState(
        string EndpointId,
        string ContainerKey,
        string Name,
        bool IsConnected);

    private sealed record BluetoothTransition(
        string ContainerKey,
        string Name,
        bool IsConnected);

    private sealed record NetworkState(
        bool IsConnected,
        bool IsWiFi,
        string Name)
    {
        internal static NetworkState Disconnected { get; } =
            new(false, false, string.Empty);
    }
}
