using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidBridge;

// Built-in items in the Face, Hair, Body, and Outfit selectors, and accessories.
public sealed partial class Plugin
{
    private const string TemplateItem = "VRoid.Studio.AccessoryEditor.TemplateItem.ViewModel.TransferableGroupTemplateItemViewModel";
    private const string CatalogEntry = "VRoidCore.AssetStorage.EditableAssetCatalog+TransferableGroupEntry";

    private static InvalidOperationException Differs(string what) =>
        new($"Cannot determine {what}: this VRoid differs from the validated 2.16.0");

    // A category that wears built-in items. `Presets` is its PresetCategoryViewModel;
    // `Conflicts` holds the kinds of item VRoid takes off when this one is worn.
    private sealed record Slot(string Section, string Name, int Index, object Presets, string Type, bool Removable,
        bool IsSet, HashSet<string> Conflicts, (string Id, object Option)[] Items)
    {
        public string Path => $"{Section}/{Name}";
    }

    // Null for categories without items, such as Expression Editor.
    private static Slot? SlotOf(string section, object category, int index)
    {
        var node = Reflect.Concrete(category);
        var kind = node.GetType().FullName!;
        if (kind.EndsWith(".ParametricPresetCategoryViewModel"))
            node = Reflect.Concrete(Reflect.Get(node, "PresetCategoryViewModel") ?? throw Differs($"the items of {section}/{Name(category)}"));
        else if (!kind.EndsWith(".PresetCategoryViewModel")) return null;
        var path = $"{section}/{Name(category)}";
        var items = Reflect.Items(Reflect.Get(node, "_presets")).Select(option =>
        {
            var resource = Reflect.Get(Reflect.Get(option, "_model"), "resourceName") as string;
            return (ItemIds.Preset(resource) ?? throw Differs($"the identifier of {resource} in {path}"), option);
        }).ToArray();
        if (items.Select(item => item.Item1).Distinct().Count() != items.Length) throw Differs($"unique item identifiers in {path}");
        var type = Reflect.Get(node, "TransferableGroupType") as string ?? throw Differs($"the item type of {path}");
        if (Reflect.Get(node, "IsRemovable") is not bool removable) throw Differs($"whether {path} is removable");
        var conflicts = Reflect.Items(Reflect.Get(node, "ConflictingTransferableTypes")).Select(value => ItemIds.Kind(value.ToString()!)).ToHashSet();
        return new Slot(section, Name(category), index, node, type, removable, ItemIds.IsSet(type), conflicts, items);
    }

    // Switches to a section's editor and returns the categories that wear items.
    private static IEnumerable<Slot[]?> Slots(string section)
    {
        object[]? categories = null;
        foreach (var step in Enter(section))
        {
            if (step == null) yield return null;
            else categories = step;
        }
        yield return categories!.Select((category, index) => SlotOf(section, category, index)).OfType<Slot>().ToArray();
    }

    // The category the request names, which must wear items.
    private static IEnumerable<Slot?> NamedSlot(Request request)
    {
        Slot[]? slots = null;
        foreach (var step in Slots(request.Text("section")))
        {
            if (step == null) yield return null;
            else slots = step;
        }
        var name = request.Text("category");
        var found = slots!.Where(slot => slot.Name == name).ToArray();
        if (found.Length != 1)
            throw new ArgumentException(found.Length == 0 ? $"{request.Text("section")}/{name}: no category of this name wears items; list them with categories"
                : $"{request.Text("section")}/{name}: {found.Length} categories have this name");
        yield return found[0];
    }

    // The worn built-in item's identifier, "custom" for an item that is not
    // built in, or null.
    private static string? State(Slot slot)
    {
        foreach (var (id, option) in slot.Items)
            if (Reflect.Get(option, "IsSelected") is true) return id;
        // True whenever the category wears an item, built in or not; always false for sets.
        // Some worn items that are not built in, such as a sample model's socks, show
        // no selected option at all.
        if (Reflect.Get(Reflect.Get(slot.Presets, "TemporaryCustomItem"), "WearingCustomItemSavableTransferableGroup") is not bool wearing)
            throw Differs($"whether {slot.Path} wears an item");
        return wearing ? ItemIds.Custom : null;
    }

    // Selects a built-in item, or takes the item off for null, through the
    // selector's own option, then waits for the rebuild and the selector's
    // update of every category in the section.
    private static IEnumerable<bool> Wear(Slot slot, string? id, Slot[] section)
    {
        // A set lists the kinds of item it selects as conflicts; those are not taken
        // off, and a set reads as selected again whenever its items match.
        var conflicting = id == null || slot.IsSet ? Array.Empty<Slot>()
            : section.Where(other => !other.IsSet && Conflict(slot, other)).ToArray();
        var before = rebuilt;
        modified = true;
        if (id == null) Reflect.Call(slot.Presets, "ClearTransferableGroup");
        else Reflect.Call(slot.Items.Single(item => item.Id == id).Option, "set_IsSelected", true);
        while (rebuilt == before || updating) yield return true;
        // The selector updates after a debounce, and other categories can change a few
        // frames after this one; wait until the whole section stops changing.
        string?[]? last = null;
        for (int frame = 0, stable = 0; stable < SettledFrames; frame++)
        {
            var states = section.Select(State).ToArray();
            var done = !updating && State(slot) == id && conflicting.All(other => State(other) == null);
            stable = done && last != null && states.SequenceEqual(last) ? stable + 1 : 0;
            last = states;
            if (frame > 300)
                throw new InvalidOperationException($"{slot.Path} reads {State(slot) ?? "null"} after selecting {id ?? "null"}"
                    + string.Concat(conflicting.Where(other => State(other) != null).Select(other => $", and {other.Path} still reads {State(other)}")));
            yield return true;
        }
    }

