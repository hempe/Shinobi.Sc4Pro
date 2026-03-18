namespace Shinobi.Sc4Pro.Packets;

/// <summary>Shot sub-packet seq=6. Detailed spin components, attack angle, and club path.</summary>
/// <param name="BackSpin">Back spin in RPM.</param>
/// <param name="SideSpin">Side spin in RPM (negative = draw, positive = fade).</param>
/// <param name="SpinAxis">Spin axis tilt in centidegrees (÷100 → degrees).</param>
/// <param name="AttackAngle">Angle of attack in centidegrees (÷100 → degrees; negative = descending blow).</param>
/// <param name="ClubPath">Club path in centidegrees (÷100 → degrees; negative = out-to-in, positive = in-to-out).</param>
public record ShotSpinDetails(
    uint BackSpin, short SideSpin, short SpinAxis,
    short AttackAngle, short ClubPath) : ShotData;
