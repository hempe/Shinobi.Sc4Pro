using Shinobi.Sc4Pro.Packets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shinobi.Sc4Pro.Analyze;

/// <summary>
/// Parses a btsnoop HCI log (btsnoop_hci.log from Android developer options)
/// and extracts SC4Pro GATT packets, emitting a human-readable JSON file.
/// </summary>
static class BtSnoopParser
{
    // btsnoop timestamps are microseconds since 2000-01-01 00:00:00 UTC
    private static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task ParseAsync(string logPath, string outputPath)
    {
        var entries = new List<JsonObject>();

        foreach (var (ts, fromDevice, value) in ExtractSc4ProPackets(logPath))
        {
            var obj = new JsonObject
            {
                ["time"] = TimeZoneInfo.ConvertTime(ts, TimeZoneInfo.Local).ToString("HH:mm:ss.fff"),
                ["direction"] = fromDevice ? "device→app" : "app→device",
            };

            DecodePacket(value, fromDevice, obj);
            entries.Add(obj);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(entries, JsonOptions));
        Console.WriteLine($"Parsed {entries.Count} SC4Pro packets → {outputPath}");
    }

    // ── btsnoop + HCI + L2CAP + ATT extraction ────────────────────────────────

    private static IEnumerable<(DateTimeOffset Ts, bool FromDevice, byte[] Value)> ExtractSc4ProPackets(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        // btsnoop file header (16 bytes)
        var magic = br.ReadBytes(8); // "btsnoop\0"
        if (magic[0] != 'b' || magic[7] != 0)
            throw new InvalidDataException("Not a btsnoop file.");
        _ = ReadU32BE(br); // version (1)
        _ = ReadU32BE(br); // datalink type (1002 = HCI UART H4)

        while (fs.Position <= fs.Length - 24)
        {
            // btsnoop record header (24 bytes)
            var origLen = ReadU32BE(br);
            var inclLen = ReadU32BE(br);
            var flags = ReadU32BE(br);
            _ = ReadU32BE(br); // cumulative drops
            var tsMicros = ReadI64BE(br);

            if (inclLen > fs.Length - fs.Position) break;
            var data = br.ReadBytes((int)inclLen);
            var ts = Epoch.AddMicroseconds(tsMicros);

            // Must be HCI ACL Data (indicator byte 0x02)
            // Layout: [0]=0x02, [1..2]=ACL handle word (LE), [3..4]=total length (LE),
            //         [5..6]=L2CAP PDU length (LE), [7..8]=L2CAP CID (LE),
            //         [9]=ATT opcode, [10..11]=ATT handle (LE), [12..]=ATT value
            if (data.Length < 13 || data[0] != 0x02) continue;

            // Skip ACL continuation fragments (PB flag = 0b01)
            int pb = (data[2] >> 4) & 0x3;
            if (pb == 1) continue;

            int pduLen = data[5] | (data[6] << 8);  // L2CAP / ATT PDU length
            int cid = data[7] | (data[8] << 8);  // L2CAP Channel ID

            // Must be ATT bearer (CID 0x0004)
            if (cid != 0x0004 || pduLen < 3) continue;

            byte attOpcode = data[9];

            // ATT_WRITE_REQ (0x12), ATT_WRITE_CMD (0x52) → app→device
            // ATT_HANDLE_VALUE_NTF (0x1b)               → device→app
            if (attOpcode != 0x12 && attOpcode != 0x52 && attOpcode != 0x1b) continue;

            int valueLen = pduLen - 3; // minus opcode (1) and attribute handle (2)
            if (valueLen < 2 || data.Length < 12 + valueLen) continue;

            var value = data[12..(12 + valueLen)];

            // SC4Pro filter: must start with 0x53 and have a valid checksum
            if (value[0] != 0x53 || !HasValidChecksum(value)) continue;

            bool fromDevice = attOpcode == 0x1b;
            yield return (ts, fromDevice, value);
        }
    }

    // ── Packet decoder ────────────────────────────────────────────────────────

