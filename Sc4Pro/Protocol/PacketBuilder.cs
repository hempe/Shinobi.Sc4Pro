using Sc4Pro.Packets;

namespace Sc4Pro.Protocol;

public static class PacketBuilder
{
    // All SC4Pro command packets: 20 bytes
    // Layout: [0x53][cmd][16 content bytes][0x45][checksum]

    public static byte[] Sync(DateTime? time = null)
    {
        var now = time ?? DateTime.Now;
        var content = new byte[16];
        BitConverter.GetBytes((ushort)now.Year).CopyTo(content, 0); // [2-3]
        content[2] = (byte)now.Month;   // [4]
        content[3] = (byte)now.Day;     // [5]
        content[4] = (byte)now.Hour;    // [6]
        content[5] = (byte)now.Minute;  // [7]
        content[6] = (byte)now.Second;  // [8]
        return Build(0x74, content);
    }

    public static byte[] ShotReady() => Build(0x77, new byte[16]);

    public static byte[] EqSetting() => Build(0x76, new byte[16]);

    /// <summary>
    /// Build a DeviceSetting1 packet.
    /// Confirmed field positions: setFlag=[2], mode=[4], club=[15].
    /// </summary>
    public static byte[] DeviceSetting1(DS1Flags flags, byte mode = 0, ClubType club = ClubType.W1)
    {
        var content = new byte[16];
        content[0] = (byte)flags;  // [2]  setFlag
        content[2] = mode;         // [4]  mode (0=normal, 2=swing_speed)
        content[13] = (byte)club;   // [15] club
        return Build(0x6F, content);
    }

    /// <summary>
    /// Build a DeviceSetting2 packet.
    /// Confirmed field positions: setFlag=[2], volume=[4], appIndex=[13], appIndexOnOff=[14].
    /// </summary>
    public static byte[] DeviceSetting2(
        DS2Flags flags, byte volume = 0, byte appIndex = 0, byte appIndexOnOff = 0)
    {
        var content = new byte[16];
        content[0] = (byte)flags;    // [2]  setFlag
        content[2] = volume;         // [4]  volume
        content[11] = appIndex;       // [13] appIndex
        content[12] = appIndexOnOff;  // [14] appIndexOnOff
        return Build(0x6E, content);
    }

    private static byte[] Build(byte cmd, byte[] content)
    {
        var pkt = new byte[20];
        pkt[0] = 0x53;
        pkt[1] = cmd;
        content.CopyTo(pkt, 2);
        pkt[18] = 0x45;
        pkt[19] = Checksum(pkt);
        return pkt;
    }

    private static byte Checksum(byte[] data)
    {
        int sum = 0;
        for (int i = 0; i < data.Length - 1; i++) sum += data[i];
        return (byte)((-sum) & 0xFF);
    }
}
