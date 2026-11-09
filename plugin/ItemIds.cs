// Item identifiers and accessory matching. Has no Unity dependencies so
// tests/protocol can exercise it directly.
using System.Text.RegularExpressions;

namespace VroidBridge;

public static class ItemIds
{
    /// Stands for an item that is not one of VRoid's built-in items.
    public const string Custom = "custom";

    private const string BuiltIn = "pixiv/VRoid/";
    private const string Attachments = "pixiv/VRoid/Attachment/";
    private const string AccessoryName = "Model.N00.Transferable.";
    // The CLI's ItemId pattern; "custom" is reserved.
    private static readonly Regex Valid = new("^[A-Za-z0-9_]+$");

    private static string? Checked(string id) => Valid.IsMatch(id) && id != Custom ? id : null;

    /// A built-in item's resource name ends in `.../ID/TYPE.transferablegroup`,
    /// such as `pixiv/VRoid/Clothing/Tops/N00/159/...`. The ID is unique within
    /// its category in VRoid 2.16.0; null means the name has no valid ID.
    public static string? Preset(string? resourceName)
    {
        if (resourceName == null || !resourceName.StartsWith(BuiltIn)) return null;
        var parts = resourceName.Split('/');
        return parts.Length >= 4 ? Checked(parts[^2]) : null;
    }

    /// A built-in accessory template's resource name is
    /// `pixiv/VRoid/Attachment/ID/...`, such as `CatEar`; null means it is not
    /// built in.
    public static string? Attachment(string? resourceName) =>
        resourceName != null && resourceName.StartsWith(Attachments)
            ? Checked(resourceName[Attachments.Length..].Split('/')[0])
            : null;

    /// A worn accessory's display name keeps the localization key of the
    /// template it was added from, `Model.N00.Transferable.ID`, even for the
    /// copies VRoid makes when it adds a second accessory of a kind; null means
    /// the name has no such key.
    public static string? AccessoryKey(string? localizationKey) =>
        localizationKey != null && localizationKey.StartsWith(AccessoryName)
            ? Checked(localizationKey[AccessoryName.Length..])
            : null;

    /// What a category wears, such as `Onepiece`: the last part of its group
    /// type (`TransferableGroupType.N00.Level1.Onepiece`) or of a conflicting
    /// type its selector lists (`TransferableType.N00.Onepiece`).
    public static string Kind(string type) => type[(type.LastIndexOf('.') + 1)..];

    /// Set categories (Face Sets, Eyes Sets, Hairstyle Sets, Whole Sets) select
    /// items in other categories and read as selected only while those match.
    public static bool IsSet(string transferableGroupType) => transferableGroupType.EndsWith("Set");
}

/// How worn accessories become the wanted ones: `Kept` pairs a worn index with
/// a wanted index, `Removed` lists worn indices, and `Added` wanted indices.
public sealed record AccessoryPlan(List<(int Worn, int Wanted)> Kept, List<int> Removed, List<int> Added)
{
    /// Keeps the first unmatched worn accessory with each wanted item, in
    /// order, so repeated items pair up in app order. "custom" matches any worn
    /// accessory that is not built in.
    public static AccessoryPlan Of(IReadOnlyList<string> worn, IReadOnlyList<string> wanted)
    {
        var used = new bool[worn.Count];
        var kept = new List<(int, int)>();
        var added = new List<int>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var match = Enumerable.Range(0, worn.Count).FirstOrDefault(j => !used[j] && worn[j] == wanted[i], -1);
            if (match < 0) added.Add(i);
            else
            {
                used[match] = true;
                kept.Add((match, i));
            }
        }
        var removed = Enumerable.Range(0, worn.Count).Where(j => !used[j]).ToList();
        return new AccessoryPlan(kept, removed, added);
    }

    /// Worn accessories in a dump's order: by item, built-in before custom,
    /// keeping app order within an item, so dumps compare across instances.
    public static IEnumerable<int> DumpOrder(IReadOnlyList<string> worn) =>
        Enumerable.Range(0, worn.Count)
            .OrderBy(i => worn[i] == ItemIds.Custom)
            .ThenBy(i => worn[i], StringComparer.Ordinal);
}
