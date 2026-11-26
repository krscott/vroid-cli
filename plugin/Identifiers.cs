// Item identifiers and accessory matching. Has no Unity dependencies so
// tests/protocol can exercise it directly.
using System.Text.RegularExpressions;

namespace VroidBridge;

public static class Identifiers
{
    /// Stands for a custom item.
    public const string Custom = "custom";

    private const string BuiltIn = "pixiv/VRoid/";
    private const string Attachments = "pixiv/VRoid/Attachment/";
    private const string AccessoryName = "Model.N00.Transferable.";
    private const string CustomItems = "user/VRoid/CustomItem/";
    // The CLI's pattern for an identifier; "custom" is reserved.
    private static readonly Regex Valid = new("^[A-Za-z0-9_]+$");

    private static string? Checked(string identifier) =>
        Valid.IsMatch(identifier) && identifier != Custom ? identifier : null;

    /// The resource name of a built-in item that a category lists ends in
    /// `.../IDENTIFIER/TYPE.transferablegroup`, such as
    /// `pixiv/VRoid/Clothing/Tops/N00/159/...`. The identifier is unique within
    /// its category in VRoid 2.16.0; null means the name has no valid one.
    /// The name is a fixed asset path, so an item's identifier is the same in
    /// every session and after every restart (R-EDIT-9).
    public static string? CategoryItem(string? resourceName)
    {
        if (resourceName == null || !resourceName.StartsWith(BuiltIn)) return null;
        var parts = resourceName.Split('/');
        return parts.Length >= 4 ? Checked(parts[^2]) : null;
    }

    /// A built-in accessory item's resource name is
    /// `pixiv/VRoid/Attachment/IDENTIFIER/...`, such as `CatEar`; null means the
    /// name is not one.
    public static string? Attachment(string? resourceName) =>
        resourceName != null && resourceName.StartsWith(Attachments)
            ? Checked(resourceName[Attachments.Length..].Split('/')[0])
            : null;

    /// A worn accessory's display name keeps the localization key of the item
    /// it was added from, `Model.N00.Transferable.IDENTIFIER`, even for the
    /// copies VRoid makes when it adds a second accessory of an accessory kind;
    /// null means the name has no such key.
    public static string? AccessoryKey(string? localizationKey) =>
        localizationKey != null && localizationKey.StartsWith(AccessoryName)
            ? Checked(localizationKey[AccessoryName.Length..])
            : null;

    /// A worn accessory's item: the built-in item its localization key names,
    /// or "custom". The key names the item even for an imported accessory, but
    /// VRoid keeps every accessory of an accessory kind in one group, and a
    /// group an imported accessory started is a `user/VRoid/CustomItem/...`
    /// resource, so every accessory in such a group is custom.
    /// An accessory that VRoid's own Import button added to a group of built-in
    /// accessories carries no mark, so it reads as the built-in item its key
    /// names, and a template drops its texture.
    public static string Accessory(string? localizationKey, string groupResourceName, IReadOnlySet<string> builtIn) =>
        AccessoryKey(localizationKey) is { } key && builtIn.Contains(key) && !Imported(groupResourceName) ? key : Custom;

    /// Whether an import started the accessory group with this resource name.
    public static bool Imported(string groupResourceName) => groupResourceName.StartsWith(CustomItems);

    /// What a category wears, such as `Onepiece`: the last part of its group
    /// type (`TransferableGroupType.N00.Level1.Onepiece`) or of a conflicting
    /// type its selector lists (`TransferableType.N00.Onepiece`).
    public static string Kind(string type) => type[(type.LastIndexOf('.') + 1)..];

    /// Set categories (Face Sets, Eyes Sets, Hairstyle Sets, Whole Sets) select
    /// items in other categories and read as selected only while those match.
    public static bool IsSet(string transferableGroupType) => transferableGroupType.EndsWith("Set");
}

