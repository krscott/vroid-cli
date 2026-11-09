// Exercises plugin/Bridge.cs, the app side of the file protocol, and the
// camera placement in plugin/Framing.cs, without Unity.
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
var parts = new[] { body, skull, ears };
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
Test("a head view shows every mesh above the neck, not the arms", _ =>
{
    foreach (var forward in forwards)
        foreach (var aspect in aspects)
            foreach (var view in views)
            {
                var shot = Framing.Head(parts, neck, head, forward, view, aspect);
                Check(Math.Abs(shot.Target.X - head.X) < 1e-9 && Math.Abs(shot.Target.Z - head.Z) < 1e-9, "not centred on the head");
                Shows(shot, aspect, new[] { skull, ears }, $"{view} head");
                Check(Math.Abs(shot.Target.Y - (neck.Y + skull.Max.Y) / 2) < 1e-9, $"{view} head is not centred from the neck up");
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

return failures == 0 ? 0 : 1;
