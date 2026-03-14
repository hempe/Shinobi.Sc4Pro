namespace Shinobi.Sc4Pro.Packets;

/// <summary>
/// Swing-speed mode result ack (cmd 0x73, 20-byte standard frame).
/// Sent by the device after each swing in swing-speed mode.
/// Contains a timestamp; the actual measured speed arrives in a separate packet.
/// </summary>
/// <param name="Year">Full year (e.g. 2026), stored on device as year-since-2000 in a single byte.</param>
/// <param name="Month">Month (1–12).</param>
/// <param name="Day">Day (1–31).</param>
/// <param name="Hour">Hour (0–23).</param>
/// <param name="Min">Minute (0–59).</param>
/// <param name="Sec">Second (0–59).</param>
public record SwingSpeedAck(uint Year, uint Month, uint Day, uint Hour, uint Min, uint Sec, string Raw)
    : Sc4ProPacket(0x73, Raw);
