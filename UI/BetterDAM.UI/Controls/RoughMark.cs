using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BetterDAM.UI.Controls;

/// <summary>
/// Marks a folder as selected or hovered, drawn as if by pencil, for the hand-drawn selection
/// experiment.
///
/// One control rather than two because the two marks are the same gesture at different weights, and
/// because it lets the control decide between them: a row that is both selected and hovered gets the
/// ring only. Two separate controls would need a converter to express that, and would occasionally
/// draw both.
///
/// Two details carry the effect, and both are only wrong in motion:
///
/// <list type="bullet">
/// <item>
/// The geometry is built at the control's real size rather than a fixed path being stretched. A
/// stretched path gets a fat stroke on its long axis and a thin one on its short.
/// </item>
/// <item>
/// The wobble is seeded from that size, so a given row draws the same line every frame. Unseeded
/// noise re-rolls on each repaint and the line crawls during scrolls and resizes.
/// </item>
/// </list>
/// </summary>
public sealed class RoughMark : Control
{
    /// <summary>The selected row. Drawn as a ring around the name.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<RoughMark, bool>(nameof(IsSelected));

    /// <summary>The row under the pointer. Drawn as an underline, and only when not selected.</summary>
    public static readonly StyledProperty<bool> IsHoveredProperty =
        AvaloniaProperty.Register<RoughMark, bool>(nameof(IsHovered));

    public static readonly StyledProperty<double> RoughnessProperty =
        AvaloniaProperty.Register<RoughMark, double>(nameof(Roughness), 1.0);

    public static readonly StyledProperty<bool> AnimatesProperty =
        AvaloniaProperty.Register<RoughMark, bool>(nameof(Animates), true);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<RoughMark, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RoughMark, double>(nameof(StrokeThickness), 1.5);

    /// <summary>How far the drawing has got, 0 to 1. Animated; not usually set by hand.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<RoughMark, double>(nameof(Progress), 1.0);

    /// <summary>
    /// The ring takes a moment; the underline does not. Hover has to keep up with a pointer moving
    /// down a list, where a leisurely draw would still be finishing as the pointer left.
    /// </summary>
    private static readonly TimeSpan RingDuration = TimeSpan.FromMilliseconds(520);

    private static readonly TimeSpan UnderlineDuration = TimeSpan.FromMilliseconds(190);

    static RoughMark()
    {
        AffectsRender<RoughMark>(
            IsSelectedProperty, IsHoveredProperty, RoughnessProperty,
            StrokeProperty, StrokeThicknessProperty, ProgressProperty);
    }

    public RoughMark()
    {
        // Purely decorative: it sits over the folder name and must never eat a click meant for it.
        IsHitTestVisible = false;
    }

    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsHovered { get => GetValue(IsHoveredProperty); set => SetValue(IsHoveredProperty, value); }
    public double Roughness { get => GetValue(RoughnessProperty); set => SetValue(RoughnessProperty, value); }
    public bool Animates { get => GetValue(AnimatesProperty); set => SetValue(AnimatesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>What is actually drawn right now. The ring wins when a row is both.</summary>
    private bool ShowsRing => IsSelected;

    private bool ShowsUnderline => !IsSelected && IsHovered;

    private DispatcherTimer? _timer;
    private DateTime _startedAt;
    private TimeSpan _duration = RingDuration;

    // Cached points. The draw-on changes only how many are used, so rebuilding per frame would be
    // waste — and re-seeding per frame would reintroduce the crawl this design exists to avoid.
    private Rect _cachedFor;
    private double _cachedRoughness = double.NaN;
    private bool _cachedRing;
    private Point[]? _first;
    private Point[]? _second;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsSelectedProperty && change.Property != IsHoveredProperty)
        {
            return;
        }

        if (ShowsRing)
        {
            Begin(RingDuration);
        }
        else if (ShowsUnderline)
        {
            Begin(UnderlineDuration);
        }
        else
        {
            _timer?.Stop();
        }
    }

    private void Begin(TimeSpan duration)
    {
        if (!Animates)
        {
            _timer?.Stop();
            Progress = 1;
            return;
        }

        _duration = duration;
        Progress = 0;
        _startedAt = DateTime.UtcNow;

        _timer ??= CreateTimer();
        _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds;

            if (t >= 1)
            {
                Progress = 1;
                timer.Stop();
                return;
            }

            // Smoothstep rather than an ease-out. An ease-out was tried first and is wrong for a pen
            // stroke: it spends most of the duration on the last sliver of line, so the mark looks
            // almost finished immediately and then creeps. A hand accelerates in and decelerates
            // out, at roughly even speed between.
            Progress = t * t * (3 - (2 * t));
        };

