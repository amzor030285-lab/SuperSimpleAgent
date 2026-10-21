using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace SuperSimpleAgentGUI
{
    /// <summary>
    /// Color configuration for a single color setting.
    /// </summary>
    public class ColorConfig
    {
        public ColorConfig(string key, string resourceKey, string displayName,
            Color defaultSystemColor)
        {
            Key = key;
            ResourceKey = resourceKey;
            DisplayName = displayName;
            DefaultSystemColor = defaultSystemColor;
        }

        public string Key { get; private set; }
        public string ResourceKey { get; private set; }

        /// <summary>Label shown in the Colors dialog.</summary>
        public string DisplayName { get; private set; }

        public Color DefaultSystemColor { get; private set; }
    }

    /// <summary>
    /// Color parsing utility for the GUI project.
    /// Handles parsing of color strings from configuration files.
    /// </summary>
    public static class ColorHelper
    {
        /// <summary>
        /// Centralized color configuration - defines all color settings in one place.
        /// </summary>
        public static readonly Dictionary<string, ColorConfig> ColorConfigs = new Dictionary<string, ColorConfig>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "buttontextcolor",
                new ColorConfig("buttontextcolor", "ButtonTextColorBrush", "Button Text",
                    SystemColors.ControlTextColor)
            },
            {
                "buttonbackgroundcolor",
                new ColorConfig("buttonbackgroundcolor", "ButtonBackgroundBrush", "Button BG",
                    SystemColors.ControlColor)
            },
            {
                "buttonbordercolor",
                new ColorConfig("buttonbordercolor", "ButtonBorderBrush", "Button Border",
                    SystemColors.ControlDarkColor)
            },
            {
                "chatbackgroundcolor",
                new ColorConfig("chatbackgroundcolor", "ChatBackgroundColorBrush", "Chat BG",
                    SystemColors.WindowColor)
            },
            {
                "chattextcolor",
                new ColorConfig("chattextcolor", "ChatTextColorBrush", "Chat Text",
                    SystemColors.WindowTextColor)
            },
            {
                "codeblockbackgroundcolor",
                new ColorConfig("codeblockbackgroundcolor", "CodeBlockBackgroundColorBrush", "Code Block BG",
                    SystemColors.ControlColor)
            },
            {
                "codeblockheadercolor",
                new ColorConfig("codeblockheadercolor", "CodeBlockHeaderBrush", "Code Block Header",
                    SystemColors.ControlColor)
            },
            {
                "labeltextcolor",
                new ColorConfig("labeltextcolor", "LabelTextColorBrush", "Label Text",
                    SystemColors.ControlTextColor)
            },
            {
                "linkcolor",
                new ColorConfig("linkcolor", "LinkBrush", "Link",
                    SystemColors.HotTrackColor)
            },
            {
                "rolelabelcolor",
                new ColorConfig("rolelabelcolor", "RoleLabelBrush", "Role Label",
                    SystemColors.ControlTextColor)
            },
            {
                "tablebordercolor",
                new ColorConfig("tablebordercolor", "TableBorderBrush", "Table Border",
                    SystemColors.ControlDarkColor)
            },
            {
                "tableheaderbackgroundcolor",
                new ColorConfig("tableheaderbackgroundcolor", "TableHeaderBackgroundBrush", "Table Header BG",
                    SystemColors.ControlColor)
            },
            {
                "userbubblebackgroundcolor",
                new ColorConfig("userbubblebackgroundcolor", "UserBubbleBackgroundBrush", "User Bubble BG",
                    SystemColors.ControlColor)
            },
            {
                "windowbackgroundcolor",
                new ColorConfig("windowbackgroundcolor", "WindowBackgroundColorBrush", "Window BG",
                    SystemColors.ControlColor)
            }
        };

        private static readonly Dictionary<string, ColorConfig> ByResourceKey = BuildByResourceKey();

        private static Dictionary<string, ColorConfig> BuildByResourceKey()
        {
            var map = new Dictionary<string, ColorConfig>(StringComparer.Ordinal);
            foreach (var config in ColorConfigs.Values)
                map[config.ResourceKey] = config;
            return map;
        }

        /// <summary>
        /// The ColorConfig driving a brush resource key, or null when unknown.
        /// </summary>
        public static ColorConfig GetConfig(string resourceKey)
        {
            ColorConfig config;
            return ByResourceKey.TryGetValue(resourceKey, out config) ? config : null;
        }

        /// <summary>
        /// Attempts to parse a color string (hex format) into a WPF Color.
        /// Supports both RGB (#RRGGBB) and ARGB (#AARRGGBB) formats.
        /// </summary>
        /// <param name="colorString">Color string in hex format (with or without # prefix)</param>
        /// <param name="color">The parsed color, or WindowTextColor if parsing fails</param>
        /// <returns>True if parsing succeeded, false otherwise</returns>
        public static bool TryParseColor(string colorString, out Color color)
        {
            color = SystemColors.WindowTextColor;

            if (string.IsNullOrWhiteSpace(colorString))
                return false;

            try
            {
                string hex = colorString.TrimStart('#');
                if (hex.Length == 6)
                    hex = "FF" + hex;
                else if (hex.Length != 8)
                    return false;
                color = (Color)ColorConverter.ConvertFromString("#" + hex);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public const string ThemeKey = "theme";
        public const string ThemeSystem = "system";
        public const string ThemeLight = "light";
        public const string ThemeDark = "dark";
        public const string ThemeClaudeCode = "claudecode";
        public const string ThemeGithubDark = "githubdark";
        public const string ThemeGithubDarkDimmed = "githubdarkdimmed";
        public const string ThemeDracula = "dracula";
        public const string ThemeCatppuccinMocha = "catppuccinmocha";
        public const string ThemeOneDark = "onedark";
        public const string ThemeSolarizedDark = "solarizeddark";
        public const string ThemeGithubLight = "githublight";

        /// <summary>
        /// Display order of themes (used by the Options combo and the quick toggle).
        /// </summary>
        public static readonly string[] ThemeOrder =
        {
            ThemeSystem, ThemeLight, ThemeDark, ThemeClaudeCode, ThemeGithubDark,
            ThemeGithubDarkDimmed, ThemeDracula, ThemeCatppuccinMocha, ThemeOneDark,
            ThemeSolarizedDark, ThemeGithubLight
        };

        /// <summary>
        /// Built-in theme palettes: theme name -> brush resource key -> color.
        /// </summary>
        public static readonly Dictionary<string, Dictionary<string, Color>> ThemePalettes =
            new Dictionary<string, Dictionary<string, Color>>(StringComparer.OrdinalIgnoreCase)
        {
            {
                ThemeLight,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0xE1, 0xE1, 0xE1) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0xAD, 0xAD, 0xAD) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0xEA, 0xEF, 0xF5) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x09, 0x69, 0xDA) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x5F, 0x5F, 0x5F) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0xAD, 0xAD, 0xAD) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA) }
                }
            },
            {
                ThemeDark,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x26, 0x26, 0x26) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x38, 0x38, 0x38) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x58, 0xA6, 0xFF) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0xB4, 0xB4, 0xB4) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A) }
                }
            },
            {
                ThemeClaudeCode,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x1E, 0x1A, 0x17) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x1E, 0x1A, 0x17) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x26, 0x22, 0x1D) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0xB1, 0xAD, 0xA1) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x2A, 0x26, 0x20) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x3A, 0x35, 0x2E) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x2A, 0x26, 0x20) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x3A, 0x35, 0x2E) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0xD9, 0x77, 0x57) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0xB1, 0xAD, 0xA1) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x3A, 0x35, 0x2E) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x3A, 0x35, 0x2E) }
                }
            },
            {
                ThemeGithubDark,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x0D, 0x11, 0x17) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x16, 0x1B, 0x22) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xE6, 0xED, 0xF3) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x21, 0x26, 0x2D) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xE6, 0xED, 0xF3) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x21, 0x26, 0x2D) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x30, 0x36, 0x3D) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x21, 0x26, 0x2D) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x1C, 0x21, 0x28) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x58, 0xA6, 0xFF) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x30, 0x36, 0x3D) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x1C, 0x21, 0x28) }
                }
            },
            {
                ThemeGithubDarkDimmed,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x22, 0x27, 0x2E) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x2D, 0x33, 0x3B) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xAD, 0xBA, 0xC7) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x3D, 0x43, 0x4C) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xAD, 0xBA, 0xC7) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0x76, 0x83, 0x90) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x2D, 0x33, 0x3B) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x3D, 0x43, 0x4C) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x2D, 0x33, 0x3B) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x3D, 0x43, 0x4C) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x7E, 0x8B, 0x99) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x76, 0x83, 0x90) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x3D, 0x43, 0x4C) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x3D, 0x43, 0x4C) }
                }
            },
            {
                ThemeDracula,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x28, 0x2A, 0x36) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x28, 0x2A, 0x36) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF2) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x21, 0x22, 0x2C) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF2) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF2) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x34, 0x37, 0x46) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x44, 0x47, 0x5A) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x34, 0x37, 0x46) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x44, 0x47, 0x5A) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x8B, 0xE9, 0xFD) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x62, 0x72, 0xA4) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x44, 0x47, 0x5A) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x44, 0x47, 0x5A) }
                }
            },
            {
                ThemeCatppuccinMocha,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x18, 0x18, 0x25) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xCD, 0xD6, 0xF4) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x31, 0x32, 0x44) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xCD, 0xD6, 0xF4) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0xA6, 0xAD, 0xC8) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x45, 0x47, 0x5A) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x58, 0x5B, 0x70) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x45, 0x47, 0x5A) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x58, 0x5B, 0x70) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x89, 0xB4, 0xFA) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0xA6, 0xAD, 0xC8) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x58, 0x5B, 0x70) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x58, 0x5B, 0x70) }
                }
            },
            {
                ThemeOneDark,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x28, 0x2C, 0x34) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x28, 0x2C, 0x34) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0xAB, 0xB2, 0xBF) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x21, 0x25, 0x2B) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0xAB, 0xB2, 0xBF) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0x9D, 0xA3, 0xB0) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x21, 0x25, 0x2B) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x3E, 0x44, 0x51) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x21, 0x25, 0x2B) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x3E, 0x44, 0x51) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x61, 0xAF, 0xEF) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x9D, 0xA3, 0xB0) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x3E, 0x44, 0x51) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x3E, 0x44, 0x51) }
                }
            },
            {
                ThemeSolarizedDark,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0x00, 0x2B, 0x36) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0x07, 0x36, 0x42) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0x93, 0xA1, 0xA1) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0x00, 0x2B, 0x36) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0x93, 0xA1, 0xA1) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0x93, 0xA1, 0xA1) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0x58, 0x6E, 0x75) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0x65, 0x7B, 0x83) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0x58, 0x6E, 0x75) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0x58, 0x6E, 0x75) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x26, 0x8B, 0xD2) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x93, 0xA1, 0xA1) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0x65, 0x7B, 0x83) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0x58, 0x6E, 0x75) }
                }
            },
            {
                ThemeGithubLight,
                new Dictionary<string, Color>
                {
                    { "WindowBackgroundColorBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
                    { "ChatBackgroundColorBrush", Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA) },
                    { "ChatTextColorBrush", Color.FromArgb(0xFF, 0x1F, 0x23, 0x28) },
                    { "CodeBlockBackgroundColorBrush", Color.FromArgb(0xFF, 0xEA, 0xEF, 0xF2) },
                    { "ButtonTextColorBrush", Color.FromArgb(0xFF, 0x1F, 0x23, 0x28) },
                    { "LabelTextColorBrush", Color.FromArgb(0xFF, 0x65, 0x6D, 0x76) },
                    { "ButtonBackgroundBrush", Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA) },
                    { "ButtonBorderBrush", Color.FromArgb(0xFF, 0xD0, 0xD6, 0xDD) },
                    { "UserBubbleBackgroundBrush", Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA) },
                    { "CodeBlockHeaderBrush", Color.FromArgb(0xFF, 0xEA, 0xEF, 0xF5) },
                    { "LinkBrush", Color.FromArgb(0xFF, 0x09, 0x69, 0xDA) },
                    { "RoleLabelBrush", Color.FromArgb(0xFF, 0x65, 0x6D, 0x76) },
                    { "TableBorderBrush", Color.FromArgb(0xFF, 0xD0, 0xD6, 0xDD) },
                    { "TableHeaderBackgroundBrush", Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA) }
                }
            }
        };

        /// <summary>
        /// Human-readable name for a theme key.
        /// </summary>
        public static string GetThemeDisplayName(string theme)
        {
            switch (theme)
            {
                case ThemeLight: return "Light";
                case ThemeDark: return "Dark";
                case ThemeClaudeCode: return "Claude Code";
                case ThemeGithubDark: return "GitHub Dark";
                case ThemeGithubDarkDimmed: return "GitHub Dark Dimmed";
                case ThemeDracula: return "Dracula";
                case ThemeCatppuccinMocha: return "Catppuccin Mocha";
                case ThemeOneDark: return "One Dark";
                case ThemeSolarizedDark: return "Solarized Dark";
                case ThemeGithubLight: return "GitHub Light";
                default: return "System";
            }
        }

        /// <summary>
        /// True for dark-family themes (used to pick the toggle icon).
        /// </summary>
        public static bool IsDarkTheme(string theme)
        {
            return GetPalette(theme) != null
                && theme != ThemeLight
                && theme != ThemeGithubLight;
        }

        /// <summary>
        /// Resolves the theme name stored in a colors settings dictionary.
        /// Unknown/missing values fall back to "system".
        /// </summary>
        public static string GetThemeName(Dictionary<string, string> colorSettings)
        {
            string value = null;
            if (colorSettings != null &&
                colorSettings.TryGetValue(ThemeKey, out value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                value = value.Trim().ToLowerInvariant();
                if (ThemePalettes.ContainsKey(value))
                    return value;
            }
            return ThemeSystem;
        }

        /// <summary>
        /// Palette of a theme key, or null for "system"/unknown themes.
        /// </summary>
        public static Dictionary<string, Color> GetPalette(string themeName)
        {
            Dictionary<string, Color> palette;
            if (!string.IsNullOrEmpty(themeName) && ThemePalettes.TryGetValue(themeName, out palette))
                return palette;
            return null;
        }

        /// <summary>
        /// The color a resource key actually shows for a theme plus custom colors:
        /// custom color first, then the theme palette, then the system default.
        /// </summary>
        public static Color ResolveColor(string resourceKey, string themeName, Dictionary<string, string> customColors)
        {
            ColorConfig config;
            bool known = ByResourceKey.TryGetValue(resourceKey, out config);

            if (known && customColors != null)
            {
                string customValue;
                if (customColors.TryGetValue(config.Key, out customValue))
                {
                    Color customColor;
                    if (TryParseColor(customValue, out customColor))
                        return customColor;
                }
            }

            Dictionary<string, Color> palette = GetPalette(themeName);
            if (palette != null)
            {
                Color paletteColor;
                if (palette.TryGetValue(resourceKey, out paletteColor))
                    return paletteColor;
            }

            return known ? config.DefaultSystemColor : SystemColors.ControlColor;
        }

        /// <summary>
        /// Every brush resource key the application themes: the configured
        /// settings plus anything a palette defines, so a palette entry can
        /// never be silently skipped because it has no ColorConfig.
        /// </summary>
        public static List<string> GetThemedResourceKeys(string themeName)
        {
            Dictionary<string, Color> palette = GetPalette(themeName);

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var config in ColorConfigs.Values)
            {
                if (seen.Add(config.ResourceKey))
                    keys.Add(config.ResourceKey);
            }

            if (palette != null)
            {
                foreach (string paletteKey in palette.Keys)
                {
                    if (seen.Add(paletteKey))
                        keys.Add(paletteKey);
                }
            }

            return keys;
        }

        /// <summary>
        /// Applies a theme palette to the global brush resources.
        /// Custom colors (from the colors INI) override the palette per key.
        /// </summary>
        public static void ApplyTheme(string themeName, Dictionary<string, string> customColors)
        {
            Dictionary<string, Color> palette = GetPalette(themeName);

            if (Application.Current == null)
                return;

            foreach (string resourceKey in GetThemedResourceKeys(themeName))
            {
                Application.Current.Resources[resourceKey] =
                    new SolidColorBrush(ResolveColor(resourceKey, themeName, customColors));
            }
        }

        /// <summary>
        /// Reads the current theme from a colors INI file ("system" when absent).
        /// </summary>
        public static string GetTheme(string colorsPath)
        {
            return GetThemeName(IniFileHandler.LoadIni(colorsPath));
        }

        /// <summary>
        /// Persists the theme in a colors INI file, preserving the other keys.
        /// </summary>
        public static void SaveTheme(string themeName, string colorsPath)
        {
            Dictionary<string, string> settings = IniFileHandler.LoadIni(colorsPath);
            settings[ThemeKey] = themeName;

            var lines = new List<string>();
            foreach (var kvp in settings)
                lines.Add(kvp.Key + "=" + kvp.Value);
            File.WriteAllLines(colorsPath, lines);
        }

    }
}