    private const int SettledFrames = 20;

    // VRoid lists each conflict on both sides in 2.16.0; checking both is defensive.
    private static bool Conflict(Slot a, Slot b) =>
        a.Conflicts.Contains(ItemIds.Kind(b.Type)) || b.Conflicts.Contains(ItemIds.Kind(a.Type));

    // A worn accessory instance: its item, VRoid's name for it, its transferable
    // and group, its available colours, its shape sliders, and its placement.
    private sealed record Worn(string Item, string Name, string Id, string Group, object Instance, Swatch[] Swatches,
        object[] Parameters, Pose Pose);

    // Where an accessory hangs: VRoid's joint name for its bone, and its position,
    // rotation quaternion (x, y, z, w), and scale relative to that bone, which
    // are the identity at VRoid's default placement. A null bone is VRoid's
    // "None", which has no placement.
    private sealed record Pose(string? Bone, float[]? Position, float[]? Rotation, float[]? Scale);

    // An accessory entry's keys that are not VRoid labels.
    private static readonly string[] PoseKeys = { "bone", "position", "rotation", "scale" };

    private static object AccessoryEditor => Reflect.Get(Root, "AccessoryEditor") ?? throw Differs("the accessory editor");

    private static IEnumerable<bool> EnterAccessories()
    {
        Reflect.Call(Root, "set_EditMode", Enum.Parse(Reflect.Type("VRoid.Studio.EditRootScreen.EditMode"), "AccessoryEditor"));
        while (Reflect.Get(AccessoryEditor, "IsFeatureActive") is not true || updating) yield return true;
    }

    private static object[] Instances() => Reflect.Items(Reflect.Get(AccessoryEditor, "Instances")).ToArray();

    // VRoid keeps one group per kind of accessory, such as glasses, so a worn
    // accessory's group names only the first of its kind; its display name's
    // localization key names its own template.
    private static Worn[] WornAccessories() => Instances().Select(instance =>
    {
        var name = Reflect.Get(instance, "TranslatedDisplayName") as string ?? "";
        var model = Reflect.Get(EditContext(instance), "editingModel") ?? throw Differs("the editing model");
        var info = Reflect.Get(instance, "transferableInfo");
        var id = Reflect.Get(info, "Id") as string ?? throw Differs($"the transferable of accessory {name}");
        var group = Reflect.Get(info, "GroupId") as string ?? throw Differs($"the group of accessory {name}");
        var display = Reflect.Call(Reflect.New("VRoidCore.Editing.Query.GetTransferableDisplayNameQuery", id), "Execute", model);
        var item = ItemIds.AccessoryKey(Reflect.Get(display, "LocalizationKey") as string);
        var swatches = new List<Swatch>();
        foreach (var selection in Reflect.Items(Reflect.Get(Reflect.Get(instance, "CategoryColorSelection"), "_colorSelections")))
        {
            if (Reflect.Get(selection, "IsAvailable") is not bool available) throw Differs($"colour availability for accessory {name}");
            if (!available) continue;
            foreach (var sub in Reflect.Items(Reflect.Get(selection, "_subColorSelections")))
                swatches.Add(Field(Reflect.Get(sub, "TranslatedDisplayName") as string ?? "", sub, "Color", new HashSet<string>()));
        }
        // VRoid's own query, which the editor's gizmo starts from; it changes nothing.
        var origin = Reflect.Call(Reflect.New("VRoidCore.Editing.Query.GetPartOriginQuery", id), "Execute", model);
        Pose pose;
        if (origin == null) pose = new Pose(null, null, null, null);
        else
        {
            var bone = Reflect.Get(Reflect.Get(origin, "ParentBoneName"), "Value") as string ?? throw Differs($"the bone of accessory {name}");
            var transform = Reflect.Get(origin, "Transform") ?? throw Differs($"the placement of accessory {name}");
            float[] Parts(string member, params string[] parts)
            {
                var value = Reflect.Get(transform, member) ?? throw Differs($"the placement of accessory {name}");
                return parts.Select(part => Convert.ToSingle(Reflect.Get(value, part) ?? throw Differs($"the placement of accessory {name}"))).ToArray();
            }
            pose = new Pose(bone, Parts("Position", "x", "y", "z"), Parts("Rotation", "x", "y", "z", "w"), Parts("Scale", "x", "y", "z"));
        }
        return new Worn(item != null && AttachmentIds.Contains(item) ? item : ItemIds.Custom, name, id, group, instance, swatches.ToArray(),
            Reflect.Items(Reflect.Get(instance, "Parameters")).ToArray(), pose);
    }).ToArray();

    private static object EditContext(object instance) =>
        Reflect.Get(Reflect.Get(instance, "_engine"), "Context") ?? throw Differs("the edit context");

