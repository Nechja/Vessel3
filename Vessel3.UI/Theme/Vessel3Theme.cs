using MudBlazor;

namespace Vessel3.UI.Theme;

internal static class Vessel3Theme
{
    private static readonly string[] Mono =
        ["ui-monospace", "SF Mono", "Cascadia Mono", "JetBrains Mono", "Fira Code", "Menlo", "Consolas", "monospace"];

    public static readonly MudTheme Dark = new()
    {
        PaletteDark = new PaletteDark
        {
            Black = "#000000",
            Background = "#000000",
            BackgroundGray = "#070705",
            Surface = "#070705",
            DrawerBackground = "#000000",
            DrawerText = "#d8d4c8",
            DrawerIcon = "#918d80",
            AppbarBackground = "#000000",
            AppbarText = "#d8d4c8",

            TextPrimary = "#d8d4c8",
            TextSecondary = "#918d80",
            TextDisabled = "#4a483f",
            ActionDefault = "#918d80",
            ActionDisabled = "#4a483f",
            ActionDisabledBackground = "#16140e",

            Primary = "#ffb454",
            PrimaryContrastText = "#000000",
            Secondary = "#87d96c",
            SecondaryContrastText = "#000000",
            Tertiary = "#d2a6ff",
            Info = "#59c2ff",
            Success = "#87d96c",
            Warning = "#e6b450",
            Error = "#f26d78",

            LinesDefault = "#1c1b16",
            LinesInputs = "#2c2a22",
            Divider = "#1c1b16",
            DividerLight = "#2c2a22",
            TableLines = "#1c1b16",
            TableStriped = "#050503",
            TableHover = "#12100a",

            HoverOpacity = 0.06,
            RippleOpacity = 0.10,
        },

        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = Mono,
                FontSize = "13px",
                LineHeight = "1.6",
                LetterSpacing = "normal",
            },
            H1 = new H1Typography { FontFamily = Mono, FontSize = "22px", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "-0.02em" },
            H2 = new H2Typography { FontFamily = Mono, FontSize = "17px", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "-0.01em" },
            H3 = new H3Typography { FontFamily = Mono, FontSize = "15px", FontWeight = "600", LineHeight = "1.4" },
            H4 = new H4Typography { FontFamily = Mono, FontSize = "13px", FontWeight = "600" },
            H5 = new H5Typography { FontFamily = Mono, FontSize = "12px", FontWeight = "600" },
            H6 = new H6Typography { FontFamily = Mono, FontSize = "11px", FontWeight = "600" },
            Body1 = new Body1Typography { FontFamily = Mono, FontSize = "13px" },
            Body2 = new Body2Typography { FontFamily = Mono, FontSize = "12px" },
            Button = new ButtonTypography { FontFamily = Mono, FontSize = "12px", FontWeight = "500", TextTransform = "lowercase" },
            Caption = new CaptionTypography { FontFamily = Mono, FontSize = "11px" },
        },

        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "3px",
            AppbarHeight = "44px",
            DrawerWidthLeft = "200px",
        },
    };
}
