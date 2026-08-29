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

    /// <summary>The presets offered, so a colour can be chosen without typing a hex value.</summary>
    public static IReadOnlyList<string> Swatches { get; } =
    [
        "#E8574A", "#E8934A", "#E8C84A", "#6ABF52",
        "#4AA3E8", "#B77BD8", "#8A8A8A", "#D8D8D8"
    ];
}
