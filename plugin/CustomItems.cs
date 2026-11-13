namespace VroidBridge;

// Accessories imported from and exported to `.vroidcustomitem` files through
// VRoid's own custom item registry, without its file dialogs.
public sealed partial class Plugin
{
    private const string ErrorType = "VRoid.Studio.ModelPresetsSelector.CustomItemErrorType";

    // The registry and the arguments its accessory methods take, all held by
    // the accessory editor.
    private sealed record Registry(object Self, object Bus, object Engine, object Context);

    private static Registry CustomItemRegistry()
    {
        var editor = AccessoryEditor;
        var engine = Reflect.Get(editor, "_engine") ?? throw Differs("the accessory editor's engine");
        return new Registry(Reflect.Get(editor, "_customItemRegistry") ?? throw Differs("the custom item registry"),
            Reflect.Get(editor, "GlobalBus") ?? throw Differs("the accessory editor's bus"), engine,
            Reflect.Get(engine, "Context") ?? throw Differs("the edit context"));
    }

    // The latest error callback; VRoid may still call it after a request times out.
    private static object? errorCallback;

    // VRoid's error callback, which records each error it reports instead of
    // showing a dialog.
    private static object OnError(List<string> errors) => errorCallback =
        Reflect.Callback(Reflect.Type("Il2CppSystem.Action`2").MakeGenericType(Reflect.Type("VRoid.Studio.GlobalBus"), Reflect.Type(ErrorType)),
            args => errors.Add(args[1]?.ToString() ?? "unknown error"));

    // The kind a custom item file holds; ArgumentException says why a file is not one.
    private static string KindOf(string path)
    {
        using var stream = File.OpenRead(path);
        return CustomItemFile.Kind(stream);
    }

