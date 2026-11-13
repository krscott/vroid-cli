// Reads VRoid's custom item files. Has no Unity dependencies so tests/protocol
// can exercise it directly.
using System.IO.Compression;
using System.Text.Json;

namespace VroidBridge;

public static class CustomItemFile
{
    /// A `.vroidcustomitem` file is a zip archive whose `v1customitem/meta.json`
    /// names the type of the item's root group, such as
    /// `TransferableGroupType.N00.Level1.AnimalEar` for a pair of animal ears.
    /// Throws ArgumentException for a file that is not one.
    public static string Kind(Stream file)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            var meta = zip.GetEntry("v1customitem/meta.json") ?? throw new InvalidDataException("it has no v1customitem/meta.json");
            using var stream = meta.Open();
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.TryGetProperty("rootTransferableGroupTypeId", out var kind) && kind.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidDataException("its meta.json names no rootTransferableGroupTypeId");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException($"not a VRoid custom item file: {error.Message}");
        }
    }
}
