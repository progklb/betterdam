using Avalonia;
using Avalonia.Media;
using BetterDAM.Core.Models;

namespace BetterDAM.UI.Services;

/// <summary>The colours a theme is made of.</summary>
/// <param name="Surface">
/// The application's chrome: toolbars, the folder tree, the metadata panel, the status bar.
/// </param>
/// <param name="Panel">
/// The working surfaces — the thumbnail grid and the preview. In some themes this is a step lighter
/// than <paramref name="Surface"/>, in others the same colour.
/// </param>
/// <param name="Selection">
/// What a selected thumbnail or folder is filled with when the selection is set to follow the theme.
/// Only used in that case — under <see cref="SelectionColour.System"/> the platform's colour wins.
/// </param>
public sealed record ThemePalette(Color Surface, Color Panel, Color Selection);

/// <summary>
/// What each theme paints, and the resource keys the rest of the application reads them through.
///
/// The surfaces stay deliberately few, which is the whole reason this remains small: almost every
/// other colour in the application is already a translucent white or black laid <i>over</i> one of
/// them — splitters at 13% white, badges at 69% black, the tile behind a thumbnail at 9% white.
/// Those composite against whatever is beneath, so redefining the surfaces repaints everything
/// resting on them and no border or overlay has to be restated per theme.
///
/// That is also why <see cref="AppTheme.Darkroom"/> can be expressed as opaque colours without
/// changing anything: black with the grid's 6% white overlay composites to exactly #101010, which
/// is what the application was already painting.
/// </summary>
public static class AppThemes
{
    public const string SurfaceKey = "AppSurfaceBrush";
    public const string PanelKey = "AppPanelBrush";

    /// <summary>
    /// Selection colours are a clear step lighter than the tile they land on — a tile is the panel
    /// plus 9% white, so anything close to that reads as a hover rather than a choice.
    /// </summary>
    private static readonly ThemePalette Darkroom = new(
        Surface: Color.FromRgb(0x00, 0x00, 0x00),
        Panel: Color.FromRgb(0x10, 0x10, 0x10),
        Selection: Color.FromRgb(0x4A, 0x4A, 0x4A));

    /// <summary>
    /// Deliberately flat: both surfaces are the tone Darkroom uses for the grid alone. Panels are
    /// then told apart by their splitters and borders rather than by a change of shade.
    /// </summary>
    private static readonly ThemePalette Graphite = new(
        Surface: Color.FromRgb(0x10, 0x10, 0x10),
        Panel: Color.FromRgb(0x10, 0x10, 0x10),
        Selection: Color.FromRgb(0x4A, 0x4A, 0x4A));

    /// <summary>
    /// Red kept very dark and only moderately saturated. A safelight is dim on purpose, and the
    /// colour has to sit behind photographs without casting the eye — a bright red would tint the
    /// judgement of every warm image in the grid.
    /// </summary>
    private static readonly ThemePalette Safelight = new(
        Surface: Color.FromRgb(0x18, 0x05, 0x05),
        Panel: Color.FromRgb(0x28, 0x09, 0x09),
        Selection: Color.FromRgb(0x6B, 0x1A, 0x1A));

    /// <summary>
    /// Pitched well below the application it takes its hue from: that one paints a writing canvas,
    /// where this sits behind photographs and has to stay out of their contrast. The first attempt
    /// kept the borrowed value as well as the hue and read as loud next to the other themes, so the
    /// value came down and the hue stayed.
    /// </summary>
    private static readonly ThemePalette Verdigris = new(
        Surface: Color.FromRgb(0x08, 0x1B, 0x1D),
        Panel: Color.FromRgb(0x0D, 0x27, 0x29),
        Selection: Color.FromRgb(0x1C, 0x53, 0x4C));

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
    public static void Apply(Application application, AppSettings settings)
    {
        var palette = For(settings.Theme);

        application.Resources[SurfaceKey] = new SolidColorBrush(palette.Surface);
        application.Resources[PanelKey] = new SolidColorBrush(palette.Panel);

        ApplyAccent(application, settings);
    }

    /// <summary>
    /// Sets the accent, which is what actually colours a selection.
    ///
    /// Everything selectable is left to Fluent to paint from this one colour rather than overridden
    /// per control. An earlier version did override the list and tree directly, and it was wrong in
    /// a way worth recording: Fluent paints the tree row from the accent <i>and</i> the header
    /// presenter sits inside that row, so overriding the presenter produced a second band in a
    /// different colour nested inside the first. Setting the source colour and letting the theme
    /// distribute it cannot produce that class of mismatch, and needs no template part names — which
    /// are neither public API nor stable.
    ///
    /// Under <see cref="SelectionColour.System"/> the overrides are <b>removed</b> rather than set to
    /// the platform colour, so lookups fall through to the ones Fluent derived from the operating
    /// system. Writing the values back would freeze them, and the point of System is that it follows
    /// a change made in System Settings.
    /// </summary>
    private static void ApplyAccent(Application application, AppSettings settings)
    {
        if (settings.SelectionColour != SelectionColour.Theme)
        {
            foreach (var key in AccentKeys)
            {
                application.Resources.Remove(key);
            }

            return;
        }

        // Fluent expects a ramp, not a single colour: the variants are what a checkbox uses when it
        // is hovered, pressed or disabled. Supplying only the base would leave those states on the
        // platform blue, which is worse than not overriding at all — the control would change colour
        // under the pointer.
        var ramp = AccentRamp(For(settings.Theme).Selection);

        for (var i = 0; i < AccentKeys.Length; i++)
        {
            application.Resources[AccentKeys[i]] = ramp[i];
        }
    }

    /// <summary>
    /// The seven colours Fluent wants for an accent, in the order of <see cref="AccentKeys"/>:
    /// the base, three lighter, three darker.
    /// </summary>
    public static IReadOnlyList<Color> AccentRamp(Color accent)
    {
        var ramp = new Color[AccentKeys.Length];
        ramp[0] = accent;

        for (var i = 0; i < LightenSteps.Length; i++)
        {
            ramp[i + 1] = Lighten(accent, LightenSteps[i]);
            ramp[i + 4] = Darken(accent, DarkenSteps[i]);
        }

        return ramp;
    }

    /// <summary>
    /// Order matters: base, Light1-3, Dark1-3, matching the indexing in <see cref="ApplyAccent"/>.
    /// </summary>
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"
    ];

    /// <summary>
    /// The ramp Avalonia itself derives from the platform accent, measured from what it publishes
    /// rather than guessed — lighter shades blend towards white, darker ones scale down. Matching it
    /// means the accented controls behave the same whichever source the colour came from.
    /// </summary>
    private static readonly double[] LightenSteps = [0.30, 0.55, 0.81];

    private static readonly double[] DarkenSteps = [0.78, 0.62, 0.42];

    private static Color Lighten(Color colour, double towardsWhite) => Color.FromRgb(
        (byte)Math.Round(colour.R + ((255 - colour.R) * towardsWhite)),
        (byte)Math.Round(colour.G + ((255 - colour.G) * towardsWhite)),
        (byte)Math.Round(colour.B + ((255 - colour.B) * towardsWhite)));

    private static Color Darken(Color colour, double scale) => Color.FromRgb(
        (byte)Math.Round(colour.R * scale),
        (byte)Math.Round(colour.G * scale),
        (byte)Math.Round(colour.B * scale));

}
