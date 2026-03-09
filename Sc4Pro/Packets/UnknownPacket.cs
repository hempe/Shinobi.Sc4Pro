namespace Sc4Pro.Packets;

public record UnknownPacket(byte Cmd, string Raw) : Sc4ProPacket(Cmd, Raw);
