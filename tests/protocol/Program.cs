// Exercises plugin/Bridge.cs, the app side of the file protocol, the colour
// rules in plugin/Colors.cs, custom item files in plugin/CustomItemFile.cs,
// the camera placement in plugin/Framing.cs, item identifiers and accessory
// matching in plugin/ItemIds.cs, and the thumbnail sheet in plugin/Sheet.cs,
// without Unity.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VroidBridge;

var failures = 0;

void Test(string name, Action<string> body)
{
    var directory = Directory.CreateTempSubdirectory("vroid-protocol-").FullName;
    try
    {
        body(directory);
        Console.WriteLine($"ok   {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {error.Message}");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

string NewId() => Guid.NewGuid().ToString("N");

RpcDirectory Rpc(string directory, string boot, bool modified = false) =>
    new(directory, boot, () => new { boot, modified }, _ => { });

void Send(string directory, string id, string boot) =>
    File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new
    {
        id, boot, method = "categories", timeout_ms = 1000, section = "Body",
    }));

JsonElement Result(string directory, string id) =>
    JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "results", id + ".json"))).RootElement;

// R-RPC-1
Test("request for another app instance is rejected, not run", directory =>
{
    var rpc = Rpc(directory, "current");
    var id = NewId();
    Send(directory, id, "previous");
    Check(rpc.Take() == null, "request was returned for execution");
    Check(!Result(directory, id).GetProperty("ok").GetBoolean(), "result reports success");
    Check(!File.Exists(Path.Combine(directory, "request.json")), "request still pending");
});

// R-RPC-2
Test("restart marks an interrupted request unknown and never replays it", directory =>
{
    var id = NewId();
    Send(directory, id, "previous");
    var rpc = Rpc(directory, "current");
    rpc.RecoverInterrupted();
    var result = Result(directory, id);
    Check(!result.GetProperty("ok").GetBoolean(), "result reports success");
    Check(result.GetProperty("error").GetString()!.Contains("unknown"), "error does not say unknown");
    Check(rpc.Take() == null, "request remains executable");
});

// R-RPC-2, R-RPC-6
Test("restart keeps a result written before the restart", directory =>
{
    var id = NewId();
    var before = Rpc(directory, "previous");
    Send(directory, id, "previous");
    var request = before.Take()!;
    before.Finish(request, true, 0.25);
    Send(directory, id, "previous"); // As if the app died before removing the request.
    Rpc(directory, "current").RecoverInterrupted();
    Check(Result(directory, id).GetProperty("ok").GetBoolean(), "completed result was replaced");
    Check(!File.Exists(Path.Combine(directory, "request.json")), "request still pending");
});

// R-RPC-3, R-RPC-6
Test("a request stays pending until finished and its result is kept", directory =>
{
    var rpc = Rpc(directory, "current");
    var first = NewId();
    Send(directory, first, "current");
    var request = rpc.Take()!;
    Check(request.Id == first && request.Text("section") == "Body", "request fields differ");
    Check(File.Exists(Path.Combine(directory, "request.json")), "request removed before finishing");
    rpc.Finish(request, true, new { value = 0.25 });
    var second = NewId();
    Send(directory, second, "current");
    rpc.Finish(rpc.Take()!, false, "engine refused");
    Check(Result(directory, first).GetProperty("ok").GetBoolean(), "first result lost");
    Check(Result(directory, second).GetProperty("error").GetString() == "engine refused", "second result lost");
});

// R-RPC-5, R-RPC-6
Test("a result that cannot be written is retried and the request never reruns", directory =>
{
    var rpc = Rpc(directory, "current");
    var id = NewId();
    Send(directory, id, "current");
    var request = rpc.Take()!;
    var results = new DirectoryInfo(Path.Combine(directory, "results"));
    results.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
    rpc.Finish(request, true, 0.25);
    Check(File.Exists(Path.Combine(directory, "request.json")), "request removed without a result");
    Check(rpc.Take() == null, "request was returned twice");
    results.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    Check(rpc.Take() == null, "request was returned after publishing");
    Check(Result(directory, id).GetProperty("ok").GetBoolean(), "result was not published");
    Check(!File.Exists(Path.Combine(directory, "request.json")), "request still pending");
});

// R-SES-4
Test("a result is published only after the current status", directory =>
{
    var rpc = Rpc(directory, "current", modified: true);
    var id = NewId();
    Send(directory, id, "current");
    var request = rpc.Take()!;
    var status = Path.Combine(directory, "status.json");
    Directory.CreateDirectory(status); // Status writes now fail.
    rpc.Finish(request, true, 0.25);
    Check(!File.Exists(Path.Combine(directory, "results", id + ".json")), "result published before status");
    Check(File.Exists(Path.Combine(directory, "request.json")), "request removed before status");
    Directory.Delete(status);
    Check(rpc.Take() == null, "request was returned after publishing");
    var written = JsonDocument.Parse(File.ReadAllText(status)).RootElement;
    Check(written.GetProperty("modified").GetBoolean(), "status not current");
    Check(Result(directory, id).GetProperty("ok").GetBoolean(), "result was not published");
});

