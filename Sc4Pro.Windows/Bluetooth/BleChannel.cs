using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace Sc4Pro.Bluetooth;

/// <summary>
/// Windows WinRT implementation of <see cref="IBleChannel"/>.
/// Scans, connects, and exposes raw byte send/receive via Windows.Devices.Bluetooth.
/// </summary>
public sealed class BleChannel : IBleChannel
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _gattService;
    private GattCharacteristic? _txChar;
    private GattCharacteristic? _rxChar;
    private string _txUuid = "";
    private string _rxUuid = "";

    /// <summary>Fired for every raw notification received from the device.</summary>
    public event Func<byte[], Task>? Received;

    /// <summary>
    /// Scans for a device advertising <paramref name="serviceUuid"/>, connects,
    /// discovers characteristics, and subscribes to notifications.
    /// Returns the device name.
    /// </summary>
    public async Task<string> ConnectAsync(string serviceUuid, string txUuid, string rxUuid)
    {
        _txUuid = txUuid;
        _rxUuid = rxUuid;

        var serviceGuid = Guid.Parse(serviceUuid);
        var found = new TaskCompletionSource<ulong>();

        _watcher = new BluetoothLEAdvertisementWatcher();
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(serviceGuid);
        _watcher.Received += (_, args) => found.TrySetResult(args.BluetoothAddress);
        _watcher.Start();

        var address = await found.Task;
        _watcher.Stop();

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Failed to open BLE device.");

        var svcResult = await _device.GetGattServicesForUuidAsync(serviceGuid, BluetoothCacheMode.Uncached);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new InvalidOperationException($"GATT service {serviceUuid} not found.");

        _gattService = svcResult.Services[0];

        _txChar = await GetCharacteristicAsync(_gattService, Guid.Parse(txUuid));
        _rxChar = await GetCharacteristicAsync(_gattService, Guid.Parse(rxUuid));

        _rxChar.ValueChanged += OnValueChanged;
        var notifyStatus = await _rxChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (notifyStatus != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Failed to subscribe to notifications: {notifyStatus}");

        return _device.Name ?? address.ToString();
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(GattDeviceService service, Guid uuid)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
            throw new InvalidOperationException($"Characteristic {uuid} not found.");
        return result.Characteristics[0];
    }

    /// <summary>Writes a raw packet to the device.</summary>
    public async Task SendAsync(byte[] packet)
    {
        if (_txChar is null) throw new InvalidOperationException("Not connected.");

        var writeType = _txChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        var status = await _txChar.WriteValueAsync(packet.AsBuffer(), writeType);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Write failed: {status}");
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        Received?.Invoke(bytes ?? []);
    }

    /// <summary>
    /// Reads all readable characteristics across all GATT services and returns a
    /// UUID → raw-bytes map. Called on startup to capture device config.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> ReadConfigAsync()
    {
        if (_device is null) throw new InvalidOperationException("Not connected.");

        var result = new Dictionary<string, byte[]>();

        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success) return result;

        foreach (var svc in servicesResult.Services)
        {
            var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success) continue;

            foreach (var ch in charsResult.Characteristics)
            {
                var uuidStr = ch.Uuid.ToString();
                if (uuidStr == _txUuid || uuidStr == _rxUuid) continue;
                if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;

                try
                {
                    var readResult = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (readResult.Status == GattCommunicationStatus.Success)
                    {
                        CryptographicBuffer.CopyToByteArray(readResult.Value, out var bytes);
                        result[uuidStr] = bytes ?? [];
                    }
                }
                catch { }
            }
        }

        return result;
    }

    /// <summary>Stops notifications, disposes GATT services, and releases the device.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_rxChar != null)
        {
            _rxChar.ValueChanged -= OnValueChanged;
            try
            {
                await _rxChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
        }

        _watcher?.Stop();
        _gattService?.Dispose();
        _device?.Dispose();
    }
}
