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

    // A worn accessory instance: its item, VRoid's name for it, its transferable,
    // and its available colours.
    private sealed record Worn(string Item, string Name, string Id, object Instance, Swatch[] Swatches);

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
        var model = Reflect.Get(Reflect.Get(Reflect.Get(instance, "_engine"), "Context"), "editingModel")
            ?? throw Differs("the editing model");
        var id = Reflect.Get(Reflect.Get(instance, "transferableInfo"), "Id") as string ?? throw Differs($"the transferable of accessory {name}");
        var display = Reflect.Call(Reflect.New("VRoidCore.Editing.Query.GetTransferableDisplayNameQuery", id), "Execute", model);
        var item = ItemIds.AccessoryKey(Reflect.Get(display, "LocalizationKey") as string);
        var swatches = new List<Swatch>();
        foreach (var selection in Reflect.Items(Reflect.Get(Reflect.Get(instance, "CategoryColorSelection"), "_colorSelections")))
        {
            if (Reflect.Get(selection, "IsAvailable") is not bool available) throw Differs($"colour availability for accessory {name}");
            if (!available) continue;
            foreach (var sub in Reflect.Items(Reflect.Get(selection, "_subColorSelections")))
                swatches.Add(new Swatch(Reflect.Get(sub, "TranslatedDisplayName") as string ?? "", sub, "Color", new HashSet<string>()));
        }
        return new Worn(item != null && AttachmentIds.Contains(item) ? item : ItemIds.Custom, name, id, instance, swatches.ToArray());
    }).ToArray();

    // An accessory as a template entry: its item, then its colours by label.
    private static Dictionary<string, object> AccessoryEntry(Worn worn)
    {
        var entry = new Dictionary<string, object> { ["item"] = worn.Item };
        foreach (var swatch in worn.Swatches)
            if (!entry.TryAdd(swatch.Label, ReadHex(swatch, $"Accessories/{worn.Name}/{swatch.Label}")))
                throw new InvalidOperationException($"Accessory {worn.Name} has two values labelled {swatch.Label}; a template cannot hold both");
        return entry;
    }

    // Accessory entries in dump order, so lists compare across instances.
    private static Dictionary<string, object>[] AccessoryList(IReadOnlyList<Dictionary<string, object>> entries) =>
        AccessoryPlan.DumpOrder(entries.Select(entry => (string)entry["item"]).ToList()).Select(index => entries[index]).ToArray();

    private static Dictionary<string, object>[] AccessoryList(Worn[] worn) => AccessoryList(worn.Select(AccessoryEntry).ToArray());

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
        public Dictionary<string, object>[] WornEntries = Array.Empty<Dictionary<string, object>>();
        public AccessoryPlan? Plan;
        public List<(string Item, Dictionary<string, string> Colors)> Wanted = new();
        public int Changed;

        public bool ItemsChange(string section) =>
            Picks.Any(pick => pick.Slot.Section == section && pick.Item != Before[pick.Slot.Path]);

        // Whether the request changes this category's own item.
        public bool ItemChanges(string section, string category) =>
            Picks.Any(pick => pick.Slot.Section == section && pick.Slot.Name == category && pick.Item != Before[pick.Slot.Path]);
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
        var available = AvailableAccessories().Select(accessory => accessory.Item).ToHashSet();
        work.Wanted = accessories.EnumerateArray().Select(entry => (entry.GetProperty("item").GetString()!,
            entry.EnumerateObject().Where(color => color.Name != "item").ToDictionary(color => color.Name, color => color.Value.GetString()!))).ToList();
        var plan = work.Plan = AccessoryPlan.Of(worn.Select(accessory => accessory.Item).ToList(), work.Wanted.Select(wanted => wanted.Item).ToList());
        for (var i = 0; i < work.Wanted.Count; i++)
            if (work.Wanted[i].Item != ItemIds.Custom && !available.Contains(work.Wanted[i].Item))
                errors.Add($"Accessories[{i}]: unknown accessory {work.Wanted[i].Item}; list them with accessories");
        foreach (var i in plan.Added.Where(i => work.Wanted[i].Item == ItemIds.Custom))
            errors.Add($"Accessories[{i}]: \"custom\" keeps an accessory that is not built in, but the project wears no other one");
        if (!discard)
            foreach (var j in plan.Removed.Where(j => worn[j].Item == ItemIds.Custom))
                errors.Add($"Accessories: {worn[j].Name} is not built in, and this request removes it; undo cannot restore it. Set discard_custom to allow it");
        var max = Convert.ToInt32(Reflect.StaticProperty("VRoid.Studio.AccessoryEditor.ViewModel", "MaxInstanceCount").GetValue(null));
        if (worn.Length - plan.Removed.Count + plan.Added.Count > max)
            errors.Add($"Accessories: VRoid allows at most {max} accessories");
        foreach (var (j, i) in plan.Kept)
            foreach (var label in work.Wanted[i].Colors.Keys)
            {
                var matches = worn[j].Swatches.Count(swatch => swatch.Label == label);
                if (matches != 1)
                    errors.Add($"Accessories[{i}]/{label}: " + (matches == 0 ? $"{worn[j].Name} has no such colour; list them with accessories"
                        : $"{matches} colours have this label; it cannot be set by label"));
            }
        // An accessory's colours are known only once it is worn; every built-in one has
        // a single colour labelled Color in VRoid 2.16.0, which the change rechecks.
        foreach (var i in plan.Added)
            foreach (var label in work.Wanted[i].Colors.Keys.Where(label => label != AccessoryColor))
                errors.Add($"Accessories[{i}]/{label}: built-in accessories have one colour, {AccessoryColor}");
    }

    private const string AccessoryColor = "Color";

    // Reports each worn item that is not built in and is now gone, including
    // ones VRoid took off itself, and drops it from `undo`, which cannot
    // restore it; `undo` keeps the category's values, some of them shared.
    private static void Discard(Outfitting work, string section, Dictionary<string, object> undo)
    {
        foreach (var slot in work.Slots[section].Where(slot => work.Before[slot.Path] == ItemIds.Custom && State(slot) != ItemIds.Custom))
        {
            if (!Discarded.Contains(slot.Path)) Discarded.Add(slot.Path);
            // The undo is a dump whenever the request changes items.
            if (undo[section] is Dictionary<string, object> categories && categories.TryGetValue(slot.Name, out var values))
                (values as Dictionary<string, object?>)?.Remove("item");
        }
    }

    // Wears the requested items in app order, so a set comes before the items
    // it selects, then checks each one reads back. `undo` is a dump from
    // before the change.
    private static IEnumerable<bool> ChangeItems(Outfitting work, Dictionary<string, object> undo)
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
                // Runs before a failure's message is written, so its undo applies.
                try
                {
                    Discard(work, section, undo);
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

    // Removes, adds, and recolours accessories to match the request.
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
            work.Changed++;
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
                    }
                }
                catch (Exception error)
                {
                    Diagnostics.LogError($"Could not check the removal of {path}: {error}");
                }
            }
        }
        var targets = plan.Kept.Select(pair => (Index: pair.Worn - plan.Removed.Count(j => j < pair.Worn), pair.Wanted)).ToList();
        foreach (var i in plan.Added)
        {
            var item = work.Wanted[i].Item;
            Changed.Add($"Accessories/{item}");
            work.Changed++;
            // The editor may rebuild its template list, so find the template in the current one.
            foreach (var step in AddAccessory(AvailableAccessories().First(available => available.Item == item).Template)) yield return step;
            var added = WornAccessories();
            if (added[^1].Item != item) throw Differs($"where VRoid lists the added accessory {item}");
            targets.Add((added.Length - 1, i));
        }
        foreach (var (index, i) in targets)
            foreach (var (label, wanted) in work.Wanted[i].Colors)
            {
                var path = $"Accessories[{i}]/{label}";
                Swatch Find()
                {
                    var matches = WornAccessories()[index].Swatches.Where(swatch => swatch.Label == label).ToArray();
                    return matches.Length == 1 ? matches[0]
                        : throw new InvalidOperationException($"{path}: " + (matches.Length == 0 ? "no such colour on the added accessory" : "two colours have this label"));
                }
                var hex = Colors.Normalize(wanted) ?? throw new ArgumentException($"{path}: {wanted} is not #RRGGBB");
                if (Colors.Write(ReadHex(Find(), path), hex) is not { } channels) continue;
                var before = rebuilt;
                modified = true;
                Write(Find(), channels);
                Changed.Add(path);
                work.Changed++;
                while (rebuilt == before || updating) yield return true;
                var actual = ReadHex(Find(), path);
                if (actual != hex) throw new InvalidOperationException($"{path} reads {actual} after setting {hex}");
            }
    }
}
