using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
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

    /// <summary>Must be called before showing: the window sizes itself from these settings.</summary>
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
    /// Fills the screen, one of two ways.
    ///
    /// **Fullscreen** hides the menu bar, but on macOS that means a Space of its own — an animation
    /// and a context switch every time, which is a lot of ceremony for a look at one photo.
    /// **Maximised** stays on the current Space and leaves the menu bar showing. Neither is
    /// obviously right, so it is a setting.
    ///
    /// Two things are needed for real fullscreen and both are easy to get wrong silently: the window
    /// must keep its system decorations (macOS will not take an undecorated window fullscreen), and
    /// it must not be owned by another window (a child window is refused too). In either case the
    /// request is ignored and the window simply stays the size it was.
    /// </summary>
    private void FillScreen()
    {
        // Positioned on the screen the main window is on, so it fills that display rather than
        // wherever it happened to open.
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            Position = screen.Bounds.Position;
        }

        var state = _viewModel?.ViewerOpensFullscreen == true
            ? WindowState.FullScreen
            : WindowState.Maximized;

        // Posted rather than set inline: assigning the state during Opened is ignored, the window
        // having only just been created natively.
        Dispatcher.UIThread.Post(() => WindowState = state, DispatcherPriority.Background);
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

        // Tens of megabytes; the inline preview does not need it and nothing else is holding it.
        Still.Source = null;
        viewModel.DiscardFullPreview();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Preview)
            or nameof(MainWindowViewModel.SelectedItem)
            or nameof(MainWindowViewModel.IsVideoSelected))
        {
            ShowCurrent();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.FullPreview) && _viewModel?.IsVideoSelected == false)
        {
            // The full-size decode has landed; swap it in without disturbing the view.
            ShowStill(_viewModel.FullPreview ?? _viewModel.Preview, isNewItem: false);
            UpdateZoomLabel();
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

        // The full-size render if there is one for this file, otherwise the cached preview, which is
        // already in memory and appears instantly. Preferring the full one is what stops a
        // re-develop flashing back to a lower-quality rendition of the same picture.
        ShowStill(viewModel.FullPreview ?? viewModel.Preview, isNewItem: true);

        _ = viewModel.EnsureFullPreviewAsync();
    }

    /// <summary>
    /// Points the viewer at a bitmap and sizes it from that bitmap's pixels, so 100% means one image
    /// pixel per screen pixel — which is only true once the full-size decode has arrived.
    /// </summary>
    /// <param name="isNewItem">
    /// True when this is a different photograph, which starts fitted. False when it is another
    /// rendering of the same one — a finished develop, or a switch between RAW and embedded JPEG —
    /// where the zoom and position are what the comparison is being made at and must survive.
    /// </param>
    private void ShowStill(Bitmap? image, bool isNewItem)
    {
        Still.Source = image;

        if (image is null)
        {
            return;
        }

        var size = new Size(image.PixelSize.Width, image.PixelSize.Height);
        if (Viewer.NaturalSize == size)
        {
            return;
        }

        var wasFitted = Viewer.IsFitted;
        Viewer.NaturalSize = size;

        // Fit a new picture, or one being looked at whole. Otherwise leave the view alone: the
        // viewer keeps the same region framed across a change of resolution.
        if (isNewItem && (wasFitted || _awaitingInitialFit))
        {
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

            // Backslash flips between the developed RAW and the embedded JPEG, the way Lightroom
            // uses it to compare two renderings. Reported under several names depending on layout.
            case Key.OemBackslash or Key.OemPipe or Key.Oem5:
                _viewModel?.ToggleRawDevelopmentCommand.Execute(null);
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
