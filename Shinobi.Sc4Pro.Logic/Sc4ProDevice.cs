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

    // ── Club groups for cycling (pressing a category button multiple times) ───

    // Button 11 (Wood): cycles W3–W7. Button 10 (Driver/W1) always maps to W1 directly.
    private static readonly ClubType[] _woods = [ClubType.W3, ClubType.W4, ClubType.W5, ClubType.W6, ClubType.W7];
    private static readonly ClubType[] _utilities = [ClubType.U3, ClubType.U4, ClubType.U5, ClubType.U6, ClubType.U7];
    private static readonly ClubType[] _irons = [ClubType.I3, ClubType.I4, ClubType.I5, ClubType.I6, ClubType.I7, ClubType.I8, ClubType.I9];
    private static readonly ClubType[] _wedges = [ClubType.PW, ClubType.GW, ClubType.SW, ClubType.LW];

    // ── Default loft angles (degrees) ────────────────────────────────────────
    // Standard golf loft values — not yet confirmed against actual SC4Pro device defaults.

    private static readonly IReadOnlyDictionary<ClubType, float> _defaultLoft = new Dictionary<ClubType, float>
    {
        [ClubType.W1] = 10.5f,
        [ClubType.W3] = 15.0f,
        [ClubType.W4] = 17.0f,
        [ClubType.W5] = 19.0f,
        [ClubType.W6] = 21.0f,
        [ClubType.W7] = 23.0f,
        [ClubType.U3] = 19.0f,
        [ClubType.U4] = 22.0f,
        [ClubType.U5] = 25.0f,
        [ClubType.U6] = 28.0f,
        [ClubType.U7] = 31.0f,
        [ClubType.I3] = 21.0f,
        [ClubType.I4] = 24.0f,
        [ClubType.I5] = 27.0f,
        [ClubType.I6] = 31.0f,
        [ClubType.I7] = 34.0f,
        [ClubType.I8] = 38.0f,
        [ClubType.I9] = 42.0f,
        [ClubType.PW] = 46.0f,
        [ClubType.GW] = 50.0f,
        [ClubType.SW] = 56.0f,
        [ClubType.LW] = 60.0f,
        [ClubType.PT] =  3.0f,
    };

    // Per-club loft overrides — populated when the user adjusts loft via the remote.
    private readonly Dictionary<ClubType, float> _loftOverrides = new();

    // ── Public properties (populated by ConnectAsync) ─────────────────────────

    /// <summary>Bluetooth device name (from the BLE advertisement).</summary>
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

    // ── Device display state (tracked from button presses since connect) ──────

    /// <summary>
    /// Currently selected club. Set from the handshake and updated on every
    /// club button press or <see cref="SetClubAsync"/> call.
    /// </summary>
    public ClubType CurrentClub { get; private set; } = ClubType.W5;

    /// <summary>
    /// Loft angle for the current club in degrees.
    /// Returns the user-adjusted value if the loft was changed via the remote,
    /// otherwise the standard default for that club.
    /// </summary>
    public float LoftAngle => _loftOverrides.TryGetValue(CurrentClub, out var v) ? v : _defaultLoft[CurrentClub];

    /// <summary>
    /// Target distance adjustment in steps since connect, tracked from button presses.
    /// Initialised to 0 (device state before connect is unknown).
    /// Actual unit/increment per step is not yet confirmed from dumps.
    /// </summary>
    public int TargetDistance { get; private set; } = 0;

    /// <summary>Fired for every unsolicited non-remote packet.</summary>
    public event Func<Sc4ProPacket, Task>? PacketReceived;

    /// <summary>
    /// Fired for every hardware remote button press, after state has been updated.
    /// </summary>
    public event Func<RemoteControlPacket, Task>? RemoteButtonPressed;

    /// <summary>
    /// Fired when all 6 shot sub-packets have been pulled from the device.
    /// Index 0 = seq 1 (ShotMetadata), …, index 5 = seq 6 (ShotSpinDetails).
    /// </summary>
    public event Func<ShotPacket[], Task>? ShotReceived;

    // ── Internals ─────────────────────────────────────────────────────────────

    private Sc4ProClient? _client;

    // ── Connect ───────────────────────────────────────────────────────────────

    /// <summary>Scans for the device, connects, reads GATT config characteristics, and runs the handshake.</summary>
    public async Task ConnectAsync()
    {
        _client = new Sc4ProClient(_ble, _logger);
        _client.PacketReceived += OnPacketReceived;
        _client.ShotReceived += async pkts =>
        {
            if (ShotReceived != null)
                await ShotReceived(pkts);
            // Re-arm the device for the next shot automatically.
            try { await _client.ShotReadyAsync(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Auto ShotReady after shot failed"); }
        };

        _logger?.LogDebug("Scanning for SC4Pro… ");
        DeviceName = await _client.ConnectAsync();
        _logger?.LogDebug($"connected: {DeviceName}");

        _logger?.LogDebug("Reading device config… ");
        ParseConfig(await _ble.ReadConfigAsync());
        _logger?.LogDebug("done");

        // Handshake: Sync → DS2 → DS1 → EqSetting (matches dump exactly).
        Console.Write("Handshake… ");
        var syncAck = await _client.SyncAsync();
        Serial = syncAck.Serial;

        await _client.SetDeviceSetting2Async(
            DS2Flags.Language | DS2Flags.Volume | DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
            volume: 3, appIndex: 1, appIndexOnOff: 1);

        const ClubType initialClub = ClubType.W5;
        await _client.SetDeviceSetting1Async(
            DS1Flags.Mode | DS1Flags.Unit | DS1Flags.Club | DS1Flags.CarryTotal,
            mode: 0, club: initialClub);
        CurrentClub = initialClub;

        await _client.SetEqAsync();

        // Activate shot-data notifications then return to idle display —
        // matches the real app's post-handshake sequence exactly.
        var ds2Full = DS2Flags.Language | DS2Flags.Volume | DS2Flags.AppIndex | DS2Flags.AppIndexOnOff;
        await _client.SetDeviceSetting2Async(ds2Full, volume: 3, appIndex: 2, appIndexOnOff: 1);
        await _client.SetDeviceSetting2Async(ds2Full, volume: 3, appIndex: 1, appIndexOnOff: 1);
        await _client.SetDeviceSetting1Async(DS1Flags.Club, club: initialClub);
        await _client.ShotReadyAsync();
        await _client.ShotReadyAsync();

        _logger?.LogDebug("done");
    }

    private Task OnPacketReceived(Sc4ProPacket pkt)
    {
        if (pkt is RemoteControlPacket remote)
        {
            HandleRemoteButton(remote);
            return RemoteButtonPressed?.Invoke(remote) ?? Task.CompletedTask;
        }

        _logger?.LogDebug("Unhandled packet {Raw} → {Cmd}", pkt.Raw, pkt.Cmd);
        return PacketReceived?.Invoke(pkt) ?? Task.CompletedTask;
    }

    private void HandleRemoteButton(RemoteControlPacket remote)
    {
        switch (remote.Button)
        {
            case 6: // LoftAngleUp
                _loftOverrides[CurrentClub] = LoftAngle + 0.5f;
                _logger?.LogDebug("Loft {Club} → {Loft:F1}°", CurrentClub, LoftAngle);
                break;

            case 7: // LoftAngleDown
                _loftOverrides[CurrentClub] = LoftAngle - 0.5f;
                _logger?.LogDebug("Loft {Club} → {Loft:F1}°", CurrentClub, LoftAngle);
                break;

            case 8: // TargetDistanceUp
                TargetDistance++;
                _logger?.LogDebug("Target distance → {Distance} steps", TargetDistance);
                break;

            case 9: // TargetDistanceDown
                TargetDistance--;
                _logger?.LogDebug("Target distance → {Distance} steps", TargetDistance);
                break;

            default:
                var club = ResolveNextClub(remote.Button);
                if (club.HasValue)
                {
                    CurrentClub = club.Value;
                    _logger?.LogDebug("Remote club button: {ButtonName} → {Club}", remote.ButtonName, CurrentClub);
                    // DS1 only — no ShotReady — device stays un-armed for further adjustments.
                    _ = Task.Run(async () =>
                    {
                        try { await Client.SetDeviceSetting1Async(DS1Flags.Club, club: CurrentClub); }
                        catch (Exception ex) { _logger?.LogError(ex, "Auto SetClub (DS1) failed for {Club}", CurrentClub); }
                    });
                }
                else
                {
                    _logger?.LogDebug("Ignoring button {ButtonName}", remote.ButtonName);
                }
                break;
        }
    }

    /// <summary>
    /// Returns the next club to select for a given remote button, cycling within
    /// the button's group if already on a club in that group.
    /// </summary>
    private ClubType? ResolveNextClub(uint button) => button switch
    {
        10 => ClubType.W1,               // Driver — always W1
        11 => CycleInGroup(_woods),      // Wood — cycles W3–W7
        12 => CycleInGroup(_utilities),  // Utility
        13 => CycleInGroup(_irons),      // Iron
        14 => CycleInGroup(_wedges),     // Wedge
        22 => ClubType.PT,               // Putter — always PT
        _ => null,
    };

    private ClubType CycleInGroup(ClubType[] group)
    {
        var idx = Array.IndexOf(group, CurrentClub);
        return idx < 0 ? group[0] : group[(idx + 1) % group.Length];
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
    public async Task<DeviceSetting1Ack> SetClubAsync(ClubType club)
    {
        var ack = await Client.SetClubAsync(club);
        CurrentClub = club;
        return ack;
    }

    /// <summary>Switches the device operating mode.</summary>
    public Task SetModeAsync(DeviceMode mode)
        => Client.SetModeAsync(mode);

    /// <summary>Arms the device for the next shot.</summary>
    public Task<ShotReadyAck> ShotReadyAsync()
        => Client.ShotReadyAsync();

    /// <summary>The underlying protocol client for advanced commands.</summary>
    public Sc4ProClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <summary>Disposes the underlying BLE channel.</summary>
    public ValueTask DisposeAsync() => _ble.DisposeAsync();
}
