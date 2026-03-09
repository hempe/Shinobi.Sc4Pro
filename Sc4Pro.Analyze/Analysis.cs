using Sc4Pro.Bluetooth;
using Sc4Pro.Logic;
using Sc4Pro.Packets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sc4Pro.Analysis;

/// <summary>
/// Replays the exact GATT sequence captured from the original SC4Pro app
/// (gatt_clicks.txt / gatt_dump.json) and saves the result to dump_test.json
/// for comparison with compare_dumps.py.
/// </summary>
static class Analyzer
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task RunAsync()
    {
        var bleChannel = new LinuxBleChannel();
        await using var sc4pro = new Sc4ProClient(bleChannel);

        sc4pro.PacketReceived += pkt =>
        {
            Console.WriteLine($"  EVENT ← {JsonSerializer.Serialize((object)pkt, JsonOptions)}");
            return Task.CompletedTask;
        };

        var DS2Full = DS2Flags.Language | DS2Flags.Volume | DS2Flags.AppIndex | DS2Flags.AppIndexOnOff;

        Console.WriteLine("Scanning for SC4Pro…");
        var name = await sc4pro.ConnectAsync();
        Console.WriteLine($"Connected: {name}");
        await Task.Delay(5_000);

        // ── Handshake ─────────────────────────────────────────────────────────
        // Note: the original app also read ASCII config from handles 0x0021,
        // 0x0023, 0x0025 just before Sync. Use Sc4ProDevice.ConnectAsync() for
        // that behaviour.

        Console.WriteLine("\n[Handshake]");

        await Step("Sync", async () =>
        {
            var ack = await sc4pro.SyncAsync();
            Console.Write($"(serial={ack.Serial}) ");
        }, delayMs: 3000);

        await Step("DS2 – vol=3, appIndex=1 (home/idle)",
            () => sc4pro.SetDeviceSetting2Async(DS2Full, volume: 3, appIndex: 1, appIndexOnOff: 1),
            delayMs: 3000);

        await Step("DS1 – mode=normal, club=W5, all flags",
            () => sc4pro.SetDeviceSetting1Async(
                DS1Flags.Mode | DS1Flags.Unit | DS1Flags.Club | DS1Flags.CarryTotal,
                mode: 0, club: ClubType.W5),
            delayMs: 3000);

        await Step("EqSetting", () => sc4pro.SetEqAsync(), delayMs: 3000);

        // ── Click 1: Swing Speed Test ─────────────────────────────────────────
        Console.WriteLine("\n[Click 1 – Swing Speed Test]");
        await Step("DS2 – appIndex=2 (active game)",
            () => sc4pro.SetDeviceSetting2Async(DS2Full, volume: 3, appIndex: 2, appIndexOnOff: 1));

        // ── Click 2: Back to Home ─────────────────────────────────────────────
        Console.WriteLine("\n[Click 2 – Back to Home]");
        await Step("DS2 – appIndex=1 (home/idle)",
            () => sc4pro.SetDeviceSetting2Async(DS2Full, volume: 3, appIndex: 1, appIndexOnOff: 1),
            delayMs: 3000);
        // App latches the previous mode (swing_speed) when returning home.
        await Step("DS1 – mode=2 (swing_speed), Mode flag only",
            () => sc4pro.SetDeviceSetting1Async(DS1Flags.Mode, mode: 2),
            delayMs: 3000);
        await Step("ShotReady", () => sc4pro.ShotReadyAsync());

        // ── Click 3: Driving Range ────────────────────────────────────────────
        Console.WriteLine("\n[Click 3 – Driving Range]");
        await Step("DS1 – mode=0 (normal), Mode flag only",
            () => sc4pro.SetDeviceSetting1Async(DS1Flags.Mode, mode: 0),
            delayMs: 3000);
        await Step("DS2 – appIndex=2 (active game)",
            () => sc4pro.SetDeviceSetting2Async(DS2Full, volume: 3, appIndex: 2, appIndexOnOff: 1),
            delayMs: 3000);

        // App then sent DS2(appIndex=1)×2 + DS1(club=W5) + ShotReady×2 —
        // automatic default-club init when entering the club-selection screen.
        await Step("DS2 – appIndex=1 (stabilise, 1/2)",
            () => sc4pro.SetDeviceSetting2Async(DS2Full, volume: 3, appIndex: 1, appIndexOnOff: 1),
            delayMs: 2000);
        await Step("DS2 – appIndex=1 (stabilise, 2/2)",
            () => sc4pro.SetDeviceSetting2Async(DS2Full, volume: 3, appIndex: 1, appIndexOnOff: 1),
            delayMs: 2000);
        await Step("DS1 – club=W5, Club flag (default club)",
            () => sc4pro.SetDeviceSetting1Async(DS1Flags.Club, club: ClubType.W5),
            delayMs: 2000);
        await Step("ShotReady (1/2)", () => sc4pro.ShotReadyAsync(), delayMs: 2000);
        await Step("ShotReady (2/2)", () => sc4pro.ShotReadyAsync());

        // ── Clicks 4–25: Club selections ──────────────────────────────────────
        // Each click = DS1(Club flag) + ShotReady, matching dump exactly.
        // Note: gatt_clicks.txt entry 19 says "6iron" but dump shows I7.
        Console.WriteLine("\n[Clicks 4–25 – Club selections]");

        (ClubType Club, string Label)[] clubs =
        [
            (ClubType.W1, "Click 4  – Driver        (W1)"),
            (ClubType.W3, "Click 5  – 3Wood         (W3)"),
            (ClubType.W4, "Click 6  – 4Wood         (W4)"),
            (ClubType.W5, "Click 7  – 5Wood         (W5)"),
            (ClubType.W6, "Click 8  – 6Wood         (W6)"),
            (ClubType.W7, "Click 9  – 7Wood         (W7)"),
            (ClubType.U3, "Click 10 – 3Hybrid       (U3)"),
            (ClubType.U4, "Click 11 – 4Hybrid       (U4)"),
            (ClubType.U5, "Click 12 – 5Hybrid       (U5)"),
            (ClubType.U6, "Click 13 – 6Hybrid       (U6)"),
            (ClubType.U7, "Click 14 – 7Hybrid       (U7)"),
            (ClubType.I3, "Click 15 – 3Iron         (I3)"),
            (ClubType.I4, "Click 16 – 4Iron         (I4)"),
            (ClubType.I5, "Click 17 – 5Iron         (I5)"),
            (ClubType.I6, "Click 18 – 6Iron         (I6)"),
            (ClubType.I7, "Click 19 – 7Iron         (I7)"),  // click list typo: was "6iron"
            (ClubType.I8, "Click 20 – 8Iron         (I8)"),
            (ClubType.I9, "Click 21 – 9Iron         (I9)"),
            (ClubType.PW, "Click 22 – PitchingWedge (PW)"),
            (ClubType.GW, "Click 23 – GapWedge      (GW)"),
            (ClubType.SW, "Click 24 – SandWedge     (SW)"),
            (ClubType.LW, "Click 25 – LobWedge      (LW)"),
        ];

        foreach (var (club, label) in clubs)
            await Step(label, () => sc4pro.SetClubAsync(club));

        // ── Clicks 26–29: Target distances ────────────────────────────────────
        // Device just receives DS1(club=LW)+ShotReady each time.
        // Target distance is app-side only — not encoded in any GATT packet.
        Console.WriteLine("\n[Clicks 26–29 – Target distances (re-arm with LW)]");
        string[] targets = ["Click 26 – Target 55 m", "Click 27 – Target 60 m",
                            "Click 28 – Target 65 m", "Click 29 – Target 70 m"];
        foreach (var label in targets)
            await Step(label, () => sc4pro.SetClubAsync(ClubType.LW));

        Console.WriteLine("\nAll clicks replicated — observing for 10 s then exit.");
        await Task.Delay(10_000);

        await bleChannel.SaveDumpAsync("dump_test.json");
        Console.WriteLine("Saved dump_test.json");
    }

    static async Task Step(string label, Func<Task> action, int delayMs = 5000)
    {
        Console.Write($"  → {label} ... ");
        await action();
        Console.WriteLine("ack");
        await Task.Delay(delayMs);
    }
}
