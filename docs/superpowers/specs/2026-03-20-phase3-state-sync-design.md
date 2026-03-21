# Phase 3: State Sync — Design Spec

## Overview

Phase 3 adds state synchronization on top of the existing position sync, making the ghost NPC feel like a real co-op partner rather than a sliding mannequin. This includes damage relay (both friendly fire and shared NPC combat), death/revive handling, combat animation sync with NPC aggro, and equipment appearance sync.

## Prerequisites

- Phase 0+1 complete (single exe, auth, config, protocol foundation)
- Phase 2 complete (API discovery — all findings documented in `probes/phase2/`)
- Reserved packet types 0x07-0x0A defined but unused in `PacketType.cs`

## Architecture

### Sync Layers

| Layer | Data | Rate | Transport |
|-------|------|------|-----------|
| Position (existing) | XYZ, rotation | ~10ms | Position/Ghost packets (0x01/0x02) |
| State (new) | Combat flags, animation, equipment | ~200ms, delta-only | StateUpdate/StateSync (0x07/0x08) |
| Events (new) | Damage, death, revive | On-demand | Event/EventRelay (0x09/0x0A) |

Position sync is unchanged. State and event sync layer on top without disrupting the tight position loop.

### Host-Authoritative Model

Unchanged from the original design spec. Host's game is source of truth. Server remains stateless — it relays packets without interpreting them.

## Sub-phases

### Phase 3.0: Probes

Targeted probes to resolve unknowns before writing code. Results gate the scope of subsequent sub-phases.

**Probe A: Knockout/Unconscious State** (status: UNKNOWN — no evidence stamina-only damage causes unconsciousness)
1. `player.soul:DealDamage(0, 99999, __null, true)` — stamina-only damage. Does it trigger unconsciousness? (speculative — probe 13 only tested health+stamina combined)
2. Search 113 enumerated `player.actor` methods for `SetUnconscious`, `KnockOut`, `Stun`, or similar
3. `game:GetHitTypeId("knockout")` — does this hit type exist?
4. Search `probes/phase2/scripts_extracted/` for unconscious/knockout handling patterns

**Probe B: Equipment Reading**
1. Iterate `player.inventory` methods for `GetEquipped`, `GetSlot`, `GetItem` variants
2. Test `player.actor:GetCurrentItem(slot)` if it exists
3. Check for `player.actor:GetClothingPreset()` / `GetWeaponPreset()` (inverse of Set methods)
4. Fallback: `player.inventory:FindItem()` with known equipment GUIDs from Tables.pak

**Probe C: Player Faction**
1. Read `player.soul` faction property or search soul methods for faction getter
2. Cross-reference with Tables.pak faction definitions
3. Determine the faction name that makes enemies (bandits, Cumans, etc.) hostile

**Probe D: Ghost Invulnerability**
1. Test `bInvulnerable = true` in ghost spawn Properties — does it block NPC damage?
2. If not, test filtering NPC-on-ghost damage in the OnHit hook (check `hit.shooterId` against known NPC entities vs player)

**Probe E: Animation Mapping**
1. Read `player.actor:GetCurrentAnimationState(0)` during combat — what animation names come back?
2. Apply those same names to ghost NPC via `ghost:StartAnimation(0, animName)` — do they work on NPCs?
3. If animation names differ between player and NPC models, identify the mapping needed

**Probe F: OnHit Hook Verification** (status: PLAUSIBLE — SinglePlayer.Client.OnHit exists in Scripts.pak but has not been hooked from mod code)
1. Hook `SinglePlayer.Client.OnHit` from mod startup script — verify the callback fires when player hits NPCs
2. Verify `hit.targetId`, `hit.shooterId`, `hit.damage` fields exist and are readable (documented in Scripts.pak probe 12 but not live-tested)
3. Verify `player.id` gives the player's entity ID for shooter filtering

**Probe G: Ghost Faction + Aggro Without AI**
1. Spawn ghost NPC with player faction + empty behavior tree near hostile NPCs — do hostiles attack it?
2. If not, test with a minimal behavior tree (e.g. `esModularBehaviorTree = "HumanCivilian"` or similar from Tables.pak)
3. Determine minimum AI config needed for enemies to target the ghost

