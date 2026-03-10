using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.OS;
using Java.Util;
using ScanMode = Android.Bluetooth.LE.ScanMode;

namespace Sc4Pro.Bluetooth;

/// <summary>
/// Android implementation of <see cref="IBleChannel"/>.
/// Scans, connects, and exposes raw byte send/receive via Android.Bluetooth GATT APIs.
/// <para>
/// The host application must declare and request <c>BLUETOOTH_SCAN</c> and
/// <c>BLUETOOTH_CONNECT</c> permissions before calling <see cref="ConnectAsync"/>.
/// </para>
/// </summary>
public sealed class BleChannel : IBleChannel
{
    private static readonly UUID CccdUuid =
        UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private BluetoothGatt? _gatt;
    private BluetoothGattCharacteristic? _txChar;
    private BluetoothGattCharacteristic? _rxChar;
    private string _txUuid = "";
    private string _rxUuid = "";

    // One TCS per pending GATT operation — Android enforces one at a time.
    private TaskCompletionSource<bool>? _connectTcs;
    private TaskCompletionSource<bool>? _discoverTcs;
    private TaskCompletionSource<bool>? _descriptorWriteTcs;
    private TaskCompletionSource<(byte[] value, GattStatus status)>? _readTcs;

    private GattCallbackImpl? _gattCallback;

    /// <summary>Fired for every raw notification received from the device.</summary>
    public event Func<byte[], Task>? Received;

    /// <summary>
    /// Scans for a device advertising <paramref name="serviceUuid"/>, connects,
    /// discovers characteristics, and subscribes to notifications.
    /// Returns the device name.
    /// </summary>
    public async Task<string> ConnectAsync(string serviceUuid, string txUuid, string rxUuid)
    {
        _txUuid = txUuid.ToLowerInvariant();
        _rxUuid = rxUuid.ToLowerInvariant();

        var btManager = Application.Context.GetSystemService(global::Android.Content.Context.BluetoothService) as BluetoothManager
            ?? throw new InvalidOperationException("BluetoothManager unavailable.");
        var adapter = btManager.Adapter
            ?? throw new InvalidOperationException("No Bluetooth adapter.");
        var scanner = adapter.BluetoothLeScanner
            ?? throw new InvalidOperationException("BLE scanner unavailable.");

        // ── Scan ─────────────────────────────────────────────────────────────
        var deviceFound = new TaskCompletionSource<BluetoothDevice>();
        var filter = new ScanFilter.Builder()
            .SetServiceUuid(ParcelUuid.FromString(serviceUuid))
            .Build();
        var settings = new ScanSettings.Builder()
            .SetScanMode(ScanMode.LowLatency)
            .Build();

        // Capture scanCallback in closure so it can stop itself on first result.
        ScanCallback? scanCallback = null;
        scanCallback = new ActionScanCallback(result =>
        {
            scanner.StopScan(scanCallback);
            deviceFound.TrySetResult(result.Device!);
        });
        scanner.StartScan(new[] { filter }, settings, scanCallback);

        BluetoothDevice device;
        try
        {
            device = await deviceFound.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            scanner.StopScan(scanCallback);
            throw;
        }

        // ── Connect ───────────────────────────────────────────────────────────
        _gattCallback = new GattCallbackImpl(this);
        _connectTcs = new TaskCompletionSource<bool>();
        _gatt = device.ConnectGatt(Application.Context, false, _gattCallback, BluetoothTransports.Le)
            ?? throw new InvalidOperationException("ConnectGatt returned null.");
        await _connectTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // ── Discover services ─────────────────────────────────────────────────
        _discoverTcs = new TaskCompletionSource<bool>();
        _gatt.DiscoverServices();
        await _discoverTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // ── Get characteristics ───────────────────────────────────────────────
        var service = _gatt.GetService(UUID.FromString(serviceUuid))
            ?? throw new InvalidOperationException($"Service {serviceUuid} not found.");
        _txChar = service.GetCharacteristic(UUID.FromString(txUuid))
            ?? throw new InvalidOperationException($"TX characteristic {txUuid} not found.");
        _rxChar = service.GetCharacteristic(UUID.FromString(rxUuid))
            ?? throw new InvalidOperationException($"RX characteristic {rxUuid} not found.");

        // ── Enable notifications ──────────────────────────────────────────────
        _gatt.SetCharacteristicNotification(_rxChar, true);
        var cccd = _rxChar.GetDescriptor(CccdUuid)
            ?? throw new InvalidOperationException("CCCD descriptor not found on RX characteristic.");
        _descriptorWriteTcs = new TaskCompletionSource<bool>();
        _gatt.WriteDescriptor(cccd, BluetoothGattDescriptor.EnableNotificationValue!.ToArray());
        await _descriptorWriteTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        return device.Name ?? device.Address ?? "";
    }