    // An accessory as a template entry: its item, colours, and shape sliders by
    // label, and its placement.
    private static Dictionary<string, object?> AccessoryEntry(Worn worn)
    {
        var entry = new Dictionary<string, object?> { ["item"] = worn.Item, ["bone"] = worn.Pose.Bone };
        if (worn.Pose.Bone != null)
        {
            entry["position"] = worn.Pose.Position;
            entry["rotation"] = worn.Pose.Rotation;
            entry["scale"] = worn.Pose.Scale;
        }
        var values = worn.Swatches.Select(swatch => (swatch.Label, Value: (object?)ReadHex(swatch, $"Accessories/{worn.Name}/{swatch.Label}")))
            .Concat(Values(worn.Parameters).Select(pair => (Label: pair.Key, pair.Value)));
        foreach (var (label, value) in values)
            if (PoseKeys.Append("item").Contains(label) || !entry.TryAdd(label, value))
                throw new InvalidOperationException($"Accessory {worn.Name} has a value labelled {label}, which another value or a reserved key "
                    + "also uses; a template cannot hold both");
        return entry;
    }

    // Accessory entries in dump order, so lists compare across instances.
    private static Dictionary<string, object?>[] AccessoryList(IReadOnlyList<Dictionary<string, object?>> entries) =>
        AccessoryPlan.DumpOrder(entries.Select(entry => (string)entry["item"]!).ToList(), entries.Select(entry => JsonSerializer.Serialize(entry)).ToList())
            .Select(index => entries[index]).ToArray();

    private static Dictionary<string, object?>[] AccessoryList(Worn[] worn) => AccessoryList(worn.Select(AccessoryEntry).ToArray());

    // The bones apply accepts. The table holds VRoid 2.16.0's; any worn
    // accessory's avatar checks it.
    private static IReadOnlyList<string> AccessoryBones(Worn[] worn)
    {
        if (worn.Length > 0)
        {
            var map = Reflect.Get(Reflect.Call(worn[0].Instance, "GetAvatar"), "HumanBoneMap") ?? throw Differs("the avatar's bones");
            var live = Reflect.Items(Reflect.Get(map, "Values")).Select(bone => Reflect.Get(bone, "Value") as string).ToHashSet();
            if (!live.SetEquals(Bones.All)) throw Differs("the avatar's bones");
        }
        return Bones.All;
    }

    // The asset catalog's built-in accessory templates, which do not change
    // while the app runs.
    private static object[]? attachments;
    private static object[] Attachments() => attachments ??= Reflect.Items(Reflect.Get(
            Reflect.Get(Reflect.Get(Reflect.Get(Main, "_mainModel"), "assetStorage"), "AssetCatalog") ?? throw Differs("the asset catalog"),
            "TransferableGroupEntries"))
        .Select(entry => Reflect.As(entry, CatalogEntry))
        .Where(entry => ItemIds.Attachment(Reflect.Get(entry, "resourceName") as string) != null)
        .ToArray();
    private static HashSet<string>? attachmentIds;
    private static HashSet<string> AttachmentIds => attachmentIds ??=
        Attachments().Select(entry => ItemIds.Attachment(Reflect.Get(entry, "resourceName") as string)!).ToHashSet();
    // Each built-in accessory's kind, the group type VRoid keeps its accessories
    // in, such as TransferableGroupType.N00.Level1.AnimalEar for cat and rabbit ears.
    private static Dictionary<string, string>? attachmentKinds;
    private static Dictionary<string, string> AttachmentKinds => attachmentKinds ??= Attachments().ToDictionary(
        entry => ItemIds.Attachment(Reflect.Get(entry, "resourceName") as string)!,
        entry => Reflect.Get(entry, "transferableGroupType") as string ?? throw Differs("the kind of a built-in accessory"));
    // Every built-in accessory needs its default bone in the table.
    private static void CheckDefaultBones()
    {
        if (AttachmentIds.FirstOrDefault(item => Bones.Default(item) == null) is { } missing)
            throw Differs($"the default bone of {missing}");
    }

    // Every built-in accessory with its name and template. The editor offers
    // only the glasses, so the catalog's other attachments, such as animal ears,
    // tails, and hats, are added to its template list first: selecting a
    // template that is not in the list crashes VRoid.
    private static (string Item, string Name, object Template)[] AvailableAccessories()
    {
        var editor = AccessoryEditor;
        var templates = Reflect.Items(Reflect.Get(editor, "Templates")).ToList();
        string? Resource(object template) => Reflect.Get(Reflect.Get(template, "transferableGroupTemplate"), "resourceName") as string;
        var listed = templates.Select(Resource).ToHashSet();
        var missing = Attachments().Where(entry => !listed.Contains(Reflect.Get(entry, "resourceName") as string)).ToList();
        if (missing.Count > 0)
        {
            var resources = Reflect.Get(editor, "Resources") ?? throw Differs("the accessory editor's resources");
            foreach (var entry in missing)
                templates.Add(Reflect.New(TemplateItem, resources, Reflect.New("VRoid.Studio.Engine.TransferableGroupTemplate",
                    Reflect.Get(entry, "transferableGroupType")!, Reflect.Get(entry, "resourceName")!, Reflect.Get(entry, "id")!)));
            var array = Activator.CreateInstance(typeof(Il2CppReferenceArray<>).MakeGenericType(Reflect.Type(TemplateItem)), (long)templates.Count)!;
            var item = array.GetType().GetProperty("Item")!;
            for (var i = 0; i < templates.Count; i++) item.SetValue(array, Reflect.As(templates[i], TemplateItem), new object[] { i });
            Reflect.Call(editor, "set_Templates", array);
        }
        return templates.Select(template => (Id: ItemIds.Attachment(Resource(template)), Template: template))
            .Where(entry => entry.Id != null)
            .Select(entry => (entry.Id!, Reflect.Get(entry.Template, "TranslatedDisplayName") as string ?? entry.Id!, entry.Template))
            .ToArray();
    }

