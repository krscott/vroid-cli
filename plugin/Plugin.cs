using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace VroidBridge;

[BepInPlugin("vroid-cli.bridge", "vroid-cli bridge", "0.1.0")]
public sealed class Plugin : BasePlugin
{
    private static readonly ManualLogSource Diagnostics = BepInEx.Logging.Logger.CreateLogSource("VroidBridge");
    private static RpcDirectory rpc = null!;
    private static object? main;
    private static object? root;
    private static bool updating;
    private static bool modified;
    private static long rebuilt;
    private static int frame = -1;
    private static readonly Dictionary<string, object> Selectors = new();
    private static Request? current;
    private static IEnumerator<bool>? operation;
    private static object? result;
    private static readonly Stopwatch Elapsed = new();
    private static readonly Stopwatch SinceHeartbeat = new();

    public override void Load()
    {
        var directory = Environment.GetEnvironmentVariable("VROID_BRIDGE_DIR")
            ?? throw new InvalidOperationException("VROID_BRIDGE_DIR is required");
        rpc = new RpcDirectory(directory, Guid.NewGuid().ToString("N"), Status);
        rpc.RecoverInterrupted();
        var harmony = new Harmony("vroid-cli.bridge");
        Patch(harmony, "VRoid.Studio.EditRootScreen.ViewModel", "OnScreenActivate", nameof(CaptureRoot));
        Patch(harmony, "VRoid.Studio.EditRootScreen.ViewModel", "BackToModelSelect", nameof(LeaveEditor));
        Patch(harmony, "VRoid.Studio.ModelPresetsSelector.BasePresetsSelectorViewModel", "UpdateSelectedPresets", nameof(CaptureSelector));
        Patch(harmony, "VRoid.Studio.Component.VRoidCoreModelUpdater", "set_IsUpdatingModel", nameof(ModelUpdating));
        Patch(harmony, "UnityEngine.EventSystems.EventSystem", "Update", nameof(Tick));
        Diagnostics.LogInfo($"vroid-cli bridge loaded; boot {rpc.Boot}");
    }

