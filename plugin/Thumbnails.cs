// Draws a category's built-in item thumbnails into one sheet. VRoid's own
// previews are prebaked textures, so nothing about the model changes.
//
// Like Render.cs, instance members of Unity engine objects are called through
// `dynamic`; static members go through Reflect.
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidBridge;

/// Scales thumbnails into Sheet cells. Dispose destroys what it created.
internal sealed class ThumbnailReader : IDisposable
{
    private readonly dynamic target;
    private readonly dynamic readback;

    internal ThumbnailReader()
    {
        target = Reflect.New("UnityEngine.RenderTexture", Sheet.ThumbWidth, Sheet.ThumbHeight, 0);
        try
        {
            readback = Reflect.New("UnityEngine.Texture2D", Sheet.ThumbWidth, Sheet.ThumbHeight, RGB24, false);
        }
        catch
        {
            Destroy(target);
            throw;
        }
    }

    private static dynamic RGB24 => Enum.Parse(Reflect.Type("UnityEngine.TextureFormat"), "RGB24");

    private static void Destroy(object created) => Reflect.CallStatic("UnityEngine.Object", "DestroyImmediate", created);

    /// Returns `texture` as Sheet.Place expects it: halved with bilinear
    /// filtering, which averages each 2x2 block of a 300x400 preview.
    internal byte[] Read(object texture)
    {
        dynamic source = Reflect.Concrete(texture);
        source.filterMode = (dynamic)Enum.Parse(Reflect.Type("UnityEngine.FilterMode"), "Bilinear");
        Reflect.Type("UnityEngine.Graphics")
            .GetMethod("Blit", new[] { Reflect.Type("UnityEngine.Texture"), Reflect.Type("UnityEngine.RenderTexture") })!
            .Invoke(null, new object[] { (object)source, (object)target });
        var active = Reflect.StaticProperty("UnityEngine.RenderTexture", "active");
        var previous = active.GetValue(null);
        try
        {
            active.SetValue(null, (object)target);
            readback.ReadPixels((dynamic)Reflect.New("UnityEngine.Rect", 0f, 0f, (float)Sheet.ThumbWidth, (float)Sheet.ThumbHeight), 0, 0);
        }
        finally
        {
            active.SetValue(null, previous);
        }
        return ((IEnumerable<byte>)readback.GetRawTextureData()).ToArray();
    }

    internal static byte[] Png(Sheet sheet)
    {
        dynamic? texture = null;
        try
        {
            texture = Reflect.New("UnityEngine.Texture2D", sheet.Width, sheet.Height, RGB24, false);
            texture.LoadRawTextureData(new Il2CppStructArray<byte>(sheet.Pixels));
            var png = Reflect.CallStatic("UnityEngine.ImageConversion", "EncodeToPNG", (object)texture)
                ?? throw new InvalidOperationException("Unity could not encode the image");
            return ((IEnumerable<byte>)png).ToArray();
        }
        finally
        {
            if (texture != null) Destroy(texture);
        }
    }

    public void Dispose()
    {
        try
        {
            Destroy(target);
        }
        finally
        {
            Destroy(readback);
        }
    }
}

public sealed partial class Plugin
{
    // Draws the requested category's thumbnails to the request's path.
    private static IEnumerable<bool> Thumbnails(Request request)
    {
        Slot? slot = null;
        foreach (var step in NamedSlot(request))
        {
            if (step == null) yield return true;
            else slot = step;
        }
        var ids = slot!.Items.Select(item => item.Id).ToArray();
        Sheet sheet;
        try
        {
            sheet = new Sheet(ids);
        }
        catch (ArgumentException error)
        {
            throw Differs($"the labels of {slot.Path}: {error.Message}");
        }
        using (var reader = new ThumbnailReader())
        {
            for (var index = 0; index < ids.Length; index++)
            {
                var where = $"{slot.Path}/{ids[index]}";
                var task = Reflect.Call(Reflect.Get(slot.Items[index].Option, "Preview") ?? throw Differs($"the thumbnail of {where}"), "Create")!;
                while (Reflect.Get(task, "IsCompleted") is not true) yield return true;
                var texture = Reflect.Get(task, "IsFaulted") is true ? null : Reflect.Get(task, "Result");
                if (texture == null) throw new InvalidOperationException($"VRoid could not load the thumbnail of {where}");
                // Each call creates a new texture that nothing else frees.
                try
                {
                    sheet.Place(index, reader.Read(texture));
                }
                finally
                {
                    Reflect.CallStatic("UnityEngine.Object", "DestroyImmediate", texture);
                }
            }
        }
        var png = ThumbnailReader.Png(sheet);
        var path = request.Text("path");
        WriteNew(path, png);
        result = new { rendered = path, bytes = png.Length, columns = Sheet.Columns, items = ids };
    }
}
