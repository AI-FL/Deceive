# CLAUDE.md — Architecture Reference

## Project Identity

- **Name:** Deceive
- **Type:** Windows WinForms application (.NET Framework 4.7.2)
- **Build output:** Single self-contained `.exe` (Costura.Fody merges all DLLs at build time)
- **Fork source:** `molenzwiebel/Deceive` — the AI-FL fork is a verbatim copy, no behavioral changes
- **Version in csproj:** `1.17.2.0`

---

## What It Does

Deceive is a local XMPP presence proxy. It sits between the Riot Client (RC) and Riot's XMPP chat servers. It rewrites outbound presence stanzas so the user appears offline (or mobile) to friends, while all other chat functionality remains intact — lobby chat, invites sent by the user, champion select chat.

It does **not**:
- Inject into any game or RC process memory
- Hook system calls or kernel APIs
- Touch Vanguard's scope (Vanguard monitors game processes, not network traffic)
- Exfiltrate credentials (auth tokens forwarded as-is to real Riot servers)

---

## Data Flow

```
Riot Client (RC)
    │
    │  TLS (XMPP over SSL, port decided by ConfigProxy)
    ▼
[ProxiedConnection.Incoming SslStream]   ← local loopback
    │
    │  Presence stanzas rewritten here before forwarding
    ▼
[ProxiedConnection.Outgoing SslStream]
    │
    │  TLS to real Riot chat server (e.g. *.chat.si.riotgames.com:5223)
    ▼
Riot Chat Servers
```

The RC's `--client-config-url` is overridden at launch to point to `ConfigProxy` (local HTTP server). `ConfigProxy` fetches the real clientconfig JSON from `https://clientconfig.rpg.riotgames.com`, rewrites `chat.host → deceive-localhost.molenzwiebel.xyz` and `chat.port → local_port`, then emits the modified JSON. The RC connects to the local port, which `MainController` is listening on.

---

## File Map

| File | Role |
|------|------|
| `StartupHandler.cs` | Entry point. Orchestrates startup sequence. Kills existing RC/game processes if needed. |
| `ConfigProxy.cs` | Embedded HTTP server (EmbedIO). Proxies clientconfig, rewrites chat host/port, fires `PatchedChatServer` event with real chat host. Also fetches geo-affinity via PAS JWT to find the player's correct regional chat server. |
| `MainController.cs` | `ApplicationContext` subclass. Manages system tray icon, status state machine (chat/offline/mobile), dispatches status updates to all active `ProxiedConnection` instances. |
| `ProxiedConnection.cs` | The core MITM. Two async loops: `IncomingLoopAsync` (RC→Server) and `OutgoingLoopAsync` (Server→RC). Presence rewriting in `PossiblyRewriteAndResendPresenceAsync`. Injects fake roster entry and fake presence for the sentinel player. |
| `Persistence.cs` | `%AppData%\Deceive\` file-based config. Stores: cached TLS cert, last prompted update version, default launch game, startup status preference. |
| `Utils.cs` | Process management (find/kill RC, LoL, VALORANT, LoR, 2XKO). Riot Client path from `%ProgramData%\Riot Games\RiotClientInstalls.json`. Self-signed cert generation via `CertificateRequest` (RSA-2048, SHA-256). No external network calls. |
| `LaunchGame.cs` | Enum: `LoL`, `LoR`, `VALORANT`, `Lion`, `RiotClient`, `Prompt`, `Auto`. |
| `GamePromptForm.cs` | WinForms dialog for initial game selection. |

---

## Startup Sequence (step-by-step)

1. Check if RC/game already running → prompt kill
2. Write empty `debug.log` to `%AppData%\Deceive\`
3. Verify `deceive-localhost.molenzwiebel.xyz` resolves to `127.0.0.1` (DNS dependency)
4. Open random loopback `TcpListener` port (chat proxy port)
5. Locate RC via `RiotClientInstalls.json`
6. Resolve launch game (Auto → persisted → Prompt dialog)
7. Start `ConfigProxy` on another random loopback port
8. Generate/load self-signed TLS cert (`GetOrCreateProxyCertificate`); install to `CurrentUser\Root` if new (one-time Windows dialog)
9. Launch RC with `--client-config-url=http://127.0.0.1:{configProxyPort}`
11. Attach RC exit watcher (recursive, handles RC self-restarts)
12. Subscribe to `ConfigProxy.PatchedChatServer` event → call `MainController.StartServingClients`
13. Enter WinForms message loop (`Application.Run`)

