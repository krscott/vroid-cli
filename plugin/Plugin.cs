using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace VroidBridge;

[BepInPlugin("vroid-cli.bridge", "vroid-cli bridge", "0.1.0")]
public sealed partial class Plugin : BasePlugin
{
    private static readonly ManualLogSource Diagnostics = BepInEx.Logging.Logger.CreateLogSource("VroidBridge");
    private static RpcDirectory rpc = null!;
    private static object? main;
    private static object? root;
    private static bool updating;
    // Holds ModelOrigin, the root transform of the model on screen.
    private static object? updater;
    private static bool modified;
    // Whether a request has started opening or creating a character in this
    // app instance; VRoid holds one per instance, even while it loads.
    private static bool loaded;
    private static long rebuilt;
    private static int frame = -1;
    private static readonly Dictionary<string, object> Selectors = new();
    private static Request? current;
    private static IEnumerator<bool>? operation;
    private static object? result;
    // Values the current operation changed, reported if it fails, and the
    // request that restores them, kept in its undo file (R-EDIT-11).
    private static readonly List<string> Changed = new();
    private static object? undo;
    // Whether the undo file may still name a custom accessory removed since.
    private static bool undoStale;
    // Items that are not built in and that the current operation replaced or
    // removed, which its undo cannot restore (R-EDIT-10).
    private static readonly List<string> Discarded = new();
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
    public static void ModelUpdating(object __instance, bool value)
    {
        updater = __instance;
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
                Discarded.Clear();
                undo = null;
                undoStale = false;
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
                // Ends the operation first, so its cleanup, which records what was
                // discarded and rewrites the undo file, runs before the message is written.
                EndOperation();
                var message = (error.InnerException ?? error).Message;
                if (Changed.Count > 0)
                {
                    message += $" Changed before the failure: {string.Join(", ", Changed)}.";
                    if (Discarded.Count > 0) message += $" Discarded, which undo cannot restore: {string.Join(", ", Discarded)}.";
                }
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

    // Runs the operation's pending cleanup; a failing cleanup never keeps a
    // result from being published.
    private static void EndOperation()
    {
        try
        {
            operation?.Dispose();
        }
        catch (Exception error)
        {
            Diagnostics.LogError($"Could not end request {current?.Id}: {error}");
        }
        operation = null;
    }

    private static void Complete(bool ok, object? payload)
    {
        var request = current!;
        EndOperation();
        current = null;
        if (undoStale)
        {
            var gone = Discarded.Where(path => path.StartsWith("Accessories/"));
            payload = $"{(ok ? "apply ran." : payload)} Its undo file could not be updated and may still list the removed accessories "
                + $"{string.Join(", ", gone)}; remove them from it before applying it";
            ok = false;
        }
        rpc.Finish(request, ok, payload);
    }

    // Rewrites the undo file after a custom accessory was dropped from `undo`,
    // so a crash leaves a file that still applies (R-EDIT-11).
    private static void SaveUndo()
    {
        // The latest write holds the whole template, so only its outcome counts.
        if (current != null && undo != null)
            undoStale = !Try(() => rpc.WriteUndo(current, undo), "update the undo file");
    }

    private static object Main => main ?? throw new InvalidOperationException("App is not ready");
    private static object Root => root ?? throw new InvalidOperationException("Open or create a character first");

    // In app order: the editor tabs run Face, Hairstyle, Body, Outfit.
    private static readonly string[] Sections = { "Face", "Hair", "Body", "Outfit" };

    // The editor behind a section; VRoid calls the Outfit editor the clothing editor.
    private static string Editor(string section) => (section == "Outfit" ? "Clothing" : section) + "Editor";

    // A label that occurs more than once in a category takes its values as an
    // array, in app order. A colour is "#RRGGBB" and has no numeric values.
    private sealed record Wanted(string Label, double[] Values, bool Repeated, string[]? Colors);
    // `Item` is the wanted item's identifier, "custom", or null to take it off;
    // `HasItem` is false when the request leaves the item alone.
    private sealed record CategoryEdit(string Name, Wanted[] Values, bool HasItem, string? Item);
    // A colour field: `Get` reads its UnityEngine.Color, `Set` writes one, and
    // a write changes the model values named by `Keys`.
    private sealed record Swatch(string Label, Func<object?> Get, Action<object> Set, HashSet<string> Keys);

    // A colour field held in `owner.member`.
    private static Swatch Field(string label, object owner, string member, HashSet<string> keys) =>
        new(label, () => Reflect.Get(owner, member), color => Reflect.Call(owner, "set_" + member, color), keys);
    // `Refused` maps colour labels the panel shows but apply cannot set to the reason.
    private sealed record Category(string Name, int Index, object Node, object[] Parameters, Swatch[] Swatches,
        Dictionary<string, string> Refused);
    private sealed record Paint(Swatch Swatch, string Hex, ColorField Field);
    private sealed record Target(string Section, Category Category,
        List<(object Parameter, float Value, string Path)> Matched, List<Paint> Painted);

    private static string Name(object category) => (string?)Reflect.Get(category, "Name") ?? "";
    private static string Label(object parameter) => (string?)Reflect.Get(parameter, "Label") ?? "";
    private static string Member(object parameter) => Reflect.Get(parameter, "CombinedValue") == null ? "Value" : "CombinedValue";
    private static float Read(object parameter) => Convert.ToSingle(Reflect.Get(parameter, Member(parameter))
        ?? throw new InvalidOperationException($"Parameter {Label(parameter)} has no value"));

    // A numeric parameter with its bounds, as `parameters` and `accessories` list it.
    private static object Slider(object parameter) => new
    {
        label = Label(parameter),
        value = Read(parameter),
        min = Convert.ToSingle(Reflect.Get(parameter, "MinValue")),
        max = Convert.ToSingle(Reflect.Get(parameter, "MaxValue")),
    };

    private static object Selector(string section) => Selectors.Values.FirstOrDefault(
        value => value.GetType().FullName!.Contains("." + Editor(section) + ".")) ?? throw new InvalidOperationException("No selector");

    // Switches to a section's editor and returns its categories once its selector is captured.
    private static IEnumerable<object[]?> Enter(string section)
    {
        if (!Sections.Contains(section)) throw new ArgumentException("Only Face, Hair, Body, and Outfit are supported");
        Reflect.Call(Root, "set_EditMode", Enum.Parse(Reflect.Type("VRoid.Studio.EditRootScreen.EditMode"), Editor(section)));
        while (!Selectors.Values.Any(value => value.GetType().FullName!.Contains("." + Editor(section) + ".")))
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
        var index = categories!.Select((value, position) => (value, position)).ToDictionary(pair => pair.value, pair => pair.position);
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
            var (swatches, refused) = Swatches(section, category, index[category]);
            yield return new Category(Name(category), index[category], category, Reflect.Items(Reflect.Get(node, "Parameters")).ToArray(),
                swatches, refused);
        }
    }

