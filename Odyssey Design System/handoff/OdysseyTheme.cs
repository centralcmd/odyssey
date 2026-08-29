// Design-system reference copy. The shipping file is Odyssey.Client/Theme/OdysseyTheme.cs,
// consumed by Odyssey.Client/Layout/OdysseyThemeProvider.razor:
//
//     <MudThemeProvider @ref="_mudThemeProvider"
//                       @bind-IsDarkMode="_isDarkMode"
//                       Theme="OdysseyTheme.Theme" />
//
// Keep this copy and the client copy in sync when tokens change.
// All color values come straight from colors_and_type.css. Names mirror MudBlazor v8's
// PaletteLight / PaletteDark property surface 1:1, so any future token edits should be
// kept in sync between the CSS and this file.

using MudBlazor;

namespace Odyssey.Client.Theme;

public static class OdysseyTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Black                    = "#080C18",
            White                    = "#FFFFFF",

            // Brand — Tide (phosphor teal)
            Primary                  = "#4FD7CB", // --tide-400
            PrimaryDarken            = "#14B8A6", // --tide-600 — hover/pressed
            PrimaryLighten           = "#5EEAD4", // --tide-300
            PrimaryContrastText      = "#080C18", // --ink-950

            Secondary                = "#38BDF8", // --sea-400
            SecondaryDarken          = "#0284C7",
            SecondaryLighten         = "#7DD3FC",
            SecondaryContrastText    = "#080C18",

            Tertiary                 = "#8B5CF6", // --violet-500
            TertiaryContrastText     = "#FFFFFF",

            Info                     = "#38BDF8", // sea-400
            Success                  = "#4ADE80", // mint-500 — Approved + income
            Warning                  = "#F59E0B", // amber-500
            Error                    = "#FF6B6B", // coral-500 — Flagged + expense

            // Surfaces
            Background               = "#0E1525", // --ink-900
            BackgroundGray           = "#080C18", // --ink-950
            Surface                  = "#141A2C", // --ink-800
            DrawerBackground         = "#0E1525",
            DrawerText               = "#E6EBF4",
            DrawerIcon               = "#98A4BC",
            AppbarBackground         = "#141A2C",
            AppbarText               = "#F5F7FB",

            // Text
            TextPrimary              = "#F5F7FB",
            TextSecondary            = "#98A4BC", // --ink-300
            TextDisabled             = "rgba(245,247,251,0.38)",

            // Actions
            ActionDefault            = "rgba(255,255,255,0.70)",
            ActionDisabled           = "rgba(255,255,255,0.26)",
            ActionDisabledBackground = "rgba(255,255,255,0.12)",
            HoverOpacity             = 0.06,

            // Lines
            Divider                  = "rgba(199,208,224,0.12)",
            DividerLight             = "rgba(199,208,224,0.06)",
            TableLines               = "rgba(199,208,224,0.10)",
            LinesDefault             = "rgba(199,208,224,0.18)",
            LinesInputs              = "rgba(199,208,224,0.28)",

            // Scrim under dialogs
            OverlayDark              = "rgba(8,12,24,0.62)",
        },

        PaletteLight = new PaletteLight
        {
            Black                    = "#080C18",
            White                    = "#FFFFFF",

            Primary                  = "#14B8A6", // --tide-600 — darker on light for contrast
            PrimaryDarken            = "#0E8A7C", // --tide-700
            PrimaryLighten           = "#2DD4BF", // --tide-500
            PrimaryContrastText      = "#FFFFFF",

            Secondary                = "#0284C7",
            SecondaryDarken          = "#0369A1",
            SecondaryLighten         = "#38BDF8",
            SecondaryContrastText    = "#FFFFFF",

            Tertiary                 = "#8B5CF6",
            TertiaryContrastText     = "#FFFFFF",

            Info                     = "#0EA5E9",
            Success                  = "#15803D", // mint-700
            Warning                  = "#B57820",
            Error                    = "#B23B3B", // coral-700

            Background               = "#FAFBFD",
            BackgroundGray           = "#F5F7FB",
            Surface                  = "#FFFFFF",
            DrawerBackground         = "#FFFFFF",
            DrawerText               = "#141A2C",
            DrawerIcon               = "#4A5670",
            AppbarBackground         = "#FFFFFF",
            AppbarText               = "#141A2C",

            TextPrimary              = "#0E1525",
            TextSecondary            = "#4A5670",
            TextDisabled             = "rgba(14,21,37,0.38)",

            ActionDefault            = "rgba(14,21,37,0.70)",
            ActionDisabled           = "rgba(14,21,37,0.26)",
            ActionDisabledBackground = "rgba(14,21,37,0.08)",
            HoverOpacity             = 0.04,

            Divider                  = "rgba(14,21,37,0.10)",
            DividerLight             = "rgba(14,21,37,0.05)",
            TableLines               = "rgba(14,21,37,0.08)",
            LinesDefault             = "rgba(14,21,37,0.14)",
            LinesInputs              = "rgba(14,21,37,0.28)",

            OverlayDark              = "rgba(14,21,37,0.50)",
        },

        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Roboto", "Helvetica Neue", "Helvetica", "Arial", "sans-serif" },
                FontSize   = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = "normal",
            },
            H1 = new H1Typography { FontWeight = "300", FontSize = "2.5rem",   LineHeight = "1.15", LetterSpacing = "-0.02em" },
            H2 = new H2Typography { FontWeight = "300", FontSize = "2rem",     LineHeight = "1.3",  LetterSpacing = "-0.02em" },
            H3 = new H3Typography { FontWeight = "400", FontSize = "1.5rem",   LineHeight = "1.3" },
            H4 = new H4Typography { FontWeight = "400", FontSize = "1.25rem",  LineHeight = "1.3" },
            H5 = new H5Typography { FontWeight = "500", FontSize = "1.125rem", LineHeight = "1.5" },
            H6 = new H6Typography { FontWeight = "500", FontSize = "1rem",     LineHeight = "1.5" },
            Body1    = new Body1Typography    { FontWeight = "400", FontSize = "1rem",      LineHeight = "1.5" },
            Body2    = new Body2Typography    { FontWeight = "400", FontSize = "0.875rem",  LineHeight = "1.5" },
            Button   = new ButtonTypography   { FontWeight = "500", FontSize = "0.875rem",  LineHeight = "1",   LetterSpacing = "0.04em", TextTransform = "uppercase" },
            Caption  = new CaptionTypography  { FontWeight = "400", FontSize = "0.75rem",   LineHeight = "1.5" },
            Overline = new OverlineTypography { FontWeight = "500", FontSize = "0.6875rem", LineHeight = "1.4", LetterSpacing = "0.08em", TextTransform = "uppercase" },
            Subtitle1 = new Subtitle1Typography { FontWeight = "400", FontSize = "1rem",     LineHeight = "1.5" },
            Subtitle2 = new Subtitle2Typography { FontWeight = "500", FontSize = "0.875rem", LineHeight = "1.5" },
        },

        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "4px",
            DrawerWidthLeft     = "240px",
            DrawerWidthRight    = "240px",
            AppbarHeight        = "56px",
        },

        // MudBlazor ships 25 elevation steps. We replace the most-used ones (1, 2, 4, 8, 16)
        // with the dark-tuned shadow + inset-highlight stack from --mud-elevation-*.
        // The remaining indices fall back to MudBlazor defaults.
        //
        // NOTE: MudBlazor v8's MudTheme.Shadows is a single static Shadow instance — it
        // cannot vary between dark and light modes. The inset white highlight below reads
        // as a faint bright line on white cards. See handoff/README.md "Light-mode shadows"
        // for the html:not([data-theme='dark']) .mud-elevation-* overrides that neutralise it.
        Shadows = new Shadow
        {
            Elevation = new[]
            {
                /*  0 */ "none",
                /*  1 */ "0 1px 2px rgba(0,0,0,0.45), 0 0 0 1px rgba(255,255,255,0.04) inset",
                /*  2 */ "0 2px 6px rgba(0,0,0,0.5),  0 0 0 1px rgba(255,255,255,0.04) inset",
                /*  3 */ "0 3px 10px rgba(0,0,0,0.5), 0 0 0 1px rgba(255,255,255,0.04) inset",
                /*  4 */ "0 4px 16px rgba(0,0,0,0.55),0 0 0 1px rgba(255,255,255,0.04) inset",
                /*  5 */ "0 5px 20px rgba(0,0,0,0.55),0 0 0 1px rgba(255,255,255,0.04) inset",
                /*  6 */ "0 6px 24px rgba(0,0,0,0.58),0 0 0 1px rgba(255,255,255,0.04) inset",
                /*  7 */ "0 8px 28px rgba(0,0,0,0.58),0 0 0 1px rgba(255,255,255,0.05) inset",
                /*  8 */ "0 10px 32px rgba(0,0,0,0.60),0 0 0 1px rgba(255,255,255,0.05) inset",
                /*  9 */ "0 12px 36px rgba(0,0,0,0.62),0 0 0 1px rgba(255,255,255,0.05) inset",
                /* 10 */ "0 14px 40px rgba(0,0,0,0.64),0 0 0 1px rgba(255,255,255,0.05) inset",
                /* 11 */ "0 16px 44px rgba(0,0,0,0.65),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 12 */ "0 18px 48px rgba(0,0,0,0.66),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 13 */ "0 18px 52px rgba(0,0,0,0.67),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 14 */ "0 20px 56px rgba(0,0,0,0.68),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 15 */ "0 22px 60px rgba(0,0,0,0.69),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 16 */ "0 24px 64px rgba(0,0,0,0.70),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 17 */ "0 26px 68px rgba(0,0,0,0.71),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 18 */ "0 28px 72px rgba(0,0,0,0.72),0 0 0 1px rgba(255,255,255,0.06) inset",
                /* 19 */ "0 30px 76px rgba(0,0,0,0.73),0 0 0 1px rgba(255,255,255,0.07) inset",
                /* 20 */ "0 32px 80px rgba(0,0,0,0.74),0 0 0 1px rgba(255,255,255,0.07) inset",
                /* 21 */ "0 34px 84px rgba(0,0,0,0.75),0 0 0 1px rgba(255,255,255,0.07) inset",
                /* 22 */ "0 36px 88px rgba(0,0,0,0.76),0 0 0 1px rgba(255,255,255,0.07) inset",
                /* 23 */ "0 38px 92px rgba(0,0,0,0.77),0 0 0 1px rgba(255,255,255,0.07) inset",
                /* 24 */ "0 40px 96px rgba(0,0,0,0.78),0 0 0 1px rgba(255,255,255,0.08) inset",
                /* 25 */ "0 42px 100px rgba(0,0,0,0.79),0 0 0 1px rgba(255,255,255,0.08) inset",
            },
        },
    };
}
