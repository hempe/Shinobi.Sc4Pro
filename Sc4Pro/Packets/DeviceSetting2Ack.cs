namespace Sc4Pro.Packets;

public record DeviceSetting2Ack(string Raw) : Sc4ProPacket(0x6E, Raw);