---

## Presence Rewriting Logic (`ProxiedConnection.cs`)

Outbound presence XML from RC is intercepted. Rules:

- `status=offline`: removes `<status>`, `<games><league_of_legends>`, `<games><bacon>` (LoR), `<games><lion>` (2XKO), `<games><keystone>`, `<games><riot_client>`, `<games><valorant>`; sets `<show>offline</show>`
- `status=mobile`: removes game presence except LoL which is stripped of `<p>` and `<m>` nodes; sets `<show>mobile</show>`
- `status=chat`: passes through unmodified (except MUC filtering if `ConnectToMuc=false`)
- MUC stanzas (presence with `to=` attribute): forwarded only if `ConnectToMuc=true`

The last seen presence is saved as `LastPresence` and replayed when status changes via `UpdateStatusAsync`.

---

## Fake Roster Entry

On first outbound roster stanza (`<query xmlns='jabber:iq:riotgames:roster'>`), injects:

```xml
<item jid='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net'
      name='&#9;Deceive Active!' subscription='both'
      puuid='41c322a1-b328-495b-a004-5ccd3e45eae8'>
  <group priority='9999'>Deceive</group>
  <state>online</state>
  ...
</item>
```

This sentinel entry is always `eu1.pvp.net` regardless of region — intentional, it routes to no real player. Messages from this JID are intercepted (`IncomingLoopAsync`) and dispatched to `MainController.HandleChatMessage` instead of being sent to the server.

---

## External Dependencies (Security-Critical)

| Endpoint | Purpose | Risk |
|----------|---------|------|
| `deceive-localhost.molenzwiebel.xyz` | DNS A record must resolve to `127.0.0.1` | LOW — public DNS; hosts file override available |
| `https://clientconfig.rpg.riotgames.com` | Riot's own config endpoint | LOW — Riot-controlled |
| `https://riot-geo.pas.si.riotgames.com` | Player affinity (regional chat server) | LOW — Riot-controlled |

**The TLS cert is generated locally by `Utils.GetOrCreateProxyCertificate()` using `System.Security.Cryptography.CertificateRequest`. No external server is contacted for the certificate. It is cached at `%AppData%\Deceive\localhostCert.pfx` and renewed when within 30 days of expiry. On generation, the public cert is installed into `CurrentUser\Root` (Windows shows a one-time confirmation dialog).**

---

## TOS / Ban Risk Assessment

- Riot officially confirmed no bans for Deceive (see original README reference)
- Mechanism: network-layer presence manipulation, fully outside Vanguard's kernel-mode scope
- Does not read/write game memory, does not hook APIs, does not modify game files
- Risk scenarios: Riot revokes their statement, Riot moves to authenticated presence (would break the proxy), Riot detects local proxy via config URL pattern
- Current detection surface: the `--client-config-url` argument passed to RC is visible to RC itself; Riot could flag this in telemetry

---

## Build

- **Target:** `.NET Framework 4.7.2`, `WinExe`, `x86/AnyCPU`
- **Key packages:** `Costura.Fody` (DLL merging), `EmbedIO` (HTTP server), `System.CommandLine.DragonFruit` (CLI arg parsing), `System.Text.Json`
- **Build command:** `msbuild Deceive.sln /p:Configuration=Release`
- **Output:** `Deceive/bin/Release/Deceive.exe` — single standalone executable

---

## Data Storage (`%AppData%\Deceive\`)

| File | Content |
|------|---------|
| `debug.log` | Trace output from last run |
| `localhostCert.pfx` | Cached TLS certificate |
| `updateVersionPrompted` | Last update version shown to user |
| `launchGame` | Persisted default game (enum name string) |
| `startupStatus` | `chat` / `offline` / `mobile` / `last` |
| `status` | Last session status (used when `startupStatus=last`) |
