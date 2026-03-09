namespace Sc4Pro.Packets;

public record ShotMetadata(
    uint Year, uint Month, uint Day, uint Hour, uint Min, uint Sec,
    ClubType Club, float LoftAngle, bool IsMetric) : ShotData;