// R-RPC-6
Test("a result that cannot be serialized is recorded as an error", directory =>
{
    var rpc = Rpc(directory, "current");
    var id = NewId();
    Send(directory, id, "current");
    rpc.Finish(rpc.Take()!, true, new { value = float.NaN });
    var result = Result(directory, id);
    Check(!result.GetProperty("ok").GetBoolean(), "result reports success");
    Check(result.GetProperty("error").GetString()!.Contains("operation ran"), "error does not say it ran");
});

// R-EDIT-11, R-RPC-6
Test("an undo file is replaced in place and kept after the result", directory =>
{
    var rpc = Rpc(directory, "current");
    var id = NewId();
    Send(directory, id, "current");
    var request = rpc.Take()!;
    var path = Path.Combine(directory, "undo", id + ".json");
    rpc.WriteUndo(request, new { method = "apply", Hair = new { Back = new { item = "custom" } } });
    rpc.WriteUndo(request, new { method = "apply", Hair = new { Back = new { } } });
    rpc.Finish(request, true, new { changed = 1 });
    var undo = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    Check(undo.GetProperty("method").GetString() == "apply", "undo is not an apply request");
    Check(!undo.GetProperty("Hair").GetProperty("Back").TryGetProperty("item", out _), "undo was not replaced");
    Check(Directory.GetFiles(Path.Combine(directory, "undo")).Length == 1, "undo left a temporary file");
});

Test("a request whose id is not a CLI id writes nothing outside results", directory =>
{
    var rpc = Rpc(directory, "current");
    Send(directory, "../escape", "current");
    Check(rpc.Take() == null, "invalid request was returned");
    Check(!File.Exists(Path.Combine(directory, "escape.json")), "wrote outside results");
    Check(File.Exists(Path.Combine(directory, "request.invalid.json")), "invalid request not kept");
});

// Framing: a sample character's meshes, as VRoid reports their bounds: the
// body with its arms, the head, and long ears that reach past the head's
// height. The model faces +Z or is turned.
var body = new Box(new Vec(-0.48, 0.0, -0.38), new Vec(0.48, 1.30, 0.33));
var skull = new Box(new Vec(-0.12, 1.19, -0.15), new Vec(0.12, 1.50, 0.12));
var ears = new Box(new Vec(-0.30, 1.28, -0.05), new Vec(0.30, 1.36, 0.02));
// Long hair from above the skull to the waist, centred below the neck.
var hair = new Box(new Vec(-0.17, 0.80, -0.20), new Vec(0.17, 1.53, 0.10));
var parts = new[] { body, skull, ears, hair };
var neck = new Vec(0, 1.19, -0.03);
var head = new Vec(0.01, 1.25, -0.02);
var forwards = new[] { new Vec(0, 0, 1), new Vec(Math.Sin(0.5), 0, Math.Cos(0.5)) };
var aspects = new[] { 0.5, 1.0, 2.0 };
var views = Enum.GetValues<View>();

// Position of `point` in the image plane of `shot`: right, up, and depth.
(double Right, double Up, double Depth) Project(Shot shot, Vec point)
{
    var back = shot.Position - shot.Target;
    var length = Math.Sqrt(back.X * back.X + back.Z * back.Z);
    Check(Math.Abs(back.Y) < 1e-9, "camera does not look horizontally");
    var (x, z) = (back.X / length, back.Z / length);
    var offset = point - shot.Target;
    var toCamera = shot.Position - point;
    var depth = toCamera.X * x + toCamera.Z * z;
    return (offset.X * z - offset.Z * x, offset.Y, depth);
}

IEnumerable<Vec> Corners(Box box) =>
    from x in new[] { box.Min.X, box.Max.X }
    from y in new[] { box.Min.Y, box.Max.Y }
    from z in new[] { box.Min.Z, box.Max.Z }
    select new Vec(x, y, z);

// Checks that every corner of `boxes` is inside the frame with its margin and unclipped.
void Shows(Shot shot, double aspect, IEnumerable<Box> boxes, string what)
{
    foreach (var corner in boxes.SelectMany(Corners))
    {
        var (right, up, depth) = Project(shot, corner);
        var where = $"{what} at aspect {aspect}: corner {corner}";
        Check(Math.Abs(right) <= shot.Size * aspect / Framing.Margin + 1e-9, $"{where} is outside the width");
        Check(Math.Abs(up) <= shot.Size / Framing.Margin + 1e-9, $"{where} is outside the height");
        Check(depth > shot.Near && depth < shot.Far, $"{where} is clipped");
    }
}

// R-VIEW-1
Test("every body view shows the whole model with a margin", _ =>
{
    foreach (var forward in forwards)
        foreach (var aspect in aspects)
            foreach (var view in views)
                Shows(Framing.Body(parts, forward, view, aspect), aspect, parts, $"{view} body");
});

