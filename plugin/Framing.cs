// Camera placement for `render`, without Unity dependencies so tests/protocol
// can check it. Coordinates are Unity world space: left-handed, Y up.
using System.Globalization;

namespace VroidBridge;

public readonly record struct Vec(double X, double Y, double Z)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k, a.Z * k);
}

/// World bounds of one mesh.
public readonly record struct Box(Vec Min, Vec Max)
{
    public Vec Center => (Min + Max) * 0.5;

    public static Box Union(IEnumerable<Box> boxes) => boxes.Aggregate((a, b) => new Box(
        new Vec(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
        new Vec(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z))));
}

public enum View { Front, ThreeQuarter, Side, Back }

public enum Frame { Body, Head }

/// An orthographic camera at `Position` looking horizontally at `Target`,
/// showing `Size` world units above and below the target.
public sealed record Shot(Vec Position, Vec Target, double Size, double Near, double Far);

/// What a frame must show: everything from `Below` under to `Above` over
/// `Anchor`, within `Reach` of the vertical line through it in any direction.
/// The anchor stays at the same place in the image at every scale.
public readonly record struct Extent(Vec Anchor, double Above, double Below, double Reach);

public static class Framing
{
    /// Space around the framed region, as a factor of its size.
    public const double Margin = 1.05;

    public static View ParseView(string name) => name switch
    {
        "front" => View.Front,
        "three_quarter" => View.ThreeQuarter,
        "side" => View.Side,
        "back" => View.Back,
        _ => throw new ArgumentException($"Unknown view: {name}"),
    };

    public static Frame ParseFrame(string name) => name switch
    {
        "body" => Frame.Body,
        "head" => Frame.Head,
        _ => throw new ArgumentException($"Unknown frame: {name}"),
    };

    /// Unit vector from the model towards the camera. Three-quarter and side
    /// views show the model's left side, as turnaround sheets usually do.
    public static Vec Direction(Vec forward, View view)
    {
        var length = Math.Sqrt(forward.X * forward.X + forward.Z * forward.Z);
        if (length < 1e-6) throw new InvalidOperationException("The character does not face a horizontal direction");
        var degrees = view switch
        {
            View.Front => 0.0,
            View.ThreeQuarter => -45.0,
            View.Side => -90.0,
            View.Back => 180.0,
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        };
        // Turning the forward vector by a negative angle about +Y moves it to the
        // model's left (-X when facing +Z).
        var angle = Math.Atan2(forward.X, forward.Z) + degrees * Math.PI / 180;
        return new Vec(Math.Sin(angle), 0, Math.Cos(angle));
    }

    /// The whole model, anchored at the floor below `origin`, the model's root,
    /// and centred on it. Width comes from the horizontal reach in any
    /// direction, so every body view of one model at one image size shares its
    /// scale and ground line.
    public static Extent Body(IReadOnlyList<Box> parts, Vec origin)
    {
        var all = Box.Union(parts);
        return new Extent(origin, all.Max.Y - origin.Y, Math.Max(0, origin.Y - all.Min.Y), Reach(parts, origin));
    }

    /// The model from the neck to its top, anchored at the neck and centred on
    /// the head. Width comes from the horizontal reach from the head, in any
    /// direction, of the parts centred above the neck, such as the head, ears,
    /// hair, and hats, so every head view of one model at one image size shares
    /// its scale. A part centred lower, such as the body with its arms or long
    /// hair, does not widen the frame, and the frame cuts through what lies
    /// below the neck.
    public static Extent Head(IReadOnlyList<Box> parts, Vec neck, Vec head)
    {
        var above = parts.Where(part => part.Center.Y > neck.Y).ToArray();
        if (above.Length == 0) throw new InvalidOperationException("The character has no meshes above its neck");
        return new Extent(new Vec(head.X, neck.Y, head.Z), Box.Union(parts).Max.Y - neck.Y, 0, Reach(above, head));
    }

    // The farthest horizontal distance of any part's bounds from `from`.
    private static double Reach(IEnumerable<Box> parts, Vec from) => parts.Max(part =>
    {
        var x = Math.Max(Math.Abs(part.Min.X - from.X), Math.Abs(part.Max.X - from.X));
        var z = Math.Max(Math.Abs(part.Min.Z - from.Z), Math.Abs(part.Max.Z - from.Z));
        return Math.Sqrt(x * x + z * z);
    });

    /// The smallest `Shot.Size` that shows `extent` with its margin. The anchor
    /// sits at a fixed height in the image, so the margin below it is
    /// `Size * (1 - 1 / Margin)` and the frame shows `2 * Size / Margin` above it.
    public static double Fit(Extent extent, double aspect) => Margin * Math.Max(
        Math.Max(extent.Above / 2, extent.Reach / aspect),
        extent.Below / (Margin - 1));

    /// The `Shot.Size` for an image `height` pixels tall: the fitted one, or
    /// the one at `scale` pixels per metre, which must show `extent`.
    public static double Size(Extent extent, double aspect, int height, double? scale)
    {
        var fit = Fit(extent, aspect);
        if (scale is not double wanted) return fit;
        var size = height / (2 * wanted);
        // Tolerates rounding when a reported scale is passed back.
        if (size < fit * (1 - 1e-9))
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"At scale {wanted} the character does not fit in the image; the largest scale that fits is {Math.Floor(Scale(fit, height) * 1000) / 1000}"));
        return size;
    }

    /// Pixels per metre of a `Shot.Size` in an image `height` pixels tall.
    public static double Scale(double size, int height) => height / (2 * size);

    /// Places the camera for `size`, with the anchor a fixed fraction of the
    /// image height above its bottom edge and on its vertical centre line, so
    /// renders at one scale and image size line up. The camera stands outside
    /// `all`, the bounds of every part, so nothing is clipped.
    public static Shot Place(Extent extent, double size, Box all, Vec forward, View view)
    {
        var target = extent.Anchor + new Vec(0, size / Margin, 0);
        var offset = target - all.Center;
        var extentOfAll = all.Max - all.Min;
        var reach = Math.Sqrt(extentOfAll.X * extentOfAll.X + extentOfAll.Y * extentOfAll.Y + extentOfAll.Z * extentOfAll.Z)
            + Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z) + 1;
        return new Shot(target + Direction(forward, view) * reach, target, size, 0.01, 2 * reach);
    }
}
