using MudBlazor;
using MudBlazor.Utilities;

namespace Vessel3.UI.Theme;

internal static class Vessel3Theme
{
    public static readonly MudTheme Dark = new()
    {
        PaletteDark = new PaletteDark
        {
            Black = "#000000",
            Background = "#000000",
            BackgroundGray = "#0a0a0a",
            Surface = "#0a0a0a",
            DrawerBackground = "#000000",
            DrawerText = "#c9d1d9",
            DrawerIcon = "#8b949e",
            AppbarBackground = "#000000",
            AppbarText = "#c9d1d9",

            TextPrimary = "#c9d1d9",
            TextSecondary = "#8b949e",
            TextDisabled = "#484f58",
            ActionDefault = "#8b949e",
            ActionDisabled = "#484f58",
            ActionDisabledBackground = "#161b22",

            Primary = "#58a6ff",
            PrimaryContrastText = "#000000",
            Secondary = "#3fb950",
            SecondaryContrastText = "#000000",
            Tertiary = "#bc8cff",
            Info = "#58a6ff",
            Success = "#3fb950",
            Warning = "#d29922",
            Error = "#f85149",

            LinesDefault = "#1a1a1a",
            LinesInputs = "#2a2a2a",
            Divider = "#1a1a1a",
            DividerLight = "#2a2a2a",
            TableLines = "#1a1a1a",
            TableStriped = "#080808",
            TableHover = "#0d1117",

            HoverOpacity = 0.06,
            RippleOpacity = 0.10,
        },

        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["-apple-system", "BlinkMacSystemFont", "Segoe UI", "system-ui", "sans-serif"],
                FontSize = "14px",
                LineHeight = "1.5",
                LetterSpacing = "normal",
            },
            H1 = new H1Typography { FontSize = "24px", FontWeight = "600", LineHeight = "1.25" },
            H2 = new H2Typography { FontSize = "20px", FontWeight = "600", LineHeight = "1.3" },
            H3 = new H3Typography { FontSize = "16px", FontWeight = "600", LineHeight = "1.4" },
            H4 = new H4Typography { FontSize = "14px", FontWeight = "600" },
            H5 = new H5Typography { FontSize = "13px", FontWeight = "600" },
            H6 = new H6Typography { FontSize = "12px", FontWeight = "600" },
            Body1 = new Body1Typography { FontSize = "14px" },
            Body2 = new Body2Typography { FontSize = "13px" },
            Button = new ButtonTypography { FontSize = "13px", FontWeight = "500", TextTransform = "none" },
            Caption = new CaptionTypography { FontSize = "12px" },
        },

        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            AppbarHeight = "48px",
            DrawerWidthLeft = "220px",
        },
    };
}
