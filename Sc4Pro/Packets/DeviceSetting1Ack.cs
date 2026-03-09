namespace Sc4Pro.Packets;

public record DeviceSetting1Ack(string Raw) : Sc4ProPacket(0x6F, Raw);
