using System.Numerics;

namespace GPConf.Utilities;

public static class ColorUtils
{
    /// <summary>Unpacks a 0x00RRGGBB uint into a Vector3 (r, g, b) in [0, 1].</summary>
    public static Vector3 ToVec3(uint packed) => new(
        ((packed >> 16) & 0xFF) / 255f,
        ((packed >>  8) & 0xFF) / 255f,
        ( packed        & 0xFF) / 255f);

    /// <summary>Unpacks a 0x00RRGGBB uint into a Vector4 (r, g, b, 1) in [0, 1].</summary>
    public static Vector4 ToVec4(uint packed) => new(ToVec3(packed), 1.0f);

    /// <summary>Returns black or white, whichever contrasts better against the given color.</summary>
    public static Vector4 ContrastingText(Vector4 c)
    {
        float luminance = 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;
        return luminance > 0.5f ? new Vector4(0, 0, 0, 1) : new Vector4(1, 1, 1, 1);
    }

    /// <summary>Returns black or white, whichever contrasts better against the packed color.</summary>
    public static Vector4 ContrastingText(uint packed) => ContrastingText(new Vector4(ToVec3(packed), 1f));

    /// <summary>Packs a Vector3 (r, g, b) in [0, 1] into a 0x00RRGGBB uint.</summary>
    public static uint Pack(Vector3 col) =>
        ((uint)(col.X * 255) << 16) |
        ((uint)(col.Y * 255) <<  8) |
         (uint)(col.Z * 255);
}