using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemoteHost.Config;

public sealed class ApiException(string message) : System.Exception(message);

public sealed record AuthResponse(string Token, string Email);
public sealed record DeviceResponse(string DeviceId, string HostToken, string Name);

/// <summary>Minimal REST client used once, during first-run setup, to obtain a device identity.</summary>
public sealed class ApiClient(string serverHttpUrl)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { BaseAddress = new System.Uri(serverHttpUrl) };

    public Task<AuthResponse> LoginAsync(string email, string password) =>
        PostAuth("/api/auth/login", email, password);

    public Task<AuthResponse> RegisterAsync(string email, string password) =>
        PostAuth("/api/auth/register", email, password);

    private async Task<AuthResponse> PostAuth(string path, string email, string password)
    {
        var res = await _http.PostAsJsonAsync(path, new { email, password });
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadFromJsonAsync<JsonErrorBody>(JsonOpts);
            throw new ApiException(body?.Error ?? $"Request failed ({(int)res.StatusCode})");
        }
        var parsed = await res.Content.ReadFromJsonAsync<JsonAuthBody>(JsonOpts);
        return new AuthResponse(parsed!.Token, parsed.Email);
    }

    public async Task<DeviceResponse> RegisterDeviceAsync(string token, string deviceName)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices")
        {
            Content = JsonContent.Create(new { name = deviceName }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadFromJsonAsync<JsonErrorBody>(JsonOpts);
            throw new ApiException(body?.Error ?? $"Request failed ({(int)res.StatusCode})");
        }
        var parsed = await res.Content.ReadFromJsonAsync<JsonDeviceBody>(JsonOpts);
        return new DeviceResponse(parsed!.DeviceId, parsed.HostToken, parsed.Name);
    }

    private sealed record JsonErrorBody(string? Error);
    private sealed record JsonAuthBody(string Token, string Email);
    private sealed record JsonDeviceBody(string DeviceId, string HostToken, string Name);
}
