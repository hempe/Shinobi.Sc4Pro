namespace Sc4Pro.Packets;

public record ShotDistanceApex(float TotalDistance, float Apex, float TotalSpin)
    : ShotData;
