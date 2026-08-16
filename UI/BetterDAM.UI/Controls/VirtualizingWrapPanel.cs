using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace BetterDAM.UI.Controls;

/// <summary>
/// A wrapping panel that only realizes the tiles currently in view.
///
/// Avalonia ships <see cref="VirtualizingStackPanel"/> but no wrapping equivalent, and a plain
/// <c>WrapPanel</c> does not virtualize at all — it realizes a container for every item, which for a
/// media browser means generating a thumbnail for every file in the folder the moment it opens.
/// That is the single biggest scalability problem for large libraries, so the grid gets its own
/// panel.
///
/// The layout assumes **uniformly sized items**, which is true here: every tile is the same square
/// plus a caption. That assumption is what makes the arithmetic — and therefore scrolling — cheap
/// and exact rather than requiring per-item measurement bookkeeping.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel
{
    /// <summary>
    /// Rows realized above and below the viewport. A small buffer keeps scrolling from showing
    /// empty tiles, without materially increasing the work done on open.
    /// </summary>
    private const int BufferRows = 1;

    /// <summary>
    /// The tile width the items are being templated at.
    ///
    /// The panel does **not** lay out from this value — it measures a live tile, which is the only
    /// thing that knows the true cell size including margins and caption. This exists purely so a
    /// size change reliably invalidates the panel's measure. Avalonia does not guarantee that a
    /// child invalidating its own measure re-runs the parent's <see cref="MeasureOverride"/>, so
    /// without it the layout kept stale cells while the tiles themselves grew, and the tiles
    /// overflowed into each other.
    /// </summary>
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), double.NaN);

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    static VirtualizingWrapPanel() => AffectsMeasure<VirtualizingWrapPanel>(ItemWidthProperty);

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<Control, object?> _recycleKeys = [];
    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];

    private Rect _viewport;
    private Size _itemSize = new(160, 190);
    private int _columns = 1;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;

        if (items.Count == 0)
        {
            UnrealizeAll();
            return default;
        }

        // Learn the tile size from a live container so the layout follows the zoom slider without
        // needing to be told about it.
        UpdateItemSize(availableSize);

        var width = double.IsInfinity(availableSize.Width) ? _itemSize.Width : availableSize.Width;
        _columns = Math.Max(1, (int)(width / _itemSize.Width));
        var rows = (int)Math.Ceiling(items.Count / (double)_columns);

        var (firstIndex, lastIndex) = GetVisibleRange(items.Count, rows);

        // Drop anything that scrolled out of view before realizing what came in, so containers are
        // recycled rather than allocated afresh.
        UnrealizeOutside(firstIndex, lastIndex);

        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var container = Realize(index);
            container.Measure(_itemSize);
        }

        return new Size(_columns * _itemSize.Width, rows * _itemSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, container) in _realized)
        {
            var row = index / _columns;
            var column = index % _columns;

            container.Arrange(new Rect(
                column * _itemSize.Width,
                row * _itemSize.Height,
                _itemSize.Width,
                _itemSize.Height));
        }

        return finalSize;
    }

    private (int First, int Last) GetVisibleRange(int itemCount, int rows)
    {
        // Before the first viewport report there is nothing to go on; realize a screenful's worth so
        // the grid is never blank on open.
        if (_viewport.Height <= 0)
        {
            return (0, Math.Min(itemCount - 1, _columns * (BufferRows + 1) - 1));
        }

        var firstRow = Math.Max(0, (int)Math.Floor(_viewport.Y / _itemSize.Height) - BufferRows);
        var lastRow = Math.Min(rows - 1, (int)Math.Ceiling(_viewport.Bottom / _itemSize.Height) + BufferRows);

        if (lastRow < firstRow)
        {
            lastRow = firstRow;
        }

        var first = Math.Min(itemCount - 1, firstRow * _columns);
        var last = Math.Min(itemCount - 1, ((lastRow + 1) * _columns) - 1);
        return (first, last);
    }

    private void UpdateItemSize(Size availableSize)
    {
        var probe = _realized.Count > 0
            ? _realized.Values.First()
            : (Items.Count > 0 ? Realize(0) : null);

        if (probe is null)
        {
            return;
        }

        // Invalidated first because Measure is a no-op when the control still considers itself
        // valid for the same constraint, which would hand back the previous tile size and pin the
        // layout to it.
        probe.InvalidateMeasure();

        // Measured unconstrained so the tile reports its natural size; the zoom slider changes it.
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var desired = probe.DesiredSize;
        if (desired.Width > 0 && desired.Height > 0)
        {
            _itemSize = desired;
        }

        if (!double.IsInfinity(availableSize.Width) && _itemSize.Width > availableSize.Width)
        {
            _itemSize = new Size(availableSize.Width, _itemSize.Height);
        }
    }

    private Control Realize(int index)
    {
        if (_realized.TryGetValue(index, out var existing))
        {
            return existing;
        }

        var item = Items[index]!;
        var generator = ItemContainerGenerator!;

        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            container = TakeFromPool(recycleKey) ?? generator.CreateContainer(item, index, recycleKey);
            _recycleKeys[container] = recycleKey;
        }
        else
        {
            // The item is its own container.
            container = (Control)item;
            _recycleKeys[container] = null;
        }

        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);

        _realized[index] = container;
        return container;
    }

    private void UnrealizeOutside(int firstIndex, int lastIndex)
    {
        if (_realized.Count == 0)
        {
            return;
        }

        var stale = _realized.Keys.Where(i => i < firstIndex || i > lastIndex).ToList();
        foreach (var index in stale)
        {
            Unrealize(index);
        }
    }

    private void UnrealizeAll()
    {
        foreach (var index in _realized.Keys.ToList())
        {
            Unrealize(index);
        }
    }

    private void Unrealize(int index)
    {
        if (!_realized.Remove(index, out var container))
        {
            return;
        }

        ItemContainerGenerator!.ClearItemContainer(container);
        RemoveInternalChild(container);

        if (_recycleKeys.TryGetValue(container, out var recycleKey) && recycleKey is not null)
        {
            if (!_recyclePool.TryGetValue(recycleKey, out var pool))
            {
                pool = new Stack<Control>();
                _recyclePool[recycleKey] = pool;
            }

            pool.Push(container);
        }

        _recycleKeys.Remove(container);
    }

    private Control? TakeFromPool(object? recycleKey)
    {
        if (recycleKey is null || !_recyclePool.TryGetValue(recycleKey, out var pool) || pool.Count == 0)
        {
            return null;
        }

        return pool.Pop();
    }

    protected override void OnItemsChanged(System.Collections.Generic.IReadOnlyList<object?> items, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Index-to-container mapping is only valid for one arrangement of the source collection.
        // Rebuilding is cheap because only a screenful is ever realized.
        UnrealizeAll();
        InvalidateMeasure();
    }

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (index < 0 || index >= items.Count)
        {
            return null;
        }

        var row = index / Math.Max(1, _columns);
        var column = index % Math.Max(1, _columns);
        var rect = new Rect(column * _itemSize.Width, row * _itemSize.Height, _itemSize.Width, _itemSize.Height);

        this.BringIntoView(rect);

        // The container only exists once the layout has caught up with the new offset.
        UpdateLayout();
        return ContainerFromIndex(index);
    }

    protected override Control? ContainerFromIndex(int index)
        => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
        {
            if (ReferenceEquals(realized, container))
            {
                return index;
            }
        }

        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values.ToList();

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var items = Items;
        if (items.Count == 0)
        {
            return null;
        }

        var current = from is Control control ? IndexFromContainer(control) : -1;
        var columns = Math.Max(1, _columns);

        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => items.Count - 1,
            NavigationDirection.Next => current + 1,
            NavigationDirection.Previous => current - 1,
            NavigationDirection.Left => current - 1,
            NavigationDirection.Right => current + 1,
            NavigationDirection.Up => current - columns,
            NavigationDirection.Down => current + columns,
            NavigationDirection.PageUp => current - (columns * VisibleRows()),
            NavigationDirection.PageDown => current + (columns * VisibleRows()),
            _ => -1
        };

        if (target < 0 || target >= items.Count)
        {
            if (!wrap || direction is not (NavigationDirection.Next or NavigationDirection.Previous))
            {
                return null;
            }

            target = target < 0 ? items.Count - 1 : 0;
        }

        return ScrollIntoView(target);
    }

    private int VisibleRows()
        => Math.Max(1, (int)(_viewport.Height / Math.Max(1, _itemSize.Height)));
}