### Phase 3.1: Damage Relay + Death Handling

#### Damage Flows

**Flow 1: Friendly Fire (player hits partner's ghost)**
```
Player B swings sword
  -> hits Player A's ghost NPC in Player B's game
  -> Lua OnHit hook detects hit on ghost entity (hit.targetId matches ghost)
  -> Queues { damage: hit.damage } in KCD2MP.pendingDamageEvents
  -> Client B polls damage events via CVar trick
  -> Client B sends Event(DamageDealt, { "amount": 50 })
  -> Server relays EventRelay to Client A
  -> Client A executes player.soul:DealDamage(50, 0, __null, true)
  -> Player A takes real damage in their game
```

**Flow 2: Shared NPC Combat (player attacks NPC, relayed to partner's game)**
```
Player A swings sword
  -> hits bandit "krab_man_4" in Player A's game
  -> Lua OnHit hook detects hit where shooterId == player (not ghost)
  -> Queues { entityName: "krab_man_4", damage: hit.damage }
  -> Client A sends Event(NpcDamage, { "entity": "krab_man_4", "amount": 50 })
  -> Server relays EventRelay to Client B
  -> Client B executes: find entity by name, apply DealDamage
  -> Bandit in Player B's game takes damage from Player A's attack
```

NPC entity name matching assumes both game instances assign the same names to world NPCs (e.g. "krab_man_4"). This is expected since both load the same world data, but the tether keeps players in the same area so the same NPCs are loaded. If `GetEntityByName` fails on the receiving side (entity not loaded or name mismatch), the damage event is silently dropped — no crash, just no sync for that hit.

**Flow 3: Death Status Sync**
```
Player A dies (any cause)
  -> Client A polls GetHealth() -> returns 0
  -> Client A sends Event(PlayerDied)
  -> Server relays to Client B
  -> Trigger revive flow or mutual game-over
```

#### NPC-on-Ghost Damage

Filtered out. The OnHit hook compares `hit.shooterId` against `player.id` (the local player's entity ID, verified via existing mod usage at kdcmp.lua). If the shooter is not the player, the event is ignored. The ghost is also set to invulnerable (Probe D) so NPC attacks don't deplete its health or kill it. Only player-to-ghost hits count as friendly fire.

#### Hit Detection (Lua)

**Depends on Probe F.** Hook `SinglePlayer.Client.OnHit` from the mod's startup script (NOT console — console hooks don't fire). The hook exists in Scripts.pak (probe 12) but has not been verified to work from mod code. If Probe F fails, damage relay is descoped to death sync only (GetHealth polling).

```lua
local origSPOnHit = SinglePlayer.Client.OnHit
SinglePlayer.Client.OnHit = function(self, hit)
    local isGhostTarget = KCD2MP.ghostEntityIds[hit.targetId]
    local isPlayerShooter = (hit.shooterId == player.id)

    if isGhostTarget and not isPlayerShooter then
        -- NPC hitting ghost -> ignore (ghost is invulnerable anyway)
    elseif isGhostTarget and isPlayerShooter then
        -- Player hitting partner's ghost -> friendly fire
        KCD2MP.pendingDamageEvents[#KCD2MP.pendingDamageEvents + 1] = {
            type = "friendly_fire",
            damage = hit.damage
        }
    elseif isPlayerShooter and not isGhostTarget then
        -- Player hitting an NPC -> shared combat
        local target = System.GetEntity(hit.targetId)
        if target and target:GetName() then
            KCD2MP.pendingDamageEvents[#KCD2MP.pendingDamageEvents + 1] = {
                type = "npc_damage",
                entityName = target:GetName(),
                damage = hit.damage
            }
        end
    end

    if origSPOnHit then origSPOnHit(self, hit) end
end
```

#### Death/Revive

**If knockout probes succeed (Probe A) — selected at build time, not runtime:**
1. Player A takes fatal damage -> instead of dying, enters unconscious/downed state
2. Client A sends `Event(PlayerDowned, { x, y, z })` with position
3. Client B receives -> `Game.SendInfoText("Your partner is down!")` + map indicator
4. Player B has 60 seconds to reach Player A's ghost and interact
5. On interaction: Client B sends `Event(Revive)`
6. Client A heals player / clears unconscious state
7. If timer expires or Player B also downed -> both game-over

Probe A results determine which death path is implemented. If Probe A finds an unconscious state but no revive method, fall back to mutual game-over (the simpler path).

**If knockout probes fail (fallback):**
1. Player A dies -> `GetHealth()` returns 0
2. Client A sends `Event(PlayerDied)`
3. Client B receives EventRelay -> immediately executes `player.soul:DealDamage(99999, 99999, __null, true)`
4. Both players see game-over screen independently
5. Both players manually reload their saves — save coordination is out of scope for Phase 3 (flagged as open question in main design spec)

#### Health Display

No health bar on the ghost. `GetHealth()` only returns 100 (alive) or 0 (dead) — a fake health bar based on damage tracking would drift due to unknown max HP and regen rates, actively misleading players.

Instead:
- Binary alive/dead status (reliable)
- `IsInCombatDanger()` flag (partner is fighting)
- "Took damage" flash notification via `Game.SendInfoText` when damage event is relayed
- Death notification when partner dies

### Phase 3.2: Combat Sync

#### Ghost Faction and Aggro

Ghost NPC spawns with the same faction as the player (determined by Probe C). **Depends on Probe G**: an empty behavior tree (`esModularBehaviorTree = ""`) may prevent enemies from targeting the ghost even with the correct faction. Probe G determines the minimum AI config needed for aggro.

**If Probe G succeeds (faction + minimal AI = aggro):** Ghost draws enemy attention, splits NPC targeting, creates tactical depth.

**If Probe G fails (no AI = no aggro):** Ghost is cosmetic in combat. Enemies only fight the local player. Still functional — damage relay and animation sync still work. Aggro splitting deferred until a behavior tree solution is found.

Ghost is set invulnerable to NPC damage (Probe D) so it doesn't die from NPC attacks. Only player-to-ghost friendly fire deals real (relayed) damage.

#### Combat State Flags

Packed into a single byte, sent at ~200ms via StateUpdate, delta-only:

```
Bit 0: weapon drawn (IsInCombatMode)
Bit 1: in combat danger (IsInCombatDanger)
Bit 2: sneaking (from OnAction hook — currently in Position flags, move here)
Bit 3: blocking (probe for detection method)
Bit 4: on horse (currently in Position flags, move here)
Bits 5-7: reserved
```

Read via CVar trick in one Lua call:
```lua
System.SetCVar("sv_servername", (function()
    local cm = player.soul:IsInCombatMode() and 1 or 0
    local cd = player.soul:IsInCombatDanger() and 1 or 0
    local sn = KCD2MP.playerSneaking and 1 or 0
    local ride = KCD2MP.isRiding and 1 or 0
    return string.format("%d,%d,%d,%d", cm, cd, sn, ride)
end)())
```

Applied to ghost:
- Weapon drawn -> `ghost.actor:HolsterItem(false)` or combat idle animation
- Sneaking -> crouch animation (already implemented)
- On horse -> horse ghost spawn (already implemented)

#### Animation Sync

Poll `player.actor:GetCurrentAnimationState(0)` at ~200ms. Relay animation name as part of state sync. Apply to ghost via `ghost:StartAnimation(0, animName)`.

Animation names may differ between player and NPC models (Probe E). If so, maintain a mapping table. Even imperfect/delayed animation mirroring is acceptable — a ghost that swings during combat is far better than one standing still.

**If Probe E fails (combat animations don't work on NPCs):** Fall back to combat state flags only — ghost shows weapon drawn/holstered and combat idle stance but doesn't mirror individual swings. Movement animations (walk, run, crouch) already work.

Sent as part of the StateUpdate alongside combat flags:
```
StateType 1 (CombatState): [flags:1][animNameLen:1][animName:N]
```

### Phase 3.3: Equipment Appearance

Scope depends on Probe B results.

#### If Equipment Reading Works

Read equipped item GUIDs from player. Send as StateUpdate on change only (equipment changes rarely):

```
StateType 2 (Equipment): [json]
{ "clothing": "guid", "weapon": "guid" }
```

Apply to ghost:
```lua
ghost.actor:EquipClothingPreset(clothingGuid)
ghost.actor:EquipWeaponPreset(weaponGuid)
```

Only send when GUIDs change from last sync. Very low bandwidth.

#### If Equipment Reading Fails

**Fallback 1: OnAction hook detection.** If equip/unequip actions fire through the OnAction hook, track equipment state locally without inventory read APIs.

**Fallback 2: Static preset.** Ghost wears default armor preset (already implemented). Functional but immersion-breaking. Acceptable as baseline with improvement deferred.

## Protocol Extension

All packets use the existing 3-byte header: `[type:1][payloadLen:2 LE][payload:N]`

### State Packets (periodic, ~200ms, delta-only)

```
Client -> Server:
  0x07 StateUpdate: [stateType:1][payload...]

Server -> Client:
  0x08 StateSync: [sourceId:1][stateType:1][payload...]
```

State types:
| StateType | Value | Payload |
|-----------|-------|---------|
| CombatState | 1 | `[flags:1][animNameLen:1][animName:N]` |
| Equipment | 2 | `[json...]` |

### Event Packets (on-demand)

```
Client -> Server:
  0x09 Event: [eventType:2 LE][json...]

Server -> Client:
  0x0A EventRelay: [sourceId:1][eventType:2 LE][json...]
```

Event types:
| EventType | Value | Payload |
|-----------|-------|---------|
| DamageDealt | 1 | `{ "amount": 50 }` |
| PlayerDied | 2 | `{}` |
| PlayerDowned | 3 | `{ "x": 1234.5, "y": 5678.9, "z": 100.0 }` |
| Revive | 4 | `{}` |
| NpcDamage | 5 | `{ "entity": "krab_man_4", "amount": 50 }` |

## C# Architecture Changes

### KcdMp.Shared

- `StateType` enum: CombatState=1, Equipment=2
- `EventType` enum: DamageDealt=1, PlayerDied=2, PlayerDowned=3, Revive=4, NpcDamage=5
- `PacketWriter`: new methods `StateUpdate(stateType, payload)`, `Event(eventType, json)`
- `PacketReader`: new methods `ParseStateUpdate()`, `ParseStateSync()`, `ParseEvent()`, `ParseEventRelay()`

### KcdMp.Client (GameBridge)

- New 200ms state loop: reads combat flags + animation via CVar trick, sends StateUpdate on change
- New ~3s equipment loop (if probe succeeds): reads equipment GUIDs, sends StateUpdate on change
- New damage event polling: reads `KCD2MP.pendingDamageEvents` via CVar trick, sends Event packets
- Death detection: polls `GetHealth()` for 0, sends PlayerDied/PlayerDowned event
- Receive handlers for StateSync (apply combat flags + animation + equipment to ghost via Lua) and EventRelay (handle incoming damage, death, NPC damage)

### KcdMp.Server (RelayServer)

- `BroadcastState(source, stateType, payload)`: relay StateUpdate as StateSync with sourceId
- `BroadcastEvent(source, eventType, json)`: relay Event as EventRelay with sourceId
- `ClientSession` packet loop: accept 0x07 and 0x09 in addition to existing 0x01
- Server remains stateless — just relays. No server-side timers or game logic.

## Lua Mod Changes

### New Hooks

```lua
-- In mod startup script (not console)
-- Hook hit system for damage detection
SinglePlayer.Client.OnHit -> filter by targetId/shooterId
    -> queue damage events for client agent
```

### New Functions

```
KCD2MP_ReadCombatState()      -- returns flags + anim via CVar
KCD2MP_ReadDamageEvents()     -- returns queued events via CVar, clears queue
KCD2MP_ReadEquipment()        -- returns equipment GUIDs via CVar (probe-dependent)
KCD2MP_ApplyCombatState(id, flags, anim)  -- set ghost combat posture + animation
KCD2MP_ApplyEquipment(id, json)           -- set ghost equipment presets
KCD2MP_HandleDamage(amount)               -- DealDamage on local player
KCD2MP_HandleNpcDamage(name, amount)      -- DealDamage on named NPC
KCD2MP_HandlePartnerDeath()               -- trigger game-over or revive timer
```

### Ghost Spawn Changes

```lua
XGenAIModule.SpawnEntity{
    Name = name,
    ClassName = "NPC",
    Pos = {x, y, z},
    Properties = {
        esFaction = "<player_faction>",        -- match player faction (Probe C)
        esModularBehaviorTree = "",
        Health = { bInvulnerable = true },     -- block NPC damage (Probe D)
    },
}
```

### CVar Multiplexing

The CVar trick (`sv_servername`) is a single string channel, currently used for rotation reads. Phase 3 adds combat state, damage events, and equipment reads.

Solution: multiplex all state reads into one Lua call returning a delimited string via `sv_servername` (already proven safe). A second CVar (`sv_maxplayers`, `g_language`) can be used if the multiplexed string becomes unwieldy — add CVar safety verification to Phase 3.0 probes before using any new CVar. Fallback is always single-CVar multiplexing.

## Testing Strategy

### Probe Tests (Phase 3.0)

- Knockout/unconscious: stamina-only DealDamage, actor method search
- Equipment reading: inventory/actor methods for equipped items
- Player faction name: soul properties or Tables.pak
- Ghost invulnerability: bInvulnerable property or OnHit filtering
- Animation mapping: player anim names on NPC entities via StartAnimation

### C# Unit Tests (xUnit)

- PacketWriter/PacketReader round-trip for StateUpdate, StateSync, Event, EventRelay
- StateType and EventType serialization
- Combat flags byte packing/unpacking
- Equipment JSON serialization
- Damage event JSON serialization

### C# Integration Tests

- Two mock clients -> relay server -> verify StateSync packets arrive with correct sourceId
- Client A sends DamageDealt event -> Client B receives EventRelay with damage amount
- Client A sends PlayerDied -> Client B receives it
- Client A sends NpcDamage -> Client B receives entity name + damage
- Mixed traffic: position packets and state packets interleaved correctly

### Lua Probe Verification (on Windows PC)

- OnHit hook fires for player-on-NPC hits (captures targetId, damage)
- OnHit hook fires for player-on-ghost hits (friendly fire detection)
- Ghost faction change -> NPCs aggro on ghost
- Ghost invulnerability -> NPC hits don't kill ghost
- Animation state read -> apply to ghost via StartAnimation
- Equipment read (if probe succeeds) -> apply to ghost

### Manual E2E Checklist

- [ ] Player A attacks NPC -> NPC takes damage in Player B's game
- [ ] Player B attacks Player A's ghost -> Player A takes real damage
- [ ] Player A dies -> both players game-over (or revive flow triggers)
- [ ] Ghost draws NPC aggro in combat encounters
- [ ] Ghost is invulnerable to NPC damage
- [ ] Ghost shows combat animations (weapon swings, blocking)
- [ ] Ghost shows correct equipment (if probe succeeds)
- [ ] Combat state flags sync (weapon drawn/sheathed visible on ghost)
- [ ] "Partner took damage" flash notification works
- [ ] Death notification displays correctly
- [ ] NPC killed by relayed damage dies visually in both games

## Minimum Viable Phase 3 (if probes fail)

If most probes yield no new APIs, Phase 3 still delivers:

**Guaranteed (all VERIFIED APIs):**
- NPC damage relay via `DealDamage` on named entities (Flow 2) — VERIFIED
- Player death detection via `GetHealth()` == 0 — VERIFIED
- Mutual game-over on death — VERIFIED (DealDamage kills player)
- Combat state flags via `IsInCombatMode` / `IsInCombatDanger` — VERIFIED
- Movement animation sync (walk, run, crouch) — VERIFIED (already implemented)
- Equipment: static preset on ghost — VERIFIED (already implemented)

**Deferred if probes fail:**
- Friendly fire (requires Probe F: OnHit hook)
- Knockout/revive (requires Probe A: unconscious state)
- Dynamic equipment sync (requires Probe B: equipment reading)
- Ghost faction aggro (requires Probe C + G: faction + AI config)
- Combat animation mirroring (requires Probe E: animation mapping)

The minimum viable Phase 3 still delivers shared NPC combat and death sync — the two most gameplay-impactful features — using only VERIFIED APIs.
