using System.Diagnostics;

namespace BetterDAM.UI.Services;

/// <summary>
/// Shows a file in the platform's file manager, selected rather than opened — the point is to get to
/// it on disk, not to launch it in another application.
/// </summary>
public static class RevealInFileManager
{
    /// <summary>Named after whatever the platform calls its file manager, so the menu reads right.</summary>
    public static string MenuHeader { get; } = OperatingSystem.IsMacOS()
        ? "Reveal in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in File Manager";

    /// <summary>
    /// The command that selects <paramref name="path"/> in the platform's file manager. Separated
    /// from launching it so the platform quirks can be tested without starting processes.
    /// </summary>
    internal static (string Command, string[] Arguments) BuildCommand(string path, PlatformKind platform)
        => platform switch
        {
            PlatformKind.MacOS => ("open", ["-R", path]),

            // No space after the comma: explorer reads "/select, path" as two arguments and opens
            // Documents instead of selecting anything.
            PlatformKind.Windows => ("explorer.exe", [$"/select,{path}"]),

            // Linux file managers vary too much to rely on a select flag, so open the folder.
            _ => ("xdg-open", [Path.GetDirectoryName(path) ?? path])
        };

    internal enum PlatformKind
    {
        MacOS,
        Windows,
        Other
    }

    private static PlatformKind Current => OperatingSystem.IsMacOS()
        ? PlatformKind.MacOS
        : OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Other;

    public static void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var (command, arguments) = BuildCommand(path, Current);

        var startInfo = new ProcessStartInfo { FileName = command, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
    }
}
