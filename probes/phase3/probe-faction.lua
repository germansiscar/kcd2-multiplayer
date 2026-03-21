-- Probe C: Faction
-- Test various ways to read and potentially set player faction

local faction = "unknown"
pcall(function() faction = tostring(player.Properties.esFaction) end)
local faction2 = "unknown"
pcall(function() faction2 = tostring(player.soul.faction) end)
local faction3 = "unknown"
pcall(function() faction3 = tostring(player.soul:GetFaction()) end)
System.SetCVar("sv_servername", string.format("prop=%s,soul=%s,get=%s", faction, faction2, faction3))
