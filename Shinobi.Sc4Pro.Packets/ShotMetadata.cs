namespace Shinobi.Sc4Pro.Packets;

/// <summary>Shot sub-packet seq=1. Timestamp, club selection, and loft angle at the moment of the shot.</summary>
/// <param name="Year">Shot timestamp — year.</param>
/// <param name="Month">Shot timestamp — month (1–12).</param>
/// <param name="Day">Shot timestamp — day (1–31).</param>
/// <param name="Hour">Shot timestamp — hour (0–23).</param>
/// <param name="Min">Shot timestamp — minute (0–59).</param>
/// <param name="Sec">Shot timestamp — second (0–59).</param>
/// <param name="Unknown1">Four bytes at p[24–27], purpose not yet identified.</param>
/// <param name="Club">Club selected at the time of the shot.</param>
/// <param name="LoftAngle">Loft angle in degrees.</param>
/// <param name="IsMetric">True when the device is configured for metric units.</param>
public record ShotMetadata(
    uint Year,
    uint Month,
    uint Day,
    uint Hour,
    uint Min,
    uint Sec,
    uint Unknown1,
    ClubType Club,
    float LoftAngle,
     bool IsMetric) : ShotData;
