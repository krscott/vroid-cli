// Draws a category's built-in item thumbnails into one sheet. VRoid's own
// previews are prebaked textures, so nothing about the model changes.
//
// Like Render.cs, instance members of Unity engine objects are called through
// `dynamic`; static members go through Reflect.
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

    /// Returns a preview as Sheet.Place expects it: halved with bilinear
    /// filtering, which averages each 2x2 block. Destroys `texture`, which
    /// `Preview.Create()` makes anew on every call and nothing else frees.
    /// `item` names it in errors.
    internal byte[] Read(object texture, string item)
    {
        try
        {
            dynamic source = Reflect.Concrete(texture);
            int width = source.width, height = source.height;
            if (width != 2 * Sheet.ThumbWidth || height != 2 * Sheet.ThumbHeight)
                throw new InvalidOperationException(
                    $"The thumbnail of {item} is {width}x{height}, not {2 * Sheet.ThumbWidth}x{2 * Sheet.ThumbHeight}: this VRoid differs from the validated 2.16.0");
            source.filterMode = (dynamic)Enum.Parse(Reflect.Type("UnityEngine.FilterMode"), "Bilinear");
            // Blit makes its destination the active render texture.
            var active = Reflect.StaticProperty("UnityEngine.RenderTexture", "active");
            var previous = active.GetValue(null);
            try
            {
                Reflect.Type("UnityEngine.Graphics")
                    .GetMethod("Blit", new[] { Reflect.Type("UnityEngine.Texture"), Reflect.Type("UnityEngine.RenderTexture") })!
                    .Invoke(null, new object[] { (object)source, (object)target });
                active.SetValue(null, (object)target);
                readback.ReadPixels((dynamic)Reflect.New("UnityEngine.Rect", 0f, 0f, (float)Sheet.ThumbWidth, (float)Sheet.ThumbHeight), 0, 0);
            }
            finally
            {
                active.SetValue(null, previous);
            }
            return ((IEnumerable<byte>)readback.GetRawTextureData()).ToArray();
        }
        finally
        {
            Destroy(texture);
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
    // Finding the category switches the editor's section; it is switched back
    // when this ends, so the editor's view stays as it was (R-VIEW-2).
    private static IEnumerable<bool> Thumbnails(Request request)
    {
        var mode = Reflect.Get(Root, "EditMode") ?? throw Differs("the editor's section");
        try
        {
            foreach (var step in DrawThumbnails(request)) yield return step;
        }
        finally
        {
            Reflect.Call(Root, "set_EditMode", mode);
        }
    }

    private static IEnumerable<bool> DrawThumbnails(Request request)
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
                var item = $"{slot.Path}/{ids[index]}";
                var task = Reflect.Call(Reflect.Get(slot.Items[index].Option, "Preview") ?? throw Differs($"the thumbnail of {item}"), "Create")!;
                while (Reflect.Get(task, "IsCompleted") is not true) yield return true;
                if (Reflect.Get(task, "IsFaulted") is true)
                {
                    // An AggregateException, whose own message names no cause.
                    var error = Reflect.Get(task, "Exception");
                    throw new InvalidOperationException($"VRoid could not load the thumbnail of {item}: {Reflect.Get(Reflect.Get(error, "InnerException") ?? error, "Message")}");
                }
                var texture = Reflect.Get(task, "Result") ?? throw new InvalidOperationException($"VRoid loaded no thumbnail for {item}");
                sheet.Place(index, reader.Read(texture, item));
            }
        }
        var png = CharacterRender.Encode(sheet);
        var path = request.Text("path");
        WriteNew(path, png);
        result = new { rendered = path, bytes = png.Length, columns = Sheet.Columns, items = ids };
    }
}