    // The selected category's colour fields that apply can set, and the reason
    // for each other field its editor panel shows.
    private static (Swatch[], Dictionary<string, string>) Swatches(string section, object node, int index)
    {
        var category = Name(node);
        var shown = new List<(Swatch Swatch, bool Available)>();
        string Unknown(string what) => $"Cannot determine {what} for {section}/{category}: this VRoid differs from the validated 2.16.0";
        var colors = Reflect.Get(Selector(section), "CategoryColorSelection");
        foreach (var selection in Reflect.Items(Reflect.Get(colors, "_colorSelections")))
        {
            if (Reflect.Get(selection, "IsAvailable") is not bool available)
                throw new InvalidOperationException(Unknown("colour availability"));
            foreach (var sub in Reflect.Items(Reflect.Get(selection, "_subColorSelections")))
            {
                var label = (string?)Reflect.Get(sub, "TranslatedDisplayName") ?? "";
                // Fields that write the same keys show one shared colour, such as skin
                // in Face and Body; without the keys, sharing cannot be told apart.
                var keys = Reflect.Items(Reflect.Get(Reflect.Get(Reflect.Get(sub, "_model"), "Accessor"), "_keysToSet"))
                    .Select(key => (Group: Reflect.Get(key, "TransferableGroupType") as string, Key: Reflect.Get(key, "Key") as string))
                    .ToArray();
                if (keys.Length == 0 || keys.Any(key => string.IsNullOrEmpty(key.Group) || string.IsNullOrEmpty(key.Key)))
                    throw new InvalidOperationException(Unknown($"shared colours for {label}"));
                shown.Add((Field(label, sub, "Color", keys.Select(key => $"{key.Group}:{key.Key}").ToHashSet()), available));
            }
        }
        if (section == "Hair")
        {
            // One hair material colour pair covers every hair category. Hairstyle
            // Sets shows an aggregate colour instead, and Base Hair its own above.
            var hair = Reflect.Get(Root, "HairEditor");
            if (Reflect.Get(hair, "IsHairSet") is not bool set || Reflect.Get(hair, "IsBaseHair") is not bool baseHair)
                throw new InvalidOperationException(Unknown("hair colours"));
            if (!set && !baseHair)
            {
                shown.Add((Field("Main Color", hair!, "HairMaterialTemplateColor", new() { "HairMaterial:Main" }), true));
                shown.Add((Field("Highlight Color", hair!, "HairMaterialTemplateEmissionColor", new() { "HairMaterial:Highlight" }), true));
            }
        }
        var swatches = new List<Swatch>();
        var refused = new Dictionary<string, string>();
        if (section == "Outfit" && OutfitSwatches(node, index) is var (outfit, builtIn))
        {
            // Only a built-in item's colours reproduce in another project.
            if (builtIn) shown.AddRange(outfit.Select(part => (part, true)));
            else foreach (var part in outfit) refused[part.Label] = NotBuiltIn;
        }
        foreach (var (swatch, available) in shown)
        {
            // A field such as "Both Eyes" writes several colours and reads only one of
            // them, so neither skipping an equal value nor its undo would be exact.
            var parts = shown.Where(other => other.Swatch.Keys.IsProperSubsetOf(swatch.Keys)).Select(other => other.Swatch.Label).ToArray();
            if (!available) refused[swatch.Label] = Unavailable;
            else if (parts.Length > 0) refused[swatch.Label] = $"sets {string.Join(" and ", parts)} together; set those instead";
            else swatches.Add(swatch);
        }
        return (swatches.ToArray(), refused);
    }

