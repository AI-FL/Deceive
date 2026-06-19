# Deceive

Local XMPP presence proxy for Riot Games clients. Makes you appear offline (or mobile) to friends while retaining full chat, lobby, invite, and champion-select functionality.

Supports: League of Legends, VALORANT, Legends of Runeterra, 2XKO, Riot Client standalone.

**Ban status:** Riot has publicly confirmed no bans for using this tool. It operates on network traffic only — outside Vanguard's scope entirely.

---

## How It Works

Deceive intercepts the Riot Client's chat connection by:

1. Launching RC with `--client-config-url` pointing to a local HTTP proxy
2. The proxy rewrites Riot's clientconfig JSON to redirect chat traffic to localhost
3. Deceive proxies the XMPP-over-TLS connection and strips/rewrites presence stanzas before they reach Riot's servers
4. Friends see you as offline/mobile; you see everything normally

No process injection. No memory reads. No kernel hooks.

---

## Requirements

- Windows 10/11
- .NET Framework 4.7.2 (pre-installed on Win10+)
- Riot Client must be installed (at least one Riot game launched before)
- `deceive-localhost.molenzwiebel.xyz` must resolve to `127.0.0.1` — if it doesn't, change DNS to `1.1.1.1` or `8.8.8.8`, or add `127.0.0.1 deceive-localhost.molenzwiebel.xyz` to `C:\Windows\System32\drivers\etc\hosts`

---

## Usage

**First run:** Close any running Riot Client or game. Launch `Deceive.exe`. A game selection dialog appears — pick the game to launch. Check "remember" to skip the dialog on future runs.

**Tray icon (right-click):**
- `Enabled` — toggle presence masking on/off
- `Status Type` — Online / Offline / Mobile
- `Default Status on Startup` — what status to apply on next launch
- `Enable lobby chat` — whether MUC (lobby/champion select) stanzas are forwarded
- `Restart and launch a different game` — kills current session, prompts game selection
- `Quit` — kills RC and game, exits Deceive

**Chat commands** (message the "Deceive Active!" contact in your friends list):
- `offline` / `mobile` / `online` — switch status
- `enable` / `disable` — toggle masking
- `status` — report current status
- `help` — list commands

**CLI launch options:**
```
Deceive.exe [lol|lor|valorant|lion|riotclient] [--gamePatchline live] [--riotClientParams "..."] [--gameParams "..."]
```

Example — always launch LoL on live:
```
Deceive.exe lol
```

---

## Invites

- You can invite anyone normally
- Friends cannot invite you while you appear offline (entering your name manually also fails)
- Workaround: disable Deceive temporarily, accept invite, re-enable

---

## Data and Logs

All persistent data lives in `%AppData%\Deceive\`:

| File | Purpose |
|------|---------|
| `debug.log` | Full trace log from last session |
| `localhostCert.pfx` | Cached TLS cert (auto-renewed when expiring) |
| `launchGame` | Remembered game choice |
| `startupStatus` | Default status on launch |
| `status` | Last session status (for "remember last" mode) |

---

## Security Notes

- The TLS certificate is generated locally on first run using `System.Security.Cryptography.CertificateRequest` (RSA-2048, SHA-256, 2-year validity). No external network call is made to obtain it. It is cached at `%AppData%\Deceive\localhostCert.pfx` and regenerated automatically when within 30 days of expiry.
- On first run (or after cert expiry), Windows shows a one-time security dialog asking to trust the generated certificate in `CurrentUser\Root`. Click **Yes** — this is required for the Riot Client to accept the local TLS proxy. Subsequent runs skip this step.
- Auth tokens (JWT/entitlements) are forwarded as-is to Riot's real servers. Deceive does not capture or store them. Lines containing `token>` are suppressed from `debug.log`.
- The DNS name `deceive-localhost.molenzwiebel.xyz` resolves to `127.0.0.1` via public DNS. If that DNS record ever disappears, the hosts file workaround below applies.

---

## Building from Source

```
nuget restore Deceive.sln
msbuild Deceive.sln /p:Configuration=Release
```

Output: `Deceive\bin\Release\Deceive.exe` — single exe, all DLLs embedded via Costura.Fody.

---

## Troubleshooting

**"Riot Client is currently running"** — click Yes to let Deceive kill and restart it, or close RC manually first.

**"Failing to resolve some required domains"** — add `127.0.0.1 deceive-localhost.molenzwiebel.xyz` to `C:\Windows\System32\drivers\etc\hosts`, or switch DNS to `1.1.1.1` or `8.8.8.8`. Requires Administrator.

**"Failed to generate or load the local TLS certificate"** — delete `%AppData%\Deceive\localhostCert.pfx` and restart to force regeneration. Check `%AppData%\Deceive\debug.log` for the specific exception.

**"Version Mismatch" warning on VALORANT fake friend** — cosmetic artifact from the sentinel roster entry. Does not affect functionality.

**Friends can still see me even though Deceive says enabled** — RC was already running before Deceive launched. The chat connection bypassed the proxy. Use the tray "Restart and launch a different game" option, or manually close RC before starting Deceive.
