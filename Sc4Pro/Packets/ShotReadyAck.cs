namespace Sc4Pro.Packets;

public record ShotReadyAck(string Raw) : Sc4ProPacket(0x77, Raw);
