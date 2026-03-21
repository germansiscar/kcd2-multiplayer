-- Probe B: Equipment
-- Test methods to read equipped items (weapon in hands)

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
