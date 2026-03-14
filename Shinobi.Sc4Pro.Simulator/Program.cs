using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Shinobi.Sc4Pro.Packets;
using Shinobi.WebSockets;
using Shinobi.WebSockets.Builders;
using Shinobi.WebSockets.Extensions;
using Shinobi.WebSockets.Http;

var assembly = Assembly.GetExecutingAssembly();
var clients = new ConcurrentDictionary<Guid, ShinobiWebSocket>();

var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddConsole());
var logger = loggerFactory.CreateLogger("Simulator");

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() },
};

// ── Simulated device state ────────────────────────────────────────────────────

var state = new SimState();

void Broadcast(string json)
{
    foreach (var (_, ws) in clients)
        _ = ws.SendTextAsync(json, CancellationToken.None);
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
        clients[ws.Context.Guid] = ws;
        await ws.SendTextAsync(JsonSerializer.Serialize(state.ToStatusMsg(), jsonOptions), ct);
        await next(ws, ct);
    })
    .OnClose((ws, status, desc, next, ct) =>
    {
        clients.TryRemove(ws.Context.Guid, out _);
        return next(ws, status, desc, ct);
    })
    .OnTextMessage(async (ws, msg, ct) =>
    {
        logger.LogDebug("WS ← {Msg}", msg);
        using var doc = JsonDocument.Parse(msg);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "setClub":
                state.Club = doc.RootElement.GetProperty("club").Deserialize<ClubType>(jsonOptions);
                Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "setClub", state.Club }, jsonOptions));
                break;

            case "setMode":
                state.Mode = doc.RootElement.GetProperty("mode").Deserialize<DeviceMode>(jsonOptions);
                Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "setMode", state.Mode }, jsonOptions));
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
                Broadcast(JsonSerializer.Serialize(new { type = "remoteButton", button }, jsonOptions));
                break;
        }
        await Task.CompletedTask;
    })
    .Build();

await server.StartAsync();
logger.LogInformation("Simulator running at http://localhost:8081");
Console.ReadLine();
await server.StopAsync();

// ── State types ───────────────────────────────────────────────────────────────

class SimState
{
    public ClubType Club { get; set; } = ClubType.W5;
    public DeviceMode Mode { get; set; } = DeviceMode.Practice;
    public bool Armed { get; set; }
    public int ShotCount { get; set; }
    public ShotState? Shot { get; set; }

    public object ToStatusMsg() => new
    {
        type = "status",
        Club,
        Mode,
        Armed,
        ShotCount,
        Shot,
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
