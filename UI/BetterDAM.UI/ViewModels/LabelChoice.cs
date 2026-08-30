namespace BetterDAM.UI.ViewModels;

/// <summary>
/// One entry in the metadata panel's label dropdown.
/// </summary>
/// <param name="Name">The word written to the file, or empty for "No label".</param>
/// <param name="Colour">How it is drawn, or null when there is nothing to draw.</param>
public sealed record LabelChoice(string Name, string? Colour)
{
    public static readonly LabelChoice None = new(string.Empty, null);

    /// <summary>
    /// A label the file carries that the library does not define — set in another application, or
    /// before the library was edited. Offered so it can be seen and kept rather than replaced.
    /// </summary>
    public static LabelChoice Unknown(string name) => new(name, null);

    public bool IsNone => string.IsNullOrEmpty(Name);

    public string Display => IsNone ? "No label" : Name;

    public bool HasColour => Colour is not null;
}
