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
    // update, including taking off the section's conflicting items.
    private static IEnumerable<bool> Wear(Slot slot, string? id, Slot[] section)
    {
        // A set lists the kinds of item it selects as conflicts; those are not taken off.
        var conflicting = id == null || slot.IsSet ? Array.Empty<Slot>() : section.Where(other => Conflict(slot, other)).ToArray();
        var before = rebuilt;
        modified = true;
        if (id == null) Reflect.Call(slot.Presets, "ClearTransferableGroup");
        else Reflect.Call(slot.Items.Single(item => item.Id == id).Option, "set_IsSelected", true);
        while (rebuilt == before || updating) yield return true;
        // The selector updates IsSelected after a debounce.
        for (var frame = 0; State(slot) != id || conflicting.Any(other => State(other) != null); frame++)
        {
            if (frame > 300)
                throw new InvalidOperationException($"{slot.Path} reads {State(slot) ?? "null"} after selecting {id ?? "null"}"
                    + string.Concat(conflicting.Where(other => State(other) != null).Select(other => $", and {other.Path} still reads {State(other)}")));
            yield return true;
        }
    }

    // VRoid declares some conflicts on one side only, such as dresses with tops.
    private static bool Conflict(Slot a, Slot b) =>
        a.Conflicts.Contains(ItemIds.Kind(b.Type)) || b.Conflicts.Contains(ItemIds.Kind(a.Type));

    // A worn accessory instance: its item, VRoid's name for it, and its available colours.
    private sealed record Worn(string Item, string Name, object Instance, Swatch[] Swatches);

    private static object AccessoryEditor => Reflect.Get(Root, "AccessoryEditor") ?? throw Differs("the accessory editor");

    private static IEnumerable<bool> EnterAccessories()
    {
        Reflect.Call(Root, "set_EditMode", Enum.Parse(Reflect.Type("VRoid.Studio.EditRootScreen.EditMode"), "AccessoryEditor"));
        while (Reflect.Get(AccessoryEditor, "IsFeatureActive") is not true || updating) yield return true;
    }

    private static Worn[] WornAccessories() => Reflect.Items(Reflect.Get(AccessoryEditor, "Instances")).Select(instance =>
    {
        var group = Reflect.Get(Reflect.Get(instance, "transferableInfo"), "GroupResourceName") as string;
        var name = Reflect.Get(instance, "TranslatedDisplayName") as string ?? "";
        var swatches = new List<Swatch>();
        foreach (var selection in Reflect.Items(Reflect.Get(Reflect.Get(instance, "CategoryColorSelection"), "_colorSelections")))
        {
            if (Reflect.Get(selection, "IsAvailable") is not bool available) throw Differs($"colour availability for accessory {name}");
            if (!available) continue;
            foreach (var sub in Reflect.Items(Reflect.Get(selection, "_subColorSelections")))
                swatches.Add(new Swatch(Reflect.Get(sub, "TranslatedDisplayName") as string ?? "", sub, "Color", new HashSet<string>()));
        }
        return new Worn(ItemIds.Attachment(group) ?? ItemIds.Custom, name, instance, swatches.ToArray());
    }).ToArray();

    // An accessory as a template entry: its item, then each colour by label.
    private static Dictionary<string, object> AccessoryEntry(Worn worn, int index)
    {
        var entry = new Dictionary<string, object> { ["item"] = worn.Item };
        foreach (var swatch in worn.Swatches)
            if (!entry.TryAdd(swatch.Label, ReadHex(swatch, $"Accessories[{index}]/{swatch.Label}")))
                throw new InvalidOperationException($"Accessory {worn.Name} has two values labelled {swatch.Label}; a template cannot hold both");
        return entry;
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
        var catalog = Reflect.Get(Reflect.Get(Reflect.Get(Main, "_mainModel"), "assetStorage"), "AssetCatalog")
            ?? throw Differs("the asset catalog");
        var missing = Reflect.Items(Reflect.Get(catalog, "TransferableGroupEntries"))
            .Select(entry => Reflect.As(entry, CatalogEntry))
            .Where(entry => Reflect.Get(entry, "resourceName") is string name && ItemIds.Attachment(name) != null && !listed.Contains(name))
            .ToList();
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
        for (var frame = 0; WornAccessories().Length != count; frame++)
        {
            if (frame > 300) throw new InvalidOperationException($"VRoid lists {WornAccessories().Length} accessories, expected {count}");
            yield return true;
        }
    }

    // Selecting a template in the editor adds one instance of it, appended to the list.
    private static IEnumerable<bool> AddAccessory(object template)
    {
        var count = WornAccessories().Length;
        var before = rebuilt;
        modified = true;
        Reflect.Call(AccessoryEditor, "set_SelectedTemplate", new object[] { null! });
        Reflect.Call(AccessoryEditor, "set_SelectedTemplate", template);
        foreach (var step in WaitForAccessories(before, count + 1)) yield return step;
    }

    private static IEnumerable<bool> RemoveAccessory(int index)
    {
        var count = WornAccessories().Length;
        var before = rebuilt;
        modified = true;
        Reflect.Call(AccessoryEditor, "set_SelectedIndex", index);
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
        // Null when the request leaves accessories alone.
        public Worn[]? Worn;
        public AccessoryPlan? Plan;
        public (string Item, string Name, object Template)[] Available = Array.Empty<(string, string, object)>();
        public List<(string Item, Dictionary<string, string> Colors)> Wanted = new();
        public readonly List<string> Discarded = new();
        public int Changed;

        public bool ItemsChange(string section) =>
            Picks.Any(pick => pick.Slot.Section == section && pick.Item != Before[pick.Slot.Path]);
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
            var wearing = work.Picks.Where(pick => pick.Slot.Section == section && pick.Item != null).ToList();
            for (var i = 0; i < wearing.Count; i++)
                for (var j = i + 1; j < wearing.Count; j++)
                    if (Conflict(wearing[i].Slot, wearing[j].Slot))
                        errors.Add(wearing[i].Slot.IsSet || wearing[j].Slot.IsSet
                            ? $"{wearing[i].Slot.Path} and {wearing[j].Slot.Path}: the set selects the other category's item itself; give one of them"
                            : $"{wearing[i].Slot.Path} and {wearing[j].Slot.Path}: VRoid cannot wear these together; take one off");
            if (discard) continue;
            // A new item replaces the category's own item and VRoid takes off conflicting
            // ones; a set may replace any item in its editor.
            var reported = new HashSet<string>();
            foreach (var (slot, item) in work.Picks.Where(pick => pick.Slot.Section == section && pick.Item != work.Before[pick.Slot.Path]))
                foreach (var other in slots.Where(other => work.Before[other.Path] == ItemIds.Custom && (other == slot
                    || (item != null && (Conflict(slot, other) || (slot.IsSet && !other.IsSet))))))
                    if (reported.Add(other.Path))
                        errors.Add($"{other.Path} wears an item that is not built in, which "
                            + (other == slot ? "this request replaces" : $"selecting {slot.Path} would replace")
                            + "; undo cannot restore it. Set discard_custom to allow it");
        }
        if (!request.Body.TryGetProperty("Accessories", out var accessories) || accessories.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var step in EnterAccessories()) yield return step;
        var worn = work.Worn = WornAccessories();
        work.Available = AvailableAccessories();
        work.Wanted = accessories.EnumerateArray().Select(entry => (entry.GetProperty("item").GetString()!,
            entry.EnumerateObject().Where(color => color.Name != "item").ToDictionary(color => color.Name, color => color.Value.GetString()!))).ToList();
        var plan = work.Plan = AccessoryPlan.Of(worn.Select(accessory => accessory.Item).ToList(), work.Wanted.Select(wanted => wanted.Item).ToList());
        for (var i = 0; i < work.Wanted.Count; i++)
            if (work.Wanted[i].Item != ItemIds.Custom && work.Available.All(available => available.Item != work.Wanted[i].Item))
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
    }

    private static Dictionary<string, object?> Entry(Dictionary<string, Dictionary<string, Dictionary<string, object?>>> previous,
        string section, string category) =>
        previous[section].TryGetValue(category, out var values) ? values : previous[section][category] = new();

    // Adds each item that differs from before the request to the undo
    // template, including items VRoid changed as a side effect; an item that is
    // not built in cannot be restored and is reported instead.
    private static void Record(Outfitting work, string section, Dictionary<string, Dictionary<string, Dictionary<string, object?>>> previous)
    {
        foreach (var slot in work.Slots[section].Where(slot => !slot.IsSet))
        {
            var before = work.Before[slot.Path];
            if (State(slot) == before) continue;
            if (before != ItemIds.Custom) Entry(previous, section, slot.Name)["item"] = before;
            else if (!work.Discarded.Contains(slot.Path)) work.Discarded.Add(slot.Path);
        }
    }

    // Wears the requested items in app order, so a set comes before the items
    // it selects, then checks each one reads back.
    private static IEnumerable<bool> ChangeItems(Outfitting work, Dictionary<string, Dictionary<string, Dictionary<string, object?>>> previous)
    {
        foreach (var section in Sections.Where(work.Slots.ContainsKey))
        {
            foreach (var step in Enter(section))
                if (step == null) yield return true;
            var picks = work.Picks.Where(pick => pick.Slot.Section == section).OrderBy(pick => pick.Slot.Index).ToList();
            foreach (var (slot, item) in picks)
            {
                // An earlier set may already have selected it.
                if (State(slot) == item) continue;
                Changed.Add(slot.Path);
                work.Changed++;
                foreach (var step in Wear(slot, item, work.Slots[section])) yield return step;
                Record(work, section, previous);
            }
            Record(work, section, previous);
            foreach (var (slot, item) in picks)
                if (State(slot) != item)
                    throw new InvalidOperationException($"{slot.Path} reads {State(slot) ?? "null"} after selecting {item ?? "null"} and the "
                        + "request's later items. A set reads as selected only while its items match it; give either the set or those items");
        }
    }

    // Removes, adds, and recolours accessories to match the request.
    private static IEnumerable<bool> ChangeAccessories(Outfitting work)
    {
        if (work.Plan is not { } plan) yield break;
        var worn = work.Worn!;
        foreach (var step in EnterAccessories()) yield return step;
        // Removing from the end keeps the earlier indices valid.
        foreach (var j in plan.Removed.OrderByDescending(j => j))
        {
            var path = $"Accessories/{worn[j].Name}";
            Changed.Add(path);
            work.Changed++;
            if (worn[j].Item == ItemIds.Custom) work.Discarded.Add(path);
            foreach (var step in RemoveAccessory(j)) yield return step;
        }
        var targets = plan.Kept.Select(pair => (Index: pair.Worn - plan.Removed.Count(j => j < pair.Worn), pair.Wanted)).ToList();
        foreach (var i in plan.Added)
        {
            var item = work.Wanted[i].Item;
            Changed.Add($"Accessories/{item}");
            work.Changed++;
            foreach (var step in AddAccessory(work.Available.First(available => available.Item == item).Template)) yield return step;
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
