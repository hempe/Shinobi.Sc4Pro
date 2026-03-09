namespace Sc4Pro.Packets;

public record ShotPacket(uint Index, uint Seq, ShotData Data, string Raw)
    : Sc4ProPacket(0x73, Raw);
