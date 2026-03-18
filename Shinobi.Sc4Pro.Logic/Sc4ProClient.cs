using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<Sc4ProPacket>> _pending = new();
    private volatile TaskCompletionSource<ShotPacket>? _pendingShotSeq;

    public Sc4ProClient(IBleChannel ble, ILogger? logger = null)
    {
        _ble = ble;
        _logger = logger;
    }

    /// <summary>Fired for every unsolicited packet (remote-control button presses, unknowns).</summary>
    public event Func<Sc4ProPacket, Task>? PacketReceived;

    /// <summary>
    /// Fired when all 6 shot sub-packets have been pulled from the device.
    /// Index 0 = seq 1 (ShotMetadata), index 1 = seq 2 (ShotBallSpeed), …, index 5 = seq 6 (ShotSpinDetails).
    /// </summary>
    public event Func<ShotPacket[], Task>? ShotReceived;

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

    /// <summary>Switches the device operating mode.</summary>
    /// <remarks>
    /// Practice   = DS2(appIndex=1) + DS1(mode=0)                           — device shows practice screen<br/>
    /// SwingSpeed = DS2(appIndex=1) + DS1(mode=2) + ShotReady                — device shows "SWG"<br/>
    /// Sim        = DS1(mode=0) + DS2(appIndex=2)                            — device shows "SIM"<br/>
    /// (confirmed from Analysis.cs click sequence; "Click 2 back to home" = SwingSpeed, "Click 3 driving range" = Sim)
    /// </remarks>
    public async Task SetModeAsync(DeviceMode mode)
    {
        switch (mode)
        {
            case DeviceMode.Practice:
                await SetDeviceSetting2Async(
                    DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                    appIndex: 1, appIndexOnOff: 1);
                await SetDeviceSetting1Async(DS1Flags.Mode, mode: 0);
                break;

            case DeviceMode.SwingSpeed:
                await SetDeviceSetting2Async(
                    DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                    appIndex: 1, appIndexOnOff: 1);
                await SetDeviceSetting1Async(DS1Flags.Mode, mode: 2);
                await ShotReadyAsync();
                break;

            case DeviceMode.Sim:
                await SetDeviceSetting1Async(DS1Flags.Mode, mode: 0);
                await SetDeviceSetting2Async(
                    DS2Flags.AppIndex | DS2Flags.AppIndexOnOff,
                    appIndex: 2, appIndexOnOff: 1);
                break;
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
        _logger?.LogDebug("BLE tx cmd=0x{Cmd:x2} {Hex}", cmd, Hex(packet));
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

    private static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", " ").ToLowerInvariant();

    // ── Incoming packet router ─────────────────────────────────────────────────

    private Task OnReceived(byte[] data)
    {
        var pkt = PacketParser.Parse(data);
        _logger?.LogDebug("BLE rx cmd=0x{Cmd:x2} type={Type} {Hex}", pkt.Cmd, pkt.GetType().Name, Hex(data));

        // Shot packets are handled via the pull handshake, not the normal ack path.
        if (pkt is ShotPacket sp)
        {
            if (sp.Seq == 1)
                _ = PullShotDataAsync(sp).ContinueWith(
                    t => _logger?.LogError(t.Exception, "PullShotData failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
            else
                _pendingShotSeq?.TrySetResult(sp);
            return Task.CompletedTask;
        }

        // Route to a waiting caller if we sent that cmd, otherwise raise the event.
        if (_pending.TryRemove(pkt.Cmd, out var tcs))
            tcs.TrySetResult(pkt);
        else
        {
            _logger?.LogDebug("BLE rx unsolicited cmd=0x{Cmd:x2} {Hex}", pkt.Cmd, Hex(data));
            PacketReceived?.Invoke(pkt);
        }

        return Task.CompletedTask;
    }

    private async Task PullShotDataAsync(ShotPacket seq1)
    {
        var packets = new ShotPacket[6];
        packets[0] = seq1;
        var index = (ushort)seq1.Index;
        _logger?.LogDebug("Shot {Index} pull start", index);

        try
        {
            for (byte seq = 1; seq <= 6; seq++)
            {
                // Set up the listener for the next response before sending the ack,
                // so we can't miss a fast reply.
                TaskCompletionSource<ShotPacket>? nextTcs = null;
                if (seq < 6)
                {
                    nextTcs = new TaskCompletionSource<ShotPacket>();
                    _pendingShotSeq = nextTcs;
                }

                _logger?.LogDebug("Shot {Index} ack seq={Seq}", index, seq);
                await _ble.SendAsync(PacketBuilder.ShotDataRequest(index, seq));

                if (nextTcs != null)
                    packets[seq] = await nextTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            _pendingShotSeq = null;
        }

        _logger?.LogDebug("Shot {Index} complete", index);
        if (ShotReceived != null)
            await ShotReceived(packets);
    }

    /// <summary>Disposes the underlying BLE channel.</summary>
    public ValueTask DisposeAsync() => _ble.DisposeAsync();
}