    private static IEnumerable<bool> WaitForAccessories(long before, int count)
    {
        while (rebuilt == before || updating) yield return true;
        for (var frame = 0; Instances().Length != count || updating; frame++)
        {
            if (frame > 300) throw new InvalidOperationException($"VRoid lists {Instances().Length} accessories, expected {count}");
            yield return true;
        }
    }

    // Selecting a template in the editor adds one instance of it, appended to the list.
    private static IEnumerable<bool> AddAccessory(object template)
    {
        var count = Instances().Length;
        var before = rebuilt;
        modified = true;
        Reflect.Call(AccessoryEditor, "set_SelectedTemplate", new object[] { null! });
        Reflect.Call(AccessoryEditor, "set_SelectedTemplate", template);
        foreach (var step in WaitForAccessories(before, count + 1)) yield return step;
    }

    // VRoid's own Duplicate appends a copy of the accessory, with its bone,
    // placement, colours, and shape.
    private static IEnumerable<bool> DuplicateAccessory(int index)
    {
        var count = Instances().Length;
        var before = rebuilt;
        modified = true;
        Reflect.Call(AccessoryEditor, "set_SelectedIndex", index);
        if (!Equals(Reflect.Get(AccessoryEditor, "SelectedIndex"), index)) throw Differs($"how to select accessory {index}");
        Reflect.Call(AccessoryEditor, "DuplicateSelectedInstance");
        foreach (var step in WaitForAccessories(before, count + 1)) yield return step;
    }

    private static IEnumerable<bool> RemoveAccessory(int index)
    {
        var count = Instances().Length;
        var before = rebuilt;
        modified = true;
        Reflect.Call(AccessoryEditor, "set_SelectedIndex", index);
        // RemoveSelectedInstance removes whichever accessory is selected.
        if (!Equals(Reflect.Get(AccessoryEditor, "SelectedIndex"), index)) throw Differs($"how to select accessory {index}");
        Reflect.Call(AccessoryEditor, "RemoveSelectedInstance");
        foreach (var step in WaitForAccessories(before, count - 1)) yield return step;
    }

    // An apply request's items and accessories, resolved and checked before
    // anything changes, and what changing them did.
    private sealed class Outfitting
    {
        public readonly Dictionary<string, Slot[]> Slots = new();
        // Each slot's item before the request, by path.
        public readonly Dictionary<string, string?> Before = new();
        public readonly List<(Slot Slot, string? Item)> Picks = new();
        // Null when the request leaves accessories alone; `WornEntries` holds their
        // entries from before the change.
        public Worn[]? Worn;
        public Dictionary<string, object?>[] WornEntries = Array.Empty<Dictionary<string, object?>>();
        public AccessoryPlan? Plan;
        public List<AccessorySpec> Wanted = new();
        public int Changed;

        public bool ItemsChange(string section) =>
            Picks.Any(pick => pick.Slot.Section == section && pick.Item != Before[pick.Slot.Path]);

        // Whether the request may change this category's item: its own pick, a
        // new item VRoid takes this one off for, or a set, which selects items
        // across its section.
        public bool ItemMayChange(string section, string category) =>
            Picks.Any(pick => pick.Slot.Section == section && pick.Item != Before[pick.Slot.Path]
                && (pick.Slot.Name == category || pick.Slot.IsSet
                    || (pick.Item != null && Slots[section].Any(slot => slot.Name == category && Conflict(pick.Slot, slot)))));
    }

