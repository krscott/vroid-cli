// Assembles images for `render` and `thumbnails`. Has no Unity dependencies so
// tests/protocol can check layout and blending directly.
namespace VroidBridge;

/// An RGB24 image. `Pixels` runs bottom row first, as Unity's raw texture data
/// does; positions count rows from the top.
public class Canvas
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// Fills the image with `background`, one RGB24 pixel.
    public Canvas(int width, int height, ReadOnlySpan<byte> background)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"Cannot make a {width}x{height} image");
        if (background.Length != 3) throw new ArgumentException("The background is not one RGB pixel");
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 3];
        for (var at = 0; at < Pixels.Length; at += 3) background.CopyTo(Pixels.AsSpan(at));
    }

    protected int Offset(int x, int top) => ((Height - 1 - top) * Width + x) * 3;

    private void Inside(int x, int top, int width, int height)
    {
        if (x < 0 || top < 0 || width <= 0 || height <= 0 || x + width > Width || top + height > Height)
            throw new ArgumentException($"A {width}x{height} image at {x},{top} does not fit in {Width}x{Height}");
    }

    /// Copies a `width` x `height` RGB24 image, bottom row first, with its
    /// top-left corner at `x`, `top`.
    public void Place(int x, int top, int width, int height, byte[] image)
    {
        Inside(x, top, width, height);
        if (image.Length != width * height * 3) throw new ArgumentException($"A {width}x{height} image has {image.Length} bytes");
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(image, row * width * 3, Pixels, Offset(x, top + height - 1 - row), width * 3);
    }

    /// The largest size of a `width` x `height` image that fits a `boxWidth` x
    /// `boxHeight` box without changing its aspect ratio.
    public static (int Width, int Height) FitSize(int boxWidth, int boxHeight, int width, int height)
    {
        if (boxWidth <= 0 || boxHeight <= 0 || width <= 0 || height <= 0)
            throw new ArgumentException($"Cannot fit a {width}x{height} image in {boxWidth}x{boxHeight}");
        var scale = Math.Min((double)boxWidth / width, (double)boxHeight / height);
        return (Math.Clamp((int)Math.Round(width * scale), 1, boxWidth), Math.Clamp((int)Math.Round(height * scale), 1, boxHeight));
    }
}
