using System.Text.Json;
using Avalonia.Media;
using BetterDAM.Core.Models;
using BetterDAM.UI.Services;
using BetterDAM.UI.ViewModels;
using Xunit;

namespace BetterDAM.Tests;

public class ThemeTests
{
    /// <summary>
    /// The overlay the thumbnail grid and preview were painted with before they were themed: white
    /// at 6% over whatever sat beneath them.
    /// </summary>
    private const byte OverlayAlpha = 0x10;

    /// <summary>Source-over compositing of an opaque-white overlay onto an opaque background.</summary>
    private static byte Composite(byte background, byte overlayAlpha)
    {
        var alpha = overlayAlpha / 255d;
        return (byte)Math.Round((255 * alpha) + (background * (1 - alpha)));
    }

    /// <summary>
    /// The guard on the whole change: Darkroom has to be the application exactly as it was. The
    /// panels used to be a translucent white laid over a black window, and are now an opaque colour;
    /// this proves the two land on the same pixel rather than merely looking similar.
    /// </summary>
    [Fact]
    public void DarkroomReproducesTheOriginalUnthemedColours()
    {
        var palette = AppThemes.For(AppTheme.Darkroom);

        Assert.Equal(Color.FromRgb(0, 0, 0), palette.Surface);

        var composited = Composite(palette.Surface.R, OverlayAlpha);
        Assert.Equal(Color.FromRgb(composited, composited, composited), palette.Panel);
    }

    /// <summary>Graphite is the grid's tone everywhere: no panel sits lighter than its neighbour.</summary>
    [Fact]
    public void GraphiteIsFlat()
    {
        var graphite = AppThemes.For(AppTheme.Graphite);

        Assert.Equal(graphite.Surface, graphite.Panel);
        Assert.Equal(AppThemes.For(AppTheme.Darkroom).Panel, graphite.Surface);
    }

    /// <summary>
    /// Themes are opaque; a translucent surface would let the desktop through the window. Driven off
    /// the enum rather than a list of cases, so a theme added later is covered without being added
    /// here — the failure this guards against is one nobody remembers to write a case for.
    /// </summary>
    [Fact]
    public void EveryThemeIsOpaque()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var palette = AppThemes.For(theme);

            Assert.Equal(255, palette.Surface.A);
            Assert.Equal(255, palette.Panel.A);
        });
    }

    /// <summary>
    /// Themes have to be told apart. Two entries resolving to the same palette would leave the
    /// dropdown offering a choice that does nothing.
    /// </summary>
    [Fact]
    public void EveryThemeIsDistinct()
    {
        var palettes = Enum.GetValues<AppTheme>().Select(AppThemes.For).ToArray();

        Assert.Equal(palettes.Length, palettes.Distinct().Count());
    }

    /// <summary>
    /// Every theme stays dark enough to judge a photograph against. A light surface would be a
    /// different product, and would arrive by way of someone nudging a colour rather than deciding.
    /// </summary>
    [Fact]
    public void EveryThemeStaysDark()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var palette = AppThemes.For(theme);

            foreach (var colour in new[] { palette.Surface, palette.Panel })
            {
                // Rec. 601 luma, which weights green the way the eye does.
                var luma = (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B);
                Assert.True(luma < 80, $"{theme} has a surface at luma {luma:F0}, which is not dark.");
            }
        });
    }

    /// <summary>
    /// A settings file naming a theme this build does not have must still open, looking like the
    /// application rather than like an unpainted window.
    /// </summary>
    [Fact]
    public void UnknownThemeFallsBackToDarkroom()
    {
        Assert.Equal(AppThemes.For(AppTheme.Darkroom), AppThemes.For((AppTheme)999));
    }

    [Fact]
    public void DefaultsToDarkroom()
    {
        Assert.Equal(AppTheme.Darkroom, AppSettings.Default.Theme);
    }

    /// <summary>
    /// Themes are persisted as their pinned numbers, so the value a previous version wrote has to
    /// still mean the same theme.
    /// </summary>
    [Fact]
    public void ThemeSurvivesARoundTrip()
    {
        var settings = AppSettings.Default with { Theme = AppTheme.Graphite };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal(AppTheme.Graphite, restored.Theme);
    }

    [Fact]
    public void PinnedThemeNumbersDoNotMove()
    {
        Assert.Equal(0, (int)AppTheme.Darkroom);
        Assert.Equal(1, (int)AppTheme.Graphite);
        Assert.Equal(2, (int)AppTheme.Safelight);
        Assert.Equal(3, (int)AppTheme.Verdigris);
    }

    /// <summary>
    /// Every theme must be offerable. A theme added to the enum but not to the dropdown would be
    /// unreachable, and would leave the dropdown blank for anyone whose settings already named it.
    /// </summary>
    [Fact]
    public void EveryThemeIsOfferedInSettings()
    {
        var offered = SettingsViewModel.Themes.Select(choice => choice.Theme).ToArray();

        Assert.Equal(Enum.GetValues<AppTheme>(), offered);
        Assert.All(SettingsViewModel.Themes, choice =>
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.Name));
            Assert.False(string.IsNullOrWhiteSpace(choice.Description));
        });
    }
}
