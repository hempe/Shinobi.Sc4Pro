namespace Shinobi.Sc4Pro.Protocol;

/// <summary>
/// Builds device→app ack packets for the SC4Pro protocol.
/// All acks are exactly 20 bytes: <c>[0x53][cmd][0x01][15 zero bytes][0x45][checksum]</c>.
/// Sync ack embeds the device serial number in content[2..15].
/// </summary>
public static class AckBuilder
{
    /// <summary>Remote-control button packet (cmd 0x78): button number as uint32 LE in content[0..3].</summary>
    public static byte[] RemoteButton(uint button)
    {
        var content = new byte[16];
        BitConverter.GetBytes(button).CopyTo(content, 0);
        return Build(0x78, content);
    }

    /// <summary>Generic ack: cmd echoed back, content[0]=0x01, rest zeros.</summary>
    public static byte[] Ack(byte cmd)
    {
        var content = new byte[16];
        content[0] = 0x01;
        return Build(cmd, content);
    }

    /// <summary>Sync ack with serial number in content[2..15] (up to 14 ASCII bytes, null-padded).</summary>
    public static byte[] SyncAck(string serial)
    {
        var content = new byte[16];
        content[0] = 0x01;
        var bytes = System.Text.Encoding.ASCII.GetBytes(serial);
        Array.Copy(bytes, 0, content, 2, Math.Min(bytes.Length, 14));
        return Build(0x74, content);
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
