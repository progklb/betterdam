using Avalonia;
using Avalonia.Media;
using BetterDAM.Core.Models;

namespace BetterDAM.UI.Services;

/// <summary>The two surface colours a theme is made of.</summary>
/// <param name="Surface">
/// The application's chrome: toolbars, the folder tree, the metadata panel, the status bar.
/// </param>
/// <param name="Panel">
/// The working surfaces — the thumbnail grid and the preview. In some themes this is a step lighter
/// than <paramref name="Surface"/>, in others the same colour.
/// </param>
public sealed record ThemePalette(Color Surface, Color Panel);

/// <summary>
/// What each theme paints, and the two resource keys the rest of the application reads them through.
///
/// Only two colours are themed, which is the whole reason this stays small: almost every other
/// colour in the application is already a translucent white or black laid <i>over</i> one of these
/// two — splitters at 13% white, badges at 69% black, the tile behind a thumbnail at 9% white.
/// Those composite against whatever is beneath them, so redefining these two repaints everything
/// resting on them and no accent, border or overlay has to be restated per theme.
///
/// That is also why <see cref="AppTheme.Darkroom"/> can be expressed as opaque colours without
/// changing anything: black with the grid's 6% white overlay composites to exactly #101010, which
/// is what the application was already painting.
/// </summary>
public static class AppThemes
{
    public const string SurfaceKey = "AppSurfaceBrush";
    public const string PanelKey = "AppPanelBrush";

    private static readonly ThemePalette Darkroom = new(
        Surface: Color.FromRgb(0x00, 0x00, 0x00),
        Panel: Color.FromRgb(0x10, 0x10, 0x10));

    /// <summary>
    /// Deliberately flat: both surfaces are the tone Darkroom uses for the grid alone. Panels are
    /// then told apart by their splitters and borders rather than by a change of shade.
    /// </summary>
    private static readonly ThemePalette Graphite = new(
        Surface: Color.FromRgb(0x10, 0x10, 0x10),
        Panel: Color.FromRgb(0x10, 0x10, 0x10));

    /// <summary>
    /// Red kept very dark and only moderately saturated. A safelight is dim on purpose, and the
    /// colour has to sit behind photographs without casting the eye — a bright red would tint the
    /// judgement of every warm image in the grid.
    /// </summary>
    private static readonly ThemePalette Safelight = new(
        Surface: Color.FromRgb(0x18, 0x05, 0x05),
        Panel: Color.FromRgb(0x28, 0x09, 0x09));

    /// <summary>
    /// Pitched darker than the application it takes its hue from: that one paints a writing canvas,
    /// where this sits behind photographs and has to stay out of their contrast. The hue is the
    /// same family, the value is not.
    /// </summary>
    private static readonly ThemePalette Verdigris = new(
        Surface: Color.FromRgb(0x0E, 0x2E, 0x31),
        Panel: Color.FromRgb(0x17, 0x41, 0x45));

    public static ThemePalette For(AppTheme theme) => theme switch
    {
        AppTheme.Graphite => Graphite,
        AppTheme.Safelight => Safelight,
        AppTheme.Verdigris => Verdigris,

        // Darkroom is also the fallback: a settings file naming a theme this build has never heard
        // of should open looking like the application, not fail to start.
        _ => Darkroom
    };

    /// <summary>
    /// Repaints the application. Every consumer binds these keys with <c>DynamicResource</c>, so
    /// replacing them here restyles windows that are already open — there is nothing to reopen and
    /// no restart to prompt for.
    /// </summary>
    public static void Apply(Application application, AppTheme theme)
    {
        var palette = For(theme);

        application.Resources[SurfaceKey] = new SolidColorBrush(palette.Surface);
        application.Resources[PanelKey] = new SolidColorBrush(palette.Panel);
    }
}
