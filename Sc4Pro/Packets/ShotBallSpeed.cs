namespace Sc4Pro.Packets;

public record ShotBallSpeed(float Pressure_hPa, float Temperature_C, float BallSpeed)
    : ShotData;
