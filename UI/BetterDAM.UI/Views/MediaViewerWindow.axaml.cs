using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BetterDAM.Core.Models;
using BetterDAM.UI.Controls;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

/// <summary>
/// Fullscreen inspection of the current selection.
///
/// A separate window rather than a mode of the main one: the main window keeps its layout untouched,
/// and closing needs nothing restored. It shares the main ViewModel, so browsing here moves the
/// selection there too and the two never disagree about what is being looked at.
/// </summary>
public partial class MediaViewerWindow : Window
{
    private static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(4);

    private MainWindowViewModel? _viewModel;
    private DispatcherTimer? _hintTimer;

    /// <summary>
    /// True until the view is first adjusted by hand. The window is created at an ordinary size and
    /// only then goes fullscreen, so the first fit is computed against the wrong viewport; refitting
    /// on resize corrects it. Deliberately stops at the first interaction, because after that the
    /// magnification is the user's and resizing must not discard it.
    /// </summary>
    private bool _awaitingInitialFit = true;

    public MediaViewerWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closed += OnClosed;

        // Tunnelled: a focused chrome button would otherwise swallow Space and re-trigger itself
        // instead of resetting the view. Safe here because the viewer has no text input to type in.
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);

        SizeChanged += (_, _) =>
        {
            if (_awaitingInitialFit)
            {
                Viewer.Fit();
            }
        };

        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.Property == ZoomPanViewer.ScaleProperty || e.Property == ZoomPanViewer.IsFittedProperty)
            {
                UpdateZoomLabel();
            }
        };
    }

    public void Attach(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Player.FrameReady += OnFrameReady;
        viewModel.Player.SurfaceCleared += OnSurfaceCleared;

        ShowCurrent();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        FillScreen();
        Viewer.Focus();

        // Shown, then faded. The transition on the border animates the change.
        _hintTimer = new DispatcherTimer { Interval = HintDuration };
        _hintTimer.Tick += (_, _) =>
        {
            Hint.Opacity = 0;
            _hintTimer?.Stop();
        };
        _hintTimer.Start();
    }

    /// <summary>
    /// Goes properly fullscreen, which on macOS is the only way to get out from under the menu bar:
    /// it draws above ordinary windows whatever their bounds, so sizing to the screen leaves it
    /// covering the top of the image.
    ///
    /// This needs the window to keep its system decorations. A window with
    /// <c>SystemDecorations="None"</c> has no titlebar, and macOS will not take a window without one
    /// into fullscreen — it simply stays the size it was, which is exactly what the first attempt
    /// did. The decorations are invisible once fullscreen anyway.
    /// </summary>
    private void FillScreen()
    {
        // Positioned on the screen the main window is on first, so it goes fullscreen on that
        // display rather than wherever it happened to open.
        if ((Screens.ScreenFromWindow(Owner as Window ?? this) ?? Screens.Primary) is { } screen)
        {
            Position = screen.Bounds.Position;
        }

        // Posted rather than set inline: assigning the state during Opened is ignored, the window
        // having only just been created natively.
        Dispatcher.UIThread.Post(() => WindowState = WindowState.FullScreen, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();

        if (_viewModel is not { } viewModel)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelChanged;
        viewModel.Player.FrameReady -= OnFrameReady;
        viewModel.Player.SurfaceCleared -= OnSurfaceCleared;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Preview)
            or nameof(MainWindowViewModel.SelectedItem)
            or nameof(MainWindowViewModel.IsVideoSelected))
        {
            ShowCurrent();
        }
    }

    /// <summary>Points the viewer at whatever is selected now, refitting for the new content.</summary>
    private void ShowCurrent()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        UpdateCounter();

        if (viewModel.IsVideoSelected)
        {
            Still.IsVisible = false;
            Surface.IsVisible = true;

            // Frames are pushed, so a surface created after the last one was sent has nothing to
            // show. Ask for the current frame again rather than waiting for playback.
            _ = viewModel.Player.RefreshFrameAsync();
            return;
        }

        Surface.IsVisible = false;
        Still.IsVisible = true;
        Still.Source = viewModel.Preview;

        if (viewModel.Preview is { } preview)
        {
            Viewer.NaturalSize = new Size(preview.PixelSize.Width, preview.PixelSize.Height);
            Viewer.Fit();
        }
    }

    private void UpdateCounter()
    {
        if (_viewModel is not { } viewModel || viewModel.SelectedItem is null)
        {
            Counter.IsVisible = false;
            return;
        }

        var index = viewModel.MediaItems.IndexOf(viewModel.SelectedItem);
        Counter.IsVisible = index >= 0;
        CounterLabel.Text = $"{index + 1} of {viewModel.MediaItems.Count}  ·  {viewModel.SelectedItem.FileName}";
    }

    private void OnFrameReady(VideoFrame frame)
    {
        var size = new Size(frame.Width, frame.Height);
        if (Viewer.NaturalSize != size)
        {
            Viewer.NaturalSize = size;
            Viewer.Fit();
        }

        Surface.Present(frame);
    }

    private void OnSurfaceCleared() => Surface.Clear();

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        _awaitingInitialFit = false;

        switch (e.Key)
        {
            case Key.Escape or Key.F:
                Close();
                break;

            // Reset the view, the way Lightroom's space bar recentres. Video play/pause is on the
            // transport and on K, because resetting the view is the thing wanted constantly here.
            case Key.Space or Key.D0 or Key.NumPad0:
                Viewer.Fit();
                break;

            case Key.D1 or Key.NumPad1:
                Viewer.ActualSize();
                break;

            case Key.Left:
                _viewModel?.SelectPreviousCommand.Execute(null);
                break;

            case Key.Right:
                _viewModel?.SelectNextCommand.Execute(null);
                break;

            case Key.K or Key.Enter when _viewModel?.IsVideoSelected == true:
                _viewModel.Player.TogglePlayCommand.Execute(null);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _awaitingInitialFit = false;

        if (e.ClickCount == 2)
        {
            if (Viewer.IsFitted)
            {
                Viewer.ActualSize();
            }
            else
            {
                Viewer.Fit();
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// "Fit" rather than a percentage while everything is visible: the number only means something
    /// once it is being compared against 100%.
    /// </summary>
    private void UpdateZoomLabel()
        => ZoomLabel.Text = Viewer.IsFitted ? "Fit" : $"{Viewer.Scale * 100:F0}%";

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _awaitingInitialFit = false;
    }

    private void OnFit(object? sender, RoutedEventArgs e) => Viewer.Fit();

    private void OnActualSize(object? sender, RoutedEventArgs e) => Viewer.ActualSize();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>The transport's fullscreen button means "leave" when the viewer is what hosts it.</summary>
    private void OnLeaveFullscreen(object? sender, EventArgs e) => Close();
}
