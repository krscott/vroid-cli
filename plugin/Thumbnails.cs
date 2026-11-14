// Draws a category's built-in item thumbnails into one sheet. VRoid's own
// previews are prebaked textures, so nothing about the model changes.
//
// Like Render.cs, instance members of Unity engine objects are called through
// `dynamic`; static members go through Reflect.
namespace VroidBridge;

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

    // Returns a preview as Sheet.Place expects it: halved with bilinear
    // filtering, which averages each 2x2 block. Destroys `texture`, which
    // `Preview.Create()` makes anew on every call and nothing else frees.
    // `item` names it in errors.
    private static byte[] ReadThumbnail(object texture, string item)
    {
        try
        {
            dynamic source = Reflect.Concrete(texture);
            int width = source.width, height = source.height;
            if (width != 2 * Sheet.ThumbWidth || height != 2 * Sheet.ThumbHeight)
                throw new InvalidOperationException(
                    $"The thumbnail of {item} is {width}x{height}, not {2 * Sheet.ThumbWidth}x{2 * Sheet.ThumbHeight}: this VRoid differs from the validated 2.16.0");
            source.filterMode = (dynamic)Enum.Parse(Reflect.Type("UnityEngine.FilterMode"), "Bilinear");
            return Textures.Read((object)source, Sheet.ThumbWidth, Sheet.ThumbHeight, "RGB24");
        }
        finally
        {
            Textures.Destroy(texture);
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
            sheet.Place(index, ReadThumbnail(texture, item));
        }
        var png = Textures.Encode(sheet);
        var path = request.Text("path");
        WriteNew(path, png);
        result = new { rendered = path, bytes = png.Length, columns = Sheet.Columns, items = ids };
    }
}
