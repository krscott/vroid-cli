// Camera placement for `render`, without Unity dependencies so tests/protocol
// can check it. Coordinates are Unity world space: left-handed, Y up.
namespace VroidBridge;

public readonly record struct Vec(double X, double Y, double Z)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k, a.Z * k);
}

public enum View { Front, ThreeQuarter, Side, Back }

public enum Frame { Body, Head }

/// An orthographic camera at `Position` looking horizontally at `Target`,
/// showing `Size` world units above and below the target.
public sealed record Shot(Vec Position, Vec Target, double Size, double Near, double Far);

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

    /// Frames the whole model. Width comes from the bounds' horizontal radius
    /// rather than the width seen from this view, so every view of one model
    /// shares its scale and ground line.
    public static Shot Body(Vec min, Vec max, Vec forward, View view, double aspect)
    {
        var center = (min + max) * 0.5;
        var half = (max - min) * 0.5;
        var radius = Math.Sqrt(half.X * half.X + half.Z * half.Z);
        return Place(center, Math.Max(half.Y, radius / aspect) * Margin, min, max, forward, view);
    }

    /// Frames a square from the neck to the top of the model, centred on the
    /// head, so ears, hair, and hats above the head stay in view.
    public static Shot Head(Vec min, Vec max, Vec neck, Vec head, Vec forward, View view, double aspect)
    {
        if (max.Y <= neck.Y) throw new InvalidOperationException("The character's neck is above its top");
        var half = (max.Y - neck.Y) / 2;
        var target = new Vec(head.X, neck.Y + half, head.Z);
        return Place(target, Math.Max(half, half / aspect) * Margin, min, max, forward, view);
    }

    // Puts the camera outside the bounds so nothing of the model is clipped.
    private static Shot Place(Vec target, double size, Vec min, Vec max, Vec forward, View view)
    {
        var extent = max - min;
        var reach = Math.Sqrt(extent.X * extent.X + extent.Y * extent.Y + extent.Z * extent.Z) + 1;
        return new Shot(target + Direction(forward, view) * reach, target, size, 0.01, 2 * reach);
    }
}
