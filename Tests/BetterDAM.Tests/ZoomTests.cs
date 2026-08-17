using Avalonia;
using BetterDAM.UI.Controls;
using Xunit;

namespace BetterDAM.Tests;

public class ZoomStateTests
{
    /// <summary>A 4000x3000 image in a 1000x1000 viewport: fit is a quarter.</summary>
    private static ZoomState Create(double contentW = 4000, double contentH = 3000,
                                    double viewW = 1000, double viewH = 1000)
    {
        var state = new ZoomState();
        state.SetContent(new Size(contentW, contentH), new Size(viewW, viewH));
        return state;
    }

    [Fact]
    public void New_content_starts_fitted()
    {
        var state = Create();

        Assert.Equal(0.25, state.Scale, precision: 4);
        Assert.True(state.IsFitted);
    }

    [Fact]
    public void Fitting_centres_the_content()
    {
        var state = Create();

        // 4000x3000 at 0.25 is 1000x750, so it fills the width and is centred vertically.
        Assert.Equal(0, state.Offset.X, precision: 4);
        Assert.Equal(125, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Actual_size_is_one_content_pixel_per_screen_pixel()
    {
        var state = Create();

        state.ActualSize();

        Assert.Equal(1, state.Scale, precision: 4);
        Assert.False(state.IsFitted);
    }

    [Fact]
    public void Zooming_keeps_the_anchor_point_still()
    {
        var state = Create();
        var anchor = new Point(300, 400);

        // The content point under the anchor before the zoom...
        var before = ((anchor.X - state.Offset.X) / state.Scale, (anchor.Y - state.Offset.Y) / state.Scale);

        state.ZoomBy(2, anchor);

        var after = ((anchor.X - state.Offset.X) / state.Scale, (anchor.Y - state.Offset.Y) / state.Scale);

        // ...must still be under it afterwards, which is what makes wheel zoom feel anchored.
        Assert.Equal(before.Item1, after.Item1, precision: 3);
        Assert.Equal(before.Item2, after.Item2, precision: 3);
    }

    [Fact]
    public void Zoom_is_clamped_at_both_ends()
    {
        var state = Create();
        var anchor = new Point(500, 500);

        state.ZoomTo(1000, anchor);
        Assert.Equal(ZoomState.MaxScale, state.Scale);

        state.ZoomTo(0.0001, anchor);
        Assert.Equal(ZoomState.MinScale, state.Scale);
    }

    [Fact]
    public void Content_smaller_than_the_viewport_stays_centred()
    {
        var state = Create(contentW: 200, contentH: 100);

        state.ZoomTo(1, new Point(500, 500));

        // Panning cannot move it, because there is nothing hidden to pan to.
        state.PanBy(new Vector(400, 400));

        Assert.Equal(400, state.Offset.X, precision: 4);
        Assert.Equal(450, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Panning_cannot_drag_the_content_off_screen()
    {
        var state = Create();
        state.ActualSize();

        // A wild drag in each direction: the edges stop at the viewport, never past them.
        state.PanBy(new Vector(9999, 9999));
        Assert.Equal(0, state.Offset.X, precision: 4);
        Assert.Equal(0, state.Offset.Y, precision: 4);

        state.PanBy(new Vector(-99999, -99999));
        Assert.Equal(1000 - 4000, state.Offset.X, precision: 4);
        Assert.Equal(1000 - 3000, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Resizing_the_viewport_keeps_the_chosen_magnification()
    {
        var state = Create();
        state.ActualSize();

        state.SetContent(new Size(4000, 3000), new Size(1400, 900));

        // Only the window changed; the user's zoom is not theirs to reset.
        Assert.Equal(1, state.Scale, precision: 4);
    }

    [Fact]
    public void Loading_different_content_refits()
    {
        var state = Create();
        state.ActualSize();

        state.SetContent(new Size(800, 600), new Size(1000, 1000));

        Assert.True(state.IsFitted);
        Assert.Equal(1.25, state.Scale, precision: 4);
    }

    [Fact]
    public void Zooming_out_past_fit_re_centres_rather_than_sticking_to_a_corner()
    {
        var state = Create();
        state.ActualSize();
        state.PanBy(new Vector(-2000, -1500));

        state.ZoomTo(0.1, new Point(0, 0));

        // At 0.1 the content is 400x300, smaller than the viewport, so it must be centred.
        Assert.Equal(300, state.Offset.X, precision: 4);
        Assert.Equal(350, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Without_content_there_is_nothing_to_fit()
    {
        var state = new ZoomState();

        Assert.False(state.HasContent);
        Assert.Equal(1, state.FitScale);
    }
}
