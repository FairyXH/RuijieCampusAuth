using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core.Debug;

public static class DebugRedaction
{
    private static readonly HashSet<string> ExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "iv",
        "password",
        "passwd",
        "secret",
        "token",
        "credential",
        "credentials",
    };

    private static readonly string[] NameFragments =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "credential",
    ];

    public static bool IsSensitiveName(string name)
    {
        if (ExactNames.Contains(name) ||
            name.EndsWith("key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return NameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static string? RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }
        try
        {
            var node = JsonNode.Parse(json);
            RedactNode(node);
            return node?.ToJsonString();
        }
        catch (JsonException)
        {
            return "[unparsed arguments]";
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(pair => pair.Key).ToList())
                {
                    if (IsSensitiveName(name))
                    {
                        obj[name] = "***";
                    }
                    else
                    {
                        RedactNode(obj[name]);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RedactNode(item);
                }
                break;
        }
    }
}

public sealed class DebugAuditLog(string baseDirectory)
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path = System.IO.Path.Combine(
        baseDirectory,
        "logs",
        "debug.log");

    public string Path => _path;

    public void Write(
        string feature,
        string action,
        bool ok,
        string? message,
        string? argumentsJson = null)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var entry = new JsonObject
            {
                ["time"] = DateTimeOffset.Now.ToString("O"),
                ["feature"] = feature,
                ["action"] = action,
                ["ok"] = ok,
                ["message"] = message,
                ["arguments"] = DebugRedaction.RedactJson(argumentsJson),
            };
            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    _path,
                    entry.ToJsonString() + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Audit failures must never break the feature call.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaxBytes)
        {
            return;
        }
        var previous = _path + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }
        File.Move(_path, previous);
    }
}
