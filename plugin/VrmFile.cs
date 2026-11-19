// Reads the metadata of VRM files. Has no Unity dependencies so tests/protocol
// can exercise it directly.
using System.Text;
using System.Text.Json;

namespace VroidBridge;

// A VRM file's version, "1.0" or "0.0", and the title and first author its
// metadata names.
public sealed record VrmMeta(string Version, string Title, string Author);

public static class VrmFile
{
    private const uint Magic = 0x46546C67; // "glTF"
    private const uint JsonChunk = 0x4E4F534A; // "JSON"
    // VRoid writes well under 1 MiB of JSON; a huge chunk would stall the app's main thread.
    private const uint MaxJson = 64 << 20;

    /// A VRM is a binary glTF 2.0 file whose JSON chunk holds a VRM 1.0
    /// `VRMC_vrm` or VRM 0.0 `VRM` extension. Throws ArgumentException for a
    /// file that is not one, including one cut short.
    public static VrmMeta Read(Stream file)
    {
        try
        {
            using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("it is not a binary glTF file");
            var version = reader.ReadUInt32();
            if (version != 2) throw new InvalidDataException($"it is glTF version {version}, not 2");
            var length = reader.ReadUInt32();
            if (length != file.Length) throw new InvalidDataException($"its header gives {length} bytes, but it holds {file.Length}");
            var size = reader.ReadUInt32();
            if (reader.ReadUInt32() != JsonChunk) throw new InvalidDataException("its first chunk is not JSON");
            if (size > MaxJson) throw new InvalidDataException($"its JSON chunk holds {size} bytes");
            var bytes = reader.ReadBytes((int)size);
            if (bytes.Length != size) throw new InvalidDataException("its JSON chunk is cut short");
            using var json = JsonDocument.Parse(bytes);
            var extensions = json.RootElement.TryGetProperty("extensions", out var found) && found.ValueKind == JsonValueKind.Object
                ? found
                : default;
            if (extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("VRMC_vrm", out var vrm1))
            {
                var meta = Object(vrm1, "meta");
                var authors = meta.TryGetProperty("authors", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
                var author = authors.ValueKind == JsonValueKind.Array && authors.GetArrayLength() > 0 ? authors[0].GetString() ?? "" : "";
                return new VrmMeta("1.0", Text(meta, "name"), author);
            }
            if (extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("VRM", out var vrm0))
            {
                var meta = Object(vrm0, "meta");
                return new VrmMeta("0.0", Text(meta, "title"), Text(meta, "author"));
            }
            throw new InvalidDataException("it has no VRMC_vrm or VRM extension");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or EndOfStreamException or InvalidOperationException)
        {
            throw new ArgumentException($"not a VRM file: {error.Message}");
        }
    }

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"its VRM extension has no {name} object");

    // VRoid leaves out empty VRM 0.0 fields.
    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