    // Wears the accessory in a file as a new one (R-EDIT-15). VRoid adds it to
    // the group of its kind, so a kind with a group of built-in accessories,
    // worn or kept after the last was removed, is refused: there nothing would
    // mark the new one as imported.
    private static IEnumerable<bool> Import(Request request)
    {
        var path = request.Text("path");
        var file = Path.GetFileName(path);
        string kind;
        try
        {
            kind = KindOf(path);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException($"{file} is {error.Message.TrimEnd('.')}. Nothing changed");
        }
        if (!AttachmentKinds.ContainsValue(kind))
            throw new ArgumentException($"{file} holds an item of kind {ItemIds.Kind(kind)}, which is not an accessory. Nothing changed");
        foreach (var step in EnterAccessories()) yield return step;
        if (AccessoryGroups().BuiltIn.Contains(kind))
            throw new ArgumentException($"{file} holds an accessory of kind {ItemIds.Kind(kind)}, and VRoid keeps a group of built-in "
                + $"{ItemIds.Kind(kind)} accessories in this project, even after the last one is removed. VRoid would add the import to it, where "
                + $"it would read as a built-in item, so vroid-cli cannot import an accessory of kind {ItemIds.Kind(kind)} into this project. "
                + "Report this VRoid limit to the user. Nothing changed");
        var worn = WornAccessories();
        // Beyond its limit VRoid shows a dialog no one can close on the private display.
        var max = Convert.ToInt32(Reflect.StaticProperty("VRoid.Studio.AccessoryEditor.ViewModel", "MaxInstanceCount").GetValue(null));
        if (worn.Length >= max) throw new ArgumentException($"VRoid allows at most {max} accessories, and the project wears {worn.Length}. Nothing changed");
        // Removing the import needs discard_custom; custom entries keep custom
        // accessories in app order, so the ones listed here keep the ones from
        // before as long as VRoid lists the import after them, as checked below.
        var restore = new Dictionary<string, object>
        {
            ["method"] = "apply",
            ["Accessories"] = AccessoryList(worn),
            ["discard_custom"] = true,
        };
        rpc.WriteUndo(request, restore);
        undo = restore;
        var registry = CustomItemRegistry();
        var errors = new List<string>();
        var before = rebuilt;
        var unsaved = modified;
        modified = true;
        var task = Reflect.Call(registry.Self, "ImportCustomItemByTransferableType", registry.Bus, path, registry.Context, OnError(errors))!;
        while (Reflect.Get(task, "IsCompleted") is not true) yield return true;
        if (Reflect.Get(task, "IsFaulted") is true)
            throw new InvalidOperationException($"VRoid failed to import {file}: {Reflect.Get(task, "Exception")}");
        // VRoid reports an error before changing anything.
        if (errors.Count > 0)
        {
            modified = unsaved;
            throw new ArgumentException($"VRoid cannot import {file}: it reports {string.Join(", ", errors)}. Nothing changed");
        }
        Changed.Add($"Accessories/{file}");
        foreach (var step in WaitForAccessories(before, worn.Length + 1)) yield return step;
        // VRoid rebuilds more than once; read the accessory once rebuilds stop.
        var settled = rebuilt;
        for (int frame = 0, stable = 0; stable < SettledFrames; frame++)
        {
            stable = updating || rebuilt != settled ? 0 : stable + 1;
            settled = rebuilt;
            if (frame > 600) throw new InvalidOperationException($"VRoid kept rebuilding after importing {file}");
            yield return true;
        }
        var now = WornAccessories();
        var added = now[^1];
        // The undo file holds for the accessories from before only if VRoid lists
        // them as before, followed by the import.
        if (!now.SkipLast(1).Select(accessory => accessory.Id).SequenceEqual(worn.Select(accessory => accessory.Id))
            || added.Kind != kind || added.Item != ItemIds.Custom)
        {
            // Its entries could match the wrong custom accessories, so the undo
            // file now keeps what is worn, the import too.
            var kept = Try(() => rpc.WriteUndo(request, new Dictionary<string, object> { ["method"] = "apply", ["Accessories"] = AccessoryList(now) }),
                "replace the undo file");
            throw Differs($"where VRoid lists the imported {file}, which is still worn; "
                + (kept ? "its undo file now keeps what is worn and cannot take it off" : "do not apply its undo file")
                + ". Report this to the user");
        }
        // VRoid builds no colour fields for an accessory imported hidden or locked,
        // so they are built as `open` builds them.
        foreach (var step in RebuildAccessoryColors()) yield return step;
        result = new { imported = AccessoryEntry(WornAccessories()[^1]) };
    }

    // Writes a worn accessory, as `accessories` lists it, to a new file
    // without changing the project (R-EDIT-16).
    private static IEnumerable<bool> Export(Request request)
    {
        var path = request.Text("path");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Refusing to overwrite destination");
        foreach (var step in EnterAccessories()) yield return step;
        var worn = WornAccessories();
        var index = request.Body.GetProperty("index").GetInt32();
        var order = AccessoryOrder(worn.Select(AccessoryEntry).ToArray());
        if (index >= order.Length)
            throw new ArgumentException($"index {index}: the project wears {order.Length} accessories; list them with accessories");
        var chosen = worn[order[index]];
        var registry = CustomItemRegistry();
        var errors = new List<string>();
        Reflect.Call(registry.Self, "ExportAccessoryTemplate", chosen.Id, chosen.Group, registry.Bus, path, registry.Context, registry.Engine,
            OnError(errors));
        if (errors.Count > 0)
        {
            File.Delete(path);
            throw new InvalidOperationException($"VRoid cannot export accessory {index}: it reports {string.Join(", ", errors)}");
        }
        if (!File.Exists(path)) throw new IOException("VRoid reported success without writing the file");
        string kind;
        try
        {
            kind = KindOf(path);
        }
        catch (ArgumentException error)
        {
            throw Differs($"the file VRoid exports, which is {error.Message}");
        }
        if (kind != chosen.Kind) throw Differs($"the kind of accessory VRoid exports, {kind} for {chosen.Kind}");
        result = new { exported = path, bytes = new FileInfo(path).Length };
    }
}