    private static void Patch(Harmony harmony, string type, string method, string callback)
    {
        var found = Reflect.Type(type)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(info => info.Name == method).ToArray();
        if (found.Length == 0) throw new MissingMethodException(type, method);
        foreach (var original in found)
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(callback)));
    }

    public static void CaptureRoot(object __instance)
    {
        root = __instance;
        main = Reflect.Get(root, "_mainViewModel") ?? main;
    }

    public static void LeaveEditor()
    {
        root = null;
        Selectors.Clear();
        updating = false;
    }

    public static void CaptureSelector(object __instance)
    {
        var value = Reflect.Concrete(__instance);
        Selectors[value.GetType().FullName!] = value;
    }

    // A completed rebuild is the only completion signal for opens and edits.
    public static void ModelUpdating(bool value)
    {
        if (!value && updating) rebuilt++;
        updating = value;
    }

    // Runs once per frame on Unity's main thread, so every read and mutation does too.
    public static void Tick()
    {
        try
        {
            var now = (int)Reflect.Type("UnityEngine.Time").GetProperty("frameCount")!.GetValue(null)!;
            if (now == frame) return;
            frame = now;
            Reflect.Type("UnityEngine.Application").GetProperty("runInBackground")!.SetValue(null, true);
            if (!SinceHeartbeat.IsRunning || SinceHeartbeat.ElapsedMilliseconds >= 1000)
            {
                main ??= FindMainViewModel();
                Try(rpc.WriteStatus, "write status");
                SinceHeartbeat.Restart();
            }
            if (operation == null && (current = rpc.Take()) != null)
            {
                Elapsed.Restart();
                result = null;
                operation = Execute(current).GetEnumerator();
            }
            if (operation != null)
            {
                // Checked between frames: a single blocking call can run past it.
                if (Elapsed.ElapsedMilliseconds > current!.TimeoutMs)
                    throw new TimeoutException("Engine operation timed out; outcome may be incomplete. Inspect state before another edit.");
                if (!operation.MoveNext()) Complete(true, result);
            }
        }
        catch (Exception error)
        {
            if (current != null) Complete(false, (error.InnerException ?? error).Message);
            else Diagnostics.LogError(error);
        }
    }

    // Constructor hooks miss the main view model; the live editor view holds it.
    private static object? FindMainViewModel()
    {
        var find = Reflect.Type("UnityEngine.Resources").GetMethods()
            .Single(method => method.Name == "FindObjectsOfTypeAll" && method.IsGenericMethodDefinition);
        var views = find.MakeGenericMethod(Reflect.Type("VRoidStudio.GUI.AvatarEditor.AvatarEditor")).Invoke(null, null);
        return Reflect.Items(views).Select(view => Reflect.Get(view, "_viewModel")).FirstOrDefault(value => value != null);
    }

    private static object Status() => new
    {
        boot = rpc.Boot,
        protocol = RpcDirectory.Protocol,
        heartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        main = main != null,
        editor = root != null,
        updating,
        modified,
        rebuilt,
    };

    private static bool Try(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error)
        {
            Diagnostics.LogError($"Could not {what}: {error}");
            return false;
        }
    }

    private static void Complete(bool ok, object? payload)
    {
        var request = current!;
        current = null;
        operation?.Dispose();
        operation = null;
        rpc.Finish(request, ok, payload);
    }

    private static object Main => main ?? throw new InvalidOperationException("App is not ready");
    private static object Root => root ?? throw new InvalidOperationException("Open a project first");

    private static T Single<T>(IEnumerable<T> items, Func<T, bool> match, string kind, string name)
    {
        var found = items.Where(match).ToArray();
        return found.Length switch
        {
            1 => found[0],
            0 => throw new ArgumentException($"Unknown {kind} {name}; list them first"),
            _ => throw new ArgumentException($"{found.Length} {kind}s are named {name}; it cannot be selected by name"),
        };
    }

    private static IEnumerable<bool> Execute(Request request)
    {
        while (updating) yield return true;
        switch (request.Method)
        {
            case "open":
            {
                if (root != null) throw new InvalidOperationException("A project is already open; save, then restart the session to open another");
                var path = request.Text("path");
                if (!File.Exists(path)) throw new ArgumentException("Project must be an existing file");
                var before = rebuilt;
                Reflect.Call(Main, "ImportFileAndMoveToEditRoot", path, false, false);
                while (root == null || rebuilt == before || updating) yield return true;
                modified = false;
                result = new { opened = path };
                break;
            }
            case "categories":
            case "parameters":
            case "set":
            {
                var mode = request.Text("section") switch
                {
                    "Face" => "FaceEditor",
                    "Body" => "BodyEditor",
                    _ => throw new ArgumentException("Only Face and Body are supported"),
                };
                Reflect.Call(Root, "set_EditMode", Enum.Parse(Reflect.Type("VRoid.Studio.EditRootScreen.EditMode"), mode));
                object? selector;
                while ((selector = Selectors.Values.FirstOrDefault(value => value.GetType().FullName!.Contains("." + mode + "."))) == null)
                    yield return true;
                var categories = Reflect.Items(Reflect.Get(Reflect.Get(selector, "Categories"), "Children")).ToArray();
                if (request.Method == "categories")
                {
                    result = categories.Select(value => Reflect.Get(value, "Name")).ToArray();
                    break;
                }
                var name = request.Text("category");
                var category = Single(categories, value => (string?)Reflect.Get(value, "Name") == name, "category", name);
                Reflect.Call(selector, "set_SelectedCategory", category);
                yield return true;
                while (updating) yield return true;
                var node = Reflect.Get(category, "ParameterCategoryViewModel") ?? category;
                var parameters = Reflect.Items(Reflect.Get(node, "Parameters")).ToArray();
                if (request.Method == "parameters")
                {
                    result = parameters.Select(value => new
                    {
                        label = Reflect.Get(value, "Label"),
                        value = Reflect.Get(value, "CombinedValue") ?? Reflect.Get(value, "Value"),
                        min = Reflect.Get(value, "MinValue"),
                        max = Reflect.Get(value, "MaxValue"),
                    }).ToArray();
                    break;
                }
                var label = request.Text("parameter");
                var parameter = Single(parameters, value => (string?)Reflect.Get(value, "Label") == label, "parameter", label);
                var target = (float)request.Number("value");
                var min = Convert.ToSingle(Reflect.Get(parameter, "MinValue"));
                var max = Convert.ToSingle(Reflect.Get(parameter, "MaxValue"));
                if (!float.IsFinite(target) || target < min || target > max)
                    throw new ArgumentException($"Value must be finite and within {min}..{max}");
                var member = Reflect.Get(parameter, "CombinedValue") == null ? "Value" : "CombinedValue";
                var previous = Convert.ToSingle(Reflect.Get(parameter, member));
                if (previous != target)
                {
                    var before = rebuilt;
                    modified = true;
                    Reflect.Call(parameter, "set_" + member, target);
                    while (rebuilt == before || updating) yield return true;
                }
                result = new { parameter = label, previous, value = Reflect.Get(parameter, member) };
                break;
            }
            case "save":
            {
                var path = request.Text("path");
                if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Refusing to overwrite destination");
                var handler = Reflect.Get(Root, "_mainActionHandler")
                    ?? throw new InvalidOperationException("Save handler is unavailable");
                if (Reflect.Call(handler, "SaveSync", path, true) is not true)
                    throw new IOException("VRoid refused to save the project");
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new IOException("VRoid reported success without writing the project");
                modified = false;
                result = new { saved = path, bytes = new FileInfo(path).Length };
                break;
            }
            default:
                throw new ArgumentException($"Unknown method: {request.Method}");
        }
    }
}