    /// <summary>Writes a raw packet to the device.</summary>
    public Task SendAsync(byte[] packet)
    {
        if (_gatt is null || _txChar is null) throw new InvalidOperationException("Not connected.");

        var writeType = _txChar.Properties.HasFlag(GattProperty.WriteNoResponse)
            ? (int)GattWriteType.NoResponse
            : (int)GattWriteType.Default;

        var status = _gatt.WriteCharacteristic(_txChar, packet, writeType);
        if (status != 0) // BluetoothStatusCodes.Success
            throw new InvalidOperationException($"WriteCharacteristic failed with status {status}.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads all readable characteristics across all GATT services and returns a
    /// UUID → raw-bytes map. Reads are serialized (Android allows one GATT op at a time).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> ReadConfigAsync()
    {
        if (_gatt is null) throw new InvalidOperationException("Not connected.");

        var result = new Dictionary<string, byte[]>();

        foreach (var svc in _gatt.Services ?? [])
        {
            foreach (var ch in svc.Characteristics ?? [])
            {
                var uuidStr = ch.Uuid?.ToString() ?? "";
                if (uuidStr == _txUuid || uuidStr == _rxUuid) continue;
                if (!ch.Properties.HasFlag(GattProperty.Read)) continue;

                _readTcs = new TaskCompletionSource<(byte[], GattStatus)>();
                _gatt.ReadCharacteristic(ch);
                try
                {
                    var (value, gattStatus) = await _readTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (gattStatus == GattStatus.Success)
                        result[uuidStr] = value;
                }
                catch (TimeoutException) { }
            }
        }

        return result;
    }

    /// <summary>Disables notifications, disconnects, and releases the GATT connection slot.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_gatt is null) return;

        if (_rxChar != null)
        {
            _gatt.SetCharacteristicNotification(_rxChar, false);
            try
            {
                var cccd = _rxChar.GetDescriptor(CccdUuid);
                if (cccd != null)
                    _gatt.WriteDescriptor(cccd, BluetoothGattDescriptor.DisableNotificationValue!.ToArray());
            }
            catch { }
        }

        _gatt.Disconnect();
        // Close() MUST be called after Disconnect() to release the system GATT slot.
        // Android has a system-wide limit (~7) on concurrent GATT connections.
        _gatt.Close();
        _gatt = null;

        await Task.CompletedTask;
    }

    // ── Inner: GattCallbackImpl ───────────────────────────────────────────────

    private sealed class GattCallbackImpl(BleChannel owner) : BluetoothGattCallback
    {
        public override void OnConnectionStateChange(
            BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected && status == GattStatus.Success)
                owner._connectTcs?.TrySetResult(true);
            else if (newState == ProfileState.Disconnected)
                owner._connectTcs?.TrySetException(
                    new InvalidOperationException(
                        $"GATT connection failed: status={status}, state={newState}"));
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (status == GattStatus.Success)
                owner._discoverTcs?.TrySetResult(true);
            else
                owner._discoverTcs?.TrySetException(
                    new InvalidOperationException($"Service discovery failed: {status}"));
        }

        public override void OnDescriptorWrite(
            BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            if (status == GattStatus.Success)
                owner._descriptorWriteTcs?.TrySetResult(true);
            else
                owner._descriptorWriteTcs?.TrySetException(
                    new InvalidOperationException($"Descriptor write failed: {status}"));
        }

        // API 33+ preferred: value delivered directly, no racy GetValue() call needed.
        public override void OnCharacteristicChanged(
            BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value)
        {
            owner.Received?.Invoke(value);
        }

        // Deprecated fallback for API < 33.
#pragma warning disable CS0618
        public override void OnCharacteristicChanged(
            BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            owner.Received?.Invoke(characteristic?.GetValue() ?? []);
        }
#pragma warning restore CS0618

        // API 33+ preferred.
        public override void OnCharacteristicRead(
            BluetoothGatt gatt, BluetoothGattCharacteristic characteristic,
            byte[] value, GattStatus status)
        {
            owner._readTcs?.TrySetResult((value, status));
        }

        // Deprecated fallback for API < 33.
#pragma warning disable CS0618
        public override void OnCharacteristicRead(
            BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            owner._readTcs?.TrySetResult((characteristic?.GetValue() ?? [], status));
        }
#pragma warning restore CS0618
    }

    // ── Inner: ActionScanCallback ─────────────────────────────────────────────

    private sealed class ActionScanCallback(Action<ScanResult> onResult) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result is not null) onResult(result);
        }
    }
}
