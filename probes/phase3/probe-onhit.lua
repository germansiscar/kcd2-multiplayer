-- Probe F: OnHit hook
-- UNTESTED - requires mod deployment
-- Test if entity.Client:OnHit(hit) hook fires for player damage

-- This probe MUST be embedded in kdcmp.lua as a hook:
-- player.Client.OnHit = function(entity, hit)
--     System.SetCVar("sv_servername", string.format("hit_dmg=%s,from=%s", tostring(hit.damage or 0), tostring(hit.shooterId or "none")))
-- end

-- Then trigger damage (e.g., fall damage, NPC attack) and read the CVar.
-- Cannot be tested from console because Script.SetTimer doesn't fire,
-- and we need persistent hook registration.

System.SetCVar("sv_servername", "probe_onhit_requires_mod_deployment")
