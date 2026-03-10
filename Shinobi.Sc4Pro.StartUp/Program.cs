using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Shinobi.Sc4Pro.Bluetooth;
using Shinobi.Sc4Pro.Logic;
using Shinobi.Sc4Pro.Packets;
using Shinobi.WebSockets;
using Shinobi.WebSockets.Builders;
using Shinobi.WebSockets.Extensions;
using Shinobi.WebSockets.Http;

var assembly = Assembly.GetExecutingAssembly();
var clients = new ConcurrentDictionary<Guid, ShinobiWebSocket>();
var loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddConsole());

var logger = loggerFactory.CreateLogger("StartUp");

Sc4ProDevice? device = null;
string currentState = "idle";

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() },
};

void Broadcast(string json)
{
    foreach (var (_, ws) in clients)
        _ = ws.SendTextAsync(json, CancellationToken.None);
}

string CurrentStateJson()
{
    if (currentState == "connected" && device != null)
        return JsonSerializer.Serialize(new
        {
            type = "status",
            state = "connected",
            device = new
            {
                name = device.DeviceName,
                serial = device.Serial,
                firmware = device.FirmwareRevision,
                hardware = device.HardwareRevision,
                battery = device.BatteryLevel,
            }
        }, jsonOptions);

    return JsonSerializer.Serialize(new { type = "status", state = currentState }, jsonOptions);
}

var server = WebSocketServerBuilder.Create()
    .UsePort(8080)
    .OnHandshake(async (context, next, ct) =>
    {
        if (!context.IsWebSocketRequest && context.Path == "/")
            return context.HttpRequest.CreateEmbeddedResourceResponse(
                assembly, "Shinobi.Sc4Pro.StartUp.Client.html");
        return await next(context, ct);
    })
    .OnConnect(async (ws, next, ct) =>
    {
        clients[ws.Context.Guid] = ws;
        await ws.SendTextAsync(CurrentStateJson(), ct);
        await next(ws, ct);
    })
    .OnClose((ws, status, desc, next, ct) =>
    {
        clients.TryRemove(ws.Context.Guid, out _);
        return next(ws, status, desc, ct);
    })
    .OnTextMessage(async (ws, msg, ct) =>
    {
        if (device == null) return;
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var type = doc.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "setClub":
                    var clubStr = doc.RootElement.GetProperty("club").GetString()!;
                    var club = Enum.Parse<ClubType>(clubStr);
                    await device.SetClubAsync(club);
                    Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "setClub", club = clubStr }, jsonOptions));
                    break;

                case "setMode":
                    var swingSpeed = doc.RootElement.GetProperty("swingSpeed").GetBoolean();
                    await device.SetModeAsync(swingSpeed);
                    Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "setMode", swingSpeed }, jsonOptions));
                    break;

                case "shotReady":
                    await device.ShotReadyAsync();
                    Broadcast(JsonSerializer.Serialize(new { type = "ack", command = "shotReady" }, jsonOptions));
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Command error: {Message}", ex.Message);
        }
    })
    .Build();

await server.StartAsync();
logger.LogDebug("Open http://localhost:8080 in your browser.");

_ = Task.Run(async () =>
{
    try
    {
        currentState = "scanning";
        Broadcast(JsonSerializer.Serialize(new { type = "status", state = "scanning" }, jsonOptions));

        device = new Sc4ProDevice(new BleChannel(), loggerFactory.CreateLogger<Sc4ProDevice>());

        device.PacketReceived += pkt =>
        {
            Broadcast(JsonSerializer.Serialize(new { type = "packet", data = pkt }, jsonOptions));
            return Task.CompletedTask;
        };

        device.RemoteButtonPressed += remote =>
        {
            var club = Shinobi.Sc4Pro.Packets.RemoteControlPacket.ButtonToClub(remote.Button);
            Broadcast(JsonSerializer.Serialize(new
            {
                type = "remoteButton",
                button = remote.ButtonName,
                club = club?.ToString(),
            }, jsonOptions));
            return Task.CompletedTask;
        };

        await device.ConnectAsync();

        currentState = "connected";
        Broadcast(CurrentStateJson());
    }
    catch (Exception ex)
    {
        currentState = "error";
        Broadcast(JsonSerializer.Serialize(new { type = "status", state = "error", message = ex.Message }, jsonOptions));
        logger.LogError(ex, "BLE error: {Message}", ex.Message);
    }
});

Console.ReadLine();
await server.StopAsync();
if (device != null)
    await device.DisposeAsync();
