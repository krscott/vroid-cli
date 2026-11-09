// Colour values and shared-colour rules. Has no Unity dependencies so
// tests/protocol can exercise them directly.
namespace VroidBridge;

/// A colour field as VRoid's editor shows it, with its current `#RRGGBB`
/// value. `Keys` are the model values a write to the field changes.
public sealed record ColorField(string Path, IReadOnlySet<string> Keys, string Hex);

public static class Colors
{
    /// `text` as uppercase `#RRGGBB`, or null if it is not one.
    public static string? Normalize(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit)) return null;
        return text.ToUpperInvariant();
    }

    /// The red, green, and blue channels of a normalized colour as VRoid stores
    /// them: exactly k/255 on the editor's sRGB scale, with no linear conversion.
    public static float[] Channels(string hex) =>
        new[] { 1, 3, 5 }.Select(index => Convert.ToByte(hex.Substring(index, 2), 16) / 255f).ToArray();

    /// Whether setting `wanted` changes a field that reads `current`. A colour
    /// that already reads as `wanted` is never written, so a stored value off
    /// the 1/255 grid keeps its exact channels.
    public static bool Differs(string current, string wanted) =>
        !string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase);

    /// The fields a dump lists, in the given (app) order. Fields with equal
    /// keys show one shared colour, listed under the first; a field whose keys
    /// strictly contain another field's, such as "Both Eyes", writes several
    /// colours at once and is not listed. Throws if a shared copy differs.
    public static List<ColorField> Listed(IReadOnlyList<ColorField> fields)
    {
        var listed = new List<ColorField>();
        foreach (var field in fields)
        {
            if (fields.Any(other => other.Keys.IsProperSubsetOf(field.Keys))) continue;
            var first = listed.FirstOrDefault(other => other.Keys.SetEquals(field.Keys));
            if (first == null) listed.Add(field);
            else if (Differs(first.Hex, field.Hex))
                throw new InvalidOperationException(
                    $"{field.Path} is {field.Hex} but {first.Path} is {first.Hex}; they are not one shared colour");
        }
        return listed;
    }

    /// One message for each pair of fields that write a common value; setting
    /// both in one request would keep whichever came last.
    public static List<string> Conflicts(IReadOnlyList<ColorField> requested)
    {
        var conflicts = new List<string>();
        for (var i = 0; i < requested.Count; i++)
            for (var j = i + 1; j < requested.Count; j++)
                if (requested[i].Keys.Overlaps(requested[j].Keys))
                    conflicts.Add($"{requested[i].Path} and {requested[j].Path} set the same colour; set it in one place");
        return conflicts;
    }
}
