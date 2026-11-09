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

    /// Whether two normalized or lowercase colours differ.
    public static bool Differs(string a, string b) => !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// The red, green, and blue channels to write to a field that reads
    /// `current` so that it reads `wanted`, or null if it already does. The
    /// channels are exactly k/255 on the editor's sRGB scale, with no linear
    /// conversion. Skipping an equal colour keeps a stored value that is off
    /// the 1/255 grid exactly as it is.
    public static float[]? Write(string current, string wanted) => Differs(current, wanted)
        ? new[] { 1, 3, 5 }.Select(index => Convert.ToByte(wanted.Substring(index, 2), 16) / 255f).ToArray()
        : null;

    /// The fields a dump lists, in the given (app) order. Fields with equal
    /// keys show one shared colour, listed under the first. Throws if a shared
    /// copy differs, or if two listed fields overlap, since apply would refuse
    /// the template.
    public static List<ColorField> Listed(IReadOnlyList<ColorField> fields)
    {
        var listed = new List<ColorField>();
        foreach (var field in fields)
        {
            var first = listed.FirstOrDefault(other => other.Keys.SetEquals(field.Keys));
            if (first == null) listed.Add(field);
            else if (Differs(first.Hex, field.Hex))
                throw new InvalidOperationException(
                    $"{field.Path} is {field.Hex} but {first.Path} is {first.Hex}; they are not one shared colour");
        }
        foreach (var (a, b) in Overlapping(listed))
            throw new InvalidOperationException($"{a.Path} and {b.Path} write some of the same colours; a template cannot hold both");
        return listed;
    }

    /// One message for each pair of fields that write a common value; setting
    /// both in one request would keep whichever came last.
    public static List<string> Conflicts(IReadOnlyList<ColorField> requested) =>
        Overlapping(requested).Select(pair => $"{pair.A.Path} and {pair.B.Path} set the same colour; set it in one place").ToList();

    private static IEnumerable<(ColorField A, ColorField B)> Overlapping(IReadOnlyList<ColorField> fields)
    {
        for (var i = 0; i < fields.Count; i++)
            for (var j = i + 1; j < fields.Count; j++)
                if (fields[i].Keys.Overlaps(fields[j].Keys))
                    yield return (fields[i], fields[j]);
    }
}
