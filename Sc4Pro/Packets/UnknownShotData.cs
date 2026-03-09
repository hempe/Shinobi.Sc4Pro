namespace Sc4Pro.Packets;

public record UnknownShotData(uint Seq, int PayloadLength) : ShotData;
