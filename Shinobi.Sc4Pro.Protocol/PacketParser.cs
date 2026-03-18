using Shinobi.Sc4Pro.Packets;

namespace Shinobi.Sc4Pro.Protocol;

/// <summary>Parses raw BLE notification bytes into typed <see cref="Sc4ProPacket"/> instances.</summary>
public static class PacketParser
{
    /// <summary>
    /// Parses a raw BLE notification into the appropriate typed packet.
    /// Returns <see cref="UnknownPacket"/> for unrecognized command bytes.
    /// </summary>
    public static Sc4ProPacket Parse(byte[] d)
    {
        var raw = BitConverter.ToString(d).Replace("-", ":");
        if (d.Length < 2) return new UnknownPacket(0, raw);

        return d[1] switch
        {
            0x74 => ParseSync(d, raw),
            0x6F => new DeviceSetting1Ack(raw),
            0x6E => new DeviceSetting2Ack(raw),
            0x76 => new EqSettingAck(raw),
            0x77 => new ShotReadyAck(raw),
            0x73 => ParseShot(d, raw),
            0x78 => ParseRemoteControl(d, raw),
            var cmd => new UnknownPacket(cmd, raw),
        };
    }

    private static Sc4ProPacket ParseSync(byte[] d, string raw)
    {
        if (d.Length < 18) return new UnknownPacket(0x74, raw);
        var serial = System.Text.Encoding.ASCII
            .GetString(d, 4, Math.Min(14, d.Length - 4))
            .TrimEnd('\0');
        return new SyncAck(serial, raw);
    }

    private static Sc4ProPacket ParseRemoteControl(byte[] d, string raw)
    {
        if (d.Length < 6) return new UnknownPacket(0x78, raw);
        uint button = BitConverter.ToUInt32(d, 2);
        return new RemoteControlPacket(button, raw);
    }

    private static Sc4ProPacket ParseShot(byte[] d, string raw)
    {
        // All shot packets from real hardware are exactly 20 bytes:
        // [0x53][0x73][index:2LE][seq:1][payload:13][0x45][cs]
        if (d.Length != 20) return new UnknownPacket(0x73, raw);

        var index = (uint)BitConverter.ToUInt16(d, 2);
        var seq   = (uint)d[4];
        var p     = d[5..^2]; // 13-byte payload

        ShotData data = seq switch
        {
            1 => new ShotMetadata(
                Year:      (uint)(p[0] + 2000),
                Month:     p[1],
                Day:       p[2],
                Hour:      p[3],
                Min:       p[4],
                Sec:       p[5],
                Unknown1:  p[6],
                Club:      (ClubType)p[7],
                LoftAngle: BitConverter.ToSingle(p, 8),
                IsMetric:  p[12] != 0),
            2 => new ShotBallSpeed(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                BitConverter.ToSingle(p, 8)),
            3 => new ShotClubCarry(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                BitConverter.ToSingle(p, 8)),
            4 => new ShotDistanceApex(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                BitConverter.ToSingle(p, 8)),
            5 => new ShotDirection(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                0, 0, 0) { Tail = p[8..] },
            6 => new ShotSpinDetails(
                (uint)BitConverter.ToUInt16(p, 0),
                BitConverter.ToInt16(p, 2),
                BitConverter.ToInt16(p, 4),
                BitConverter.ToInt16(p, 6),
                BitConverter.ToInt16(p, 8)) { Tail = p[10..] },
            _ => new UnknownShotData(seq, p),
        };

        return new ShotPacket(index, seq, data, raw);
    }
}