    private const string Unavailable = "unavailable for the selected item";

    private static object ColorOf(Swatch swatch) => swatch.Get()
        ?? throw new InvalidOperationException($"Colour {swatch.Label} has no value");

    // The hex VRoid's editor shows for the field.
    private static string ReadHex(Swatch swatch, string path)
    {
        var text = (string?)Reflect.Type("VRoid.UI.Util.ColorUtil").GetMethod("ColorToColorHex")!
            .Invoke(null, new[] { ColorOf(swatch) });
        return Colors.Normalize("#" + text?.TrimStart('#'))
            ?? throw new InvalidOperationException($"{path}: VRoid shows the colour as {text}, not RRGGBB");
    }

    // Sets the red, green, and blue channels and keeps the alpha.
    private static void Write(Swatch swatch, float[] channels)
    {
        var alpha = Convert.ToSingle(Reflect.Get(ColorOf(swatch), "a"));
        var color = Activator.CreateInstance(Reflect.Type("UnityEngine.Color"), channels[0], channels[1], channels[2], alpha)!;
        swatch.Set(color);
    }

    private static CategoryEdit[] Edits(Request request, string section) =>
        request.Body.TryGetProperty(section, out var categories)
            ? categories.EnumerateObject().Select(category => new CategoryEdit(category.Name, WantedValues(category.Value, new[] { "item" }),
                category.Value.TryGetProperty("item", out var item), item.ValueKind == JsonValueKind.String ? item.GetString() : null)).ToArray()
            : Array.Empty<CategoryEdit>();

