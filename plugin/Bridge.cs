// Session-local file protocol shared with vroid_cli/rpc.py. Has no Unity
// dependencies so tests/protocol can exercise it directly.
using System.Text.Json;

namespace VroidBridge;

public sealed record Request(string Id, string Boot, string Method, int TimeoutMs, JsonElement Body)
{
    public string Text(string name) => Body.GetProperty(name).GetString()
        ?? throw new ArgumentException($"Missing {name}");

    public double Number(string name) => Body.GetProperty(name).GetDouble();
}

public sealed class RpcDirectory
{
    /// Change together with PROTOCOL in vroid_cli/rpc.py.
    public const int Protocol = 1;

    private readonly string root;
    private readonly HashSet<string> taken = new();
    public string Boot { get; }

    public RpcDirectory(string root, string boot)
    {
        this.root = root;
        Boot = boot;
        Directory.CreateDirectory(Path.Combine(root, "results"));
    }

    private string RequestPath => Path.Combine(root, "request.json");
    private string ResultPath(string id) => Path.Combine(root, "results", id + ".json");

    /// Marks a request left by an earlier app instance as having an unknown
    /// outcome; it is never replayed. A result written before the restart is kept.
    public void RecoverInterrupted()
    {
        if (!File.Exists(RequestPath)) return;
        var request = Parse();
        if (request == null) return;
        if (File.Exists(ResultPath(request.Id))) File.Delete(RequestPath);
        else Finish(request, false, "App restarted before this request finished; outcome unknown. "
            + "Inspect the project before another edit.");
    }

    /// Returns the pending request if it is addressed to this app instance and
    /// was not returned before; each request runs at most once. Requests for
    /// another instance are answered with an error and not run.
    public Request? Take()
    {
        if (!File.Exists(RequestPath)) return null;
        var request = Parse();
        if (request == null || taken.Contains(request.Id)) return null;
        if (request.Boot != Boot)
        {
            Finish(request, false, "Request belongs to another app instance; not executed");
            return null;
        }
        taken.Add(request.Id);
        return request;
    }

    /// Serializes a result. A payload that cannot be serialized becomes an error,
    /// since the operation has already run.
    public static string Result(bool ok, object? payload)
    {
        try
        {
            return JsonSerializer.Serialize(new { ok, result = ok ? payload : null, error = ok ? null : payload });
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                result = (object?)null,
                error = $"The operation ran, but its result could not be recorded ({error.Message}). Inspect the project.",
            });
        }
    }

    /// Writes the result, then removes the request. Results are kept for inspection.
    public void Finish(Request request, bool ok, object? payload) => Publish(request, Result(ok, payload));

    public void Publish(Request request, string result)
    {
        WriteText(ResultPath(request.Id), result);
        File.Delete(RequestPath);
    }

    public void WriteStatus(object status) => Write(Path.Combine(root, "status.json"), status);

    private Request? Parse()
    {
        try
        {
            var body = JsonDocument.Parse(File.ReadAllText(RequestPath)).RootElement.Clone();
            var id = body.GetProperty("id").GetString()!;
            // The id names the result file; accept only the CLI's format.
            if (!Guid.TryParseExact(id, "N", out _) || id != id.ToLowerInvariant())
                throw new FormatException("Invalid request id");
            return new Request(id, body.GetProperty("boot").GetString()!,
                body.GetProperty("method").GetString()!, body.GetProperty("timeout_ms").GetInt32(), body);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or NullReferenceException)
        {
            File.Move(RequestPath, Path.Combine(root, "request.invalid.json"), true);
            return null;
        }
    }

    private static void Write(string path, object value) => WriteText(path, JsonSerializer.Serialize(value));

    private static void WriteText(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
    }
}
