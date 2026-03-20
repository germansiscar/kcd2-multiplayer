# KCD2 Multiplayer Campaign Co-op — Design Spec

## Vision

Two players play through the Kingdom Come: Deliverance 2 campaign together in a shared world. One player hosts, the other joins. Both exist in the same world, fight together, experience quests together. The host's game is the source of truth for world state.

This is a fork of [marczukmichal/kcd2-multiplayer](https://github.com/marczukmichal/kcd2-multiplayer), which implements sandbox position sync. We extend it toward full campaign co-op.

## Architecture

### Fundamental Constraint

KCD2's Lua environment has no socket library. The only bridge between the game and outside world is the **debug REST API** on `localhost:1403`, exposed when running through KCD2 Modding Tools. This constrains all communication to:

```
Game 1 <--HTTP--> Client Agent 1 <--TCP--> Relay Server <--TCP--> Client Agent 2 <--HTTP--> Game 2
  (Lua mod)         (.NET exe)               (.NET exe)             (.NET exe)          (Lua mod)
```

The client agent is the critical bridge — it translates between the game's HTTP/Lua interface and the network protocol.

### Three Sync Layers

| Layer | Purpose | Data | Tick Rate | Status |
|-------|---------|------|-----------|--------|
| **Position Sync** | Ghost NPC movement | XYZ, rotation, riding flag | ~10ms | VERIFIED, working |
| **State Sync** | Player appearance & status | Equipment, health, combat stance | ~200ms | Design phase |
| **Event Sync** | World/quest events | Quest triggers, dialogue, NPC kills | On-demand | Design phase |

Position sync stays fast and dumb (current approach works). State and event sync use a new extensible packet type carrying typed payloads.

### Host-Authoritative Model

- **Host player's game** = source of truth for world state
- **Joining player's game** = mirrors host world state as closely as the API allows
- Server tracks session state (quest progress, player states, world events)
- Conflict resolution: host wins

### Player Proximity Tether

Players must stay within a configurable range of each other (tunable, roughly 500m-1km). This is a technical necessity — independent roaming causes unsolvable world state divergence between the two game instances (NPC schedules, quest triggers, entity streaming all break down when players are in different areas).

**Behavior:**
- Within range: free movement, no restrictions
- Approaching limit: HUD warning ("You're getting far from your partner")
- At limit: progressive movement slowdown when moving away from partner

**Design goal:** Replicate the game's existing world-border mechanic where Henry slows down and comments that he should turn back. If we can hook into or replicate that system, the tether feels native rather than like a multiplayer constraint. This requires Phase 2 probing (see PLAUSIBLE APIs).

**No teleport** for now — just the natural slowdown. Teleport can be added later if testing reveals it's needed.

### Friendly Fire

Players can injure each other's ghost NPCs. This is a deliberate design choice — it prevents wild weapon swinging in combat and matches how KCD2's existing combat works (enemies avoid hitting each other, try to flank). It creates tactical depth in co-op encounters.

**Technical dependencies (all UNKNOWN, Phase 2 probes needed):**
- Can we detect damage on a spawned ghost NPC?
- Can we read NPC health values?
- Can we apply damage to the player entity via Lua?
- Does a spawned NPC even have a functional combat/health system?

### Wire Protocol Extension

All packets share the same 3-byte header: `[type:1][payloadLen:2 LE]` followed by `[payload:N]`. The existing Handshake (0x00) payload is just the UTF-8 name bytes (the 2-byte header length serves as nameLen). New packet types follow the same framing:

```
C->S  0x07  StateUpdate:  payload = [stateType:1][json...]
S->C  0x08  StateSync:    payload = [sourceId:1][stateType:1][json...]
C->S  0x09  Event:        payload = [eventType:2 LE][json...]
S->C  0x0A  EventRelay:   payload = [sourceId:1][eventType:2 LE][json...]
C->S  0x0B  Auth:         payload = [password:UTF-8]
S->C  0x0C  AuthResult:   payload = [ok:1][message:UTF-8...]
```

State types and event types are enumerated and documented as they're implemented.

## Project Rules

### No Assumptions About Game APIs

Every feature dependency is categorized:

- **VERIFIED** — used in the existing mod or confirmed with in-game testing
- **PLAUSIBLE** — referenced in game scripts (Scripts.pak) but not tested by us
- **UNKNOWN** — speculation with zero evidence

**Hard rule:** Before writing any code that depends on a PLAUSIBLE or UNKNOWN API, we write a probe script first. The probe is a small Lua snippet run in-game that tests whether the API exists and what it returns. Only after the probe confirms functionality do we build on it.

### API Verification Matrix

#### VERIFIED (working in existing mod or API doc)

| API | Evidence |
|-----|----------|
| `player:GetWorldPos()` / `entity:SetWorldPos()` | Used every tick in mod |
| `player:GetWorldAngles()` / `entity:SetWorldAngles()` | Used every tick |
| `System.SpawnEntity({class="NPC",...})` | Ghost spawning works |
| `System.RemoveEntity(entityId)` | Ghost removal works |
| `entity:StartAnimation(slot, name)` | Animation system works |
| `entity.actor:EquipClothingPreset(guid)` | Visual armor works |
| `entity.actor:EquipWeaponPreset(guid)` | Weapon preset works |
| `player.human:IsInDialog()` | Listed as verified in API doc |
| `player.soul:IsInCombatDanger()` | Listed as verified in API doc |
| `player.soul:GetSkillLevel(name)` | Listed as verified in API doc |
| `entity.human:ForceMount(horseId)` / `ForceDismount()` | Horse riding works |
| `Physics.RayWorldIntersection()` | Floor detection works |
| `Script.SetTimer(ms, callback)` | Tick loops work |
| `System.SetCVar()` / REST `GetCvarValue` | CVar eval trick works |
| `System.DrawLabel()` / `System.DrawText()` | HUD rendering works |
| `AI.ChangeParameter()` / `AI.Signal()` | AI control works |
| `ItemManager.CreateItem(guid, qty, condition)` | Item creation works |
| `entity.inventory:AddItem(handle)` | Inventory works |
| Debug REST API (ExecuteString, GetCvarValue, PlayerSoul, Calendar) | All actively used |

#### PLAUSIBLE (needs probing)

| API | Why we think it exists | Probe plan |
|-----|----------------------|------------|
| Quest state Lua functions | Scripts.pak likely contains quest management scripts | Extract and search Scripts.pak for quest-related Lua |
| `os.execute()` / `io.popen()` | Standard Lua 5.1 library — may be sandboxed by CryEngine | Run `os.execute("echo test > C:\\test.txt")` and check |
| `UIAction.RegisterElementListener()` | In API doc, could detect dialogue/menu state changes | Test with known UI elements from API doc |
| Inventory snapshot (read equipped items) | `entity.inventory:FindItem()` exists, full enumeration unknown | Probe iteration methods on inventory |
| `entity.soul.name` write | Used in mod, results inconsistent per logs | Needs systematic testing with different entity types |
| Equipment GUID reading from player | Needed to sync appearance — extraction method unknown | Probe `player.inventory` and `player.actor` methods |
| World border slowdown mechanic | Reuse for player proximity tether — would feel native | Search Scripts.pak for boundary/border/slowdown handling |
| Player movement speed modifier | Needed for tether slowdown | Probe `player.actor` for speed-related methods |
| Henry voice line triggering | Tether feedback ("I should stay with my companion") | Search Scripts.pak for voice/dialogue trigger methods |
| NPC damage detection / health read | Friendly fire — detect ghost NPC taking damage | Probe spawned NPC for health, damage callback, OnHit |
| Player damage application via Lua | Relay friendly fire damage to other player | Probe `player.actor:Damage()` or similar methods |

#### UNKNOWN (no evidence, needs research)

| Capability | Why we want it | Research approach |
|------------|---------------|-------------------|
| Quest progress read/write | Campaign sync | Extract Scripts.pak, search for quest APIs |
| Combat hit callbacks | Damage sync | Search Scripts.pak for damage/hit handlers |
| NPC death events | World state sync | Search for OnDeath, OnKill patterns |
| Dialogue choice interception | Shared conversations | Search for dialogue/conversation APIs |
| World time set | Time sync between players | Probe Calendar API for write methods |
| Save game read/manipulation | Shared save state | Probably not possible via Lua, research needed |

## Repository Consolidation

### Fork Strategy

- Keep `origin` pointing to upstream for reference
- All work on local branches
- `main` becomes our baseline

### Branch Disposition

| Branch | Action | Rationale |
|--------|--------|-----------|
| `refactor/server-framework` | **Cherry-pick concepts** | ASP.NET hosted service + DI + Serilog is the right direction, but we reshape for our stateful server design |
| `feat/voice-chat` | **Cherry-pick in Phase 4** | Proximity voice chat is valuable but not foundational; integrate after core sync is solid |
| `master-server` | **Skip** | Server discovery for public matchmaking is not needed for 2-player private sessions |
| `feat/kcdmp-launcher` | **Skip** | Blazor server browser is overkill; has committed .vs artifacts; single exe approach replaces this |

## Frictionless Setup

### Player Experience

**Host (one-time setup):**
1. Port-forward one TCP port on router
2. Run `setup.ps1` — auto-detects game paths, installs mod, configures firewall
3. Choose a session password

**Host (every session):**
1. Run `kcdmp.exe` — starts relay server + client agent in one process
2. Launch game, load save
3. Playing

**Joining player (one-time setup):**
1. Run `setup.ps1` — installs mod
2. Enter host's IP/hostname + password (saved to config)

**Joining player (every session):**
1. Run `kcdmp.exe` — connects to host automatically
2. Launch game, load save
3. Playing

### Config File (`kcdmp.json`)

```json
{
  "mode": "host",
  "port": 7778,
  "password": "henrysrevenge",
  "friendIp": "",
  "gameApiPort": 1404,
  "steamName": "auto",
  "lastSession": "2026-03-20T10:00:00Z"
}
```

### Single Executable

`kcdmp.exe` consolidates relay server + client agent into one process:
- `mode: "host"` — starts embedded relay server + client agent
- `mode: "join"` — starts client agent only, connects to configured host
- System tray icon with status (connected/waiting/error)
- Auto-detects game readiness (existing logic)
- Auto-reads Steam name (existing logic)
- Reconnects on disconnection (existing logic)

## Development Workflow

### Dev Environment (Mac to Windows Bridge)

1. **Tailscale** connects Mac (dev) and Windows (game) PCs — dev use only
2. **SSH** (OpenSSH server on Windows) for remote access
3. **Port forwarding** via SSH for game API access from Mac: `ssh -L 1404:localhost:1404 windowspc`

### Scripts

| Script | Purpose |
|--------|---------|
| `scripts/deploy.sh` | Cross-compile for win-x64 on Mac, SCP to Windows, optionally restart |
| `scripts/probe.sh <lua>` | Send Lua snippet to game API via SSH tunnel, print result |
| `scripts/build.sh` | Local build + test |
| `setup.ps1` | Windows player setup (mod install, firewall, port proxy) |

### Probe Script Workflow

```bash
# From Mac terminal — test if os.execute exists
./scripts/probe.sh 'System.SetCVar("sv_servername", tostring(os.execute ~= nil))'

# Read the result
curl http://localhost:1404/api/System/Console/GetCvarValue?name=sv_servername
```

Probes are saved in `probes/` directory with results documented for future reference.

## Implementation Phases

### Phase 0: Foundation
- Fork cleanup: consolidate .gitignore, remove dead code
- Cherry-pick server framework refactor concepts (DI, logging, config)
- Merge into clean architecture: single solution with Server, Client, Shared projects
  - Shared project contains: packet type enum, binary helpers (ReadFloat/WriteFloat/BuildPacket/ReadExactAsync — currently duplicated in both projects), protocol constants, config model, shared DTOs
- Set up dev environment (Tailscale, SSH, deploy script, probe script)
- Write CLAUDE.md with project conventions
- Write comprehensive project documentation

### Phase 1: Polish Sandbox
- Single exe (host + join modes)
- Password authentication on relay server
- Config file (kcdmp.json) with saved settings
- Windows setup script (setup.ps1)
- Auto-detect Modding Tools path from Steam registry
- Frictionless first-run experience

### Phase 2: Discovery (CRITICAL GATE)
- Extract and catalog KCD2's Scripts.pak Lua files for modding API surface
- Run probe scripts for every PLAUSIBLE API in the verification matrix
- Research UNKNOWN capabilities systematically
- Document findings — update verification matrix
- Determine scope ceiling for campaign features based on discoveries
- This phase directly shapes Phases 5 and 6

### Phase 3: State Sync
- Equipment appearance sync (ghost wears what the player wears) — VERIFIED APIs only
- Health/stamina indicators
- Combat stance sync (weapon drawn, blocking, attacking)
- New StateUpdate/StateSync packet types

### Phase 4: Voice Chat
- Cherry-pick proximity voice chat from `feat/voice-chat` branch
- Integrate NAudio-based VoiceChat.cs into single exe
- Add voice packets to protocol
- Distance-based volume (already implemented in branch)

### Phase 5: Event System
- Event bus protocol (Event/EventRelay packet types)
- Lua event detection hooks (dialogue, combat, quest — based on Phase 2 findings)
- Server-side event processing and state tracking
- Client-side event application (mirror events in joining player's game)

### Phase 6: Campaign Foundations
- Quest state mirroring (scope determined by Phase 2)
- Dialogue spectating or shared dialogue
- World state sync (NPC deaths, time of day — scope determined by Phase 2)
- Shared combat encounters
- This phase's scope is entirely dependent on Phase 2 discoveries

## Technical Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language (server/client) | C# / .NET 8 | Existing codebase, cross-compile to Windows from Mac |
| Language (game mod) | Lua 5.1 | Engine requirement, no choice |
| Protocol | TCP with custom binary framing | Existing, works, reliable for quest/state events |
| Hosting model | Player-hosted relay | No infrastructure costs, simple, private sessions |
| Build target | `win-x64` self-contained single file | No .NET runtime dependency for players |
| Config format | JSON | Simple, human-readable, .NET has built-in support |
| Logging | Serilog | Already in server-framework branch, structured logging |

## Testing Strategy

- **C# unit tests:** xUnit project (`KcdMp.Tests`) for the Shared library — packet serialization, protocol parsing, config handling. Run on Mac during development.
- **C# integration tests:** Connect two mock clients to the relay server, verify packet relay, auth, disconnect handling. Run on Mac.
- **Lua testing:** Inherently manual. Probe scripts serve as the test suite — each probe is saved in `probes/` with expected vs actual results. Run on Windows gaming PC via deploy + probe workflow.
- **End-to-end:** Manual testing with two game instances. Checklist-driven (documented per phase).
- **Regression:** Before each phase milestone, re-run the full probe suite + C# test suite to catch regressions.

## Existing Code Worth Noting

The Lua mod contains `KCD2MP_Exchange()` (kdcmp.lua) which combines reading local player state and applying ghost state in a single call, returning position + rotation + stance as CSV. This is more efficient than separate read/write calls. Phase 3 state sync should evaluate building on this pattern for batched state exchange.

## Port 1403 vs 1404

The game's debug API binds to `localhost:1403` (loopback only). The `setup.ps1` script creates a Windows port proxy (`netsh portproxy`) from `0.0.0.0:1404` to `127.0.0.1:1403`, making the API accessible to the client agent. The config default of `gameApiPort: 1404` reflects this proxy port.

## Open Questions

1. Can we read quest state from Lua? (Phase 2)
2. Can we trigger quest state changes from Lua? (Phase 2)
3. Is `os.execute()` available for auto-launching the client agent? (Phase 2)
4. What combat events can we detect? (Phase 2)
5. Can we sync time of day between games? (Phase 2)
6. How do we handle save games — separate saves with synced state, or shared save file? (Phase 2 + design)
7. What happens when one player is far from the other — does CryEngine unload the ghost entity? (Phase 2 — testable with a probe: spawn entity, walk far away, check if it persists)
8. How to handle cutscenes — does the joining player see them? (Phase 2)
