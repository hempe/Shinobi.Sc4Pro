using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Shinobi.Sc4Pro.Packets;
using Shinobi.Sc4Pro.Protocol;
using Shinobi.WebSockets;
using Shinobi.WebSockets.Builders;
using Shinobi.WebSockets.Extensions;
using Shinobi.WebSockets.Http;

var assembly = Assembly.GetExecutingAssembly();
var clients = new ConcurrentDictionary<Guid, ShinobiWebSocket>();
ShinobiWebSocket? bleWs = null;

var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddConsole());
var logger = loggerFactory.CreateLogger("Simulator");

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() },
};

var state = new SimState();

void Broadcast(string json)
{
    foreach (var (_, ws) in clients)
        _ = ws.SendTextAsync(json, CancellationToken.None);
}

// ── BLE path handler ──────────────────────────────────────────────────────────

async Task HandleBleAsync(ShinobiWebSocket ws, string msg, CancellationToken ct)
{
    using var doc = JsonDocument.Parse(msg);
    if (doc.RootElement.GetProperty("type").GetString() != "tx") return;

    var d = Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!);
    if (d.Length < 2) return;

    byte cmd = d[1];
    byte[] ack;

    switch (cmd)
    {
        case 0x74: // Sync — respond with fake serial
            ack = AckBuilder.SyncAck("SC4ProSim-01");
            break;

        case 0x6F: // DS1 — parse club and mode
            if ((d[2] & (byte)DS1Flags.Club) != 0)
                state.Club = (ClubType)d[15];
            state.LastMode = d[4];
            state.UpdateDeviceMode();
            Broadcast(JsonSerializer.Serialize(state.ToStatusMsg(), jsonOptions));
            ack = AckBuilder.Ack(cmd);
            break;

        case 0x6E: // DS2 — parse appIndex
            state.LastAppIndex = d[13];
            state.UpdateDeviceMode();
            Broadcast(JsonSerializer.Serialize(state.ToStatusMsg(), jsonOptions));
            ack = AckBuilder.Ack(cmd);
            break;

        case 0x77: // ShotReady — arm device
            state.Armed = true;
            Broadcast(JsonSerializer.Serialize(state.ToStatusMsg(), jsonOptions));
            ack = AckBuilder.Ack(cmd);
            break;

        default:
            ack = AckBuilder.Ack(cmd);
            break;
    }

    var response = JsonSerializer.Serialize(new { type = "rx", data = Convert.ToBase64String(ack) });
    await ws.SendTextAsync(response, ct);
}

// ── UI path handler ───────────────────────────────────────────────────────────

async Task HandleUiAsync(ShinobiWebSocket ws, string msg, CancellationToken ct)
{
    logger.LogDebug("UI ← {Msg}", msg);
    using var doc = JsonDocument.Parse(msg);
    var type = doc.RootElement.GetProperty("type").GetString();

    switch (type)
    {
        case "setClub":
            state.Club = doc.RootElement.GetProperty("club").Deserialize<ClubType>(jsonOptions);
            Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "setClub", club = state.Club, loftAngle = state.LoftAngle }, jsonOptions));
            break;

        case "setMode":
            state.Mode = doc.RootElement.GetProperty("mode").Deserialize<DeviceMode>(jsonOptions);
            Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "setMode", mode = state.Mode }, jsonOptions));
            break;

        case "shotReady":
            state.Armed = true;
            Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "shotReady" }, jsonOptions));
            break;

        case "injectShot":
            var shot = doc.RootElement.Deserialize<ShotState>(jsonOptions);
            if (shot != null)
            {
                state.Shot = shot;
                state.ShotCount++;
                state.Armed = false;
                Broadcast(JsonSerializer.Serialize(new { type = "shot", state.ShotCount, shot }, jsonOptions));
            }
            break;

        case "remoteButton":
            var button = doc.RootElement.GetProperty("button").GetUInt32();
            state.HandleRemoteButton(button);
            Broadcast(JsonSerializer.Serialize(new
            {
                type = "remoteButton",
                button,
                club = state.Club,
                loftAngle = state.LoftAngle,
                targetDistance = state.TargetDistance,
            }, jsonOptions));
            // Forward as a RemoteControlPacket to the BLE client (StartUp)
            if (bleWs != null)
            {
                var pkt = AckBuilder.RemoteButton(button);
                var rx = JsonSerializer.Serialize(new { type = "rx", data = Convert.ToBase64String(pkt) });
                _ = bleWs.SendTextAsync(rx, CancellationToken.None);
            }
            break;
    }
    await Task.CompletedTask;
}

// ── Server ────────────────────────────────────────────────────────────────────

