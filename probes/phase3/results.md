# Phase 3 Probe Results

Date: 2026-03-21
Game API: http://100.106.212.128:1404

## Summary

Tested 7 probes for Phase 3 State Sync implementation. 4 probes ran successfully from console, 3 require mod deployment.

## Probe Results

### Probe A: Knockout ✅ TESTED

**File:** `probe-knockout.lua`

**Test:** Can we knockout the player with stamina damage without killing them?

**Code:**
```lua
player.soul:DealDamage(0, 99999, __null, true)
System.SetCVar("sv_servername", string.format("hp=%s,dead=%s",
    tostring(player.actor:GetHealth()), tostring(player.actor:IsDead())))
```

**Result:** `hp=0,dead=true`

**Conclusion:** NEGATIVE - Massive stamina damage (99999) kills the player outright. `GetHealth()` returns 0 and `IsDead()` returns true. There is NO knockout state separate from death in the current API.

**Impact on Phase 3:**
- Cannot use stamina-only damage for non-lethal knockouts
- Must treat all damage as potentially lethal
- Death synchronization is binary (alive/dead), no intermediate state

---

### Probe B: Equipment ⚠️ PARTIAL

**File:** `probe-equipment.lua`

**Test:** Can we read equipped weapon/items in player's hands?

**Code:**
```lua
local results = {}
pcall(function() table.insert(results, "getCurrentItem0=" .. tostring(player.actor:GetCurrentItem(0))) end)
pcall(function() table.insert(results, "getCurrentItem1=" .. tostring(player.actor:GetCurrentItem(1))) end)
pcall(function() table.insert(results, "getEquipped=" .. tostring(player.inventory:GetEquippedItem(0))) end)
pcall(function()
    for k,v in pairs(getmetatable(player.inventory).__index or {}) do
        if type(v) == "function" then table.insert(results, "inv:" .. k) end
    end
end)
System.SetCVar("sv_servername", table.concat(results, "|"))
```

**Result:**
```
inv:RemoveMoney|inv:FindItem|inv:GetMoney|inv:GetCountOfClass|inv:HasItem|
inv:GetCountOfCategory|inv:DeleteItemOfClass|inv:DeleteItem|inv:GetId|
inv:AddItem|inv:Dump|inv:GetCount|inv:RemoveAllItems|inv:MoveItemOfClass|
inv:GetInventoryTable|inv:CreateItem
```

**Conclusion:** PARTIAL - `GetCurrentItem()` and `GetEquippedItem()` both failed (not in results). However, `player.inventory` exposes many methods. `GetInventoryTable()` is promising for reading all equipped items.

**Follow-up needed:** Test `player.inventory:GetInventoryTable()` to see if it includes equipped item slots.

**Impact on Phase 3:**
- May need to enumerate inventory table to find equipped items
- Cannot directly query "what's in player's hands" via simple method
- Animation state might be more reliable for combat sync than equipment state

---

### Probe C: Faction ✅ TESTED

**File:** `probe-faction.lua`

**Test:** Can we read player faction (for ghost NPC aggro matching)?

**Code:**
```lua
local faction = "unknown"
pcall(function() faction = tostring(player.Properties.esFaction) end)
local faction2 = "unknown"
pcall(function() faction2 = tostring(player.soul.faction) end)
local faction3 = "unknown"
pcall(function() faction3 = tostring(player.soul:GetFaction()) end)
System.SetCVar("sv_servername", string.format("prop=%s,soul=%s,get=%s", faction, faction2, faction3))
```

**Result:** `prop=nil,soul=nil,get=unknown`

**Conclusion:** NEGATIVE - No faction API accessible on player entity. All three approaches failed:
- `player.Properties.esFaction` - nil
- `player.soul.faction` - nil
- `player.soul:GetFaction()` - crashed (unknown method)

