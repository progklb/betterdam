using Avalonia.Input;

namespace BetterDAM.UI.Services;

/// <summary>
/// The menu differences between macOS and everywhere else, in one place so the XAML can state them
/// declaratively.
///
/// macOS owns the menu bar: it is drawn by the system at the top of the screen, Settings belongs
/// under the application menu rather than File, and the modifier is Command. Windows and Linux draw
/// the menu inside the window, have no application menu to put Settings in, and use Control.
/// </summary>
public static class MenuConventions
{
    public static bool IsMac { get; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// The Open Recent header, shared between the XAML that declares the item and the code that
    /// fills it in. NativeMenuItem gets no generated field from x:Name, so the item has to be found
    /// by header — and a literal in both places would drift the first time one was reworded.
    /// </summary>
    public const string OpenRecentHeader = "Open Recent";

    /// <summary>
    /// True everywhere except macOS, where Settings already appears in the application menu and
    /// listing it again under File would just be a duplicate.
    /// </summary>
    public static bool ShowSettingsInFileMenu { get; } = !IsMac;

    public static KeyGesture OpenFolder { get; } = KeyGesture.Parse(IsMac ? "Cmd+O" : "Ctrl+O");

    /// <summary>Command-comma is the standard macOS shortcut for preferences.</summary>
    public static KeyGesture Settings { get; } = KeyGesture.Parse(IsMac ? "Cmd+," : "Ctrl+,");

    /// <summary>Matches VS Code, where Shift makes it "close the workspace, not the window".</summary>
    public static KeyGesture CloseWorkspace { get; } =
        KeyGesture.Parse(IsMac ? "Cmd+Shift+W" : "Ctrl+Shift+W");
}
