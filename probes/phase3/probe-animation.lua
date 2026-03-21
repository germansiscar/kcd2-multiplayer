-- Probe E: Animation
-- Test if we can read animation state from different layers

local anim = "none"
pcall(function() anim = tostring(player.actor:GetCurrentAnimationState(0)) end)
local anim1 = "none"
pcall(function() anim1 = tostring(player.actor:GetCurrentAnimationState(1)) end)
System.SetCVar("sv_servername", string.format("layer0=%s,layer1=%s", anim, anim1))
