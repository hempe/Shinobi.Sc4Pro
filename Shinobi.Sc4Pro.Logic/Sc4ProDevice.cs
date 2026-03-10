using Microsoft.Extensions.Logging;
using Shinobi.Sc4Pro.Bluetooth;
using Shinobi.Sc4Pro.Packets;

namespace Shinobi.Sc4Pro.Logic;

/// <summary>
/// High-level SC4Pro device entry point.
/// Connects over BLE, reads device config characteristics, performs the
/// handshake, and exposes the device serial and current settings.
/// </summary>
public sealed class Sc4ProDevice(IBleChannel _ble, ILogger? _logger = null) : IAsyncDisposable
{
    // ── Well-known GATT characteristic UUIDs ─────────────────────────────────

    private const string UuidManufacturer = "00002a29-0000-1000-8000-00805f9b34fb";
    private const string UuidModel = "00002a24-0000-1000-8000-00805f9b34fb";
    private const string UuidSerial = "00002a25-0000-1000-8000-00805f9b34fb";
    private const string UuidFirmware = "00002a26-0000-1000-8000-00805f9b34fb";
    private const string UuidHardware = "00002a27-0000-1000-8000-00805f9b34fb";
    private const string UuidBattery = "00002a19-0000-1000-8000-00805f9b34fb";
    private const string UuidVolume = "00002b02-0000-1000-8000-00805f9b34fb";
    private const string UuidModeConfig = "00002b03-0000-1000-8000-00805f9b34fb";
    private const string UuidEqBands = "00002b04-0000-1000-8000-00805f9b34fb";

    // ── Public properties (populated by ConnectAsync) ─────────────────────────

    /// <summary>
    /// Bluetooth device name (from the BLE advertisement).
    /// </summary>
    public string DeviceName { get; private set; } = "";
    /// <summary>Device serial number, populated from the Sync ack (e.g. "SC40B250038-03").</summary>
    public string Serial { get; private set; } = "";
    /// <summary>Manufacturer name read from GATT characteristic 0x2A29.</summary>
    public string Manufacturer { get; private set; } = "";
    /// <summary>Model number read from GATT characteristic 0x2A24.</summary>
    public string Model { get; private set; } = "";
    /// <summary>Firmware revision read from GATT characteristic 0x2A26.</summary>
    public string FirmwareRevision { get; private set; } = "";
    /// <summary>Hardware revision read from GATT characteristic 0x2A27.</summary>
    public string HardwareRevision { get; private set; } = "";

    /// <summary>Battery level in percent (0–100), or -1 if not available.</summary>
    public int BatteryLevel { get; private set; } = -1;

    /// <summary>Sensitivity/volume setting read from the device (e.g. "10").</summary>
    public string Volume { get; private set; } = "";

    /// <summary>Mode config string read from the device (e.g. "110").</summary>
    public string ModeConfig { get; private set; } = "";

    /// <summary>EQ band values as a comma-separated string (e.g. "008,008,…").</summary>
    public string EqBands { get; private set; } = "";

    /// <summary>Fired for every unsolicited packet (shots, button presses).</summary>
    public event Func<Sc4ProPacket, Task>? PacketReceived;

    // ── Internals ─────────────────────────────────────────────────────────────

    private Sc4ProClient? _client;

    // ── Connect ───────────────────────────────────────────────────────────────

    /// <summary>Scans for the device, connects, reads GATT config characteristics, and runs the handshake.</summary>
    public async Task ConnectAsync()
    {
        _client = new Sc4ProClient(_ble);
        _client.PacketReceived += pkt => PacketReceived?.Invoke(pkt) ?? Task.CompletedTask;

        _logger?.LogDebug("Scanning for SC4Pro… ");
        DeviceName = await _client.ConnectAsync();
        _logger?.LogDebug($"connected: {DeviceName}");

        Console.Write("Reading device config… ");
        ParseConfig(await _ble.ReadConfigAsync());
        _logger?.LogDebug("done");

        // Handshake: Sync → DS2 → DS1 → EqSetting (matches dump exactly).
        Console.Write("Handshake… ");
        var syncAck = await _client.SyncAsync();
        Serial = syncAck.Serial;

        await _client.SetDeviceSetting2Async(
            DS2Flags.Language | DS2Flags.Volume | DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
            volume: 3, appIndex: 1, appIndexOnOff: 1);

        await _client.SetDeviceSetting1Async(
            DS1Flags.Mode | DS1Flags.Unit | DS1Flags.Club | DS1Flags.CarryTotal,
            mode: 0, club: ClubType.W5);

        await _client.SetEqAsync();
        _logger?.LogDebug("done");
    }

    private void ParseConfig(IReadOnlyDictionary<string, byte[]> raw)
    {
        static string Ascii(byte[] b) => System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0');

        if (raw.TryGetValue(UuidManufacturer, out var v)) Manufacturer = Ascii(v);
        if (raw.TryGetValue(UuidModel, out v)) Model = Ascii(v);
        if (raw.TryGetValue(UuidFirmware, out v)) FirmwareRevision = Ascii(v);
        if (raw.TryGetValue(UuidHardware, out v)) HardwareRevision = Ascii(v);
        if (raw.TryGetValue(UuidVolume, out v)) Volume = Ascii(v);
        if (raw.TryGetValue(UuidModeConfig, out v)) ModeConfig = Ascii(v);
        if (raw.TryGetValue(UuidEqBands, out v)) EqBands = Ascii(v);
        if (raw.TryGetValue(UuidBattery, out v) && v.Length > 0) BatteryLevel = v[0];
    }

    // ── Convenience wrappers ──────────────────────────────────────────────────

    /// <summary>Selects a club and re-arms the device for the next shot.</summary>
    public Task<DeviceSetting1Ack> SetClubAsync(ClubType club)
        => Client.SetClubAsync(club);

    /// <summary>Switches between normal (<see langword="false"/>) and swing-speed (<see langword="true"/>) mode.</summary>
    public Task SetModeAsync(bool swingSpeed)
        => Client.SetModeAsync(swingSpeed);

    /// <summary>Arms the device for the next shot.</summary>
    public Task<ShotReadyAck> ShotReadyAsync()
        => Client.ShotReadyAsync();

    /// <summary>The underlying protocol client for advanced commands.</summary>
    public Sc4ProClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    // ── Logging ───────────────────────────────────────────────────────────────

    /// <summary>Prints all device settings and identity fields to stdout.</summary>
    public void LogSettings()
    {
        _logger?.LogDebug($"  Device name  : {DeviceName}");
        _logger?.LogDebug($"  Manufacturer : {Manufacturer}");
        _logger?.LogDebug($"  Model        : {Model}");
        _logger?.LogDebug($"  Serial       : {Serial}");
        _logger?.LogDebug($"  Firmware     : {FirmwareRevision}");
        _logger?.LogDebug($"  Hardware     : {HardwareRevision}");
        _logger?.LogDebug($"  Battery      : {(BatteryLevel >= 0 ? $"{BatteryLevel}%" : "(unknown)")}");
        _logger?.LogDebug($"  Volume       : {Volume}");
        _logger?.LogDebug($"  Mode config  : {ModeConfig}");
        _logger?.LogDebug($"  EQ bands     : {EqBands}");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <summary>Disposes the underlying BLE channel.</summary>
    public ValueTask DisposeAsync() => _ble.DisposeAsync();
}
