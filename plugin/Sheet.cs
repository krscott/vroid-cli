// Lays out item thumbnails in a labelled grid. Has no Unity dependencies so
// tests/protocol can exercise it directly.
namespace VroidBridge;

/// An image of item thumbnails, each above its identifier, in rows of
/// `Columns`.
public sealed class Sheet : Canvas
{
    public const int Columns = 10;
    // Half of the 300x400 thumbnails VRoid 2.16.0 ships.
    public const int ThumbWidth = 150;
    public const int ThumbHeight = 200;
    private const int Gutter = 4;
    private const int Scale = 2;
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 9;
    private const int Advance = (GlyphWidth + 1) * Scale;
    private const int LabelPadding = 3;
    private const int LabelHeight = GlyphHeight * Scale + 2 * LabelPadding;
    private const int CellHeight = ThumbHeight + LabelHeight;
    /// The longest label that fits under a thumbnail.
    public const int MaxLabel = (ThumbWidth - 2 * LabelPadding + Scale) / Advance;
    private const byte Ink = 0x20;
    private static readonly byte[] White = { 0xFF, 0xFF, 0xFF };

    /// Draws one label per thumbnail. Refuses a label the font cannot draw or
    /// that does not fit under its thumbnail.
    public Sheet(IReadOnlyList<string> labels)
        : base(Math.Min(Count(labels), Columns) * (ThumbWidth + Gutter) + Gutter,
            (labels.Count + Columns - 1) / Columns * (CellHeight + Gutter) + Gutter, White)
    {
        for (var index = 0; index < labels.Count; index++) Label(index, labels[index]);
    }

