namespace Sc4Pro.Packets;

public record ShotDirection(
    float LaunchDirection, float Tilt,
    int CalAngleC, int CalAngleD, uint Equalizer) : ShotData;