**Impact on Phase 3:**
- Cannot programmatically read player faction to assign to ghost NPC
- Must HARDCODE ghost faction (e.g., always "eFaction_Player" or similar)
- Risk: if ghost faction doesn't match player, hostile NPCs may not aggro on both
- Need to test faction assignment on spawned NPC separately (Probe G)

---

### Probe D: Invulnerability ⚠️ PARTIAL

**File:** `probe-invulnerability.lua`

**Test:** Can we make ghost NPC invulnerable to NPC damage (while allowing friendly fire)?

**Approach 1:** Spawn NPC and search for invulnerability methods
```lua
local npc = System.SpawnEntity({class = "TagNPC", ...})
-- Search actor metatable for invuln/immune/godmode methods
```

**Result:** `spawn_failed` - TagNPC spawn failed from console (expected, Phase 2 finding)

**Approach 2:** Search player actor for invulnerability methods
```lua
-- Search player.actor metatable for methods matching "invuln", "immune", "godmode"
```

**Result:** `none_found`

**Approach 3:** Search player Properties for invulnerability flags
```lua
-- Search player.Properties for keys matching "invuln", "immune", "damage"
```

**Result:** `none_found`

**Conclusion:** NO EVIDENCE of invulnerability API. This is a critical gap.

**Workarounds:**
1. **Ignore NPC damage to ghost** - Don't relay damage from NPC shooterId to ghost entity
2. **Hook ghost OnHit** - Filter out NPC damage in the hook, only process player shooterId
3. **Health restoration** - After NPC damage, immediately restore ghost health (if SetHealth worked)
4. **Accept ghost death** - Let ghost die to NPCs, respawn on next position sync

**Impact on Phase 3:**
- Ghost will take damage from hostile NPCs unless we filter damage in OnHit hook
- Since SetHealth() is a no-op (Phase 2), we CANNOT restore health after NPC damage
- MUST implement damage filtering: only relay damage where shooterId == other_player_entity_id
- This requires Probe F (OnHit hook) to verify shooterId is available in hit data

---

### Probe E: Animation ✅ TESTED

**File:** `probe-animation.lua`

**Test:** Can we read animation state for ghost mirroring?

**Code:**
```lua
local anim = "none"
pcall(function() anim = tostring(player.actor:GetCurrentAnimationState(0)) end)
local anim1 = "none"
pcall(function() anim1 = tostring(player.actor:GetCurrentAnimationState(1)) end)
System.SetCVar("sv_servername", string.format("layer0=%s,layer1=%s", anim, anim1))
```

**Result:** `layer0=MotionIdle,layer1=MotionIdle`

**Conclusion:** POSITIVE - `player.actor:GetCurrentAnimationState(layer)` returns animation state strings. Both layer 0 and 1 return "MotionIdle" when player is standing still.

**Follow-up needed:**
- Test during combat (sword swing, block, dodge)
- Test during movement (walk, run, jump)
- Test during interactions (dialog, looting)
- Determine which layer is most relevant for combat sync

**Impact on Phase 3:**
- CAN read animation state for sync
- May need to sync multiple layers (0 and 1) or determine primary layer
- Animation strings can be relayed to set ghost animation (if SetAnimationState exists)
- Even if we can only read (not set), this confirms combat state for validation

---

### Probe F: OnHit Hook ❌ UNTESTED - Requires Mod Deployment

**File:** `probe-onhit.lua`

**Test:** Does `entity.Client:OnHit(hit)` fire when player takes damage? What data is in `hit`?

**Approach:**
```lua
player.Client.OnHit = function(entity, hit)
    System.SetCVar("sv_servername", string.format("hit_dmg=%s,from=%s",
        tostring(hit.damage or 0), tostring(hit.shooterId or "none")))
end
```

**Why untested:**
- Hook registration from console doesn't persist
- Script.SetTimer doesn't fire from console (Phase 2 finding)
- Needs to be embedded in kdcmp.lua and loaded with mod

