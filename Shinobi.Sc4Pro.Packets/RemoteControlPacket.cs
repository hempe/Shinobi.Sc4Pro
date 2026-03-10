namespace Shinobi.Sc4Pro.Packets;

/// <summary>
/// Hardware remote-control button press (cmd 0x78).
/// The remote has buttons for mode, unit, loft angle, target distance, and five club presets.
/// </summary>
/// <param name="Button">Raw button identifier sent by the device.</param>
/// <param name="Raw">Hex-encoded raw bytes.</param>
public record RemoteControlPacket(uint Button, string Raw) : Sc4ProPacket(0x78, Raw)
{
    /// <summary>Human-readable name for the button (e.g. "Club_W1_Driver", "Mode").</summary>
    public string ButtonName => Button switch
    {
        1 => "Mode",
        2 => "Unit",
        3 => "Language",
        6 => "LoftAngleUp",
        7 => "LoftAngleDown",
        8 => "TargetDistanceUp",
        9 => "TargetDistanceDown",
        10 => "Club_W1_Driver",
        11 => "Club_W3_3Wood",
        12 => "Club_U3_Hybrid",
        13 => "Club_I3_Iron",
        14 => "Club_PW_Wedge",
        15 => "CarryTotal",
        22 => "Club_Putter",
        _ => $"Unknown({Button})",
    };

    /// <summary>
    /// Maps a hardware remote button to a ClubType.
    /// Returns null for putter (button 22) and unknown buttons — no packet should be sent.
    /// </summary>
    public static ClubType? ButtonToClub(uint button) => button switch
    {
        10 => ClubType.W1,
        11 => ClubType.W3,
        12 => ClubType.U3,
        13 => ClubType.I3,
        14 => ClubType.PW,
        _ => null,
    };
}