// R-VIEW-1
Test("views of one model at one size share their scale", _ =>
{
    foreach (var forward in forwards)
        foreach (var aspect in aspects)
        {
            var bodies = views.Select(view => Framing.Body(parts, forward, view, aspect)).ToArray();
            Check(bodies.All(shot => Math.Abs(shot.Size - bodies[0].Size) < 1e-9), $"body scale differs at aspect {aspect}");
            Check(bodies.All(shot => Math.Abs(shot.Target.Y - bodies[0].Target.Y) < 1e-9), $"ground line differs at aspect {aspect}");
            var heads = views.Select(view => Framing.Head(parts, neck, head, forward, view, aspect)).ToArray();
            Check(heads.All(shot => Math.Abs(shot.Size - heads[0].Size) < 1e-9), $"head scale differs at aspect {aspect}");
        }
});

// R-VIEW-1
Test("each view looks from its side of the model", _ =>
{
    // Facing +Z, the model's left is -X.
    var forward = new Vec(0, 0, 1);
    var expected = new Dictionary<View, (int X, int Z)>
    {
        [View.Front] = (0, 1),
        [View.ThreeQuarter] = (-1, 1),
        [View.Side] = (-1, 0),
        [View.Back] = (0, -1),
    };
    foreach (var (view, (x, z)) in expected)
    {
        var direction = Framing.Direction(forward, view);
        Check(Math.Sign(Math.Round(direction.X, 9)) == x && Math.Sign(Math.Round(direction.Z, 9)) == z,
            $"{view} camera is at {direction}");
    }
});

// R-VIEW-1
Test("a head view shows every mesh above the neck and the top, not the arms", _ =>
{
    foreach (var forward in forwards)
        foreach (var aspect in aspects)
            foreach (var view in views)
            {
                var shot = Framing.Head(parts, neck, head, forward, view, aspect);
                Check(Math.Abs(shot.Target.X - head.X) < 1e-9 && Math.Abs(shot.Target.Z - head.Z) < 1e-9, "not centred on the head");
                Shows(shot, aspect, new[] { skull, ears }, $"{view} head");
                Check(Math.Abs(shot.Target.Y - (neck.Y + hair.Max.Y) / 2) < 1e-9, $"{view} head is not centred from the neck to the top");
                var (_, crown, _) = Project(shot, new Vec(head.X, hair.Max.Y, head.Z));
                Check(crown <= shot.Size / Framing.Margin + 1e-9, $"{view} head at aspect {aspect} cuts off the top of the hair");
                // The arms reach further from the head than anything above the neck.
                Check(shot.Size * aspect < body.Max.X - head.X, $"{view} head at aspect {aspect} is as wide as the arms");
            }
});

Test("unknown views and frames are refused", _ =>
{
    foreach (var name in new[] { "front", "three_quarter", "side", "back" }) Framing.ParseView(name);
    foreach (var name in new[] { "body", "head" }) Framing.ParseFrame(name);
    foreach (var parse in new Action[] { () => Framing.ParseView("left"), () => Framing.ParseFrame("face") })
    {
        try
        {
            parse();
            throw new Exception("an unknown name was accepted");
        }
        catch (ArgumentException) { }
    }
});

ColorField Field(string path, string hex, params string[] keys) => new(path, keys.ToHashSet(), hex);

void Throws(Action action, params string[] parts)
{
    try
    {
        action();
    }
    catch (InvalidOperationException error)
    {
        Check(parts.All(error.Message.Contains), error.Message);
        return;
    }
    throw new Exception("did not throw");
}

// R-EDIT-8
Test("every channel value round-trips through #RRGGBB as k/255", _ =>
{
    for (var k = 0; k < 256; k++)
    {
        // Blue is 255 - k, so the colour always differs from black.
        var channels = Colors.Write("#000000", Colors.Normalize($"#{k:x2}{k:X2}{255 - k:x2}")!)!;
        Check(channels[0] == k / 255f && channels[1] == k / 255f && channels[2] == (255 - k) / 255f, $"channel {k} drifted");
        Check((int)Math.Round(channels[0] * 255) == k && (int)(channels[0] * 255) == k, $"channel {k} does not read back");
    }
    foreach (var bad in new[] { "#12345", "123456", "#1234567", "#12345678", "#GGGGGG", "#12345\n", "" })
        Check(Colors.Normalize(bad) == null, $"accepted {bad}");
    Check(Colors.Normalize("#faebde") == "#FAEBDE", "not normalized to uppercase");
});

// R-EDIT-7
Test("a colour off the 1/255 grid dumps and reapplies without a write", _ =>
{
    // A stored channel such as 0.5 is off the grid; the dump holds the hex
    // VRoid shows for it, H, and the field still reads H when it is reapplied.
    var stored = Field("Face/Eyebrows/Color", "#808080", "Eyebrow:Overlay");
    var dumped = Colors.Listed(new[] { stored }).Single();
    Check(Colors.Write(stored.Hex, dumped.Hex) == null, "reapplying the dump writes the colour");
    Check(Colors.Write(stored.Hex, "#808081") is { } changed && changed[2] == 129 / 255f, "a changed colour is not written");
});

