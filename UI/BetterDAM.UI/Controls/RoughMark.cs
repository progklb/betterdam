using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BetterDAM.UI.Controls;

/// <summary>
/// A ring drawn as if by pencil, used to mark the selected folder when the hand-drawn selection
/// style is switched on.
///
/// Two details carry the whole effect, and both are easy to get wrong in ways that only show in
/// motion:
///
/// <list type="bullet">
/// <item>
/// The geometry is built at the control's real size rather than a fixed path being stretched. A
/// stretched path gets a fat stroke on its long axis and a thin one on its short.
/// </item>
/// <item>
/// The wobble is seeded from the control's own size, so a given row draws the same ring every
/// frame. Unseeded noise re-rolls on each repaint and the ring crawls during scrolls and resizes.
/// </item>
/// </list>
/// </summary>
public sealed class RoughRing : Control
{
    /// <summary>Whether this row is the selected one. Turning it on starts the draw.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<RoughRing, bool>(nameof(IsActive));

    public static readonly StyledProperty<double> RoughnessProperty =
        AvaloniaProperty.Register<RoughRing, double>(nameof(Roughness), 1.0);

    public static readonly StyledProperty<bool> AnimatesProperty =
        AvaloniaProperty.Register<RoughRing, bool>(nameof(Animates), true);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<RoughRing, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RoughRing, double>(nameof(StrokeThickness), 1.5);

    /// <summary>How far the drawing has got, 0 to 1. Animated; not usually set by hand.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<RoughRing, double>(nameof(Progress), 1.0);

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(520);

    static RoughRing()
    {
        AffectsRender<RoughRing>(
            IsActiveProperty, RoughnessProperty, StrokeProperty, StrokeThicknessProperty, ProgressProperty);
    }

    public RoughRing()
    {
        // Purely decorative: it sits over the folder name and must never eat a click meant for it.
        IsHitTestVisible = false;
    }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Roughness { get => GetValue(RoughnessProperty); set => SetValue(RoughnessProperty, value); }
    public bool Animates { get => GetValue(AnimatesProperty); set => SetValue(AnimatesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    private DispatcherTimer? _timer;
    private DateTime _startedAt;

    // Cached points. The draw-on changes only how many of them are used, so rebuilding per frame
    // would be waste — and re-seeding per frame would reintroduce the crawl this design avoids.
    private Rect _cachedFor;
    private double _cachedRoughness = double.NaN;
    private Point[]? _first;
    private Point[]? _second;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty)
        {
            if (IsActive)
            {
                Begin();
            }
            else
            {
                _timer?.Stop();
            }
        }
    }

    private void Begin()
    {
        if (!Animates)
        {
            _timer?.Stop();
            Progress = 1;
            return;
        }

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
            var t = (DateTime.UtcNow - _startedAt).TotalMilliseconds / Duration.TotalMilliseconds;

            if (t >= 1)
            {
                Progress = 1;
                timer.Stop();
                return;
            }

            // Smoothstep rather than an ease-out. An ease-out was tried first and is wrong for a pen
            // stroke: it spends most of the duration on the last sliver of line, so the ring looks
            // almost finished immediately and then creeps. A hand accelerates in and decelerates
            // out, at roughly even speed between.
            Progress = t * t * (3 - (2 * t));
        };

        return timer;
    }

    public override void Render(DrawingContext context)
    {
        if (!IsActive || Stroke is null || Bounds.Width <= 8 || Bounds.Height <= 8)
        {
            return;
        }

        var box = new Rect(Bounds.Size).Deflate(StrokeThickness);
        EnsurePoints(box);

        var pen = new Pen(Stroke, StrokeThickness, lineCap: PenLineCap.Round);
        var progress = Math.Clamp(Progress, 0, 1);

        // The second pass sets off before the first has finished, the way a hand comes back round
        // without pausing at the top.
        DrawPass(context, pen, _first!, Window(progress, 0.0, 0.72));
        DrawPass(context, pen, _second!, Window(progress, 0.28, 1.0));
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

    private void EnsurePoints(Rect box)
    {
        if (_first is not null && _cachedFor == box && _cachedRoughness.Equals(Roughness))
        {
            return;
        }

        // Seeded from the size rather than a random number, so the ring is stable across repaints
        // and two rows of different widths do not get identical wobble.
        var seed = HashCode.Combine(Math.Round(box.Width), Math.Round(box.Height));
        var start = new Random(seed).NextDouble() * Math.PI * 2;

        _first = Pass(box, seed, Roughness, start, (Math.PI * 2) + 0.35);
        _second = Pass(box, seed + 977, Roughness, start + 0.5, (Math.PI * 2) + 0.15);

        _cachedFor = box;
        _cachedRoughness = Roughness;
    }

    private static Point[] Pass(Rect box, int seed, double roughness, double startAngle, double sweep)
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
}
