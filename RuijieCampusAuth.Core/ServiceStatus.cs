using System.Text.Json;

namespace RuijieCampusAuth.Core;

public static class ServiceStatus
{
    private static readonly object Gate = new();

    public static string Root => AppContext.BaseDirectory;
    public static string StatusPath => Path.Combine(Root, "service_status.json");

    public static void Write(
        string state,
        string message,
        string? interfaceName = null,
        string? server = null)
    {
        var payload = new
        {
            time = DateTimeOffset.Now.ToString("O"),
            state,
            message,
            @interface = interfaceName ?? "",
            server = server ?? "",
        };
        lock (Gate)
        {
            try
            {
                var temp = StatusPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(payload));
                File.Move(temp, StatusPath, true);
            }
            catch
            {
                // Status reporting must never stop authentication.
            }
        }
    }

    public static ServiceStatusSnapshot? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceStatusSnapshot>(
                File.ReadAllText(StatusPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ServiceStatusSnapshot
{
    public string? Time { get; set; }
    public string? State { get; set; }
    public string? Message { get; set; }
    public string? Interface { get; set; }
    public string? Server { get; set; }
}