// R-EDIT-7
Test("a dump lists a shared colour once, under the first category", _ =>
{
    var fields = new[]
    {
        Field("Face/Irises/R Eye", "#9E6233", "Iris:Left"),
        Field("Face/Irises/L Eye", "#2E8B57", "Iris:Right"),
        Field("Face/Eyelids/Color", "#FAEBDE", "Eyelid:Main", "FaceSkin:Main", "BodySkin:Main"),
        Field("Face/Skin/Color", "#FAEBDE", "FaceSkin:Main", "BodySkin:Main", "Eyelid:Main"),
        Field("Face/Eyebrows/Color", "#8F5836", "Eyebrow:Overlay"),
        Field("Body/Whole Body/Color", "#FAEBDE", "BodySkin:Main", "FaceSkin:Main", "Eyelid:Main"),
    };
    var listed = Colors.Listed(fields).Select(field => field.Path).ToArray();
    Check(listed.SequenceEqual(new[] { "Face/Irises/R Eye", "Face/Irises/L Eye", "Face/Eyelids/Color", "Face/Eyebrows/Color" }),
        $"listed {string.Join(", ", listed)}");
});

// R-EDIT-7
Test("a dump fails rather than drop a colour or list one apply would refuse", _ =>
{
    Throws(() => Colors.Listed(new[]
    {
        Field("Face/Skin/Color", "#FAEBDE", "FaceSkin:Main", "BodySkin:Main"),
        Field("Body/Whole Body/Color", "#FF0000", "BodySkin:Main", "FaceSkin:Main"),
    }), "Body/Whole Body/Color", "Face/Skin/Color", "not one shared colour");
    // A field writing a superset of another's keys, or part of them.
    Throws(() => Colors.Listed(new[]
    {
        Field("Face/Irises/R Eye", "#9E6233", "Iris:Left"),
        Field("Face/Other/Both", "#9E6233", "Iris:Left", "Iris:Right"),
    }), "Face/Irises/R Eye and Face/Other/Both", "cannot hold both");
    Throws(() => Colors.Listed(new[]
    {
        Field("Face/A/Color", "#000000", "k1", "k2"),
        Field("Body/B/Color", "#000000", "k2", "k3"),
    }), "Face/A/Color and Body/B/Color");
});

// R-EDIT-3
Test("a request may set each shared colour in only one place", _ =>
{
    var skin = Field("Face/Skin/Color", "#FAEBDE", "FaceSkin:Main", "BodySkin:Main");
    var body = Field("Body/Whole Body/Color", "#FAEBDE", "BodySkin:Main", "FaceSkin:Main");
    var right = Field("Face/Irises/R Eye", "#9E6233", "Iris:Left");
    var left = Field("Face/Irises/L Eye", "#9E6233", "Iris:Right");
    var brows = Field("Face/Eyebrows/Color", "#8F5836", "Eyebrow:Overlay");
    Check(Colors.Conflicts(new[] { skin, right, left, brows }).Count == 0, "disjoint colours conflict");
    var shared = Colors.Conflicts(new[] { skin, body });
    Check(shared.Single() == "Face/Skin/Color and Body/Whole Body/Color set the same colour; set it in one place", shared.Single());
});

