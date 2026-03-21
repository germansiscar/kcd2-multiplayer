# Phase 3 E2E Validation Results

Date: 2026-03-21
Game: Kuttenburg Debug (Suchdol)

## Combat State Sync — VERIFIED

- `player.soul:IsInCombatMode()` toggles correctly (0 idle, 1 fists/weapon up)
- `player.soul:IsInCombatDanger()` reads correctly
- `player.actor:GetCurrentAnimationState(0)` returns distinct strings:
  - `MotionIdle` when standing still
  - `MotionMovement` when moving/fighting
- StateLoopAsync can read all values in a single CVar call

## Death Detection — VERIFIED

- Player death detectable via `Health="0"` / `IsDead="true"` in PlayerSoul API
- Multiple deaths confirmed during testing

## OnHit Hook — NOT WORKING

- `SinglePlayer.Client.OnHit` exists as a function
- Hook installs successfully (both from mod and console injection)
- Hook NEVER fires on melee combat (punching, kicking NPCs)
- Tested: hit proprietor NPC, fought guards, died — zero events captured
- Conclusion: KCD2's melee hit system does not route through `SinglePlayer.Client.OnHit`

### Impact

- Friendly fire relay: DESCOPED (can't detect player hitting ghost)
- Shared NPC combat: DESCOPED (can't detect which NPC player hit)
- Need alternative approach (health delta polling, different hook point)

## Deployment Notes

- Symlink created: `KCD2Mod\Mods\kdcmp` -> `Documents\kcdmp\kdcmp`
- Future deploys via `deploy.sh` automatically update game mod (no setup.ps1 needed)
- Kuttenburg Debug starts player with NO equipment — use API to add items if needed
