using System.Text.Json;
using System.Text.Json.Serialization;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() },
};

await using var device = new Sc4Pro.Logic.Sc4ProDevice();

device.PacketReceived += pkt =>
{
    Console.WriteLine(JsonSerializer.Serialize((object)pkt, jsonOptions));
    return Task.CompletedTask;
};

await device.ConnectAsync();

Console.WriteLine("\nDevice settings:");
device.LogSettings();

Console.WriteLine("\nListening for shot events — press Enter to exit.");
Console.ReadLine();