    // Resolves every requested item and accessory and adds a message to
    // `errors` for each one apply cannot wear (R-EDIT-3) or that would discard
    // an item that is not built in without `discard_custom` (R-EDIT-10).
    private static IEnumerable<bool> CheckItems(Request request, Dictionary<string, CategoryEdit[]> edits, bool discard,
        Outfitting work, List<string> errors)
    {
        foreach (var section in Sections.Where(section => edits[section].Any(edit => edit.HasItem)))
        {
            Slot[]? found = null;
            foreach (var step in Slots(section))
            {
                if (step == null) yield return true;
                else found = step;
            }
            var slots = work.Slots[section] = found!;
            foreach (var slot in slots) work.Before[slot.Path] = State(slot);
            foreach (var edit in edits[section].Where(edit => edit.HasItem))
            {
                var path = $"{section}/{edit.Name}";
                var named = slots.Where(slot => slot.Name == edit.Name).ToArray();
                if (named.Length != 1)
                {
                    errors.Add(named.Length == 0 ? $"{path}: no category of this name wears items; list them with categories and items"
                        : $"{path}: {named.Length} categories have this name; it cannot be selected by name");
                    continue;
                }
                var current = work.Before[named[0].Path];
                // Asking for what the category already wears changes nothing.
                if (edit.Item == current) work.Picks.Add((named[0], edit.Item));
                else if (edit.Item == null && !named[0].Removable) errors.Add($"{path}: its item cannot be taken off; select another");
                else if (edit.Item == ItemIds.Custom && current != ItemIds.Custom)
                    errors.Add($"{path}: \"custom\" keeps an item that is not built in, but the category wears {current ?? "nothing"}");
                else if (edit.Item is { } id && id != ItemIds.Custom && named[0].Items.All(item => item.Id != id))
                    errors.Add($"{path}: unknown item {id}; list them with items");
                else work.Picks.Add((named[0], edit.Item));
            }
            // A set selects its items itself, so it may not be named with any of them, even
            // to take one off; other conflicting items may not both be worn.
            var picked = work.Picks.Where(pick => pick.Slot.Section == section).ToList();
            for (var i = 0; i < picked.Count; i++)
                for (var j = i + 1; j < picked.Count; j++)
                {
                    var (a, b) = (picked[i], picked[j]);
                    if (!Conflict(a.Slot, b.Slot)) continue;
                    if ((a.Slot.IsSet && a.Item != null) || (b.Slot.IsSet && b.Item != null))
                        errors.Add($"{a.Slot.Path} and {b.Slot.Path}: the set selects the other category's item itself; give one of them");
                    else if (a.Item != null && b.Item != null)
                        errors.Add($"{a.Slot.Path} and {b.Slot.Path}: VRoid cannot wear these together; take one off");
                }
            // VRoid also replaces items it does not list as conflicts, such as a sample
            // model's base hair when its overall hair changes, so any item change in a
            // section may replace the section's items that are not built in.
            var custom = slots.Where(slot => work.Before[slot.Path] == ItemIds.Custom).Select(slot => slot.Path).ToArray();
            if (!discard && custom.Length > 0 && work.ItemsChange(section))
                errors.Add($"{string.Join(", ", custom)} {(custom.Length == 1 ? "wears an item" : "wear items")} that "
                    + $"{(custom.Length == 1 ? "is" : "are")} not built in, which changing {section} items may replace; "
                    + "undo cannot restore them. Set discard_custom to allow it; undoing such a change needs it too");
        }
        if (!request.Body.TryGetProperty("Accessories", out var accessories) || accessories.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var step in EnterAccessories()) yield return step;
        var worn = work.Worn = WornAccessories();
        work.WornEntries = worn.Select(AccessoryEntry).ToArray();
        var bones = AccessoryBones(worn).ToHashSet();
        var available = AvailableAccessories().Select(accessory => accessory.Item).ToHashSet();
        CheckDefaultBones();
        work.Wanted = accessories.EnumerateArray().Select(SpecOf).ToList();
        var plan = work.Plan = AccessoryPlan.Of(worn.Select(accessory => new WornAccessory(accessory.Item, accessory.Pose.Bone, accessory.Group)).ToList(),
            work.Wanted.Select(wanted => new WantedAccessory(wanted.Item, wanted.HasBone, wanted.Bone,
                AttachmentKinds.GetValueOrDefault(wanted.Item, wanted.Item))).ToList(),
            (i, j) => Satisfies(work.Wanted[i], work.WornEntries[j]));
        for (var i = 0; i < work.Wanted.Count; i++)
            if (work.Wanted[i].Item != ItemIds.Custom && !available.Contains(work.Wanted[i].Item))
                errors.Add($"Accessories[{i}]: unknown accessory {work.Wanted[i].Item}; list them with accessories");
        foreach (var addition in plan.Added.Where(addition => work.Wanted[addition.Wanted].Item == ItemIds.Custom))
            errors.Add($"Accessories[{addition.Wanted}]: \"custom\" keeps an accessory that is not built in, but the project wears no other one");
        if (!discard)
            foreach (var j in plan.Removed.Where(j => worn[j].Item == ItemIds.Custom))
                errors.Add($"Accessories: {worn[j].Name} is not built in, and this request removes it; undo cannot restore it. Set discard_custom to allow it");
        var max = Convert.ToInt32(Reflect.StaticProperty("VRoid.Studio.AccessoryEditor.ViewModel", "MaxInstanceCount").GetValue(null));
        if (worn.Length - plan.Removed.Count + plan.Added.Count > max)
            errors.Add($"Accessories: VRoid allows at most {max} accessories");
        errors.AddRange(plan.Errors);
        // The worn accessory whose labels each wanted one has: the one it keeps, or
        // the one a rebuild replaces.
        var from = plan.Kept.ToDictionary(pair => pair.Wanted, pair => pair.Worn);
        foreach (var addition in plan.Added)
            if (addition.Was is { } was) from[addition.Wanted] = was;
        for (var i = 0; i < work.Wanted.Count; i++)
        {
            var spec = work.Wanted[i];
            var path = $"Accessories[{i}]";
            var matched = from.TryGetValue(i, out var j) ? worn[j] : null;
            // An unmatched "custom" is refused above; its values have nothing to check against.
            if (matched == null && spec.Item == ItemIds.Custom) continue;
            // A kept accessory may stay on a bone the table lacks, such as a custom one's.
            if (spec.HasBone && spec.Bone != null && !bones.Contains(spec.Bone) && matched?.Pose.Bone != spec.Bone)
                errors.Add($"{path}/bone: unknown bone {spec.Bone}; list them with accessories");
            var bone = spec.HasBone ? spec.Bone : matched != null ? matched.Pose.Bone : Bones.Default(spec.Item);
            if (bone == null && spec.Places) errors.Add($"{path}: has no bone, so it takes no position, rotation, or scale");
            foreach (var (key, vector, check) in new (string, float[]?, Func<IReadOnlyList<float>, string?>)[]
                     {
                         ("position", spec.Position, _ => null), ("rotation", spec.Rotation, Placement.Rotation), ("scale", spec.Scale, Placement.Scale),
                     })
                if (vector == null) continue;
                else if (vector.Any(part => !float.IsFinite(part))) errors.Add($"{path}/{key}: is outside float range");
                else if (check(vector) is { } reason) errors.Add($"{path}/{key}: {reason}");
            if (matched != null) Match("Accessories", AsCategory(matched), new CategoryEdit("", spec.Values, false, null), errors, false, path);
            // An added accessory's sliders are known only once it is worn (R-EDIT-13);
            // every built-in one has a single colour labelled Color in VRoid 2.16.0.
            else
                foreach (var wanted in spec.Values.Where(wanted => wanted.Colors != null && wanted.Label != AccessoryColor))
                    errors.Add($"{path}/{wanted.Label}: built-in accessories have one colour, {AccessoryColor}");
        }
    }

