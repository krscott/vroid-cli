using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
    // Values the current operation changed, reported if it fails.
    private static readonly List<string> Changed = new();
    private static readonly Stopwatch Elapsed = new();
    private static readonly Stopwatch SinceHeartbeat = new();

    public override void Load()
    {
        var directory = Environment.GetEnvironmentVariable("VROID_BRIDGE_DIR")
            ?? throw new InvalidOperationException("VROID_BRIDGE_DIR is required");
        rpc = new RpcDirectory(directory, Guid.NewGuid().ToString("N"), Status, Diagnostics.LogError);
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
                Changed.Clear();
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
            if (current != null)
            {
                var message = (error.InnerException ?? error).Message;
                if (Changed.Count > 0) message += $" Changed before the failure: {string.Join(", ", Changed)}";
                Complete(false, message);
            }
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

    private static readonly string[] Sections = { "Face", "Body" };

    // A label that occurs more than once in a category takes its values as an array, in app order.
    private sealed record Wanted(string Label, double[] Values, bool Repeated);
    private sealed record CategoryEdit(string Name, Wanted[] Values);
    private sealed record Category(string Name, object[] Parameters);
    private sealed record Target(string Section, Category Category, List<(object Parameter, float Value, string Path)> Matched);

    private static string Name(object category) => (string?)Reflect.Get(category, "Name") ?? "";
    private static string Label(object parameter) => (string?)Reflect.Get(parameter, "Label") ?? "";
    private static string Member(object parameter) => Reflect.Get(parameter, "CombinedValue") == null ? "Value" : "CombinedValue";
    private static float Read(object parameter) => Convert.ToSingle(Reflect.Get(parameter, Member(parameter))
        ?? throw new InvalidOperationException($"Parameter {Label(parameter)} has no value"));

    private static object Selector(string section) => Selectors.Values.FirstOrDefault(
        value => value.GetType().FullName!.Contains("." + section + "Editor.")) ?? throw new InvalidOperationException("No selector");

    // Switches to a section's editor and returns its categories once its selector is captured.
    private static IEnumerable<object[]?> Enter(string section)
    {
        if (!Sections.Contains(section)) throw new ArgumentException("Only Face and Body are supported");
        Reflect.Call(Root, "set_EditMode", Enum.Parse(Reflect.Type("VRoid.Studio.EditRootScreen.EditMode"), section + "Editor"));
        while (!Selectors.Values.Any(value => value.GetType().FullName!.Contains("." + section + "Editor.")))
            yield return null;
        yield return Reflect.Items(Reflect.Get(Reflect.Get(Selector(section), "Categories"), "Children")).ToArray();
    }

    // Selects each named category, or every category in app order, and yields
    // it while it is selected; null means wait a frame. A name that matches no
    // single category adds a message to `errors` and is skipped.
    private static IEnumerable<Category?> Visit(string section, IEnumerable<string>? names, List<string> errors)
    {
        object[]? categories = null;
        foreach (var step in Enter(section))
        {
            if (step == null) yield return null;
            else categories = step;
        }
        var selected = names == null ? categories!.ToList() : new List<object>();
        foreach (var name in names ?? Array.Empty<string>())
        {
            var found = categories!.Where(value => Name(value) == name).ToArray();
            if (found.Length == 1) selected.Add(found[0]);
            else if (found.Length == 0) errors.Add($"{section}/{name}: unknown category; list them first");
            else errors.Add($"{section}/{name}: {found.Length} categories have this name; it cannot be selected by name");
        }
        foreach (var category in selected)
        {
            Reflect.Call(Selector(section), "set_SelectedCategory", category);
            yield return null;
            while (updating) yield return null;
            var node = Reflect.Get(category, "ParameterCategoryViewModel") ?? category;
            yield return new Category(Name(category), Reflect.Items(Reflect.Get(node, "Parameters")).ToArray());
        }
    }

    private static CategoryEdit[] Edits(Request request, string section) =>
        request.Body.TryGetProperty(section, out var categories)
            ? categories.EnumerateObject().Select(category => new CategoryEdit(category.Name,
                category.Value.EnumerateObject().Select(parameter => parameter.Value.ValueKind == JsonValueKind.Array
                    ? new Wanted(parameter.Name, parameter.Value.EnumerateArray().Select(value => value.GetDouble()).ToArray(), true)
                    : new Wanted(parameter.Name, new[] { parameter.Value.GetDouble() }, false)).ToArray())).ToArray()
            : Array.Empty<CategoryEdit>();

    // Resolves each wanted value to its parameter and checks its bounds,
    // adding a message to `errors` for each value it cannot set (R-EDIT-3).
    private static List<(object Parameter, float Value, string Path)> Match(string section, Category category, CategoryEdit edit, List<string> errors)
    {
        var matched = new List<(object, float, string)>();
        foreach (var wanted in edit.Values)
        {
            var path = $"{section}/{category.Name}/{wanted.Label}";
            var found = category.Parameters.Where(parameter => Label(parameter) == wanted.Label).ToArray();
            if (found.Length == 0)
            {
                errors.Add($"{path}: unknown parameter; list them first");
                continue;
            }
            if (found.Length != wanted.Values.Length || (found.Length == 1 && wanted.Repeated))
            {
                errors.Add(found.Length == 1
                    ? $"{path}: give one number"
                    : $"{path}: {found.Length} parameters have this label; give an array of {found.Length} numbers in app order");
                continue;
            }
            foreach (var (parameter, index) in found.Select((parameter, index) => (parameter, index)))
            {
                var target = wanted.Values[index];
                var min = Convert.ToSingle(Reflect.Get(parameter, "MinValue"));
                var max = Convert.ToSingle(Reflect.Get(parameter, "MaxValue"));
                var where = wanted.Repeated ? $"{path}[{index}]" : path;
                // Compared as the float VRoid stores, so a dumped bound such as 0.7 stays
                // in range; a value beyond float range becomes infinite, so out of range.
                var single = (float)target;
                if (!double.IsFinite(target)) errors.Add($"{where}: value is not finite");
                else if (single < min || single > max) errors.Add($"{where}: {target} is outside {min}..{max}");
                else matched.Add((parameter, single, where));
            }
        }
        return matched;
    }

    // Values grouped by label in app order; repeated labels become arrays.
    private static Dictionary<string, object> Values(IEnumerable<object> parameters) =>
        parameters.GroupBy(Label).ToDictionary(group => group.Key,
            group => group.Count() == 1 ? (object)Read(group.First()) : group.Select(Read).ToArray());

    // Visits each edited category with the values it resolved; null means wait a frame.
    private static IEnumerable<Target?> Targets(Dictionary<string, CategoryEdit[]> edits, List<string> errors)
    {
        foreach (var section in Sections.Where(section => edits[section].Length > 0))
            foreach (var category in Visit(section, edits[section].Select(edit => edit.Name), errors))
                yield return category == null ? null
                    : new Target(section, category, Match(section, category, edits[section].Single(edit => edit.Name == category.Name), errors));
    }

    private static IEnumerable<bool> Apply(Request request)
    {
        var edits = Sections.ToDictionary(section => section, section => Edits(request, section));
        // Check every value before changing any.
        var errors = new List<string>();
        var previous = Sections.ToDictionary(section => section, _ => new Dictionary<string, object>());
        foreach (var target in Targets(edits, errors))
        {
            if (target is not { } found)
            {
                yield return true;
                continue;
            }
            var parameters = found.Matched.Select(item => item.Parameter).ToHashSet();
            previous[found.Section][found.Category.Name] = Values(found.Category.Parameters.Where(parameters.Contains));
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors) + ". Nothing changed");
        var changed = 0;
        foreach (var target in Targets(edits, errors))
        {
            // The first pass found none, so the app changed underneath; stop changing it.
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
            if (target is not { } found)
            {
                yield return true;
                continue;
            }
            foreach (var (parameter, value, path) in found.Matched)
            {
                if (Read(parameter) == value) continue;
                var before = rebuilt;
                // Set first so an interrupted edit still counts as unsaved.
                modified = true;
                Reflect.Call(parameter, "set_" + Member(parameter), value);
                Changed.Add(path);
                changed++;
                while (rebuilt == before || updating) yield return true;
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        var undo = new Dictionary<string, object> { ["method"] = "apply", ["Face"] = previous["Face"], ["Body"] = previous["Body"] };
        result = new { changed, undo };
    }

    private static bool Same(object a, object b) => a is float[] x && b is float[] y ? x.SequenceEqual(y) : a.Equals(b);

    private static IEnumerable<bool> Dump()
    {
        var dump = new Dictionary<string, object> { ["method"] = "apply" };
        foreach (var section in Sections)
        {
            var categories = new Dictionary<string, object>();
            var seen = new HashSet<string>();
            // Categories share parameters by label; list each under the first category
            // that has it, and fail if a later copy disagrees.
            var listed = new Dictionary<string, (string Category, object Value)>();
            foreach (var category in Visit(section, null, new List<string>()))
            {
                if (category == null)
                {
                    yield return true;
                    continue;
                }
                // apply selects categories by name, so even an empty duplicate breaks the template.
                if (!seen.Add(category.Name))
                    throw new InvalidOperationException($"Two {section} categories are named {category.Name}; a template cannot hold both");
                var values = new Dictionary<string, object>();
                foreach (var (label, value) in Values(category.Parameters))
                {
                    if (!listed.TryGetValue(label, out var first))
                    {
                        listed[label] = (category.Name, value);
                        values[label] = value;
                    }
                    else if (!Same(first.Value, value))
                        throw new InvalidOperationException(
                            $"{section}/{category.Name}/{label} differs from {section}/{first.Category}/{label}; they are not one shared parameter");
                }
                if (values.Count > 0) categories[category.Name] = values;
            }
            dump[section] = categories;
        }
        result = dump;
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
            {
                foreach (var step in Enter(request.Text("section")))
                {
                    if (step == null) yield return true;
                    else result = step.Select(Name).ToArray();
                }
                break;
            }
            case "parameters":
            {
                var errors = new List<string>();
                foreach (var category in Visit(request.Text("section"), new[] { request.Text("category") }, errors))
                {
                    if (category == null) yield return true;
                    else result = category.Parameters.Select(value => new
                    {
                        label = Label(value),
                        value = Read(value),
                        min = Convert.ToSingle(Reflect.Get(value, "MinValue")),
                        max = Convert.ToSingle(Reflect.Get(value, "MaxValue")),
                    }).ToArray();
                }
                if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
                break;
            }
            case "dump":
                foreach (var step in Dump()) yield return step;
                break;
            case "apply":
                foreach (var step in Apply(request)) yield return step;
                break;
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