// R-EDIT-9
Test("item identifiers come from VRoid's resource names", _ =>
{
    Check(ItemIds.Preset("pixiv/VRoid/Clothing/Tops/N00/159/TransferableGroupType.N00.Level1.Tops.transferablegroup") == "159", "Tops 159");
    Check(ItemIds.Preset("pixiv/VRoid/Hair/N00/Preset_F/TransferableGroupType.N00.Level1.HairFront.transferablegroup") == "Preset_F", "Preset_F");
    Check(ItemIds.Preset("user/VRoid/Clothing/abc/TransferableGroupType.N00.Level1.Tops.transferablegroup") == null, "a user item is built in");
    Check(ItemIds.Preset(null) == null, "no resource is an item");
    Check(ItemIds.Preset("pixiv/VRoid/Clothing/Tops/N00/a-b/x.transferablegroup") == null, "an ID the CLI refuses");
    Check(ItemIds.Preset("pixiv/VRoid/Clothing/Tops/N00/custom/x.transferablegroup") == null, "the reserved ID");
    Check(ItemIds.AccessoryKey("Model.N00.Transferable.GlassesLow") == "GlassesLow", "accessory key");
    Check(ItemIds.AccessoryKey(null) == null && ItemIds.AccessoryKey("Other.GlassesLow") == null, "a user accessory is built in");
    Check(ItemIds.Attachment("pixiv/VRoid/Attachment/CatEar/N00/Base_N00_000_00_Accessory_CatEar/TransferableGroupType.N00.Level1.AnimalEar.transferablegroup") == "CatEar", "CatEar");
    Check(ItemIds.Attachment("pixiv/VRoid/Clothing/Tops/N00/159/x.transferablegroup") == null, "a top is an accessory");
    Check(ItemIds.Attachment("user/VRoid/AdditionalAccessoryTemplate/e004.transferable") == null, "a user accessory is built in");
    Check(ItemIds.IsSet("TransferableGroupType.N00.Level0.HairSet") && ItemIds.IsSet("TransferableGroupType.N00.Level1.EyeSet"), "sets");
    Check(!ItemIds.IsSet("TransferableGroupType.N00.Level1.Tops"), "Tops is a set");
    // An accessory's key names its template; a group an import started makes it custom.
    var builtIn = new HashSet<string> { "CatEar", "RabbitEar" };
    const string ears = "pixiv/VRoid/Attachment/CatEar/N00/Base_N00_000_00_Accessory_CatEar/TransferableGroupType.N00.Level1.AnimalEar.transferablegroup";
    Check(ItemIds.Accessory("Model.N00.Transferable.CatEar", ears, builtIn) == "CatEar", "a built-in accessory");
    Check(ItemIds.Accessory("Model.N00.Transferable.CatEar", "user/VRoid/CustomItem/ea62.transferablegroup", builtIn) == "custom",
        "an imported accessory is built in");
    Check(ItemIds.Accessory("Model.N00.Transferable.RabbitEar", "user/VRoid/CustomItem/ea62.transferablegroup", builtIn) == "custom",
        "a built-in accessory in an imported one's group keeps its identifier");
    Check(ItemIds.Accessory("Model.N00.Transferable.FoxTail", ears, builtIn) == "custom", "an accessory the catalog lacks is built in");
    Check(ItemIds.Accessory(null, ears, builtIn) == "custom", "an accessory without a key is built in");
    Check(ItemIds.Imported("user/VRoid/CustomItem/ea62.transferablegroup") && !ItemIds.Imported(ears), "imported groups");
    // Selectors list conflicts as transferable types, categories as group types.
    Check(ItemIds.Kind("TransferableType.N00.Onepiece") == ItemIds.Kind("TransferableGroupType.N00.Level1.Onepiece"), "kinds differ");
});

const string Head = "J_Bip_C_Head", Hips = "J_Bip_C_Hips", Chest = "J_Bip_C_Chest", Neck = "J_Bip_C_Neck";
// VRoid keeps one group per kind, such as every pair of animal ears.
string Kind(string item) => item is "CatEar" or "RabbitEar" ? "ears" : item;
WornAccessory Worn(string item, string? bone, string group = "") => new(item, bone, group == "" ? Kind(item) : group);
WantedAccessory Any(string item) => new(item, false, null, Kind(item));
WantedAccessory On(string item, string? bone) => new(item, true, bone, Kind(item));
string Show(AccessoryPlan plan) =>
    $"kept {string.Join(",", plan.Kept)} rebone {string.Join(",", plan.Rebone)} removed {string.Join(",", plan.Removed)} "
    + $"added {string.Join(",", plan.Added)} rebuilt {string.Join(",", plan.Rebuilt)} errors {string.Join("; ", plan.Errors)}";

Test("accessories keep matching items in order and add or remove the rest", _ =>
{
    var plan = AccessoryPlan.Of(new[] { Worn("GlassesHi", Head), Worn("CatEar", Head), Worn("custom", Head, "user"), Worn("CatEar", Head) },
        new[] { Any("CatEar"), Any("FoxTail"), Any("custom") });
    Check(plan.Kept.SequenceEqual(new[] { (1, 0), (2, 2) }) && plan.Removed.SequenceEqual(new[] { 0, 3 }) && plan.Rebone.Count == 0
        && plan.Added.SequenceEqual(new[] { new Addition(1, null, false, Hips) }) && plan.Errors.Count == 0, Show(plan));
    // "custom" with no worn accessory that is not built in is an addition, which apply refuses.
    var missing = AccessoryPlan.Of(Array.Empty<WornAccessory>(), new[] { Any("custom") });
    Check(missing.Added.SequenceEqual(new[] { new Addition(0, null, false, null) }), Show(missing));
    var same = AccessoryPlan.Of(new[] { Worn("CatEar", Head), Worn("CatEar", Chest) }, new[] { On("CatEar", Head), On("CatEar", Chest) });
    Check(same.Removed.Count == 0 && same.Added.Count == 0 && same.Rebone.Count == 0 && same.Kept.SequenceEqual(new[] { (0, 0), (1, 1) }), Show(same));
});