    // A wanted accessory: its item, its bone if the request names one, the parts of
    // its placement it names, and its colours and shape sliders.
    private sealed record AccessorySpec(string Item, bool HasBone, string? Bone, float[]? Position, float[]? Rotation, float[]? Scale,
        Wanted[] Values)
    {
        public bool Places => Position != null || Rotation != null || Scale != null;
    }

    private static AccessorySpec SpecOf(JsonElement entry)
    {
        // A value beyond float range becomes infinite, which the checks refuse.
        float[]? Vector(string key) =>
            entry.TryGetProperty(key, out var value) ? value.EnumerateArray().Select(part => (float)part.GetDouble()).ToArray() : null;
        var hasBone = entry.TryGetProperty("bone", out var bone);
        return new AccessorySpec(entry.GetProperty("item").GetString()!, hasBone, hasBone ? bone.GetString() : null,
            Vector("position"), Vector("rotation"), Vector("scale"), WantedValues(entry, PoseKeys.Append("item").ToArray()));
    }

    // Whether a worn accessory's entry already holds everything `spec` names.
    private static bool Satisfies(AccessorySpec spec, Dictionary<string, object?> entry) =>
        entry["item"] as string == spec.Item && (!spec.HasBone || entry["bone"] as string == spec.Bone)
        && new[] { ("position", spec.Position), ("rotation", spec.Rotation), ("scale", spec.Scale) }
            .All(part => part.Item2 == null || Same(part.Item2, entry.GetValueOrDefault(part.Item1)))
        && spec.Values.All(wanted => entry.GetValueOrDefault(wanted.Label) is var value && (wanted.Colors is { } colors
            ? !wanted.Repeated && Colors.Normalize(colors[0]) is { } hex && Equals(value, hex)
            : Same(value, wanted.Repeated ? wanted.Values.Select(number => (float)number).ToArray() : (float)wanted.Values[0])));

    // A dumped entry as a spec that names every value.
    private static AccessorySpec SpecOf(Dictionary<string, object?> entry) =>
        new((string)entry["item"]!, true, entry["bone"] as string, entry.GetValueOrDefault("position") as float[],
            entry.GetValueOrDefault("rotation") as float[], entry.GetValueOrDefault("scale") as float[],
            entry.Where(pair => pair.Key != "item" && !PoseKeys.Contains(pair.Key)).Select(pair => pair.Value switch
            {
                string hex => new Wanted(pair.Key, Array.Empty<double>(), false, new[] { hex }),
                float[] values => new Wanted(pair.Key, values.Select(value => (double)value).ToArray(), true, null),
                float value => new Wanted(pair.Key, new[] { (double)value }, false, null),
                _ => throw new InvalidOperationException($"Accessory value {pair.Key} has no known kind"),
            }).ToArray());

    // `over` on top of `under`: what over names, and what it leaves out from under.
    // A bone of None has no placement.
    private static AccessorySpec Over(AccessorySpec under, AccessorySpec over)
    {
        var bone = over.HasBone ? over.Bone : under.Bone;
        float[]? Part(float[]? mine, float[]? theirs) => mine ?? (bone != null ? theirs : null);
        return new AccessorySpec(over.Item, true, bone, Part(over.Position, under.Position), Part(over.Rotation, under.Rotation),
            Part(over.Scale, under.Scale),
            under.Values.Where(value => over.Values.All(other => other.Label != value.Label)).Concat(over.Values).ToArray());
    }

    // An accessory's colours and shape sliders as a category, so they resolve like
    // a category's values.
    private static Category AsCategory(Worn worn) =>
        new(worn.Name, 0, worn.Instance, worn.Parameters, worn.Swatches, new Dictionary<string, string>());

    private const string AccessoryColor = "Color";

