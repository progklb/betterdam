using Avalonia.Input;
using BetterDAM.UI.Controls;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// The viewer's keyboard map. Worth stating rather than inferring from a switch in an event handler,
/// because the one interesting rule looks inconsistent written down and is the opposite in practice.
/// </summary>
public class ViewerShortcutTests
{
    /// <summary>
    /// Space is the universal play/pause. A video that ignored it in favour of re-fitting a frame
    /// that already fits would be the surprising thing — and it is what Space does in the inline
    /// preview, so the two agree.
    /// </summary>
    [Fact]
    public void Space_plays_a_video()
        => Assert.Equal(ViewerAction.TogglePlayback, ViewerShortcuts.Resolve(Key.Space, isVideo: true));

    /// <summary>A still has nothing to play, so the key means what Lightroom's space bar means.</summary>
    [Fact]
    public void Space_fits_a_still()
        => Assert.Equal(ViewerAction.Fit, ViewerShortcuts.Resolve(Key.Space, isVideo: false));

    /// <summary>
    /// Fit is never only on Space, or it would be unreachable from the keyboard while a video is on
    /// screen.
    /// </summary>
    [Theory]
    [InlineData(Key.D0)]
    [InlineData(Key.NumPad0)]
    [InlineData(Key.Enter)]
    public void Fit_has_keys_that_work_whatever_is_showing(Key key)
    {
        Assert.Equal(ViewerAction.Fit, ViewerShortcuts.Resolve(key, isVideo: true));
        Assert.Equal(ViewerAction.Fit, ViewerShortcuts.Resolve(key, isVideo: false));
    }

    [Theory]
    [InlineData(Key.D1)]
    [InlineData(Key.NumPad1)]
    public void One_is_actual_size(Key key)
        => Assert.Equal(ViewerAction.ActualSize, ViewerShortcuts.Resolve(key, isVideo: false));

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.F)]
    public void Escape_and_f_close(Key key)
        => Assert.Equal(ViewerAction.Close, ViewerShortcuts.Resolve(key, isVideo: false));

    [Fact]
    public void The_arrows_browse()
    {
        Assert.Equal(ViewerAction.Previous, ViewerShortcuts.Resolve(Key.Left, isVideo: false));
        Assert.Equal(ViewerAction.Next, ViewerShortcuts.Resolve(Key.Right, isVideo: false));
    }

    /// <summary>Reported under several names depending on keyboard layout.</summary>
    [Theory]
    [InlineData(Key.OemBackslash)]
    [InlineData(Key.OemPipe)]
    [InlineData(Key.Oem5)]
    public void Backslash_switches_between_raw_and_jpeg(Key key)
        => Assert.Equal(ViewerAction.ToggleRawDevelopment, ViewerShortcuts.Resolve(key, isVideo: false));

    /// <summary>The video player's own convention, kept so the habit works here too.</summary>
    [Fact]
    public void K_plays_a_video_and_does_nothing_to_a_still()
    {
        Assert.Equal(ViewerAction.TogglePlayback, ViewerShortcuts.Resolve(Key.K, isVideo: true));
        Assert.Equal(ViewerAction.None, ViewerShortcuts.Resolve(Key.K, isVideo: false));
    }

    [Fact]
    public void An_unmapped_key_does_nothing()
        => Assert.Equal(ViewerAction.None, ViewerShortcuts.Resolve(Key.Q, isVideo: false));

    /// <summary>The hint has to describe the keys that actually apply to what is on screen.</summary>
    [Fact]
    public void The_hint_follows_the_medium()
    {
        Assert.Contains("space to play", ViewerShortcuts.Hint(isVideo: true));
        Assert.Contains("0 to fit", ViewerShortcuts.Hint(isVideo: true));
        Assert.Contains("space to fit", ViewerShortcuts.Hint(isVideo: false));
    }
}