// R-EDIT-7, R-EDIT-11
Test("accessories of one item reach every arrangement of bones VRoid can make", _ =>
{
    // A wanted bone keeps the worn accessory already on it.
    var keep = AccessoryPlan.Of(new[] { Worn("CatEar", Head), Worn("CatEar", Chest) }, new[] { On("CatEar", Chest) });
    Check(keep.Kept.SequenceEqual(new[] { (1, 0) }) && keep.Removed.SequenceEqual(new[] { 0 }) && keep.Rebone.Count == 0, Show(keep));
    // One left alone moves itself.
    var lone = AccessoryPlan.Of(new[] { Worn("FoxTail", Hips), Worn("CatEar", Head) }, new[] { On("FoxTail", Chest), Any("CatEar") });
    Check(lone.Rebone.SequenceEqual(new (int, string?)[] { (0, Chest) }) && lone.Added.Count == 0, Show(lone));
    // Undoing the removal of a moved one while its twin stays: the twin cannot
    // stay, since the moved one must be added alone.
    var undo = AccessoryPlan.Of(new[] { Worn("CatEar", Head) }, new[] { On("CatEar", Head), On("CatEar", Chest) });
    Check(undo.Kept.Count == 0 && undo.Removed.SequenceEqual(new[] { 0 }) && undo.Rebuilt.SetEquals(new[] { 0 })
        && undo.Added.SequenceEqual(new[] { new Addition(1, null, true, Chest), new Addition(0, null, false, Head) })
        && undo.Matched.SequenceEqual(new int?[] { 0, null }), Show(undo));
    // Two kept ones cannot move, so both are added again: one moved, then a copy.
    var both = AccessoryPlan.Of(new[] { Worn("CatEar", Head), Worn("CatEar", Head) }, new[] { On("CatEar", Chest), On("CatEar", Chest) });
    Check(both.Removed.SequenceEqual(new[] { 0, 1 }) && both.Rebuilt.Count == 2
        && both.Added.SequenceEqual(new[] { new Addition(0, null, true, Chest), new Addition(1, 0, false, Chest) })
        && both.Matched.SequenceEqual(new int?[] { 0, 1 }), Show(both));
    // A new set moves its first one away from home while it is alone.
    var fresh = AccessoryPlan.Of(Array.Empty<WornAccessory>(), new[] { On("CatEar", Head), On("CatEar", Chest), On("CatEar", Chest) });
    Check(fresh.Added.SequenceEqual(new[] { new Addition(1, null, true, Chest), new Addition(0, null, false, Head),
        new Addition(2, 1, false, Chest) }) && fresh.Rebuilt.Count == 0, Show(fresh));
    // Cat and rabbit ears share a group, so one cannot move while the other is worn.
    var ears = AccessoryPlan.Of(new[] { Worn("CatEar", Head), Worn("RabbitEar", Head) }, new[] { On("CatEar", Chest), Any("RabbitEar") });
    Check(ears.Rebuilt.Count == 2 && ears.Added.SequenceEqual(new[] { new Addition(0, null, true, Chest), new Addition(1, null, false, Head) }),
        Show(ears));
    // One that names no bone keeps a worn one on the default bone whichever VRoid
    // lists first, so the other may move.
    foreach (var worn in new[] { new[] { Worn("CatEar", Neck), Worn("CatEar", Head) }, new[] { Worn("CatEar", Head), Worn("CatEar", Neck) } })
    {
        // Even though the bare entry matches either.
        var order = AccessoryPlan.Of(worn, new[] { Any("CatEar"), On("RabbitEar", Chest) }, (i, _) => i == 0);
        var source = order.Matched[0];
        Check(order.Errors.Count == 0 && source != null && worn[source.Value].Bone == Head, Show(order));
    }
    // An entry that already matches a worn one keeps it, so reapplying a dump
    // changes nothing whichever order VRoid lists them in.
    var dumped = AccessoryPlan.Of(new[] { Worn("CatEar", Head), Worn("CatEar", Head) }, new[] { On("CatEar", Head), On("CatEar", Head) },
        (i, j) => i != j);
    Check(dumped.Kept.SequenceEqual(new[] { (1, 0), (0, 1) }) && dumped.Removed.Count == 0, Show(dumped));
    // Custom entries keep custom accessories in order, even where values match
    // others: two imports with equal values may differ in texture.
    var customs = AccessoryPlan.Of(new[] { Worn("custom", Head, "ears"), Worn("custom", Neck, "ears"), Worn("custom", Hips, "tail") },
        new[] { On("custom", Head), On("custom", Hips) }, (i, j) => (i, j) is (0, 1) or (1, 0));
    Check(customs.Kept.SequenceEqual(new[] { (0, 0), (1, 1) }) && customs.Removed.SequenceEqual(new[] { 2 }), Show(customs));
    // "None" is a bone too.
    var none = AccessoryPlan.Of(new[] { Worn("CatTail", Hips) }, new[] { On("CatTail", null) });
    Check(none.Rebone.SequenceEqual(new (int, string?)[] { (0, null) }), Show(none));
});

