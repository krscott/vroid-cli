// Unity texture helpers shared by `render` and `thumbnails`. Like Render.cs,
// instance members of Unity engine objects are called through `dynamic`;
// static members go through Reflect.
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidBridge;

internal static class Textures
{
    internal static dynamic Format(string name) => Enum.Parse(Reflect.Type("UnityEngine.TextureFormat"), name);

    internal static void Destroy(object? created)
    {
        if (created != null) Reflect.CallStatic("UnityEngine.Object", "DestroyImmediate", created);
    }

    /// Scales `texture` to `width` x `height` with its own filtering and
    /// returns its pixels in `format`, RGB24 or RGBA32, bottom row first.
    internal static byte[] Read(object texture, int width, int height, string format)
    {
        var active = Reflect.StaticProperty("UnityEngine.RenderTexture", "active");
        var previous = active.GetValue(null);
        dynamic? target = null, readback = null;
        try
        {
            target = Reflect.New("UnityEngine.RenderTexture", width, height, 0);
            Reflect.Type("UnityEngine.Graphics")
                .GetMethod("Blit", new[] { Reflect.Type("UnityEngine.Texture"), Reflect.Type("UnityEngine.RenderTexture") })!
                .Invoke(null, new object[] { texture, (object)target });
            active.SetValue(null, (object)target);
            readback = Reflect.New("UnityEngine.Texture2D", width, height, Format(format), false);
            readback.ReadPixels((dynamic)Reflect.New("UnityEngine.Rect", 0f, 0f, (float)width, (float)height), 0, 0);
            return ((IEnumerable<byte>)readback.GetRawTextureData()).ToArray();
        }
        finally
        {
            // Blit makes its destination the active render texture.
            try
            {
                active.SetValue(null, previous);
            }
            finally
            {
                Destroy(target);
                Destroy(readback);
            }
        }
    }

    /// Encodes `canvas` as PNG.
    internal static byte[] Encode(Canvas canvas)
    {
        dynamic? texture = null;
        try
        {
            texture = Reflect.New("UnityEngine.Texture2D", canvas.Width, canvas.Height, Format("RGB24"), false);
            texture.LoadRawTextureData(new Il2CppStructArray<byte>(canvas.Pixels));
            var png = Reflect.CallStatic("UnityEngine.ImageConversion", "EncodeToPNG", (object)texture)
                ?? throw new InvalidOperationException("Unity could not encode the image");
            return ((IEnumerable<byte>)png).ToArray();
        }
        finally
        {
            Destroy(texture);
        }
    }
}
