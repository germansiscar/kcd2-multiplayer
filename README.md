# KCD2 Multiplayer Campaign Co-op

Multiplayer campaign co-op mod for Kingdom Come: Deliverance 2. Each player runs their own game, connected via a relay server. The other player appears as a ghost NPC with real-time position, rotation, and combat state sync.

Fork of [marczukmichal/kcd2-multiplayer](https://github.com/marczukmichal/kcd2-multiplayer) by [MichaelBehr](https://github.com/MichaelBehr), extending sandbox position sync toward full campaign co-op.

## Architecture

```
Game (Lua mod) ←HTTP→ Client Agent ←TCP→ Relay Server ←TCP→ Client Agent ←HTTP→ Game (Lua mod)
   PC1                  kcdmp.exe              kcdmp.exe              kcdmp.exe        PC2
                        (host mode)                                  (join mode)
```

- **kcdmp.exe** — single binary, runs in `host` or `join` mode
- **Host mode** — runs the relay server + client agent on the same machine
- **Join mode** — connects to a host's relay server
- The game's Lua environment has no sockets — all communication goes through the debug REST API on localhost:1403 (proxied to 1404)

## What's Synced

- Player position and rotation (10ms tick)
- Riding state (horse detection)
- Combat state (weapon drawn/sheathed, combat mode)
- Animation state mirroring
- Death detection (mutual game-over)
- Player names (via Steam)

## Requirements

- Kingdom Come: Deliverance II
- KCD2 Modding Tools (free on Steam — separate library entry, enables the debug API)
- Windows (game is Windows-only)

## Quick Start

### 1. Download

Grab `kcdmp.exe` from [Releases](https://github.com/MichaelBehr/kcd2-multiplayer-campaign/releases), or [build from source](#building-from-source).

### 2. Run Setup (one-time, both PCs)

Place `kcdmp.exe`, the `kdcmp/` mod folder, and `setup.ps1` in a directory (e.g. `C:\Users\You\Documents\kcdmp\`).

Run PowerShell **as Administrator**:

```powershell
cd C:\Users\You\Documents\kcdmp
.\setup.ps1
```

This will:
- Install the `kdcmp` mod into your Modding Tools `Mods/` directory
- Set up port proxy (1403 → 1404) so the client agent can reach the game API
- Open firewall ports (1404 for game API, 7778 for relay server)

### 3. Start the Game

Launch KCD2 through **Modding Tools** (not the base game). Load a save.

### 4. Start kcdmp.exe

**Player 1 (host):**
```
kcdmp.exe host
```

**Player 2 (join):**
```
kcdmp.exe join 192.168.1.10
```
Replace `192.168.1.10` with the host's IP address.

That's it. Both players should see each other's ghosts.

## Configuration

On first run, `kcdmp.exe` creates a `kcdmp.json` config file:

```json
{
  "mode": "host",
  "port": 7778,
  "password": "",
  "friendIp": "",
  "gameApiPort": 1404,
  "steamName": "auto"
}
```

| Field | Description |
|-------|-------------|
| `mode` | `host` (run server + client) or `join` (client only) |
| `port` | Relay server port |
| `password` | Optional server password |
| `friendIp` | Host's IP (join mode) |
| `gameApiPort` | Game debug API port (default 1404) |
| `steamName` | Display name — `auto` reads from Steam registry |

CLI args override the config: `kcdmp.exe host` or `kcdmp.exe join 10.0.0.5`.

## Building from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cd dotnet

# Build
dotnet build

# Run tests
dotnet test

# Cross-compile for Windows (standalone .exe, no .NET required)
dotnet publish KcdMp.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish/app
```

Output: `dotnet/publish/app/kcdmp.exe`

## Project Structure

```
dotnet/
  KcdMp.App/          — unified entry point (host/join modes)
  KcdMp.Client/       — game bridge client agent
  KcdMp.Server/       — TCP relay server
  KcdMp.Shared/       — protocol types, config, packet builder/reader
  KcdMp.Tests/        — xUnit tests (30 tests)
kdcmp/                — Lua game mod
scripts/              — dev scripts (build, deploy, probe)
probes/               — Lua API probe scripts and results
docs/                 — design specs and implementation plans
```

## Wire Protocol

All packets: `[type:1][payloadLen:2 LE][payload:N]`

| Type | Direction | Purpose |
|------|-----------|---------|
| 0x00 Handshake | C→S | Player name |
| 0x01 Position | C→S | x, y, z, rotZ, flags |
| 0x02 Ghost | S→C | Ghost position update |
| 0x03 Name | S→C | Ghost display name |
| 0x04/0x05 Ping/Pong | Both | Latency measurement |
| 0x06 Disconnect | S→C | Ghost removed |
| 0x07 StateUpdate | C→S | Combat state + animation |
| 0x08 StateSync | S→C | Relayed combat state |
| 0x09 Event | C→S | Game events (damage, death) |
| 0x0A EventRelay | S→C | Relayed game events |
| 0x0B/0x0C Auth | Both | Password authentication |

## Troubleshooting

### Game API not responding (`localhost:1404`)
- Launch through **Modding Tools**, not the base game
- A save must be loaded (API returns nothing on main menu)
- Re-run `setup.ps1` if port proxy was lost (resets on Windows restart)

### Port 1403 blocked
```powershell
netsh http show urlacl | findstr 1403
# If found:
netsh http delete urlacl url=http://+:1403/
```
Then restart the game.

### Client can't connect to host
- Check host IP with `ipconfig` → IPv4 Address
- Ensure port 7778 firewall rule exists on host PC
- Both PCs must be on the same network (or use VPN like Tailscale)

### Mod not loading
Check `kcd.log` for `[KCD2-MP] === MOD INIT ===`. If missing, verify the `kdcmp` folder is in the Modding Tools `Mods/` directory and re-run `setup.ps1`.

## Removing Network Setup

```powershell
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=1404
netsh advfirewall firewall delete rule name="KCD2 API 1404"
netsh advfirewall firewall delete rule name="KCD2MP Relay 7778"
```

## Known Limitations

- No damage relay yet — combat state syncs but hit detection is not available in KCD2's Lua API
- Ghost NPC uses static appearance preset (equipment sync pending)
- Both players must have a save loaded
- Requires KCD2 Modding Tools (debug API not available in base game)

## License

See [LICENSE](LICENSE) for details.