// R-EDIT-3
Test("apply refuses bones VRoid cannot combine, built-in accessories that would read as custom, and placements that distort", _ =>
{
    var two = AccessoryPlan.Of(Array.Empty<WornAccessory>(), new[] { On("CatEar", Chest), On("CatEar", Neck) });
    Check(two.Errors.Single().Contains($"only as copies of one, but these put CatEar on {Chest}, CatEar on {Neck}"), Show(two));
    // Kept ones are still matched, so their values are checked too.
    var refused = AccessoryPlan.Of(new[] { Worn("CatEar", Neck), Worn("RabbitEar", Head) }, new[] { On("CatEar", Neck), On("RabbitEar", Chest) });
    Check(refused.Errors.Count == 1 && refused.Matched.SequenceEqual(new int?[] { 0, 1 }), Show(refused));
    // A copy is of one item, so two items of a kind cannot both move.
    var mixed = AccessoryPlan.Of(Array.Empty<WornAccessory>(), new[] { On("CatEar", Chest), On("RabbitEar", Chest) });
    Check(mixed.Errors.Count == 1, Show(mixed));
    // A kept one counts with the bone it stays on.
    var kept = AccessoryPlan.Of(new[] { Worn("CatEar", Neck) }, new[] { Any("CatEar"), On("CatEar", Chest) });
    Check(kept.Errors.Count == 1, Show(kept));
    // A custom accessory moves only while alone in its group.
    var crowded = AccessoryPlan.Of(new[] { Worn("custom", Head, "user"), Worn("custom", Head, "user") }, new[] { On("custom", Chest), Any("custom") });
    Check(crowded.Errors.Single().Contains("only while no other accessory of its kind is worn") && crowded.Rebone.Count == 0, Show(crowded));
    var alone = AccessoryPlan.Of(new[] { Worn("custom", Head, "user"), Worn("custom", Head, "other") }, new[] { On("custom", Chest), Any("custom") });
    Check(alone.Errors.Count == 0 && alone.Rebone.SequenceEqual(new (int, string?)[] { (0, Chest) }), Show(alone));
    // A built-in accessory added to a kind whose group an import started would read
    // as custom, even once no accessory of the kind is worn, since VRoid keeps the group.
    var imported = new HashSet<string> { "ears" };
    var joins = AccessoryPlan.Of(new[] { Worn("custom", Head, "ears") }, new[] { Any("custom"), Any("RabbitEar"), Any("FoxTail") }, null, imported);
    Check(joins.Errors.Single().StartsWith("Accessories[1]: VRoid keeps the group of ears accessories that an import started")
        && joins.Errors.Single().EndsWith("Report this VRoid limit to the user")
        && joins.Added.SequenceEqual(new[] { new Addition(2, null, false, Hips) }), Show(joins));
    var empty = AccessoryPlan.Of(Array.Empty<WornAccessory>(), new[] { Any("RabbitEar") }, null, imported);
    Check(empty.Errors.Count == 1, Show(empty));
    var free = AccessoryPlan.Of(new[] { Worn("custom", Head, "ears") }, new[] { Any("RabbitEar") });
    Check(free.Errors.Count == 0 && free.Removed.SequenceEqual(new[] { 0 }), Show(free));
    Check(Placement.Rotation(new[] { 0f, 0f, 0f, 1f }) == null && Placement.Rotation(new[] { 0.1f, 0.2f, 0.3f, 0.9273618f }) == null, "a unit rotation");
    Check(Placement.Rotation(new[] { 0f, 0f, 0f, 2f }) != null && Placement.Rotation(new[] { 0f, 0f, 0f, 1.001f }) != null, "a long rotation");
    Check(Placement.Scale(new[] { 1f, 0.5f, 2f }) == null && Placement.Scale(new[] { 1f, 0f, 1f }) != null && Placement.Scale(new[] { -1f, 1f, 1f }) != null,
        "a scale that is not positive");
    Check(Bones.Default("FoxTail") == Hips && Bones.Default("WitchHat") == Head && Bones.Default("Nope") == null, "default bones");
    Check(Bones.All.Count == 54 && Bones.All.Distinct().Count() == 54 && Bones.All.Contains(Head), "bones");
});

// R-EDIT-15
Test("a custom item file names its kind", directory =>
{
    Stream Zip(params (string Name, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8))
                    writer.Write(text);
        stream.Position = 0;
        return stream;
    }
    using (var item = Zip(("meta.json", "{}"), ("v1customitem/meta.json",
               """{"customItemMetaDataVersion": 3, "rootTransferableGroupTypeId": "TransferableGroupType.N00.Level1.AnimalEar"}""")))
        Check(CustomItemFile.Kind(item) == "TransferableGroupType.N00.Level1.AnimalEar", "the kind of animal ears");
    string Refusal(Stream file)
    {
        try
        {
            return $"read {CustomItemFile.Kind(file)}";
        }
        catch (ArgumentException error)
        {
            return error.Message;
        }
    }
    foreach (var (file, reason) in new (Stream, string)[]
             {
                 (new MemoryStream(Encoding.UTF8.GetBytes("garbage")), "not a VRoid custom item file"),
                 (Zip(("meta.json", "{}")), "it has no v1customitem/meta.json"),
                 (Zip(("v1customitem/meta.json", "{\"rootTransferableGroupTypeId\": 3}")), "not a VRoid custom item file"),
                 (Zip(("v1customitem/meta.json", "[]")), "not a VRoid custom item file"),
                 (Zip(("v1customitem/meta.json", "{")), "not a VRoid custom item file"),
                 (Zip(("v1customitem/meta.json", "{}")), "names no rootTransferableGroupTypeId"),
             })
        using (file)
            Check(Refusal(file).Contains(reason), Refusal(file));
});