    // Reports each worn item that is not built in and is now gone, including
    // ones VRoid took off itself. `undo` never names such items.
    private static void Discard(Outfitting work, string section)
    {
        foreach (var slot in work.Slots[section].Where(slot => work.Before[slot.Path] == ItemIds.Custom && State(slot) != ItemIds.Custom))
            if (!Discarded.Contains(slot.Path)) Discarded.Add(slot.Path);
    }

    // Wears the requested items in app order, so a set comes before the items
    // it selects, then checks each one reads back.
    private static IEnumerable<bool> ChangeItems(Outfitting work)
    {
        foreach (var section in Sections.Where(work.Slots.ContainsKey))
        {
            foreach (var step in Enter(section))
                if (step == null) yield return true;
            var picks = work.Picks.Where(pick => pick.Slot.Section == section).OrderBy(pick => pick.Slot.Index).ToList();
            try
            {
                foreach (var (slot, item) in picks)
                {
                    // An earlier set may already have selected it.
                    if (State(slot) == item) continue;
                    Changed.Add(slot.Path);
                    work.Changed++;
                    foreach (var step in Wear(slot, item, work.Slots[section])) yield return step;
                }
            }
            finally
            {
                // Runs before a failure's message is written, so it names what was discarded.
                try
                {
                    Discard(work, section);
                }
                catch (Exception error)
                {
                    Diagnostics.LogError($"Could not check discarded items in {section}: {error}");
                }
            }
            foreach (var (slot, item) in picks)
                if (State(slot) != item)
                    throw new InvalidOperationException($"{slot.Path} reads {State(slot) ?? "null"} after selecting {item ?? "null"} and the "
                        + "request's later items. A set reads as selected only while its items match it; give either the set or those items");
        }
    }

    // Removes, moves, adds, places, recolours, and reshapes accessories to match
    // the request.
    private static IEnumerable<bool> ChangeAccessories(Outfitting work, Dictionary<string, object> undo)
    {
        if (work.Plan is not { } plan) yield break;
        var worn = work.Worn!;
        var gone = new HashSet<int>();
        foreach (var step in EnterAccessories()) yield return step;
        // Removing from the end keeps the earlier indices valid.
        foreach (var j in plan.Removed.OrderByDescending(j => j))
        {
            var path = $"Accessories/{(worn[j].Item == ItemIds.Custom ? worn[j].Name : worn[j].Item)}";
            Changed.Add(path);
            // A rebuild adds it again.
            if (!plan.Rebuilt.Contains(j)) work.Changed++;
            var removed = false;
            try
            {
                foreach (var step in RemoveAccessory(j)) yield return step;
                removed = true;
            }
            finally
            {
                // Undo cannot restore a custom accessory once it is gone. If the removal
                // fails or times out, it is gone when no instance has its transferable;
                // instances can share one, so a completed removal needs no check.
                try
                {
                    if (worn[j].Item == ItemIds.Custom && (removed
                        || Instances().All(instance => Reflect.Get(Reflect.Get(instance, "transferableInfo"), "Id") as string != worn[j].Id)))
                    {
                        Discarded.Add(path);
                        gone.Add(j);
                        undo["Accessories"] = AccessoryList(work.WornEntries.Where((_, k) => !gone.Contains(k)).ToArray());
                        SaveUndo();
                    }
                }
                catch (Exception error)
                {
                    Diagnostics.LogError($"Could not check the removal of {path}: {error}");
                }
            }
        }
        // Where a worn accessory is listed once the removals are done.
        int Now(int j) => j - plan.Removed.Count(k => k < j);
        foreach (var (j, bone) in plan.Rebone)
        {
            foreach (var step in MoveBone(Now(j), bone, $"Accessories[{plan.Kept.Single(pair => pair.Worn == j).Wanted}]")) yield return step;
            work.Changed++;
        }
        var targets = plan.Kept.Select(pair => (Index: Now(pair.Worn), pair.Wanted, Was: (int?)null)).ToList();
        // Where each added accessory is listed, by wanted index.
        var made = new Dictionary<int, int>();
        foreach (var addition in plan.Added)
        {
            var item = work.Wanted[addition.Wanted].Item;
            var path = $"Accessories[{addition.Wanted}]";
            Changed.Add($"Accessories/{item}");
            if (addition.Was == null) work.Changed++;
            if (addition.CopyOf is { } source)
                foreach (var step in DuplicateAccessory(made[source])) yield return step;
            // The editor may rebuild its template list, so find the template in the current one.
            else foreach (var step in AddAccessory(AvailableAccessories().First(available => available.Item == item).Template)) yield return step;
            var added = WornAccessories();
            if (added[^1].Item != item) throw Differs($"where VRoid lists the added accessory {item}");
            made[addition.Wanted] = added.Length - 1;
            var expected = addition.CopyOf is { } copied ? added[made[copied]].Pose.Bone : Bones.Default(item);
            if (added[^1].Pose.Bone != expected) throw Differs($"the bone VRoid puts an added {item} on");
            if (addition.SetBone)
            {
                foreach (var step in MoveBone(made[addition.Wanted], addition.Bone, path)) yield return step;
                if (addition.Was == null) work.Changed++;
            }
            targets.Add((made[addition.Wanted], addition.Wanted, addition.Was));
        }
        foreach (var (index, i, was) in targets)
        {
            // A rebuild's accessory takes over the values of the one it replaced.
            var spec = was is { } j ? Over(SpecOf(work.WornEntries[j]), work.Wanted[i]) : work.Wanted[i];
            var before = was is { } k ? work.WornEntries[k] : AccessoryEntry(WornAccessories()[index]);
            foreach (var step in Fit(index, spec, $"Accessories[{i}]")) yield return step;
            work.Changed += Differences(before, AccessoryEntry(WornAccessories()[index]));
        }
    }

