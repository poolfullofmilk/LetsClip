namespace LetsClip.Data.Enums;

[Flags]
public enum HotkeyModifiers
{
    // The Same Bits RegisterHotKey Takes
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}