        return timer;
    }

    public override void Render(DrawingContext context)
    {
        if (Stroke is null || Bounds.Width <= 8 || Bounds.Height <= 8)
        {
            return;
        }

        if (!ShowsRing && !ShowsUnderline)
        {
            return;
        }

        var box = new Rect(Bounds.Size).Deflate(StrokeThickness);
        EnsurePoints(box, ShowsRing);

        var pen = new Pen(Stroke, StrokeThickness, lineCap: PenLineCap.Round);
        var progress = Math.Clamp(Progress, 0, 1);

        if (ShowsRing)
        {
            // The second pass sets off before the first has finished, the way a hand comes back
            // round without pausing at the top.
            DrawPass(context, pen, _first!, Window(progress, 0.0, 0.72));
            DrawPass(context, pen, _second!, Window(progress, 0.28, 1.0));
            return;
        }

        DrawPass(context, pen, _first!, progress);
    }

    private static double Window(double t, double from, double to)
        => Math.Clamp((t - from) / (to - from), 0, 1);

    private static void DrawPass(DrawingContext context, IPen pen, Point[] points, double portion)
    {
        var count = (int)Math.Round((points.Length - 1) * portion);
        if (count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();

        using (var sink = geometry.Open())
        {
            sink.BeginFigure(points[0], isFilled: false);

            for (var i = 0; i < count; i++)
            {
                var p0 = points[Math.Max(i - 1, 0)];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = points[Math.Min(i + 2, points.Length - 1)];

                // Catmull-Rom through the sampled points, written as the cubics Avalonia takes.
                sink.CubicBezierTo(
                    new Point(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6)),
                    new Point(p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6)),
                    p2);
            }

            sink.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void EnsurePoints(Rect box, bool ring)
    {
        if (_first is not null
            && _cachedFor == box
            && _cachedRoughness.Equals(Roughness)
            && _cachedRing == ring)
        {
            return;
        }

        // Seeded from the size rather than a random number, so the mark is stable across repaints
        // and two rows of different widths do not get identical wobble.
        var seed = HashCode.Combine(Math.Round(box.Width), Math.Round(box.Height));

        if (ring)
        {
            var start = new Random(seed).NextDouble() * Math.PI * 2;

            _first = RingPass(box, seed, Roughness, start, (Math.PI * 2) + 0.35);
            _second = RingPass(box, seed + 977, Roughness, start + 0.5, (Math.PI * 2) + 0.15);
        }
        else
        {
            _first = UnderlinePass(box, seed, Roughness);
            _second = null;
        }

        _cachedFor = box;
        _cachedRoughness = Roughness;
        _cachedRing = ring;
    }

    private static Point[] RingPass(Rect box, int seed, double roughness, double startAngle, double sweep)
    {
        var random = new Random(seed);
        var cx = box.Center.X;
        var cy = box.Center.Y;
        var rx = box.Width / 2;
        var ry = box.Height / 2;

        // Scaled by the smaller radius, so a wide row does not wobble more than a narrow one.
        var amplitude = roughness * Math.Min(rx, ry) * 0.09;

        const int steps = 26;
        var points = new Point[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = startAngle + (sweep * i / steps);

            // Tapered at both ends, so the overshoot settles instead of flying off.
            var taper = Math.Sin(Math.PI * i / steps);
            var jx = (random.NextDouble() - 0.5) * 2 * amplitude * taper;
            var jy = (random.NextDouble() - 0.5) * 2 * amplitude * taper;

            points[i] = new Point(cx + (rx * Math.Cos(t)) + jx, cy + (ry * Math.Sin(t)) + jy);
        }

        return points;
    }

    /// <summary>
    /// A single stroke under the name. One pass, not two: a second would read as a deliberate double
    /// underline rather than as the lighter-weight sibling of the ring.
    /// </summary>
    private static Point[] UnderlinePass(Rect box, int seed, double roughness)
    {
        var random = new Random(seed);

        // Wobble in points rather than as a fraction of the row: an underline that scaled its
        // waviness with the name's length would ripple wildly under a long folder name.
        var amplitude = roughness * 1.4;

        const int steps = 14;
        var points = new Point[steps + 1];

        // Runs a little past the name at both ends, the way a hand does not stop exactly on the mark.
        var left = box.Left - 2;
        var right = box.Right + 4;
        var baseline = box.Bottom - 1;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var taper = Math.Sin(Math.PI * t);

            points[i] = new Point(
                left + ((right - left) * t),
                baseline + ((random.NextDouble() - 0.5) * 2 * amplitude * taper));
        }

        return points;
    }
}
