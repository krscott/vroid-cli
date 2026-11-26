// Colour values and shared-colour rules. Has no Unity dependencies so
// tests/protocol can exercise them directly.
namespace VroidBridge;

/// A colour as VRoid's editor shows it, with its current `#RRGGBB` value.
/// `Keys` are the character's values a write to it changes.
public sealed record ShownColor(string Path, IReadOnlySet<string> Keys, string Hex);

public static class Colors
{
    /// `text` as uppercase `#RRGGBB`, or null if it is not one.
    /// VRoid's own `TryParseColorHex` throws on bad input, so it is not used.
    public static string? Normalize(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit)) return null;
        return text.ToUpperInvariant();
    }

    /// The red, green, and blue channels to write to a colour that reads
    /// `current` so that it reads `wanted`, both normalized, or null if it
    /// already does. The channels are exactly k/255 on the editor's sRGB
    /// scale, with no linear conversion. Skipping an equal colour keeps a
    /// stored value that is off the 1/255 grid exactly as it is.
    public static float[]? Write(string current, string wanted) => current != wanted
        ? new[] { 1, 3, 5 }.Select(index => Convert.ToByte(wanted.Substring(index, 2), 16) / 255f).ToArray()
        : null;

    /// The colours a dump lists, in the given order, which is VRoid's.
    /// Colours with equal keys are one shared parameter, listed under the
    /// first. Throws if a shared copy differs, or if two listed colours
    /// overlap, since apply would refuse the template.
    public static List<ShownColor> Listed(IReadOnlyList<ShownColor> colors)
    {
        var listed = new List<ShownColor>();
        foreach (var color in colors)
        {
            var first = listed.FirstOrDefault(other => other.Keys.SetEquals(color.Keys));
            if (first == null) listed.Add(color);
            else if (first.Hex != color.Hex)
                throw new InvalidOperationException(
                    $"{color.Path} is {color.Hex} but {first.Path} is {first.Hex}; they are not one shared colour");
        }
        foreach (var (a, b) in Overlapping(listed))
            throw new InvalidOperationException($"{a.Path} and {b.Path} write some of the same colours; a template cannot hold both");
        return listed;
    }

    /// One message for each pair of colours that write a common value; setting
    /// both in one request would keep whichever came last.
    public static List<string> Conflicts(IReadOnlyList<ShownColor> requested) =>
        Overlapping(requested).Select(pair => $"{pair.A.Path} and {pair.B.Path} set the same colour; set it in one place").ToList();

    private static IEnumerable<(ShownColor A, ShownColor B)> Overlapping(IReadOnlyList<ShownColor> colors)
    {
        for (var i = 0; i < colors.Count; i++)
            for (var j = i + 1; j < colors.Count; j++)
                if (colors[i].Keys.Overlaps(colors[j].Keys))
                    yield return (colors[i], colors[j]);
    }
}
