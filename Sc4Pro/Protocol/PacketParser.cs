using Sc4Pro.Packets;

namespace Sc4Pro.Protocol;

public static class PacketParser
{
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
        if (d.Length < 18) return new UnknownPacket(0x73, raw);

        uint index = BitConverter.ToUInt32(d, 2);
        uint seq = BitConverter.ToUInt32(d, 6);
        byte[] p = d[10..^2]; // payload between seq and [0x45][cs]

        ShotData data = seq switch
        {
            1 when p.Length >= 40 => new ShotMetadata(
                BitConverter.ToUInt32(p, 0), BitConverter.ToUInt32(p, 4),
                BitConverter.ToUInt32(p, 8), BitConverter.ToUInt32(p, 12),
                BitConverter.ToUInt32(p, 16), BitConverter.ToUInt32(p, 20),
                (ClubType)BitConverter.ToUInt32(p, 28),
                BitConverter.ToSingle(p, 32),
                BitConverter.ToUInt32(p, 36) == 0),
            2 when p.Length >= 12 => new ShotBallSpeed(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                BitConverter.ToSingle(p, 8)),
            3 when p.Length >= 12 => new ShotClubCarry(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                BitConverter.ToSingle(p, 8)),
            4 when p.Length >= 12 => new ShotDistanceApex(
                BitConverter.ToSingle(p, 0),
                BitConverter.ToSingle(p, 4),
                BitConverter.ToSingle(p, 8)),
            5 when p.Length >= 20 => new ShotDirection(
                BitConverter.ToSingle(p, 0), BitConverter.ToSingle(p, 4),
                BitConverter.ToInt32(p, 8), BitConverter.ToInt32(p, 12),
                BitConverter.ToUInt32(p, 16)),
            6 when p.Length >= 12 => new ShotSpinDetails(
                BitConverter.ToUInt32(p, 0), BitConverter.ToInt16(p, 4),
                BitConverter.ToInt16(p, 6), BitConverter.ToInt16(p, 8),
                BitConverter.ToInt16(p, 10)),
            _ => new UnknownShotData(seq, p.Length),
        };

        return new ShotPacket(index, seq, data, raw);
    }
}
