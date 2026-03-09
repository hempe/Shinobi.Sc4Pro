namespace Sc4Pro.Packets;

public record ShotSpinDetails(
    uint BackSpin, short SideSpin, short SpinAxis,
    short AttackAngle, short ClubPath) : ShotData;
