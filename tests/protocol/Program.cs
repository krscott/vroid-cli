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

// Framing: a sample character's bounds, as VRoid reports them, facing +Z or turned.
var low = new Vec(-0.48, 0.0, -0.38);
var high = new Vec(0.48, 1.5, 0.33);
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

IEnumerable<Vec> Corners(Vec min, Vec max) =>
    from x in new[] { min.X, max.X }
    from y in new[] { min.Y, max.Y }
    from z in new[] { min.Z, max.Z }
    select new Vec(x, y, z);

// R-VIEW-1
Test("every body view shows the whole model with a margin", _ =>
{
    foreach (var forward in forwards)
        foreach (var aspect in aspects)
            foreach (var view in views)
            {
                var shot = Framing.Body(low, high, forward, view, aspect);
                foreach (var corner in Corners(low, high))
                {
                    var (right, up, depth) = Project(shot, corner);
                    var where = $"{view} at aspect {aspect}: corner {corner}";
                    Check(Math.Abs(right) <= shot.Size * aspect / Framing.Margin + 1e-9, $"{where} is outside the width");
                    Check(Math.Abs(up) <= shot.Size / Framing.Margin + 1e-9, $"{where} is outside the height");
                    Check(depth > shot.Near && depth < shot.Far, $"{where} is clipped");
                }
            }
});

// R-VIEW-1
Test("body views share one scale and ground line", _ =>
{
    foreach (var forward in forwards)
        foreach (var aspect in aspects)
        {
            var shots = views.Select(view => Framing.Body(low, high, forward, view, aspect)).ToArray();
            Check(shots.All(shot => Math.Abs(shot.Size - shots[0].Size) < 1e-9), $"scale differs at aspect {aspect}");
            Check(shots.All(shot => Math.Abs(shot.Target.Y - shots[0].Target.Y) < 1e-9), $"ground line differs at aspect {aspect}");
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
Test("a head frame holds the neck and everything above it", _ =>
{
    var neck = new Vec(0, 1.19, -0.03);
    var head = new Vec(0.01, 1.25, -0.02);
    foreach (var aspect in aspects)
        foreach (var view in views)
        {
            var shot = Framing.Head(low, high, neck, head, forwards[0], view, aspect);
            Check(Math.Abs(shot.Target.X - head.X) < 1e-9 && Math.Abs(shot.Target.Z - head.Z) < 1e-9, "not centred on the head");
            foreach (var point in new[] { neck, new Vec(head.X, high.Y, head.Z) })
            {
                var (_, up, depth) = Project(shot, point);
                Check(Math.Abs(up) <= shot.Size / Framing.Margin + 1e-9, $"{view} at aspect {aspect} cuts off {point}");
                Check(depth > shot.Near && depth < shot.Far, $"{view} at aspect {aspect} clips {point}");
            }
            // The frame is at least as wide as it is tall.
            Check(shot.Size * aspect >= (high.Y - neck.Y) / 2 * Framing.Margin - 1e-9, $"{view} at aspect {aspect} is narrower than the head");
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