    // The values in a template object by label, except its `reserved` keys.
    private static Wanted[] WantedValues(JsonElement values, string[] reserved) =>
        values.EnumerateObject().Where(parameter => !reserved.Contains(parameter.Name)).Select(parameter => parameter.Value.ValueKind switch
        {
            // The CLI checks that an array holds only numbers or only colours.
            JsonValueKind.Array when parameter.Value.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String) =>
                new Wanted(parameter.Name, Array.Empty<double>(), true,
                    parameter.Value.EnumerateArray().Select(value => value.GetString()!).ToArray()),
            JsonValueKind.Array => new Wanted(parameter.Name,
                parameter.Value.EnumerateArray().Select(value => value.GetDouble()).ToArray(), true, null),
            JsonValueKind.String => new Wanted(parameter.Name, Array.Empty<double>(), false, new[] { parameter.Value.GetString()! }),
            _ => new Wanted(parameter.Name, new[] { parameter.Value.GetDouble() }, false, null),
        }).ToArray();

    // Resolves each wanted value to its parameter or colour and checks its
    // bounds or kind, adding a message to `errors` for each value it cannot set
    // (R-EDIT-3). When the request may change the category's item, it skips
    // colours the next item may offer differently: in Outfit every colour,
    // since each item brings its own parts, and elsewhere colours unavailable
    // for the current item. The pass after the items change checks them
    // (R-EDIT-13). `prefix` names the category in messages, SECTION/CATEGORY
    // by default.
    private static Target Match(string section, Category category, CategoryEdit edit, List<string> errors, bool itemMayChange,
        string? prefix = null)
    {
        var matched = new List<(object, float, string)>();
        var painted = new List<Paint>();
        prefix ??= $"{section}/{category.Name}";
        foreach (var wanted in edit.Values)
        {
            var path = $"{prefix}/{wanted.Label}";
            var found = category.Parameters.Where(parameter => Label(parameter) == wanted.Label).ToArray();
            var swatches = category.Swatches.Where(swatch => swatch.Label == wanted.Label).ToArray();
            if (found.Length > 0 && swatches.Length > 0)
            {
                errors.Add($"{path}: is both a number and a colour; it cannot be set by label");
                continue;
            }
            var refused = category.Refused.TryGetValue(wanted.Label, out var reason) ? $"{path}: {reason}" : null;
            if (itemMayChange && (section == "Outfit" ? wanted.Colors != null : reason == Unavailable))
            {
                if (wanted.Colors == null) errors.Add($"{path}: is a colour; give \"#RRGGBB\"");
                continue;
            }
            if (wanted.Colors is { } colors)
            {
                var hexes = colors.Select(Colors.Normalize).ToArray();
                if (swatches.Length == 0)
                    errors.Add(refused ?? (found.Length > 0 ? $"{path}: is a number; give a number" : $"{path}: unknown parameter; list them first"));
                else if (swatches.Length != colors.Length || (swatches.Length == 1 && wanted.Repeated))
                    errors.Add(swatches.Length == 1 ? $"{path}: give one colour"
                        : $"{path}: {swatches.Length} colours have this label; give an array of {swatches.Length} colours in app order");
                else if (hexes.Any(hex => hex == null))
                    errors.Add($"{path}: {string.Join(", ", colors.Where((_, at) => hexes[at] == null))} is not #RRGGBB");
                else
                    foreach (var (swatch, at) in swatches.Select((swatch, at) => (swatch, at)))
                    {
                        var where = wanted.Repeated ? $"{path}[{at}]" : path;
                        painted.Add(new Paint(swatch, hexes[at]!, new ColorField(where, swatch.Keys, ReadHex(swatch, where))));
                    }
                continue;
            }
            if (found.Length == 0)
            {
                errors.Add(swatches.Length switch
                {
                    1 => $"{path}: is a colour; give \"#RRGGBB\"",
                    > 1 => $"{path}: is {swatches.Length} colours; give an array of {swatches.Length} \"#RRGGBB\" in app order",
                    _ => refused ?? $"{path}: unknown parameter; list them first",
                });
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
        return new Target(section, category, matched, painted);
    }

    // Values grouped by label in app order; repeated labels become arrays.
    private static Dictionary<string, object?> Values(IEnumerable<object> parameters) =>
        parameters.GroupBy(Label).ToDictionary(group => group.Key,
            group => group.Count() == 1 ? (object?)Read(group.First()) : group.Select(Read).ToArray());

    // Visits each edited category with the values it resolved; null means wait a frame.
    private static IEnumerable<Target?> Targets(Dictionary<string, CategoryEdit[]> edits, List<string> errors,
        Func<string, string, bool> itemMayChange)
    {
        // Item-only categories are checked with the items; any other, even an empty
        // one, must name a category.
        static bool Visited(CategoryEdit edit) => edit.Values.Length > 0 || !edit.HasItem;
        foreach (var section in Sections.Where(section => edits[section].Any(Visited)))
        {
            var valued = edits[section].Where(Visited).ToArray();
            foreach (var category in Visit(section, valued.Select(edit => edit.Name), errors))
                yield return category == null ? null
                    : Match(section, category, valued.Single(edit => edit.Name == category.Name), errors, itemMayChange(section, category.Name));
        }
    }

    private static IEnumerable<bool> Apply(Request request)
    {
        var edits = Sections.ToDictionary(section => section, section => Edits(request, section));
        var discard = request.Body.TryGetProperty("discard_custom", out var flag) && flag.ValueKind == JsonValueKind.True;
        // Check every item, accessory, and value before changing any.
        var errors = new List<string>();
        var work = new Outfitting();
        foreach (var step in CheckItems(request, edits, discard, work, errors)) yield return step;
        var previous = Sections.ToDictionary(section => section, _ => new Dictionary<string, Dictionary<string, object?>>());
        var colors = new List<ColorField>();
        foreach (var target in Targets(edits, errors, work.ItemMayChange))
        {
            if (target is not { } found)
            {
                yield return true;
                continue;
            }
            var parameters = found.Matched.Select(item => item.Parameter).ToHashSet();
            var values = Values(found.Category.Parameters.Where(parameters.Contains));
            // Match paints every colour of a label or none.
            foreach (var label in found.Painted.GroupBy(paint => paint.Swatch.Label))
                values[label.Key] = label.Count() > 1 ? label.Select(paint => paint.Field.Hex).ToArray() : label.Single().Field.Hex;
            previous[found.Section][found.Category.Name] = values;
            colors.AddRange(found.Painted.Select(paint => paint.Field));
        }
        errors.AddRange(Colors.Conflicts(colors));
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors) + ". Nothing changed");
        var restore = new Dictionary<string, object>();
        // A new item can reset its category's values, and VRoid changes other
        // categories' items with it, so the undo is a dump of everything.
        if (Sections.Any(work.ItemsChange))
        {
            foreach (var step in Dump(restore)) yield return step;
            // Undo cannot restore an item that is not built in, and "custom" only
            // keeps one still worn, so leaving it out changes nothing while it is
            // worn and keeps the undo applying once it is gone.
            foreach (var section in Sections)
                foreach (var values in ((Dictionary<string, object>)restore[section]).Values.Cast<Dictionary<string, object?>>())
                    if (values.TryGetValue("item", out var item) && item as string == ItemIds.Custom)
                        values.Remove("item");
        }
        else
        {
            restore["method"] = "apply";
            foreach (var section in Sections) restore[section] = previous[section];
        }
        if (work.Worn != null) restore["Accessories"] = AccessoryList(work.WornEntries);
        // Written before the first change, so a failed or interrupted apply
        // leaves it too (R-EDIT-11).
        rpc.WriteUndo(request, restore);
        undo = restore;
        foreach (var step in ChangeItems(work)) yield return step;
        foreach (var step in ChangeAccessories(work, restore)) yield return step;
        var changed = work.Changed;
        foreach (var target in Targets(edits, errors, (_, _) => false))
        {
            // The first pass found none, unless new items made a colour unavailable or
            // the app changed underneath; stop changing it.
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors) + ".");
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
            foreach (var (swatch, hex, field) in found.Painted)
            {
                if (Colors.Write(ReadHex(swatch, field.Path), hex) is not { } channels) continue;
                var before = rebuilt;
                modified = true;
                Write(swatch, channels);
                Changed.Add(field.Path);
                changed++;
                while (rebuilt == before || updating) yield return true;
                // Catches clamping or a colour-space conversion inside VRoid (R-EDIT-8).
                var actual = ReadHex(swatch, field.Path);
                if (actual != hex)
                    throw new InvalidOperationException($"{field.Path} reads {actual} after setting {hex}");
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors) + ".");
        result = new { changed, discarded = Discarded };
    }

    private static bool Same(object? a, object? b) => a is float[] x && b is float[] y ? x.SequenceEqual(y) : Equals(a, b);

    // Fills `dump` with an apply request for every item, accessory, value, and colour.
    private static IEnumerable<bool> Dump(Dictionary<string, object> dump)
    {
        var sections = new Dictionary<string, List<(string Name, Dictionary<string, object?> Values)>>();
        // Every available colour in app order, with the category values that would
        // list it and, for a repeated label, its number of colours.
        var colors = new List<(ColorField Field, Dictionary<string, object?> Values, string Label, int Count)>();
        foreach (var section in Sections)
        {
            var categories = sections[section] = new();
            var seen = new HashSet<string>();
            // Categories share parameters by label; list each under the first category
            // that has it, and fail if a later copy disagrees.
            var listed = new Dictionary<string, (string Category, object? Value)>();
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
                var values = new Dictionary<string, object?>();
                // Sets only select other categories' items, which are listed instead.
                if (SlotOf(section, category.Node, category.Index) is { IsSet: false } slot) values["item"] = State(slot);
                if (category.Parameters.Select(Label).Concat(category.Swatches.Select(swatch => swatch.Label)).Contains("item"))
                    throw new InvalidOperationException($"{section}/{category.Name} has a value labelled item; a template cannot hold it and the item");
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
                var labels = category.Parameters.Select(Label).ToHashSet();
                foreach (var label in category.Swatches.GroupBy(swatch => swatch.Label))
                {
                    var path = $"{section}/{category.Name}/{label.Key}";
                    // apply sets values by label, so a template could not hold both.
                    if (labels.Contains(label.Key))
                        throw new InvalidOperationException($"{path} is both a number and a colour; a template cannot hold both");
                    var count = label.Count() > 1 ? label.Count() : 0;
                    foreach (var (swatch, at) in label.Select((swatch, at) => (swatch, at)))
                    {
                        var where = count > 0 ? $"{path}[{at}]" : path;
                        colors.Add((new ColorField(where, swatch.Keys, ReadHex(swatch, where)), values, label.Key, count));
                    }
                }
                categories.Add((category.Name, values));
            }
        }
        // A shared colour is listed once, under the first category in app order.
        var once = Colors.Listed(colors.Select(color => color.Field).ToList()).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var (field, values, label, count) in colors.Where(color => once.Contains(color.Field)))
        {
            if (count == 0)
            {
                values[label] = field.Hex;
                continue;
            }
            if (!values.TryGetValue(label, out var list)) values[label] = list = new List<string>();
            ((List<string>)list!).Add(field.Hex);
        }
        // apply sets a repeated label as a whole array, which a colour listed
        // elsewhere would leave short.
        foreach (var color in colors.Where(color => color.Count > 0 && !once.Contains(color.Field)))
            throw new InvalidOperationException($"{color.Field.Path} is shared with another colour; a template cannot hold its label");
        dump["method"] = "apply";
        foreach (var (section, categories) in sections)
            dump[section] = categories.Where(category => category.Values.Count > 0)
                .ToDictionary(category => category.Name, category => (object)category.Values);
        foreach (var step in EnterAccessories()) yield return step;
        var worn = WornAccessories();
        dump["Accessories"] = AccessoryList(worn);
    }

    // Writes a new file and removes it again if writing or closing it fails.
    // The CLI links it into place, so an existing file is never replaced.
    private static void WriteNew(string path, byte[] bytes)
    {
        // Outside the try: a file that already exists is not ours to delete.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using (stream) stream.Write(bytes);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    // Opens or creates the character, then waits for its editor and first rebuild.
    private static IEnumerable<bool> Load(Action start)
    {
        if (loaded) throw new InvalidOperationException("A character is already open or loading; save, then restart the session to open or create another");
        loaded = true;
        var before = rebuilt;
        start();
        while (root == null || rebuilt == before || updating) yield return true;
        modified = false;
    }

    private static IEnumerable<bool> Execute(Request request)
    {
        while (updating) yield return true;
        switch (request.Method)
        {
            case "open":
            {
                var path = request.Text("path");
                if (!File.Exists(path)) throw new ArgumentException("Project must be an existing file");
                foreach (var step in Load(() => Reflect.Call(Main, "ImportFileAndMoveToEditRoot", path, false, false))) yield return step;
                result = new { opened = path };
                break;
            }
            case "new":
            {
                var name = request.Text("base");
                // The start screen's base dialog (BaseSelectDialogViewModel) offers these.
                var variant = name switch
                {
                    "Preset_F" => "F00",
                    "Preset_M" => "M00",
                    _ => throw new ArgumentException($"Unknown base {name}"),
                };
                foreach (var step in Load(() => Reflect.Call(Main, "InitializeCurrentFileAndMoveToEditRoot", name, variant))) yield return step;
                result = new { created = name };
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
                    else result = category.Parameters.Select(Slider).Concat(category.Swatches.Select(swatch => (object)new
                    {
                        label = swatch.Label,
                        value = ReadHex(swatch, $"{request.Text("section")}/{category.Name}/{swatch.Label}"),
                    })).ToArray();
                }
                if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
                break;
            }
            case "items":
            {
                Slot? slot = null;
                foreach (var step in NamedSlot(request))
                {
                    if (step == null) yield return true;
                    else slot = step;
                }
                result = new { selected = State(slot!), removable = slot!.Removable, set = slot.IsSet, items = slot.Items.Select(item => item.Id).ToArray() };
                break;
            }
            case "thumbnails":
                foreach (var step in Thumbnails(request)) yield return step;
                break;
            case "accessories":
            {
                foreach (var step in EnterAccessories()) yield return step;
                var worn = WornAccessories();
                var entries = worn.Select(AccessoryEntry).ToArray();
                var order = AccessoryOrder(entries);
                result = new
                {
                    worn = order.Select(index => entries[index]).ToArray(),
                    sliders = order.Select(index => worn[index].Parameters.Select(Slider).ToArray()).ToArray(),
                    available = AvailableAccessories().Select(accessory => new { item = accessory.Item, name = accessory.Name }).ToArray(),
                    bones = AccessoryBones(worn),
                };
                break;
            }
            case "dump":
            {
                var dump = new Dictionary<string, object>();
                foreach (var step in Dump(dump)) yield return step;
                result = dump;
                break;
            }
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
            case "render":
            {
                _ = Root;
                var origin = Reflect.Get(updater, "ModelOrigin")
                    ?? throw new InvalidOperationException("The model is not loaded; open or create a character first");
                var png = CharacterRender.Png(origin, Framing.ParseView(request.Text("view")), Framing.ParseFrame(request.Text("frame")),
                    request.Body.GetProperty("width").GetInt32(), request.Body.GetProperty("height").GetInt32());
                var path = request.Text("path");
                WriteNew(path, png);
                result = new { rendered = path, bytes = png.Length };
                break;
            }
            default:
                throw new ArgumentException($"Unknown method: {request.Method}");
        }
    }
}
