// Assembles images for `render` and `thumbnails`. Has no Unity dependencies so
// tests/protocol can check layout, scaling, and blending directly.
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

    /// Scales a `width` x `height` RGBA32 image, bottom row first, to fit the
    /// box at `x`, `top` without changing its aspect ratio, centres it there,
    /// and blends it over the canvas by its alpha.
    public void Fit(int x, int top, int boxWidth, int boxHeight, int width, int height, byte[] image)
    {
        Inside(x, top, boxWidth, boxHeight);
        if (width <= 0 || height <= 0 || image.Length != width * height * 4)
            throw new ArgumentException($"A {width}x{height} image has {image.Length} bytes");
        var scale = Math.Min((double)boxWidth / width, (double)boxHeight / height);
        var fitWidth = Math.Clamp((int)Math.Round(width * scale), 1, boxWidth);
        var fitHeight = Math.Clamp((int)Math.Round(height * scale), 1, boxHeight);
        // Premultiplied, so transparent pixels' colours do not bleed into their
        // neighbours.
        var source = new float[image.Length];
        for (var at = 0; at < image.Length; at += 4)
        {
            var alpha = image[at + 3] / 255f;
            source[at] = image[at] * alpha;
            source[at + 1] = image[at + 1] * alpha;
            source[at + 2] = image[at + 2] * alpha;
            source[at + 3] = alpha;
        }
        // Each pass resamples rows and transposes, so two give fitHeight rows
        // of fitWidth pixels, bottom row first.
        var scaled = ResampleRows(ResampleRows(source, height, width, fitWidth), fitWidth, height, fitHeight);
        var left = x + (boxWidth - fitWidth) / 2;
        var bottom = top + (boxHeight - fitHeight) / 2 + fitHeight - 1;
        for (var row = 0; row < fitHeight; row++)
        {
            var at = Offset(left, bottom - row);
            for (var column = 0; column < fitWidth; column++, at += 3)
            {
                var from = (row * fitWidth + column) * 4;
                var keep = 1 - scaled[from + 3];
                for (var channel = 0; channel < 3; channel++)
                    Pixels[at + channel] = (byte)Math.Clamp(Math.Round(scaled[from + channel] + Pixels[at + channel] * keep), 0, 255);
            }
        }
    }

    // Resamples each of `rows` rows of `from` RGBA samples to `to` samples and
    // returns the result transposed: `to` rows of `rows` samples.
    private static float[] ResampleRows(float[] data, int rows, int from, int to)
    {
        var output = new float[to * rows * 4];
        var weights = Weights(from, to);
        for (var row = 0; row < rows; row++)
            for (var sample = 0; sample < to; sample++)
            {
                var (first, kernel) = weights[sample];
                float r = 0, g = 0, b = 0, a = 0;
                for (int i = 0, at = (row * from + first) * 4; i < kernel.Length; i++, at += 4)
                {
                    r += kernel[i] * data[at];
                    g += kernel[i] * data[at + 1];
                    b += kernel[i] * data[at + 2];
                    a += kernel[i] * data[at + 3];
                }
                var into = (sample * rows + row) * 4;
                (output[into], output[into + 1], output[into + 2], output[into + 3]) = (r, g, b, a);
            }
        return output;
    }

    // A triangle filter that widens when shrinking, so every source pixel
    // contributes: the first source sample and normalized weights per output.
    private static (int First, float[] Kernel)[] Weights(int from, int to)
    {
        var step = (double)from / to;
        var support = Math.Max(1, step);
        var weights = new (int, float[])[to];
        for (var sample = 0; sample < to; sample++)
        {
            var centre = (sample + 0.5) * step;
            var first = Math.Max(0, (int)Math.Floor(centre - support));
            var last = Math.Min(from - 1, (int)Math.Ceiling(centre + support));
            var raw = new double[last - first + 1];
            for (var i = first; i <= last; i++) raw[i - first] = Math.Max(0, 1 - Math.Abs(i + 0.5 - centre) / support);
            var total = raw.Sum();
            weights[sample] = (first, raw.Select(weight => (float)(weight / total)).ToArray());
        }
        return weights;
    }
}