**Critical questions:**
1. Does OnHit hook fire for all damage types (melee, fall, fire)?
2. Is `hit.shooterId` available to identify damage source?
3. Is `hit.damage` a gradual value or binary?
4. Can we return false from OnHit to cancel damage?

**Impact on Phase 3:**
- REQUIRED for friendly fire relay - must identify when damage is from other player
- If shooterId not available, cannot filter NPC damage vs player damage
- If cannot cancel damage, ghost will die to NPC attacks (no invulnerability API found)

**Next step:** Embed hook in kdcmp.lua, deploy mod, trigger damage, read CVar

---

### Probe G: Aggro ❌ UNTESTED - Requires Mod Deployment

**File:** `probe-aggro.lua`

**Test:** Do hostile NPCs aggro on ghost NPC if ghost faction matches player?

**Approach:**
```lua
local ghost = System.SpawnEntity({...})
ghost.Properties.esFaction = "eFaction_Player"  -- or player.Properties.esFaction
-- Move to hostile area (Cuman camp)
-- Observe if hostiles attack ghost
```

**Why untested:**
- Requires persistent NPC spawn (console spawn is unstable)
- Requires being in hostile area
- Requires time for AI to detect and react
- Needs mod deployment with stable ghost entity

**Critical questions:**
1. What is the correct faction value for player? ("eFaction_Player", "eFaction_Friendly", other?)
2. Does faction assignment work on spawned NPCs?
3. Do hostile NPCs treat ghost as valid target?
4. Is there a minimum distance or LOS requirement?

**Impact on Phase 3:**
- Core requirement: both players must draw aggro from shared enemies
- If faction doesn't work, combat will be one-sided (enemies only aggro on host)
- May need to manually trigger aggro via AI messages (XGenAIModule)

**Next step:** Embed ghost spawn in kdcmp.lua, test in Cuman camp, observe aggro behavior

---

## Critical Findings Summary

### Working APIs (Verified)
- ✅ `player.actor:GetCurrentAnimationState(layer)` - returns animation state string
- ✅ `player.actor:GetHealth()` - returns 100 (alive) or 0 (dead)
- ✅ `player.actor:IsDead()` - returns boolean
- ✅ `player.soul:DealDamage(hp, stamina, __null, true)` - applies damage

### Non-Working / Unknown APIs
- ❌ Faction read - no API found on player
- ❌ Invulnerability - no API found on player or actor
- ❌ SetHealth - confirmed no-op in Phase 2
- ❌ Equipment read - GetCurrentItem/GetEquippedItem failed
- ⚠️ OnHit hook - untested, needs mod deployment
- ⚠️ Faction assignment - untested on spawned NPC

### Phase 3 Blockers

1. **No invulnerability API** - Cannot prevent ghost from dying to NPC damage
   - Mitigation: Filter damage in OnHit hook (if shooterId available)
   - Fallback: Accept ghost death, respawn on sync

2. **No faction read API** - Cannot dynamically match ghost faction to player
   - Mitigation: Hardcode ghost faction, test common values
   - Risk: May not work across all quest contexts

3. **OnHit hook unverified** - Critical for friendly fire and damage filtering
   - Required: Mod deployment test ASAP
   - Gating: All damage sync tasks

### Recommendations

1. **Immediate:** Test OnHit hook (Probe F) via mod deployment - this is the highest priority
2. **Before Protocol:** Test `player.inventory:GetInventoryTable()` for equipment sync
3. **Before Lua Mod:** Test faction assignment on spawned NPC (Probe G) in hostile area
4. **Design decision:** Accept that ghost cannot be made invulnerable, filter damage in OnHit instead
5. **Design decision:** If OnHit cannot cancel damage, implement ghost respawn on death

### Test Environment Notes

- Game API: http://100.106.212.128:1404 (via Tailscale)
- All console probes run successfully
- TagNPC spawn from console still fails (consistent with Phase 2)
- Player was likely dead during equipment test (from knockout probe) - may need retest
