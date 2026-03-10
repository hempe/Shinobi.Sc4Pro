using System.Collections.Concurrent;
using Shinobi.Sc4Pro.Bluetooth;
using Shinobi.Sc4Pro.Packets;
using Shinobi.Sc4Pro.Protocol;

namespace Shinobi.Sc4Pro.Logic;

/// <summary>
/// High-level SC4Pro client. Wraps BLE communication with typed async commands
/// and raises events for unsolicited device packets (shots, button presses).
/// </summary>
public sealed class Sc4ProClient : IAsyncDisposable
{
    private const string ServiceUuid = "50340001-2065-6964-6461-63676e697773";
    private const string TxUuid = "50340002-2065-6964-6461-63676e697773"; // we write here
    private const string RxUuid = "50340003-2065-6964-6461-63676e697773"; // device notifies here

    private readonly IBleChannel _ble;
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<Sc4ProPacket>> _pending = new();

    public Sc4ProClient(IBleChannel ble) => _ble = ble;

    /// <summary>Fired for every unsolicited packet (shots, remote-control button presses).</summary>
    public event Func<Sc4ProPacket, Task>? PacketReceived;

    // ── Connection ─────────────────────────────────────────────────────────────

    /// <summary>Scans, connects, and subscribes for notifications. Returns the device name.</summary>
    public async Task<string> ConnectAsync()
    {
        var name = await _ble.ConnectAsync(ServiceUuid, TxUuid, RxUuid);
        _ble.Received += OnReceived;
        return name;
    }

    // ── Handshake ──────────────────────────────────────────────────────────────

    /// <summary>Full connection handshake: Sync → DS2 → DS1 → EqSetting.</summary>
    public async Task HandshakeAsync(ClubType initialClub = ClubType.W1)
    {
        await SyncAsync();
        await SetDeviceSetting2Async(
            DS2Flags.Language | DS2Flags.Volume | DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
            volume: 5, appIndex: 2, appIndexOnOff: 1);
        await SetDeviceSetting1Async(
            DS1Flags.Mode | DS1Flags.Unit | DS1Flags.Club | DS1Flags.CarryTotal,
            mode: 0, club: initialClub);
        await SetEqAsync();
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    /// <summary>Sends the current datetime and returns the device serial from the ack.</summary>
    public Task<SyncAck> SyncAsync() =>
        SendAndWaitAsync<SyncAck>(PacketBuilder.Sync());

    /// <summary>Arms the device for the next shot.</summary>
    public Task<ShotReadyAck> ShotReadyAsync() =>
        SendAndWaitAsync<ShotReadyAck>(PacketBuilder.ShotReady());

    /// <summary>Selects a club and re-arms the device. Awaits both the DS1 and ShotReady acks.</summary>
    public async Task<DeviceSetting1Ack> SetClubAsync(ClubType club)
    {
        var ack = await SetDeviceSetting1Async(DS1Flags.Club, club: club);
        await ShotReadyAsync();
        return ack;
    }

    /// <summary>Switches between normal (false) and swing-speed (true) mode.</summary>
    /// <remarks>
    /// Swing speed = DS2(appIndex=2) only — no DS1 needed.
    /// Normal mode = DS2(appIndex=1) + DS1(mode=0).
    /// </remarks>
    public async Task SetModeAsync(bool swingSpeed)
    {
#warning tSetModeAsync has to little options see code and commented code below.

        if (!swingSpeed)
        {
            // This sets it to practice mode
            await SetDeviceSetting2Async(
                DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                appIndex: 1,
                appIndexOnOff: 1);

            await SetDeviceSetting1Async(DS1Flags.Mode, mode: 0);

            /*
            // This sets it to SIM (Simulator mode?)
            await SetDeviceSetting2Async(
                DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                appIndex: 2,
                appIndexOnOff: 1);

            await SetDeviceSetting1Async(DS1Flags.Mode, mode: 0);
            */
        }
        else
        {
            // This sets it to swing mode:
            await SetDeviceSetting2Async(
            DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                appIndex: 0,
                appIndexOnOff: 1);

            await SetDeviceSetting1Async(DS1Flags.Mode, mode: 2);


            /*
            // This sets it to target mode.
            await SetDeviceSetting2Async(
            DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                appIndex: 0,
                appIndexOnOff: 1);

            await SetDeviceSetting1Async(DS1Flags.Mode, mode: 1);
            */
        }
    }

    /// <summary>Sends a DeviceSetting1 command with the given flags and field values, and awaits the ack.</summary>
    public Task<DeviceSetting1Ack> SetDeviceSetting1Async(
        DS1Flags flags, byte mode = 0, ClubType club = ClubType.W1) =>
        SendAndWaitAsync<DeviceSetting1Ack>(PacketBuilder.DeviceSetting1(flags, mode, club));

    /// <summary>Sends a DeviceSetting2 command with the given flags and field values, and awaits the ack.</summary>
    public Task<DeviceSetting2Ack> SetDeviceSetting2Async(
        DS2Flags flags, byte volume = 0, byte appIndex = 0, byte appIndexOnOff = 0) =>
        SendAndWaitAsync<DeviceSetting2Ack>(PacketBuilder.DeviceSetting2(flags, volume, appIndex, appIndexOnOff));

    /// <summary>Sends an EqSetting command and awaits the ack.</summary>
    public Task<EqSettingAck> SetEqAsync() =>
        SendAndWaitAsync<EqSettingAck>(PacketBuilder.EqSetting());

    // ── Core: send and await the matching ack ──────────────────────────────────

    private async Task<T> SendAndWaitAsync<T>(byte[] packet, TimeSpan? timeout = null)
        where T : Sc4ProPacket
    {
        byte cmd = packet[1];
        var tcs = new TaskCompletionSource<Sc4ProPacket>();
        _pending[cmd] = tcs;
        try
        {
            await _ble.SendAsync(packet);
            return (T)await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
        }
        finally
        {
            _pending.TryRemove(cmd, out _);
        }
    }

    // ── Incoming packet router ─────────────────────────────────────────────────

    private Task OnReceived(byte[] data)
    {
        var pkt = PacketParser.Parse(data);

        // Route to a waiting caller if we sent that cmd, otherwise raise the event
        if (_pending.TryRemove(pkt.Cmd, out var tcs))
            tcs.TrySetResult(pkt);
        else
            PacketReceived?.Invoke(pkt);

        return Task.CompletedTask;
    }

    /// <summary>Disposes the underlying BLE channel.</summary>
    public ValueTask DisposeAsync() => _ble.DisposeAsync();
}