/// VRoid's accessory bones: the joints of its humanoid skeleton that an
/// accessory can hang from, and the one each built-in accessory starts on.
public static class Bones
{
    /// The character's humanoid bones in VRoid 2.16.0, in its order.
    public static readonly IReadOnlyList<string> All = new[]
    {
        "J_Bip_C_Hips", "J_Bip_L_UpperLeg", "J_Bip_R_UpperLeg", "J_Bip_L_LowerLeg", "J_Bip_R_LowerLeg",
        "J_Bip_L_Foot", "J_Bip_R_Foot", "J_Bip_C_Spine", "J_Bip_C_Chest", "J_Bip_C_Neck", "J_Bip_C_Head",
        "J_Bip_L_Shoulder", "J_Bip_R_Shoulder", "J_Bip_L_UpperArm", "J_Bip_R_UpperArm", "J_Bip_L_LowerArm",
        "J_Bip_R_LowerArm", "J_Bip_L_Hand", "J_Bip_R_Hand", "J_Bip_L_ToeBase", "J_Bip_R_ToeBase",
        "J_Adj_L_FaceEye", "J_Adj_R_FaceEye",
        "J_Bip_L_Thumb1", "J_Bip_L_Thumb2", "J_Bip_L_Thumb3", "J_Bip_L_Index1", "J_Bip_L_Index2", "J_Bip_L_Index3",
        "J_Bip_L_Middle1", "J_Bip_L_Middle2", "J_Bip_L_Middle3", "J_Bip_L_Ring1", "J_Bip_L_Ring2", "J_Bip_L_Ring3",
        "J_Bip_L_Little1", "J_Bip_L_Little2", "J_Bip_L_Little3",
        "J_Bip_R_Thumb1", "J_Bip_R_Thumb2", "J_Bip_R_Thumb3", "J_Bip_R_Index1", "J_Bip_R_Index2", "J_Bip_R_Index3",
        "J_Bip_R_Middle1", "J_Bip_R_Middle2", "J_Bip_R_Middle3", "J_Bip_R_Ring1", "J_Bip_R_Ring2", "J_Bip_R_Ring3",
        "J_Bip_R_Little1", "J_Bip_R_Little2", "J_Bip_R_Little3", "J_Bip_C_UpperChest",
    };

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["GlassesHi"] = "J_Bip_C_Head", ["GlassesLow"] = "J_Bip_C_Head", ["BaseballCap"] = "J_Bip_C_Head",
        ["CatEar"] = "J_Bip_C_Head", ["RabbitEar"] = "J_Bip_C_Head", ["SilkHat"] = "J_Bip_C_Head",
        ["StrawHat"] = "J_Bip_C_Head", ["WitchHat"] = "J_Bip_C_Head", ["WorkCap"] = "J_Bip_C_Head",
        ["CatTail"] = "J_Bip_C_Hips", ["FoxTail"] = "J_Bip_C_Hips", ["RabbitTail"] = "J_Bip_C_Hips",
    };

    /// The bone VRoid 2.16.0 puts a newly added built-in accessory on, or null
    /// for any other item.
    public static string? Default(string item) => Defaults.GetValueOrDefault(item);
}

/// Checks of an accessory's placement relative to its bone.
/// VRoid stores exactly the float32 values it is given and does not normalize
/// a rotation, so a placement reads back exactly, also after a save and a
/// reopen.
public static class Placement
{
    /// How far a rotation's length may be from 1. VRoid's own gizmo leaves
    /// about 1e-7.
    public const double Tolerance = 1e-4;

    /// A reason `rotation` (x, y, z, w) is no unit quaternion, or null.
    public static string? Rotation(IReadOnlyList<float> rotation)
    {
        var length = Math.Sqrt(rotation.Sum(part => (double)part * part));
        return Math.Abs(length - 1) <= Tolerance ? null : $"has length {length}, not 1; give a unit quaternion [x, y, z, w], or angles as {{\"degrees\": [x, y, z]}}";
    }

    /// A reason `scale` is not positive on every axis, or null.
    public static string? Scale(IReadOnlyList<float> scale) =>
        scale.All(factor => factor > 0) ? null : "every factor must be positive";
}

/// A worn accessory as matching sees it: its item, bone, and accessory kind,
/// the group type VRoid keeps one group of accessories for, such as every pair
/// of animal ears. A null bone is VRoid's "None".
public sealed record WornAccessory(string Item, string? Bone, string Kind);

/// A wanted accessory: its item, its bone if the request names one, and its
/// accessory kind, the group type a built-in one joins.
public sealed record WantedAccessory(string Item, bool HasBone, string? Bone, string Kind);

/// One accessory to add for a wanted index: a new one of its item, or a copy
/// of the one added for `CopyOf`, which copies its bone. `SetBone` moves it to
/// its wanted bone once it is added, while no other of its accessory kind is
/// worn. `Bone` is the bone it ends on. When it replaces a worn accessory that
/// was removed to be added again, `Matched` names that one, and the new one
/// takes over its values.
public sealed record Addition(int Wanted, int? CopyOf, bool SetBone, string? Bone);

