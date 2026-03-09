namespace Sc4Pro.Packets;

public record SyncAck(string Serial, string Raw) : Sc4ProPacket(0x74, Raw);
