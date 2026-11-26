using UnityEngine;

namespace BoneCheats.Core
{
    /// <summary>One shared colour list for ESP, target highlight, tracers and charms (index 0-8).</summary>
    internal static class Colors
    {
        public static readonly string[] Names = { "Red", "Green", "Blue", "Yellow", "Magenta", "Cyan", "White", "Orange", "Rainbow" };
        public static string Name(int index) { return Names[Mathf.Clamp(index, 0, Names.Length - 1)]; }

        private static readonly Color[] Palette =
        {
            Color.red, Color.green, Color.blue, Color.yellow, Color.magenta, Color.cyan, Color.white, new Color(1f, 0.5f, 0f),
        };

        public static Color Get(int index)
        {
            if (index >= Palette.Length) return Color.HSVToRGB(Mathf.Repeat(Time.unscaledTime * 0.3f, 1f), 1f, 1f);
            return Palette[Mathf.Clamp(index, 0, Palette.Length - 1)];
        }
    }
}
