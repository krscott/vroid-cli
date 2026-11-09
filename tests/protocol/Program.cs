// Exercises plugin/Bridge.cs, the app side of the file protocol, and
// plugin/Colors.cs, the bridge's colour rules, without Unity.
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

return failures == 0 ? 0 : 1;
