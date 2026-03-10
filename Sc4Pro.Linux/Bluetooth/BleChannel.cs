using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using System.Diagnostics;
using System.Text.Json;

namespace Sc4Pro.Bluetooth;

/// <summary>
/// Linux/BlueZ implementation of <see cref="IBleChannel"/>.
/// Scans, connects, and exposes raw byte send/receive via BlueZ D-Bus.
/// </summary>
public sealed class BleChannel : IBleChannel
{
    private readonly Dictionary<string, object> _writeOpts = new() { ["type"] = "command" };
    private GattCharacteristic? _txChar;
    private GattCharacteristic? _rxChar;

    private readonly List<object> _log = [];
    private readonly Stopwatch _sw = new();

    private IGattService1? _service;
    private IDevice1? _device;
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
        var adapters = await BlueZManager.GetAdaptersAsync();
        if (adapters.Count == 0) throw new InvalidOperationException("No Bluetooth adapter found.");

        var adapter = adapters.First();
        await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>
        {
            ["UUIDs"] = new[] { serviceUuid },
            ["Transport"] = "le",
        });

        var found = new TaskCompletionSource<IDevice1>();
        adapter.DeviceFound += async (_, args) =>
        {
            try
            {
                await Task.Delay(100);
                if ((await args.Device.GetUUIDsAsync()).Contains(serviceUuid))
                    found.TrySetResult(args.Device);
            }
            catch { }
        };

        await adapter.StartDiscoveryAsync();
        var device = await found.Task;
        await adapter.StopDiscoveryAsync();

        await device.ConnectAsync();
        await device.WaitForPropertyValueAsync("Connected", value: true, timeout: TimeSpan.FromSeconds(15));

        _device = device;
        _sw.Restart(); // t=0 is when the BLE connection is established

        _service = await device.GetServiceAsync(serviceUuid);
        _txChar = await _service.GetCharacteristicAsync(txUuid);
        _rxChar = await _service.GetCharacteristicAsync(rxUuid);

        _rxChar.Value += OnValue;
        await _rxChar.StartNotifyAsync();

        string name;
        try { name = await device.GetNameAsync() ?? await device.GetAliasAsync(); }
        catch { name = await device.GetAliasAsync() ?? await device.GetAddressAsync(); }
        return name;
    }

    /// <summary>Writes a raw packet to the device.</summary>
    public async Task SendAsync(byte[] packet)
    {
        if (_txChar is null) throw new InvalidOperationException("Not connected.");
        _log.Add(new
        {
            time = $"{_sw.Elapsed.TotalSeconds:F9}",
            direction = "host→device",
            opcode = "0x12",   // ATT_WRITE_REQ
            handle = "0x000d", // TX characteristic
            value = BitConverter.ToString(packet).Replace("-", ":").ToLowerInvariant(),
        });
        await _txChar.WriteValueAsync(packet, _writeOpts);
    }

    private Task OnValue(GattCharacteristic _, GattCharacteristicValueEventArgs args)
    {
        _log.Add(new
        {
            time = $"{_sw.Elapsed.TotalSeconds:F9}",
            direction = "device→host",
            opcode = "0x1b",   // ATT_HANDLE_VALUE_NTF
            handle = "0x000f", // RX characteristic
            value = BitConverter.ToString(args.Value).Replace("-", ":").ToLowerInvariant(),
        });
        Received?.Invoke(args.Value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads all readable characteristics across all GATT services and returns a
    /// UUID → raw-bytes map. Called on startup to capture device config.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> ReadConfigAsync()
    {
        if (_device is null) throw new InvalidOperationException("Not connected.");

        var result = new Dictionary<string, byte[]>();
        var services = await _device.GetServicesAsync();

        foreach (var svc in services)
        {
            var chars = await svc.GetCharacteristicsAsync();
            foreach (var ch in chars)
            {
                var props = await ch.GetAllAsync();
                if (props.UUID == _txUuid || props.UUID == _rxUuid) continue;
                if (!props.Flags.Contains("read")) continue;

                try
                {
                    result[props.UUID] = await ch.ReadValueAsync(new Dictionary<string, object>());
                }
                catch { }
            }
        }

        return result;
    }

    /// <summary>Saves the captured send/receive log to a JSON file.</summary>
    public async Task SaveDumpAsync(string path)
    {
        var json = JsonSerializer.Serialize(_log, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>Stops notifications on the RX characteristic and releases the BLE subscription.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_rxChar != null)
        {
            _rxChar.Value -= OnValue;
            try { await _rxChar.StopNotifyAsync(); } catch { }
        }
    }
}
