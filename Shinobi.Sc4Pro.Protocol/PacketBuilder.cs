using Shinobi.Sc4Pro.Packets;

namespace Shinobi.Sc4Pro.Protocol;

/// <summary>
/// Builds outgoing command packets for the SC4Pro protocol.
/// All packets are exactly 20 bytes: <c>[0x53][cmd][16 content bytes][0x45][checksum]</c>.
/// Checksum = <c>(-sum of bytes 0–18) &amp; 0xFF</c>.
/// </summary>
public static class PacketBuilder
{
    /// <summary>
    /// Builds a Sync packet encoding the given time (defaults to <see cref="DateTime.Now"/>).
    /// The device responds with a <see cref="Shinobi.Sc4Pro.Packets.SyncAck"/> containing its serial number.
    /// </summary>
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

    /// <summary>Builds a ShotReady packet that arms the device for the next shot.</summary>
    public static byte[] ShotReady() => Build(0x77, new byte[16]);

    /// <summary>
    /// Builds a shot data request packet (app→device ack for one shot sub-packet).
    /// After the device notifies seq 1, the app sends this for seq 1..6 in turn
    /// to pull each data sub-packet from the device.
    /// </summary>
    public static byte[] ShotDataRequest(ushort shotIndex, byte seq)
    {
        var pkt = new byte[20];
        pkt[0] = 0x53;
        pkt[1] = 0x73;
        BitConverter.GetBytes(shotIndex).CopyTo(pkt, 2);
        pkt[4] = seq;
        // bytes 5..17 = zero payload
        pkt[18] = 0x45;
        pkt[19] = Checksum(pkt);
        return pkt;
    }

    /// <summary>Builds an EqSetting packet (all-zero content; sent as part of the connection handshake).</summary>
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