    // Moves the accessory listed at `index` to `bone`, which VRoid's command does
    // only while no other accessory of its group is worn.
    private static IEnumerable<bool> MoveBone(int index, string? bone, string path)
    {
        var worn = WornAccessories();
        if (worn[index].Pose.Bone == bone) yield break;
        if (worn.Count(other => other.Group == worn[index].Group) > 1)
            throw new InvalidOperationException($"{path}: another accessory of its kind is worn, so VRoid cannot move it to {bone ?? "None"}");
        var before = rebuilt;
        modified = true;
        Run(worn[index], "SetPartOriginParentCommand", worn[index].Group, bone == null ? null : Reflect.New("VRoidCore.Common.Types.ResolvedBoneId", bone));
        Changed.Add($"{path}/bone");
        while (rebuilt == before || updating) yield return true;
        var actual = WornAccessories()[index].Pose.Bone;
        if (actual != bone) throw new InvalidOperationException($"{path}/bone reads {actual ?? "None"} after setting {bone ?? "None"}");
    }

    // Sets the placement, colours, and shape sliders `spec` names on the accessory
    // listed at `index`, waiting for the rebuild after each change and reading it
    // back. An added accessory's labels and bounds are checked here (R-EDIT-13).
    private static IEnumerable<bool> Fit(int index, AccessorySpec spec, string path)
    {
        Worn Find() => WornAccessories()[index];
        if (spec.Places)
        {
            var now = Find().Pose;
            if (now.Bone == null) throw new InvalidOperationException($"{path}: has no bone, so it takes no position, rotation, or scale");
            var want = new Pose(now.Bone, spec.Position ?? now.Position, spec.Rotation ?? now.Rotation, spec.Scale ?? now.Scale);
            if (!SamePlace(want, now))
            {
                var worn = Find();
                var before = rebuilt;
                modified = true;
                Run(worn, "SetPartOriginTransformCommand", worn.Id, Reflect.New("VRoidCore.Common.Types.Transform",
                    Unity("UnityEngine.Vector3", want.Position!), Unity("UnityEngine.Quaternion", want.Rotation!), Unity("UnityEngine.Vector3", want.Scale!)));
                Changed.Add($"{path}/placement");
                while (rebuilt == before || updating) yield return true;
                if (!SamePlace(want, Find().Pose)) throw new InvalidOperationException($"{path}: VRoid did not keep the placement it was given");
            }
        }
        foreach (var wanted in spec.Values)
        {
            // Values are resolved again after each change, since a rebuild may replace
            // the accessory's sliders and colour fields.
            var set = new HashSet<string>();
            while (true)
            {
                var errors = new List<string>();
                var target = Match("Accessories", AsCategory(Find()), new CategoryEdit("", new[] { wanted }, false, null), errors, false, path);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors) + ".");
                var number = target.Matched.FirstOrDefault(match => Read(match.Parameter) != match.Value);
                var paint = target.Painted.FirstOrDefault(match => match.Field.Hex != match.Hex);
                var where = number.Path ?? paint?.Field.Path;
                if (where == null) break;
                if (!set.Add(where))
                    throw new InvalidOperationException(number.Path != null ? $"{where} reads {Read(number.Parameter)} after setting {number.Value}"
                        : $"{where} reads {paint!.Field.Hex} after setting {paint.Hex}");
                var before = rebuilt;
                modified = true;
                if (number.Path != null) Reflect.Call(number.Parameter, "set_" + Member(number.Parameter), number.Value);
                else Write(paint!.Swatch, Colors.Write(paint.Field.Hex, paint.Hex)!);
                Changed.Add(where);
                while (rebuilt == before || updating) yield return true;
            }
        }
    }

    private static bool SamePlace(Pose a, Pose b) =>
        a.Bone == b.Bone && Same(a.Position, b.Position) && Same(a.Rotation, b.Rotation) && Same(a.Scale, b.Scale);

    private static object Unity(string type, float[] parts) => Activator.CreateInstance(Reflect.Type(type), parts.Cast<object>().ToArray())!;

    // Runs one of VRoid's edit commands, which records it in VRoid's own history.
    private static void Run(Worn worn, string command, params object?[] args) =>
        Reflect.Call(EditContext(worn.Instance), "ExecuteSyncCommand", Reflect.New("VRoidCore.Editing.History.Command." + command, args!));

    // The number of values that differ between two entries of one accessory; each
    // element of a repeated label counts.
    private static int Differences(Dictionary<string, object?> before, Dictionary<string, object?> after) =>
        before.Keys.Union(after.Keys).Where(key => key != "item").Sum(key =>
            !PoseKeys.Contains(key) && before.GetValueOrDefault(key) is float[] a && after.GetValueOrDefault(key) is float[] b && a.Length == b.Length
                ? a.Zip(b).Count(pair => pair.First != pair.Second)
                : Same(before.GetValueOrDefault(key), after.GetValueOrDefault(key)) ? 0 : 1);
}
