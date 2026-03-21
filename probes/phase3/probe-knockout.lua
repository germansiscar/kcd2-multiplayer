-- Probe A: Knockout
-- Test if stamina damage can knock out the player without killing them

player.soul:DealDamage(0, 99999, __null, true)
System.SetCVar("sv_servername", string.format("hp=%s,dead=%s", tostring(player.actor:GetHealth()), tostring(player.actor:IsDead())))
