using BetterDAM.Core.Models;

namespace BetterDAM.Core.Services;

/// <summary>
/// Adds the labels a workspace's photographs already carry to the label library.
///
/// The more useful of the two imports, because <c>xmp:Label</c> stores a word and not a colour: a
/// workspace labelled in Lightroom arrives carrying "Yellow" and "Green" while the library still
/// holds Bridge's "Select" and "Second". Those labels are drawn on tiles and found by the search
/// already, but until they are in the library there is no swatch to filter by and no way to put one
/// on another photograph.
/// </summary>
public static class LabelImport
{
    /// <summary>
    /// <paramref name="existing"/> with any of <paramref name="found"/> it does not already define
    /// appended to the end.
    ///
    /// <para><b>Appended, never reordered or inserted.</b> A label's position in the library is its
    /// slot, and the slot is what digiKam's ColorLabel and Photo Mechanic's ColorClass write as a
    /// number. Putting an imported label above an existing one would silently change what those
    /// numbers mean for every file already labelled.</para>
    ///
    /// <para>Incoming order is kept, so a caller passing the catalog's most-used-first order gives
    /// the commonest label the earliest free slot.</para>
    /// </summary>
    public static LabelLibrary Merge(LabelLibrary existing, IEnumerable<string> found)
    {
        var added = new List<LabelDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in found)
        {
            var name = raw?.Trim() ?? string.Empty;

            // A blank label is indistinguishable from no label at all, so there is nothing to add.
            if (name.Length == 0 || existing.Find(name) is not null || !seen.Add(name))
            {
                continue;
            }

            // Coloured by the word where it names one, so a Lightroom "Yellow" arrives yellow. Grey
            // otherwise: a starting point to change rather than an answer.
            added.Add(new LabelDefinition(name, LabelColours.Resolve(null, name) ?? LabelColours.Unrecognised));
        }

        return added.Count == 0
            ? existing
            : existing with { Labels = [.. existing.Labels, .. added] };
    }
}
