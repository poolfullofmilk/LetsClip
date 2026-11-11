using MudBlazor;

namespace LetsClip.Desktop.Theming;

public static class AppTheme
{
    // The One Icon For Details, Notes And About
    public const string InfoIcon = Icons.Material.Rounded.Notes;

    // Application Font First, System Fonts Until It Loads
    private static readonly string[] s_fontFamily =
    [
        "Nunito",
        "Segoe UI",
        "Roboto",
        "Helvetica",
        "Arial",
    ];

    // Three Weight Steps Across The Whole App
    private const string LabelWeight = "500";
    private const string HeadingWeight = "700";
    private const string NormalLetterSpacing = "normal";

    // Shared Dark Surfaces, Bars Sit Darkest
    private const string ChromeSurface = "#141414";
    private const string PageBackground = "#202020";
    private const string RaisedSurface = "#2a2a2a";

    // Light Blue Accents, The Strong Blue Tints Tonal Fills
    private const string AccentBlue = "#3d8bfd";
    private const string AccentLight = "#b1d1fe";
    private const string AccentBlueDarken = "#2b74e0";

    // Shared White Overlay Steps
    private const string TextStrong = "rgba(255,255,255, 0.85)";
    private const string TextMedium = "rgba(255,255,255, 0.60)";
    private const string TextFaint = "rgba(255,255,255, 0.35)";
    private const string LineStrong = "rgba(255,255,255, 0.15)";
    private const string LineFaint = "rgba(255,255,255, 0.10)";

    // Two Depths, A Very Slight Lift And A Panel Drop
    private const string SlightShadow = "0 1px 9px rgba(0,0,0, 0.16)";
    private const string PanelShadow = "0 8px 24px -10px rgba(0,0,0, 0.6)";

    public static readonly MudTheme Custom = new()
    {
        PaletteDark = new PaletteDark
        {
            // Core Colors
            Primary = Colors.Gray.Lighten1,
            Secondary = AccentBlue,
            Tertiary = TextMedium,

            // Status Colors
            Info = AccentLight,
            Warning = "#f5b544",

            // UI Elements
            AppbarBackground = ChromeSurface,
            AppbarText = TextStrong,
            DrawerBackground = ChromeSurface,
            DrawerText = TextStrong,
            DrawerIcon = TextStrong,

            // Content Areas
            Background = PageBackground,
            Surface = RaisedSurface,
            BackgroundGray = PageBackground,

            // Text Colors
            TextPrimary = TextStrong,
            TextSecondary = TextMedium,
            TextDisabled = TextFaint,

            // Icons And Disabled Controls Share The Text Greys
            ActionDefault = TextMedium,
            ActionDisabled = TextFaint,

            // Link Colors
            HoverOpacity = 0.12,

            // Form Elements
            PrimaryDarken = Colors.Gray.Darken2,
            PrimaryLighten = Colors.Gray.Lighten4,
            SecondaryDarken = AccentBlueDarken,
            SecondaryLighten = Colors.Shades.White,
            TertiaryDarken = Colors.Gray.Darken3,
            TertiaryLighten = Colors.Gray.Lighten2,

            // Lines And Dividers
            Divider = LineStrong,
            DividerLight = LineFaint,
            LinesDefault = LineStrong,
            LinesInputs = LineStrong,

            // Focus States
            Dark = RaisedSurface,
            DarkContrastText = TextStrong,
            DarkLighten = Colors.Gray.Darken3,
            DarkDarken = PageBackground,

            // Tables
            TableLines = LineStrong,
            TableStriped = "rgba(255,255,255, 0.05)",
            TableHover = "rgba(0,0,0, 0.12)",

            // System
            OverlayDark = "rgba(0, 0, 0, 0.5)",
            OverlayLight = "rgba(255, 255, 255, 0.15)",
            Black = "#000000",
            GrayLight = Colors.Gray.Lighten2,
            GrayLighter = Colors.Gray.Lighten4,
            GrayDark = Colors.Gray.Darken2,
            GrayDarker = Colors.Gray.Darken4,
            Skeleton = LineFaint,
        },
        Shadows = new Shadow
        {
            // Level One Lifts Chips And Cards, The Rest Are Panels
            Elevation = ["none", SlightShadow, .. Enumerable.Repeat(PanelShadow, 24)],
        },
        Typography = BuildTypography(),
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "15px",
            AppbarHeight = "55px",
        },
    };

    private static Typography BuildTypography()
    {
        // Inherited Text Keeps A 16 Pixel Body
        var typography = new Typography
        {
            Default = new DefaultTypography { FontSize = "1rem", LineHeight = "1.5" },
            Button = new ButtonTypography { TextTransform = "none" },
        };
        BaseTypography[] headings =
        [
            typography.H1,
            typography.H2,
            typography.H3,
            typography.H4,
            typography.H5,
            typography.H6,
        ];
        BaseTypography[] others =
        [
            typography.Default,
            typography.Subtitle1,
            typography.Subtitle2,
            typography.Body1,
            typography.Body2,
            typography.Button,
            typography.Caption,
            typography.Overline,
        ];
        foreach (var heading in headings)
        {
            heading.FontWeight = HeadingWeight;
        }

        typography.Subtitle2.FontWeight = LabelWeight;
        typography.Button.FontWeight = LabelWeight;

        // Every Variant Takes The Font
        foreach (var variant in headings.Concat(others))
        {
            variant.FontFamily = s_fontFamily;
            variant.LetterSpacing = NormalLetterSpacing;
        }

        return typography;
    }
}
