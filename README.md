# Self-Hosted Remote Desktop

Attended, self-hosted remote desktop for your own Windows computers: a Windows
host app, a signaling server you run on your own VPS, and a responsive web
controller usable from Android Chrome or any desktop browser. Screen and
input travel peer-to-peer over WebRTC once your VPS has helped the two
devices find each other.

## Architecture

```
┌─────────────────┐        WebSocket (auth, pairing,        ┌──────────────────┐
│  Windows Host    │◄──────  SDP/ICE relay only) ──────────►│  Web Controller  │
│  (WPF, .NET 8)   │        wss://your-vps/ws                │ (React, Vite)    │
└────────┬─────────┘                                          └────────┬─────────┘
         │                 ┌──────────────────┐                        │
         │  screen (VP8)   │  Signaling server │   REST (login/pair)   │
         └───────WebRTC────┤  Node + Express   │◄───────────────────────┘
             + control     │  + ws, on your VPS │
              data channel └─────────┬──────────┘
                                     │
                              ┌──────┴──────┐
                              │   coturn    │  (TURN relay, same VPS)
                              └─────────────┘
```

- **Windows host** (`windows-host/`): C# / .NET 8 WPF app. Captures the screen
  via DXGI Desktop Duplication, encodes VP8 via SIPSorcery, injects mouse/
  keyboard via `SendInput`, and is always the WebRTC *answerer*.
- **Signaling server** (`signaling-server/`): Node + TypeScript. Owns
  accounts, pairing codes, and session authorization; relays WebRTC signaling
  but never sees screen/input content (that's peer-to-peer/TURN-relayed,
  end-to-end DTLS-SRTP encrypted).
- **Web controller** (`web-controller/`): React + TypeScript + Vite. Login,
  enter device ID + pairing code, full-screen viewer with mouse/touch/
  keyboard controls. Always the WebRTC *offerer*.
- **coturn**: TURN relay so connections still work when direct peer-to-peer
  fails (symmetric NATs, restrictive networks) - the acceptance scenario
  ("two different networks") depends on this.

### Why this stack

- **Signaling server in Node/TypeScript**: WebSocket + REST is a natural fit,
  and `node:sqlite` (built into Node 22.5+) avoids a native-compile toolchain
  entirely - the first attempt used `better-sqlite3`, which needed a full
  Visual Studio C++ workload this machine didn't have; switching to the
  built-in driver made the server buildable and deployable with nothing but
  Node itself, on Windows or the Ubuntu VPS alike.
- **Web controller in React/Vite**: works unmodified on desktop Chrome/Firefox
  and Android Chrome; a Pointer Events-based gesture engine covers mouse and
  touch with one code path.
- **Windows host in C# / SIPSorcery**: SIPSorcery is a pure-managed WebRTC
  stack (no native WebRTC library to cross-compile for Windows), and DXGI
  Desktop Duplication + `SendInput` are the standard, supported Win32 APIs
  for screen capture and input injection respectively.

## Repository layout

```
signaling-server/    Node/TypeScript signaling + pairing + auth server
web-controller/      React/Vite web app (desktop + Android Chrome)
windows-host/        C# .NET 8 WPF host app
  RemoteHost/             the shipped app
  RemoteHost.TestHarness/ headless dev-only smoke test (see its warning header)
deploy/              docker-compose, coturn config, nginx config
```

## Requirements

- **Dev machine building the Windows host**: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- **Signaling server / web controller**: Node.js 22.5+ (Node 24 recommended).
- **VPS**: Ubuntu 22.04+, a domain name pointed at it, ports 80/443/3478 (+
  49160-49200 UDP for TURN) open.

## 1. Run it locally first (no VPS needed)

```bash
cd signaling-server
npm install
cp .env.example .env          # defaults are fine for local testing
npm run dev                   # listens on :8443
```

```bash
cd web-controller
npm install
cp .env.example .env.local    # points at http://localhost:8443 by default
npm run dev                   # opens on :5173
```

```powershell
cd windows-host
dotnet run --project RemoteHost
```
On first launch the host app asks for the signaling server URL
(`http://localhost:8443` for this local test) plus an email/password - it
registers that account and this device automatically. It then shows a
**device ID** and **pairing code**.

Open `http://localhost:5173` in a browser, sign in (register if it's a new
account), enter that device ID + code, and approve the request in the host
app's window. You should see your own screen and be able to click/type into
it.

## 2. Deploy the signaling server + TURN to your VPS