var server = WebSocketServerBuilder.Create()
    .UsePort(8081)
    .OnHandshake(async (ctx, next, ct) =>
    {
        if (!ctx.IsWebSocketRequest && ctx.Path == "/")
            return ctx.HttpRequest.CreateEmbeddedResourceResponse(
                assembly, "Shinobi.Sc4Pro.Simulator.Simulator.html");
        return await next(ctx, ct);
    })
    .OnConnect(async (ws, next, ct) =>
    {
        if (ws.Context.Path == "/ble")
            bleWs = ws;
        else
        {
            clients[ws.Context.Guid] = ws;
            await ws.SendTextAsync(JsonSerializer.Serialize(state.ToStatusMsg(), jsonOptions), ct);
        }
        await next(ws, ct);
    })
    .OnClose((ws, status, desc, next, ct) =>
    {
        if (ws == bleWs) bleWs = null;
        clients.TryRemove(ws.Context.Guid, out _);
        return next(ws, status, desc, ct);
    })
    .OnTextMessage(async (ws, msg, ct) =>
    {
        if (ws == bleWs)
            await HandleBleAsync(ws, msg, ct);
        else
            await HandleUiAsync(ws, msg, ct);
    })
    .Build();

await server.StartAsync();
logger.LogInformation("Simulator running at http://localhost:8081");
Console.ReadLine();
await server.StopAsync();

// ── State ─────────────────────────────────────────────────────────────────────

class SimState
{
    private static readonly Dictionary<ClubType, float> _defaultLoft = new()
    {
        [ClubType.W1]=10.5f,[ClubType.W3]=15f,[ClubType.W4]=17f,[ClubType.W5]=19f,
        [ClubType.W6]=21f,[ClubType.W7]=23f,[ClubType.U3]=19f,[ClubType.U4]=22f,
        [ClubType.U5]=25f,[ClubType.U6]=28f,[ClubType.U7]=31f,[ClubType.I3]=21f,
        [ClubType.I4]=24f,[ClubType.I5]=27f,[ClubType.I6]=31f,[ClubType.I7]=34f,
        [ClubType.I8]=38f,[ClubType.I9]=42f,[ClubType.PW]=46f,[ClubType.GW]=50f,
        [ClubType.SW]=56f,[ClubType.LW]=60f,[ClubType.PT]=3f,
    };

    private static readonly ClubType[] _woods     = [ClubType.W3, ClubType.W4, ClubType.W5, ClubType.W6, ClubType.W7];
    private static readonly ClubType[] _utilities = [ClubType.U3, ClubType.U4, ClubType.U5, ClubType.U6, ClubType.U7];
    private static readonly ClubType[] _irons     = [ClubType.I3, ClubType.I4, ClubType.I5, ClubType.I6, ClubType.I7, ClubType.I8, ClubType.I9];
    private static readonly ClubType[] _wedges    = [ClubType.PW, ClubType.GW, ClubType.SW, ClubType.LW];

    public ClubType Club { get; set; } = ClubType.W5;
    public DeviceMode Mode { get; set; } = DeviceMode.Practice;
    public bool Armed { get; set; }
    public int ShotCount { get; set; }
    public ShotState? Shot { get; set; }
    public int TargetDistance { get; set; }

    // Raw values from BLE commands, used to derive Mode
    public byte LastMode { get; set; } = 0;
    public byte LastAppIndex { get; set; } = 1;

    private readonly Dictionary<ClubType, float> _loftOverrides = new();

    public float LoftAngle => _loftOverrides.TryGetValue(Club, out var v) ? v
        : _defaultLoft.TryGetValue(Club, out var d) ? d : 0f;

    public void UpdateDeviceMode()
    {
        Mode = LastAppIndex == 2 ? DeviceMode.Sim
            : LastMode == 2     ? DeviceMode.SwingSpeed
            :                     DeviceMode.Practice;
    }

    public void HandleRemoteButton(uint button)
    {
        switch (button)
        {
            case 6:  _loftOverrides[Club] = LoftAngle + 0.5f; break;
            case 7:  _loftOverrides[Club] = LoftAngle - 0.5f; break;
            case 8:  TargetDistance++; break;
            case 9:  TargetDistance--; break;
            case 10: Club = ClubType.W1; break;
            case 11: Club = Cycle(_woods); break;
            case 12: Club = Cycle(_utilities); break;
            case 13: Club = Cycle(_irons); break;
            case 14: Club = Cycle(_wedges); break;
            case 22: Club = ClubType.PT; break;
        }
    }

    private ClubType Cycle(ClubType[] group)
    {
        var idx = Array.IndexOf(group, Club);
        return idx < 0 ? group[0] : group[(idx + 1) % group.Length];
    }

    public object ToStatusMsg() => new
    {
        type = "status",
        club = Club,
        loftAngle = LoftAngle,
        mode = Mode,
        armed = Armed,
        shotCount = ShotCount,
        shot = Shot,
    };
}

record ShotState(
    float Carry,
    float TotalDistance,
    float BallSpeed,
    float ClubSpeed,
    float LaunchAngle,
    float LaunchDirection,
    float Apex,
    float SmashFactor,
    float TotalSpin,
    string Unit = "y"
);
