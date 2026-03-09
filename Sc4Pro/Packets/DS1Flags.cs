namespace Sc4Pro.Packets;

[Flags]
public enum DS1Flags : byte
{
    Mode = 1 << 0,
    Unit = 1 << 1,
    TeeHeight = 1 << 2,
    DistanceToBall = 1 << 3,
    TargetDistance = 1 << 4,
    Club = 1 << 5,
    LoftAngle = 1 << 6,
    CarryTotal = 1 << 7
}
