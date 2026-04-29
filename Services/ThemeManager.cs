using System;
using System.Windows.Media;

namespace Zexus.Services
{
    public enum ThemeMode
    {
        Dark,
        Light
    }

    /// <summary>
    /// Central theme management. Holds Dark/Light color palettes as static properties
    /// that ChatWindow.xaml.cs reads in place of the old static readonly Color fields.
    /// Call SetTheme() to swap all values and raise ThemeChanged so the UI can re-apply.
    /// </summary>
    public static class ThemeManager
    {
        // ─── Current Mode ───
        public static ThemeMode Current { get; private set; } = ThemeMode.Dark;

        // ─── Event raised after palette swap ───
        public static event Action ThemeChanged;

        // ─── Color Properties (mutable — swapped by SetTheme) ───
        public static Color ColBg { get; private set; }
        public static Color ColSurface { get; private set; }
        public static Color ColCard { get; private set; }
        public static Color ColBorder { get; private set; }
        public static Color ColPrimary { get; private set; }
        public static Color ColPrimaryLt { get; private set; }
        public static Color ColAccent { get; private set; }
        public static Color ColSuccess { get; private set; }
        public static Color ColWarning { get; private set; }
        public static Color ColError { get; private set; }
        public static Color ColText { get; private set; }
        public static Color ColTextSec { get; private set; }
        public static Color ColMuted { get; private set; }
        public static Color ColGlass { get; private set; }
        public static Color ColGlassBorder { get; private set; }
        public static Color ColCodeBg { get; private set; }

        static ThemeManager()
        {
            // Initialize from persisted preference (default: Dark)
            var saved = ConfigManager.Config.Theme;
            var mode = string.Equals(saved, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeMode.Light
                : ThemeMode.Dark;
            ApplyPalette(mode);
        }

        /// <summary>
        /// Switch theme, persist, and notify listeners.
        /// </summary>
        public static void SetTheme(ThemeMode mode)
        {
            if (mode == Current) return;
            ApplyPalette(mode);
            ConfigManager.SetTheme(mode.ToString());
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// Toggle between Dark ↔ Light.
        /// </summary>
        public static void Toggle()
        {
            SetTheme(Current == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);
        }

        private static void ApplyPalette(ThemeMode mode)
        {
            Current = mode;
            if (mode == ThemeMode.Dark)
                ApplyDark();
            else
                ApplyLight();
        }

        // ─── Dark Palette (original Glassmorphism) ───
        private static void ApplyDark()
        {
            ColBg         = Color.FromRgb(0x08, 0x09, 0x0D);           // #08090D  cool black
            ColSurface    = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);    // 4% white
            ColCard       = Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF);    // 5.5% white
            ColBorder     = Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF);    // 8% white
            ColPrimary    = Color.FromRgb(0x42, 0x85, 0xF4);           // #4285F4
            ColPrimaryLt  = Color.FromRgb(0x5A, 0x95, 0xF5);           // #5A95F5
            ColAccent     = Color.FromRgb(0x7B, 0x61, 0xFF);           // #7B61FF
            ColSuccess    = Color.FromRgb(0x10, 0xB9, 0x81);           // #10B981
            ColWarning    = Color.FromRgb(0xF5, 0x9E, 0x0B);           // #F59E0B
            ColError      = Color.FromRgb(0xEA, 0x43, 0x35);           // #EA4335
            ColText       = Color.FromRgb(0xE8, 0xEA, 0xED);           // #E8EAED
            ColTextSec    = Color.FromRgb(0x9A, 0xA0, 0xA6);           // #9AA0A6
            ColMuted      = Color.FromRgb(0x5F, 0x63, 0x68);           // #5F6368
            ColGlass      = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);    // 4% white
            ColGlassBorder = Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF);   // 8% white
            ColCodeBg     = Color.FromRgb(0x06, 0x07, 0x0A);           // #06070A
        }

        // ─── Light Palette (daylight-friendly) ───
        private static void ApplyLight()
        {
            ColBg         = Color.FromRgb(0xF5, 0xF6, 0xFA);           // #F5F6FA  cool gray bg
            ColSurface    = Color.FromRgb(0xFF, 0xFF, 0xFF);           // #FFFFFF  white
            ColCard       = Color.FromRgb(0xF0, 0xF1, 0xF5);           // #F0F1F5  subtle card
            ColBorder     = Color.FromRgb(0xD8, 0xDA, 0xE0);           // #D8DAE0  light border
            ColPrimary    = Color.FromRgb(0x1A, 0x73, 0xE8);           // #1A73E8  Google blue
            ColPrimaryLt  = Color.FromRgb(0x42, 0x85, 0xF4);           // #4285F4  hover
            ColAccent     = Color.FromRgb(0x6C, 0x47, 0xFF);           // #6C47FF  purple
            ColSuccess    = Color.FromRgb(0x0D, 0x9A, 0x6D);           // #0D9A6D  green
            ColWarning    = Color.FromRgb(0xD9, 0x7B, 0x06);           // #D97B06  amber
            ColError      = Color.FromRgb(0xD9, 0x33, 0x25);           // #D93325  red
            ColText       = Color.FromRgb(0x1F, 0x29, 0x37);           // #1F2937  dark text
            ColTextSec    = Color.FromRgb(0x6B, 0x72, 0x80);           // #6B7280  secondary
            ColMuted      = Color.FromRgb(0x9C, 0xA3, 0xAF);           // #9CA3AF  muted
            ColGlass      = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);    // 50% white
            ColGlassBorder = Color.FromRgb(0xD8, 0xDA, 0xE0);         // same as border
            ColCodeBg     = Color.FromRgb(0xF3, 0xF4, 0xF6);           // #F3F4F6  light code bg
        }
    }
}
