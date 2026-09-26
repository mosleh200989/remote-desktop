using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemoteHost.Config;

public sealed class ApiException(string message) : System.Exception(message);

public sealed record DeviceResponse(string DeviceId, string HostToken, string Name);

/// <summary>Minimal REST client used once, during first-run setup, to obtain a device identity.</summary>
public sealed class ApiClient(string serverHttpUrl)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { BaseAddress = new System.Uri(serverHttpUrl) };

    // No accounts (AnyDesk-style): anyone can register a device and get back
    // a device ID + a private host token, shown once and then stored locally.
    public async Task<DeviceResponse> RegisterDeviceAsync(string deviceName)
    {
        var res = await _http.PostAsJsonAsync("/api/devices", new { name = deviceName });
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadFromJsonAsync<JsonErrorBody>(JsonOpts);
            throw new ApiException(body?.Error ?? $"Request failed ({(int)res.StatusCode})");
        }
        var parsed = await res.Content.ReadFromJsonAsync<JsonDeviceBody>(JsonOpts);
        return new DeviceResponse(parsed!.DeviceId, parsed.HostToken, parsed.Name);
    }

    private sealed record JsonErrorBody(string? Error);
    private sealed record JsonDeviceBody(string DeviceId, string HostToken, string Name);
}