```bash
ssh you@your-vps
sudo apt update && sudo apt install -y docker.io docker-compose-plugin nginx certbot python3-certbot-nginx
git clone <your fork of this repo> remote-desktop && cd remote-desktop
```

Point your domain's DNS A record at the VPS first, then:

```bash
sudo nginx -t   # after copying the config below, before enabling TLS
```

Set up nginx (HTTP-only first, so certbot can issue a certificate):
```bash
sudo cp deploy/nginx/remote-desktop.conf /etc/nginx/sites-available/remote-desktop.conf
sudo sed -i 's/remote.your-domain.example.com/YOUR_REAL_DOMAIN/g' /etc/nginx/sites-available/remote-desktop.conf
sudo ln -s /etc/nginx/sites-available/remote-desktop.conf /etc/nginx/sites-enabled/
sudo certbot --nginx -d YOUR_REAL_DOMAIN   # issues the cert and finishes the TLS config for you
```

Configure and start the signaling server + coturn:
```bash
cd deploy
cp .env.example .env
cp coturn/turnserver.conf.example coturn/turnserver.conf
# Edit .env: set JWT_SECRET (see the comment in .env.example for how to
# generate one), ALLOWED_ORIGINS to https://YOUR_REAL_DOMAIN, and ICE_SERVERS
# with your TURN credentials.
# Edit coturn/turnserver.conf: set external-ip, realm, and a real password
# matching what you put in .env's ICE_SERVERS.
docker compose up -d --build
```

Build and publish the web controller:
```bash
cd ../web-controller
cp .env.production.example .env.production
sed -i 's/remote.your-domain.example.com/YOUR_REAL_DOMAIN/g' .env.production
npm ci && npm run build
sudo mkdir -p /var/www/remote-desktop
sudo cp -r dist /var/www/remote-desktop/web-controller-dist
sudo systemctl reload nginx
```

Open `https://YOUR_REAL_DOMAIN` from any browser - that's your controller URL.

### Firewall

```bash
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow 3478/tcp
sudo ufw allow 3478/udp
sudo ufw allow 49160:49200/udp
```

## 3. Set up a Windows host for real use

```powershell
winget install --id Microsoft.DotNet.SDK.8
git clone <your fork of this repo>
cd remote-desktop\windows-host
dotnet publish RemoteHost -c Release -r win-x64 --self-contained false -o .\publish
```
Run `publish\RemoteHost.exe` on the computer you want to control. On first
run, give it `https://YOUR_REAL_DOMAIN` as the server URL and sign in/
register. It saves its device ID + a private host token under
`%APPDATA%\RemoteDesktopHost\device.json` (delete that file to re-run setup).

To start it automatically at login, put a shortcut to `RemoteHost.exe` in
`shell:startup`.

## Using it

1. Host app shows a **device ID** and an 8-character **pairing code** that
   expires after 3 minutes.
2. On the controller (Android Chrome or any desktop browser), sign in, enter
   the device ID + code, and request access.
