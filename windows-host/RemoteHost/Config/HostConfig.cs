using System.IO;
using System.Text.Json;

namespace RemoteHost.Config;

public class HostConfig
{
    public string ServerHttpUrl { get; set; } = "https://your-vps-domain.example.com";
    public string ServerWsUrl { get; set; } = "wss://your-vps-domain.example.com/ws";
    public string DeviceId { get; set; } = "";
    public string HostToken { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int MonitorAdapterIndex { get; set; } = 0;
    public int MonitorOutputIndex { get; set; } = 0;

    private static string ConfigPath =>
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "RemoteDesktopHost",
            "device.json"
        );

    public static HostConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<HostConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        // Host token is a bearer secret for this device - keep the file
        // private to this Windows user account (ACLs default to that under
        // %APPDATA%, which is already per-user and not world-readable).
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsRegistered => !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(HostToken);
}
