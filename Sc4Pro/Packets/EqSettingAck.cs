namespace Sc4Pro.Packets;

public record EqSettingAck(string Raw) : Sc4ProPacket(0x76, Raw);
