// Headless verification harness: exercises the real HostService/
// ControllerService (real DXGI capture, real SIPSorcery WebRTC, real
// SendInput on the host side) against the real running signaling server,
// without needing to click through the WPF UI. Not part of the shipped
// product - dev-only smoke test.
//
// WARNING: running the "host" mode actually captures and remote-controls
// whatever machine it runs on, exactly like the shipped host app - there is
// no sandbox. Any device that pairs with it will see your real screen and
// can move your real mouse and type into whatever window has real OS focus.
// Only ever run "host" mode against a throwaway target (e.g. a blank Notepad
// window you opened yourself), never while anything sensitive is on screen.
// "controller" mode below never calls SendControl, so it only ever *views* -
// it cannot move a mouse or type anywhere.
using System;
using System.Threading.Tasks;
using RemoteHost;
using RemoteHost.Config;

var mode = args.Length > 0 ? args[0] : "host";
var serverUrl = args.Length > 1 ? args[1] : "http://localhost:8443";

if (mode == "controller")
{
    var deviceId = args[2];
    var code = args[3];
    var wsUrl = serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws";

    Console.WriteLine($"Connecting as controller to {deviceId} via {wsUrl}...");
    var controller = new ControllerService();
    controller.OnStatus += s => Console.WriteLine($"[status] {s}");
    controller.OnPairingPending += () => Console.WriteLine("[pairing] pending host approval");
    controller.OnRejected += r => Console.WriteLine($"[pairing] REJECTED: {r}");
    controller.OnConnectionStateChanged += s => Console.WriteLine($"[webrtc] {s}");
    controller.OnSessionStarted += () => Console.WriteLine("[session] STARTED");
    controller.OnSessionEnded += r => Console.WriteLine($"[session] ENDED: {r}");
    int frameCount = 0;
    controller.OnDecodedFrame += (sample, w, h, stride, pf) =>
    {
        frameCount++;
        if (frameCount <= 5 || frameCount % 50 == 0)
        {
            Console.WriteLine($"[video] frame #{frameCount}: {w}x{h} stride={stride} format={pf} bytes={sample.Length} (VIEW ONLY - no input is ever sent by this harness mode)");
        }
    };

    await controller.ConnectAndPairAsync(wsUrl, "Test Controller Harness", deviceId, code);
    Console.WriteLine("Controller harness running (view-only). Press Ctrl+C to exit.");
    await Task.Delay(Timeout.Infinite);
    return;
}

Console.WriteLine($"Registering a test device (no account needed) against {serverUrl}...");
var api = new ApiClient(serverUrl);
var device = await api.RegisterDeviceAsync("Test Harness Host");

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
host.OnPairingCode += (code2, expiresAt) => Console.WriteLine($"\n>>> PAIRING CODE: {code2} <<<\n");
host.OnPairingRequest += req =>
{
    Console.WriteLine($"[pairing] incoming request from {req.ControllerName} -> auto-accepting");
    host.RespondToPairingRequest(req.SessionRequestId, true);
};
host.OnSessionStarted += (sessionId, controllerName) => Console.WriteLine($"[session] STARTED {sessionId} with {controllerName}");
host.OnSessionEnded += reason => Console.WriteLine($"[session] ENDED: {reason}");

await host.StartAsync();

Console.WriteLine("Host harness running. Press Ctrl+C to exit.");
await Task.Delay(Timeout.Infinite);
