namespace Sc4Pro.Packets;

[Flags]
public enum DS2Flags : byte
{
    Language = 1 << 0,
    Volume = 1 << 1,
    VerticalOffset = 1 << 2,
    HorizontalOffset = 1 << 3,
    AppIndex = 1 << 4,
    AppIndexOnOff = 1 << 5
}