/// How worn accessories become the wanted ones. `Kept` pairs a worn index with
/// a wanted index, and `Rebone` moves kept ones to their wanted bone. `Removed`
/// lists worn indices to remove, `Added` the accessories to add in order.
/// `Readded` is the worn indices removed only to add them again, which do not
/// count as changes. `Matched` is the worn accessory each wanted one keeps or
/// replaces, even where `Errors` holds what VRoid cannot do.
public sealed record AccessoryPlan(List<(int Worn, int Wanted)> Kept, List<(int Worn, string? Bone)> Rebone,
    List<int> Removed, List<Addition> Added, HashSet<int> Readded, int?[] Matched, List<string> Errors)
{
    /// VRoid changes an accessory's bone only while no other accessory of its
    /// accessory kind is worn; it adds one on its default bone, and a duplicate
    /// copies the item and bone. So the accessories of one accessory kind sit on
    /// their default bone, except copies of one item on one other bone. Built-in
    /// accessories of one item are interchangeable: a wanted one keeps a worn
    /// one that `same` says already matches it, first among those on the
    /// default bone if it names no bone; then one that names its bone keeps one
    /// on that bone, and one that names no bone keeps one on the default bone,
    /// then any. When a kept one cannot move, every one of that accessory kind
    /// is removed and added again: one first, moved while alone, then copies of
    /// it and new ones. "custom" matches any worn custom accessory, in order,
    /// which is its only identity, and moves only while alone in its accessory
    /// kind. VRoid keeps an accessory kind's group after its last accessory is
    /// removed, and adds a built-in one to a group an import started, where it
    /// reads as custom, so none is added to the `imported` accessory kinds.
    /// Removing the accessories of an accessory kind and adding them again
    /// reaches every arrangement of bones VRoid's own controls can make for
    /// built-in accessories (R-EDIT-7, R-EDIT-11).
    public static AccessoryPlan Of(IReadOnlyList<WornAccessory> worn, IReadOnlyList<WantedAccessory> wanted,
        Func<int, int, bool>? same = null, IReadOnlySet<string>? imported = null)
    {
        var matched = new int?[wanted.Count];
        var plan = new AccessoryPlan(new(), new(), new(), new(), new(), matched, new());
        var used = new bool[worn.Count];
        void Pair(int i, int j)
        {
            used[j] = true;
            matched[i] = j;
        }
        int Free(int i, Func<int, bool> fits) =>
            Enumerable.Range(0, worn.Count).FirstOrDefault(j => !used[j] && worn[j].Item == wanted[i].Item && fits(j), -1);
        // A built-in one that names no bone takes one on its default bone first, so
        // it never leaves the default bone only because of VRoid's order.
        bool BuiltIn(int i) => wanted[i].Item != Identifiers.Custom;
        foreach (var pass in new Func<int, int, bool>[]
                 {
                     (i, j) => BuiltIn(i) && same != null && same(i, j)
                         && (wanted[i].HasBone || worn[j].Bone == Bones.Default(wanted[i].Item)),
                     (i, j) => BuiltIn(i) && same != null && same(i, j),
                     (i, j) => BuiltIn(i) && wanted[i].HasBone && worn[j].Bone == wanted[i].Bone,
                     (i, j) => BuiltIn(i) && !wanted[i].HasBone && worn[j].Bone == Bones.Default(wanted[i].Item),
                     (i, j) => BuiltIn(i) && !wanted[i].HasBone,
                     (_, _) => true,
                 })
            for (var i = 0; i < wanted.Count; i++)
                if (matched[i] == null && Free(i, j => pass(i, j)) is var j and >= 0)
                    Pair(i, j);
        plan.Removed.AddRange(Enumerable.Range(0, worn.Count).Where(j => !used[j]));
        string? Final(int i) => wanted[i].HasBone ? wanted[i].Bone : matched[i] is { } j ? worn[j].Bone : Bones.Default(wanted[i].Item);
        bool Moves(int i) => matched[i] is { } j && wanted[i].HasBone && worn[j].Bone != wanted[i].Bone;

        for (var i = 0; i < wanted.Count; i++)
            // An unmatched one is added, which the caller refuses.
            if (wanted[i].Item == Identifiers.Custom && matched[i] == null) plan.Added.Add(new Addition(i, null, false, null));
            else if (wanted[i].Item == Identifiers.Custom && matched[i] is { } j)
            {
                plan.Kept.Add((j, i));
                if (!Moves(i)) continue;
                if (Enumerable.Range(0, worn.Count).Count(k => used[k] && worn[k].Kind == worn[j].Kind) > 1)
                    plan.Errors.Add($"Accessories[{i}]: VRoid moves an accessory to another bone only while no other accessory of its accessory kind is worn");
                else plan.Rebone.Add((j, wanted[i].Bone));
            }
        foreach (var kind in wanted.Where(entry => entry.Item != Identifiers.Custom).Select(entry => entry.Kind).Distinct())
        {
            var entries = Enumerable.Range(0, wanted.Count).Where(i => wanted[i].Item != Identifiers.Custom && wanted[i].Kind == kind).ToList();
            var kept = entries.Where(i => matched[i] != null).ToList();
            if (imported != null && imported.Contains(kind))
            {
                // A built-in one of this accessory kind would read as custom, so none is worn.
                foreach (var i in entries)
                    plan.Errors.Add($"Accessories[{i}]: VRoid keeps the group of {Identifiers.Kind(kind)} accessories that an import started, even "
                        + $"after the last one is removed, and would add {wanted[i].Item} to it, where it would read as custom, so vroid-cli cannot "
                        + $"add a built-in accessory of the accessory kind {Identifiers.Kind(kind)} to this character. Report this VRoid limit to the user");
                continue;
            }
            // An item VRoid does not offer, which the caller refuses.
            if (entries.Any(i => Bones.Default(wanted[i].Item) == null))
            {
                plan.Kept.AddRange(kept.Select(i => (matched[i]!.Value, i)));
                plan.Added.AddRange(entries.Where(i => matched[i] == null).Select(i => new Addition(i, null, false, null)));
                continue;
            }
            bool Away(int i) => Final(i) != Bones.Default(wanted[i].Item);
            var away = entries.Where(Away).Select(i => (wanted[i].Item, Bone: Final(i))).Distinct().ToList();
            if (away.Count > 1)
            {
                plan.Errors.Add($"Accessories: VRoid moves accessories of one accessory kind off their default bone only as copies of one, "
                    + $"but these put {string.Join(", ", away.Select(entry => $"{entry.Item} on {entry.Bone ?? "None"}"))}");
                continue;
            }
            var moved = kept.Count(Moves);
            var direct = kept.Count == 0 || ((moved == 0 || kept.Count == 1) && !entries.Any(i => matched[i] == null && Away(i)));
            if (direct)
                foreach (var i in kept)
                {
                    plan.Kept.Add((matched[i]!.Value, i));
                    if (Moves(i)) plan.Rebone.Add((matched[i]!.Value, wanted[i].Bone));
                }
            else
                foreach (var i in kept)
                {
                    plan.Removed.Add(matched[i]!.Value);
                    plan.Readded.Add(matched[i]!.Value);
                }
            // The first one away from its default bone moves while it is alone; the
            // others there copy it, and the rest start at their default.
            var added = direct ? entries.Where(i => matched[i] == null).ToList() : entries;
            var first = added.FirstOrDefault(Away, -1);
            foreach (var i in added.OrderBy(i => i != first))
                plan.Added.Add(new Addition(i, i != first && Away(i) ? first : null, i == first, Final(i)));
        }
        plan.Removed.Sort();
        plan.Kept.Sort((a, b) => a.Wanted.CompareTo(b.Wanted));
        return plan;
    }

    /// Worn accessories in a dump's order: by item, built-in before custom.
    /// Built-in accessories of one item are interchangeable, so they are ordered
    /// by `key`, their accessory entry, and dumps compare across app instances
    /// whichever one VRoid lists first; custom ones keep VRoid's order.
    public static IEnumerable<int> DumpOrder(IReadOnlyList<string> worn, IReadOnlyList<string> key) =>
        Enumerable.Range(0, worn.Count)
            .OrderBy(i => worn[i] == Identifiers.Custom)
            .ThenBy(i => worn[i], StringComparer.Ordinal)
            .ThenBy(i => worn[i] == Identifiers.Custom ? "" : key[i], StringComparer.Ordinal);
}
