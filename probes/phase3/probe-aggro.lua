-- Probe G: Aggro
-- UNTESTED - requires mod deployment
-- Test if ghost NPC with matching faction gets aggro from hostile NPCs

-- This probe requires:
-- 1. Spawning a ghost NPC near the player
-- 2. Setting ghost faction to match player
-- 3. Having hostile NPCs nearby (e.g., Cumans)
-- 4. Observing if hostiles attack the ghost

-- Cannot test from console because:
-- - Need persistent NPC spawn
-- - Need to be in hostile area
-- - Need time for AI to react

-- Example approach (needs mod):
-- local ghost = System.SpawnEntity({...})
-- ghost.Properties.esFaction = player.Properties.esFaction
-- -- OR
-- ghost.soul:SetFaction(player.soul:GetFaction())
-- -- Then wait and observe if nearby hostiles aggro on ghost

System.SetCVar("sv_servername", "probe_aggro_requires_mod_deployment")