    // The number of labels, once each is known to be drawable.
    private static int Count(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) throw new ArgumentException("A sheet needs at least one item");
        foreach (var label in labels)
            if (label.Length == 0 || label.Length > MaxLabel || label.Any(c => !Font.ContainsKey(c)))
                throw new ArgumentException($"Cannot label item {label}");
        return labels.Count;
    }

    // The top-left corner of a cell, counting rows from the top.
    private static (int X, int Top) Cell(int index) =>
        (Gutter + index % Columns * (ThumbWidth + Gutter), Gutter + index / Columns * (CellHeight + Gutter));

    /// Copies a ThumbWidth x ThumbHeight RGB24 thumbnail, bottom row first,
    /// into cell `index`.
    public void Place(int index, byte[] thumbnail)
    {
        if (thumbnail.Length != ThumbWidth * ThumbHeight * 3)
            throw new ArgumentException($"Thumbnail {index} has {thumbnail.Length} bytes");
        var (x, top) = Cell(index);
        Place(x, top, ThumbWidth, ThumbHeight, thumbnail);
    }

    private void Label(int index, string text)
    {
        var (x, top) = Cell(index);
        var left = x + (ThumbWidth - (text.Length * Advance - Scale)) / 2;
        top += ThumbHeight + LabelPadding;
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = Font[text[i]].Split(' ');
            for (var row = 0; row < glyph.Length; row++)
                for (var column = 0; column < GlyphWidth; column++)
                    if (glyph[row][column] == '#')
                        Dot(left + i * Advance + column * Scale, top + row * Scale);
        }
    }

    private void Dot(int x, int top)
    {
        for (var dy = 0; dy < Scale; dy++)
            Array.Fill(Pixels, Ink, Offset(x, top + dy), Scale * 3);
    }

    /// A 5-column font covering item identifiers' characters: rows of `#` for
    /// ink, top first, separated by spaces. Capitals and digits are 7 rows
    /// high; descenders reach rows 8 and 9.
    public static readonly IReadOnlyDictionary<char, string> Font = new Dictionary<char, string>
    {
        ['0'] = ".###. #...# #..## #.#.# ##..# #...# .###.",
        ['1'] = "..#.. .##.. ..#.. ..#.. ..#.. ..#.. .###.",
        ['2'] = ".###. #...# ....# ...#. ..#.. .#... #####",
        ['3'] = "##### ...#. ..#.. ...#. ....# #...# .###.",
        ['4'] = "...#. ..##. .#.#. #..#. ##### ...#. ...#.",
        ['5'] = "##### #.... ####. ....# ....# #...# .###.",
        ['6'] = "..##. .#... #.... ####. #...# #...# .###.",
        ['7'] = "##### ....# ...#. ..#.. .#... .#... .#...",
        ['8'] = ".###. #...# #...# .###. #...# #...# .###.",
        ['9'] = ".###. #...# #...# .#### ....# ...#. .##..",
        ['A'] = ".###. #...# #...# ##### #...# #...# #...#",
        ['B'] = "####. #...# #...# ####. #...# #...# ####.",
        ['C'] = ".###. #...# #.... #.... #.... #...# .###.",
        ['D'] = "###.. #..#. #...# #...# #...# #..#. ###..",
        ['E'] = "##### #.... #.... ####. #.... #.... #####",
        ['F'] = "##### #.... #.... ####. #.... #.... #....",
        ['G'] = ".###. #...# #.... #.### #...# #...# .####",
        ['H'] = "#...# #...# #...# ##### #...# #...# #...#",
        ['I'] = ".###. ..#.. ..#.. ..#.. ..#.. ..#.. .###.",
        ['J'] = "..### ...#. ...#. ...#. ...#. #..#. .##..",
        ['K'] = "#...# #..#. #.#.. ##... #.#.. #..#. #...#",
        ['L'] = "#.... #.... #.... #.... #.... #.... #####",
        ['M'] = "#...# ##.## #.#.# #.#.# #...# #...# #...#",
        ['N'] = "#...# #...# ##..# #.#.# #..## #...# #...#",
        ['O'] = ".###. #...# #...# #...# #...# #...# .###.",
        ['P'] = "####. #...# #...# ####. #.... #.... #....",
        ['Q'] = ".###. #...# #...# #...# #.#.# #..#. .##.#",
        ['R'] = "####. #...# #...# ####. #.#.. #..#. #...#",
        ['S'] = ".#### #.... #.... .###. ....# ....# ####.",
        ['T'] = "##### ..#.. ..#.. ..#.. ..#.. ..#.. ..#..",
        ['U'] = "#...# #...# #...# #...# #...# #...# .###.",
        ['V'] = "#...# #...# #...# #...# #...# .#.#. ..#..",
        ['W'] = "#...# #...# #...# #.#.# #.#.# #.#.# .#.#.",
        ['X'] = "#...# #...# .#.#. ..#.. .#.#. #...# #...#",
        ['Y'] = "#...# #...# .#.#. ..#.. ..#.. ..#.. ..#..",
        ['Z'] = "##### ....# ...#. ..#.. .#... #.... #####",
        ['a'] = "..... ..... .###. ....# .#### #...# .####",
        ['b'] = "#.... #.... #.##. ##..# #...# #...# ####.",
        ['c'] = "..... ..... .###. #.... #.... #...# .###.",
        ['d'] = "....# ....# .##.# #..## #...# #...# .####",
        ['e'] = "..... ..... .###. #...# ##### #.... .###.",
        ['f'] = "..##. .#..# .#... ###.. .#... .#... .#...",
        ['g'] = "..... ..... .#### #...# #...# #...# .#### ....# .###.",
        ['h'] = "#.... #.... #.##. ##..# #...# #...# #...#",
        ['i'] = "..#.. ..... .##.. ..#.. ..#.. ..#.. .###.",
        ['j'] = "...#. ..... ..##. ...#. ...#. ...#. ...#. #..#. .##..",
        ['k'] = "#.... #.... #..#. #.#.. ##... #.#.. #..#.",
        ['l'] = ".##.. ..#.. ..#.. ..#.. ..#.. ..#.. .###.",
        ['m'] = "..... ..... ##.#. #.#.# #.#.# #.#.# #.#.#",
        ['n'] = "..... ..... #.##. ##..# #...# #...# #...#",
        ['o'] = "..... ..... .###. #...# #...# #...# .###.",
        ['p'] = "..... ..... ####. #...# #...# #...# ####. #.... #....",
        ['q'] = "..... ..... .#### #...# #...# #...# .#### ....# ....#",
        ['r'] = "..... ..... #.##. ##..# #.... #.... #....",
        ['s'] = "..... ..... .#### #.... .###. ....# ####.",
        ['t'] = ".#... .#... ###.. .#... .#... .#..# ..##.",
        ['u'] = "..... ..... #...# #...# #...# #..## .##.#",
        ['v'] = "..... ..... #...# #...# #...# .#.#. ..#..",
        ['w'] = "..... ..... #...# #...# #.#.# #.#.# .#.#.",
        ['x'] = "..... ..... #...# .#.#. ..#.. .#.#. #...#",
        ['y'] = "..... ..... #...# #...# #...# #...# .#### ....# .###.",
        ['z'] = "..... ..... ##### ...#. ..#.. .#... #####",
        ['_'] = "..... ..... ..... ..... ..... ..... ..... #####",
    };
}
