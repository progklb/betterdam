using BetterDAM.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterDAM.UI.ViewModels;

/// <summary>
/// One editable row of the label library.
///
/// The name is the part that matters: it is what goes into the file and what another application
/// reads. The colour never leaves this machine.
/// </summary>
public sealed partial class LabelRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public LabelRowViewModel(string name, string colour, Action changed)
    {
        _name = name;
        _colour = colour;
        _changed = changed;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _colour;

    partial void OnNameChanged(string value) => _changed();

    partial void OnColourChanged(string value) => _changed();

    /// <summary>
    /// The presets offered, so a colour can be chosen without typing a hex value.
    ///
    /// The neutral grey is <see cref="LabelColours.Unrecognised"/> rather than a shade of its own.
    /// A new row and an imported label both start on it, and if it were not in this list their
    /// dropdown would open with nothing selected — the control saying it does not recognise a colour
    /// the application had just chosen for it.
    /// </summary>
    public static IReadOnlyList<string> Swatches { get; } =
    [
        "#E8574A", "#E8934A", "#E8C84A", "#6ABF52",
        "#4AA3E8", "#B77BD8", LabelColours.Unrecognised, "#D8D8D8"
    ];
}