    private static void DecodePacket(byte[] d, bool fromDevice, JsonObject obj)
    {
        obj["hex"] = BitConverter.ToString(d).Replace("-", ":").ToLowerInvariant();

        if (d.Length < 2) { obj["type"] = "Unknown"; return; }

        switch (d[1])
        {
            case 0x74: // Sync / SyncAck
                if (fromDevice)
                {
                    obj["type"] = "SyncAck";
                    if (d.Length >= 18)
                        obj["serial"] = System.Text.Encoding.ASCII
                            .GetString(d, 4, 14).TrimEnd('\0');
                }
                else
                {
                    obj["type"] = "Sync";
                    if (d.Length >= 9)
                    {
                        int year = d[2] | (d[3] << 8);
                        obj["deviceTime"] = $"{year:0000}-{d[4]:00}-{d[5]:00} {d[6]:00}:{d[7]:00}:{d[8]:00}";
                    }
                }
                break;

            case 0x6F: // DS1
                obj["type"] = fromDevice ? "DS1Ack" : "DS1Command";
                if (!fromDevice && d.Length >= 16)
                {
                    obj["flags"] = ((DS1Flags)d[2]).ToString();
                    obj["mode"] = d[4];
                    obj["club"] = ((ClubType)d[15]).ToString();
                }
                break;

            case 0x6E: // DS2
                obj["type"] = fromDevice ? "DS2Ack" : "DS2Command";
                if (!fromDevice && d.Length >= 15)
                {
                    obj["flags"] = ((DS2Flags)d[2]).ToString();
                    obj["volume"] = d[4];
                    obj["appIndex"] = d[13];
                    obj["appIndexOnOff"] = d[14];
                }
                break;

            case 0x76:
                obj["type"] = fromDevice ? "EqSettingAck" : "EqSetting";
                break;

            case 0x77:
                obj["type"] = fromDevice ? "ShotReadyAck" : "ShotReady";
                break;

            case 0x73: // Shot data (all packets exactly 20 bytes)
                if (d.Length == 20)
                {
                    var index = (uint)BitConverter.ToUInt16(d, 2);
                    var seq   = d[4];
                    var p     = d[5..^2]; // 13-byte payload
                    obj["type"]  = fromDevice ? "ShotData" : "ShotDataRequest";
                    obj["index"] = index;
                    obj["seq"]   = seq;
                    if (fromDevice)
                        DecodeShotSeq(seq, p, obj);
                }
                else
                    obj["type"] = $"Unknown(0x73,len={d.Length})";
                break;

            case 0x78: // RemoteControl
                obj["type"] = "RemoteControl";
                if (d.Length >= 6)
                {
                    uint btn = BitConverter.ToUInt32(d, 2);
                    obj["button"] = btn;
                    obj["buttonName"] = new RemoteControlPacket(btn, "").ButtonName;
                }
                break;

            default:
                obj["type"] = $"Unknown(0x{d[1]:x2})";
                break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DecodeShotSeq(byte seq, byte[] p, JsonObject obj)
    {
        switch (seq)
        {
            case 1:
                obj["shotTime"]   = $"{p[0] + 2000:0000}-{p[1]:00}-{p[2]:00} {p[3]:00}:{p[4]:00}:{p[5]:00}";
                obj["unknown1"]   = p[6];
                obj["club"]       = ((ClubType)p[7]).ToString();
                obj["loftAngle"]  = BitConverter.ToSingle(p, 8);
                obj["isMetric"]   = p[12] != 0;
                break;
            case 2:
                obj["pressure_hPa"] = BitConverter.ToSingle(p, 0);
                obj["temp_C"]       = BitConverter.ToSingle(p, 4);
                obj["ballSpeed"]    = BitConverter.ToSingle(p, 8);
                break;
            case 3:
                obj["clubSpeed"]    = BitConverter.ToSingle(p, 0);
                obj["launchAngle"]  = BitConverter.ToSingle(p, 4);
                obj["carry"]        = BitConverter.ToSingle(p, 8);
                break;
            case 4:
                obj["totalDist"]    = BitConverter.ToSingle(p, 0);
                obj["apex"]         = BitConverter.ToSingle(p, 4);
                obj["totalSpin"]    = BitConverter.ToSingle(p, 8);
                break;
            case 5:
                obj["launchDir"]    = BitConverter.ToSingle(p, 0);
                obj["tilt"]         = BitConverter.ToSingle(p, 4);
                obj["tailHex"]      = BitConverter.ToString(p, 8).Replace("-", ":").ToLowerInvariant();
                break;
            case 6:
                obj["backSpin"]          = (uint)BitConverter.ToUInt16(p, 0);
                obj["sideSpin"]          = BitConverter.ToInt16(p, 2);
                obj["spinAxis_deg"]      = BitConverter.ToInt16(p, 4) / 100.0;
                obj["attackAngle_deg"]   = BitConverter.ToInt16(p, 6) / 100.0;
                obj["clubPath_deg"]      = BitConverter.ToInt16(p, 8) / 100.0;
                obj["tailHex"]           = BitConverter.ToString(p, 10).Replace("-", ":").ToLowerInvariant();
                break;
        }
    }

    private static bool HasValidChecksum(byte[] d)
    {
        int sum = 0;
        for (int i = 0; i < d.Length - 1; i++) sum += d[i];
        return (byte)((-sum) & 0xFF) == d[^1];
    }

    private static uint ReadU32BE(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static long ReadI64BE(BinaryReader br)
    {
        var b = br.ReadBytes(8);
        return (long)(
            ((ulong)b[0] << 56) | ((ulong)b[1] << 48) |
            ((ulong)b[2] << 40) | ((ulong)b[3] << 32) |
            ((ulong)b[4] << 24) | ((ulong)b[5] << 16) |
            ((ulong)b[6] << 8) | b[7]);
    }
}
