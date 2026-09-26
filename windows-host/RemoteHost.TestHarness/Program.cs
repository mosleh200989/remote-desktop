// Headless verification harness: exercises the real HostService (real DXGI
// capture, real SIPSorcery WebRTC, real SendInput) against the real running
// signaling server, without needing to click through the WPF UI. Not part
// of the shipped product - dev-only smoke test.
//
// WARNING: running this actually captures and remote-controls whatever
// machine it runs on, exactly like the shipped host app - there is no
// sandbox. Any device that pairs with it will see your real screen and can
// move your real mouse and type into whatever window has real OS focus.
// Only ever run this against a throwaway target (e.g. a blank Notepad
// window you opened yourself), never while anything sensitive is on screen,
// and never on a machine you don't want remotely controllable during the
// test.
using System;
using System.Threading.Tasks;
using RemoteHost;
using RemoteHost.Config;

var serverUrl = args.Length > 0 ? args[0] : "http://localhost:8443";
var email = $"harness-{DateTime.Now.Ticks}@example.com";
var password = "harness-test-password-123";

Console.WriteLine($"Registering test account {email} against {serverUrl}...");
var api = new ApiClient(serverUrl);
var auth = await api.RegisterAsync(email, password);
var device = await api.RegisterDeviceAsync(auth.Token, "Test Harness Host");

var config = new HostConfig
{
    ServerHttpUrl = serverUrl,
    ServerWsUrl = serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws",
    DeviceId = device.DeviceId,
    HostToken = device.HostToken,
    DeviceName = "Test Harness Host",
};
config.Save();

Console.WriteLine($"\n>>> DEVICE ID: {device.DeviceId} <<<\n");

var host = new HostService(config);
host.OnConnectedChanged += c => Console.WriteLine($"[connected={c}]");
host.OnStatus += s => Console.WriteLine($"[status] {s}");
host.OnPairingCode += (code, expiresAt) => Console.WriteLine($"\n>>> PAIRING CODE: {code} <<<\n");
host.OnPairingRequest += req =>
{
    Console.WriteLine($"[pairing] incoming request from {req.ControllerEmail} -> auto-accepting");
    host.RespondToPairingRequest(req.SessionRequestId, true);
};
host.OnSessionStarted += (sessionId, controllerEmail) => Console.WriteLine($"[session] STARTED {sessionId} with {controllerEmail}");
host.OnSessionEnded += reason => Console.WriteLine($"[session] ENDED: {reason}");

await host.StartAsync();

Console.WriteLine("Harness running. Press Ctrl+C to exit.");
await Task.Delay(Timeout.Infinite);