3. The host app shows the request with **Accept**/**Reject** - nothing is
   controllable until you accept.
4. Once accepted: full-screen view, click/drag/scroll/right-click (long-press
   on touch) work immediately; tap **Keyboard** to type.
5. **End session** on either side closes the connection immediately. Only one
   controller is allowed per device at a time; the host can revoke access at
   any moment via **End session now** in its window, and a red banner is
   shown across the top of the host's screen for the entire duration of any
   active session.

## Troubleshooting

- **"That device isn't online right now"**: the host app isn't running or
  hasn't connected to the signaling server - check its window/log panel.
- **Stuck on "Connecting..." after approval**: almost always a TURN problem.
  Confirm coturn is running (`docker compose ps`), its `external-ip` matches
  the VPS's real public IP, and UDP 49160-49200 + 3478 are open in the
  firewall/cloud provider's security group.
- **Pairing code rejected as "expired" or "already used"**: codes are
  single-use and expire in 3 minutes - generate a new one from the host app.
- **Video is present but laggy**: this build targets ~15 fps VP8 for
  simplicity/CPU headroom; see Known limitations below.
- **`dotnet` not found after installing the SDK**: open a new terminal (PATH
  is only refreshed for new shells).

## Known limitations (v1)

- **Video is capped at ~15 fps** and CPU-encoded VP8 - fine for
  admin/remote-work use, not for video/games. Raising the cap is a one-line
  change (`HostService`'s frame-rate gate) if your CPU has headroom.
- **Keyboard shortcuts and typed text only work while the controller's
  on-screen keyboard is focused** (the "⌨ Keyboard" button) - a physical
  keyboard typed directly at the video element is not forwarded, by design,
  to avoid hijacking normal browser shortcuts on the controller side.
- **No clipboard sync, file transfer, remote shell, or session recording** -
  intentionally out of scope for this version (see Security review).
- **One controller at a time**, attended access only - no unattended/silent
  mode, by design.
- **Monitor picker enumerates DXGI outputs at session start**; a monitor
  unplugged mid-session isn't hot-swapped (the capture thread will retry and
  recover once a monitor exists at that adapter/output index again).

## Security review

- **Transport**: WebRTC media/data channels are DTLS-SRTP encrypted
  peer-to-peer (or via TURN, which only relays already-encrypted packets).
  The signaling/REST layer must be served over TLS (nginx + certbot above) -
  never deploy `ws://`/`http://` to a real domain.
- **Passwords**: hashed with bcrypt (cost 12), never logged. JWTs are
  short-lived (12h) session tokens, not the password itself.
- **Host identity**: each device gets a long-lived, high-entropy bearer token
  (`crypto.randomBytes(32)`), stored server-side only as a bcrypt hash - a
  server compromise doesn't hand over usable host tokens. The token lives
  locally at `%APPDATA%\RemoteDesktopHost\device.json` on the host machine.
- **Pairing codes**: 8 characters from a 32-symbol alphabet (~38 bits),
  single-use, expire in 3 minutes, and are rate-limited per device (5 attempts
  / 5 minutes) independent of whether the code was right - this makes online
  brute-forcing impractical without also compromising the host's network
  presence.
- **Authorization boundary**: the signaling server's WebSocket hub only
  relays a `signal`/`session:end` message between the exact host and
  controller sockets tied to that specific approved session ID - every other
  combination is silently dropped. A session only exists after the host
  explicitly clicked Accept.
- **No unattended access**: every session requires an explicit Accept from
  someone physically at the host machine. There's no hidden background
  listener, no silent auto-approve path, and no bypass of Windows UAC, the
  lock screen, or any secure-desktop boundary - `SendInput` simply cannot
  deliver input to those surfaces, which is standard Windows behavior, not
  something this app works around.
- **Least data**: the server never sees screen content, keystrokes, or mouse
  movements - only signaling metadata (who's pairing with whom, SDP/ICE
  blobs). No clipboard, file transfer, or session recording exist in this
  version to keep the exposure surface minimal.
- **Rate limiting**: login/register are rate-limited per IP (8/min) against
  credential stuffing; pairing attempts are rate-limited per device as above.
- **Residual risks / what you're trusting**: (1) the VPS itself - anyone with
  root there could observe pairing metadata and modify server code, so use a
  VPS you trust and keep it patched; (2) `JWT_SECRET` must be kept secret and
  rotated if ever leaked (this invalidates all sessions); (3) this review
  covers the code as written, not a substitute for a professional pentest if
  you plan to expose this beyond personal use.

## Verification performed

- Signaling server: automated end-to-end test (`signaling-server/src/test/e2e.ts`)
  covering register → pair → wrong-code rejection → single-use enforcement →
  host approval → signal relay → session end, run against the real server.
- Web controller: driven through an actual browser against a scripted fake
  host - register/login, pairing (including rejection paths), and a real
  `RTCPeerConnection` offer/ICE exchange were all confirmed.
- Windows host: built and run for real on a Windows 11 machine. Confirmed
  real DXGI screen capture, real VP8 encoding via SIPSorcery, a real
  browser-to-host WebRTC negotiation reaching `connected`, **live video of
  the real desktop rendering in the browser**, and a real `SendInput` mouse
  click landing at the correct screen location (verified by reading the
  actual OS cursor position before/after). Keyboard injection uses the
  identical `SendInput` mechanism but could not be independently verified on
  a single shared test machine (typing into the controller UI necessarily
  moves real OS focus away from any target app first); this is a limitation
  of same-machine testing, not something specific to keyboard input, and it
  resolves itself in real use in this app since host and controller are on
  separate machines.
- Not yet verified by me: the full cross-network acceptance test (two actual
  separate devices, on two different networks, through a deployed VPS +
  TURN). That requires hardware/network topology I don't have access to in
  this environment - please run through the "Using it" steps above with your
  real Android phone / second computer once deployed, and treat that as the
  final acceptance check.