// R-EDIT-7
Test("dumps order accessories so they compare across instances", _ =>
{
    // By item, then the others in app order; one item's built-in accessories by entry.
    Check(AccessoryPlan.DumpOrder(new[] { "custom", "FoxTail", "CatEar", "CatEar", "custom" }, new[] { "z", "a", "b", "a", "y" })
        .SequenceEqual(new[] { 3, 2, 1, 0, 4 }), "dump order");
});

// R-VIEW-4
Test("the sheet font draws every identifier character distinctly", _ =>
{
    var allowed = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_";
    Check(Sheet.Font.Keys.OrderBy(c => c).SequenceEqual(allowed.OrderBy(c => c)), "the font does not match ItemIds' characters");
    // Every character the font covers is valid in an identifier.
    Check(ItemIds.Preset($"pixiv/VRoid/Hair/N00/{allowed}/x.transferablegroup") == allowed, "the font covers characters ItemIds refuses");
    foreach (var (c, glyph) in Sheet.Font)
    {
        var rows = glyph.Split(' ');
        Check(rows.Length is >= 7 and <= 9 && rows.All(row => row.Length == 5 && row.All(dot => dot is '.' or '#')), $"malformed glyph {c}");
        Check(glyph.Contains('#'), $"blank glyph {c}");
    }
    var shapes = Sheet.Font.Values.Select(glyph => glyph.PadRight(53, '.')).ToList();
    Check(shapes.Distinct().Count() == shapes.Count, "two glyphs look the same");
});

// R-VIEW-4
Test("the sheet lays thumbnails out in labelled rows of ten", _ =>
{
    var labels = Enumerable.Range(0, 82).Select(i => i.ToString("000")).ToArray();
    var sheet = new Sheet(labels);
    Check((sheet.Width, sheet.Height) == (1544, 2056), $"82 items: {sheet.Width}x{sheet.Height}");
    Check(sheet.Pixels.Length == sheet.Width * sheet.Height * 3, "pixel buffer size");
    var small = new Sheet(new[] { "Default", "Preset_F" });
    Check((small.Width, small.Height) == (312, 232), $"2 items: {small.Width}x{small.Height}");

    // Pixels run bottom row first; this reads them top first.
    byte Red(Sheet image, int x, int top) => image.Pixels[((image.Height - 1 - top) * image.Width + x) * 3];
    var thumbnail = new byte[Sheet.ThumbWidth * Sheet.ThumbHeight * 3];
    // The source's bottom row is dark, the rest mid-grey.
    for (var i = 0; i < thumbnail.Length; i++) thumbnail[i] = i < Sheet.ThumbWidth * 3 ? (byte)10 : (byte)128;
    sheet.Place(11, thumbnail);
    // Cell 11 is the second in the second row: x = 4 + 154, top = 4 + 228.
    Check(Red(sheet, 158, 232) == 128 && Red(sheet, 307, 232) == 128, "thumbnail top row misplaced");
    Check(Red(sheet, 158, 431) == 10, "thumbnail bottom row misplaced");
    Check(Red(sheet, 157, 232) == 255 && Red(sheet, 158, 231) == 255 && Red(sheet, 308, 232) == 255, "thumbnail spills into the gutter");
    Check(Red(sheet, 4, 4) == 255, "an empty cell is not white");

    bool Inked(Sheet image, int index)
    {
        var (x, top) = (4 + index % 10 * 154, 4 + index / 10 * 228 + Sheet.ThumbHeight);
        for (var row = top; row < top + 24; row++)
            for (var column = x; column < x + Sheet.ThumbWidth; column++)
                if (Red(image, column, row) < 128) return true;
        return false;
    }
    Check(Inked(sheet, 0) && Inked(sheet, 81), "a label is missing");

    // The longest label stays inside its cell, with its first and last glyphs drawn.
    var longest = new Sheet(new[] { new string('W', Sheet.MaxLabel) });
    var ink = Enumerable.Range(0, longest.Width).Where(x =>
        Enumerable.Range(4 + Sheet.ThumbHeight, 24).Any(top => Red(longest, x, top) < 128)).ToArray();
    Check(ink.Length > 0 && ink.Min() >= 4 && ink.Max() < 4 + Sheet.ThumbWidth, "the longest label leaves its cell");
    Check(ink.Min() < 4 + 10 && ink.Max() >= 4 + Sheet.ThumbWidth - 10, "the longest label is not centred across its cell");
    foreach (var bad in new[] { new string('W', Sheet.MaxLabel + 1), "a-b", "" })
    {
        try
        {
            var drawn = new Sheet(new[] { bad });
            Check(false, $"drew label {bad}");
        }
        catch (ArgumentException)
        {
        }
    }
});

return failures == 0 ? 0 : 1;
