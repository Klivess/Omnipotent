-- OmniKit · Util
-- Small helpers shared by server and client.

local Players = game:GetService("Players")

local Util = {}

-- Reads a setting placed on an object by OmniBlox: an attribute, or a child Value object of that name.
function Util.Setting(inst: Instance, name: string, default: any): any
	local attribute = inst:GetAttribute(name)
	if attribute ~= nil then
		return attribute
	end
	local child = inst:FindFirstChild(name)
	if child and child:IsA("ValueBase") then
		return (child :: any).Value
	end
	return default
end

-- The player whose character a touching part belongs to, plus that character's humanoid.
function Util.PlayerFromHit(hit: BasePart): (Player?, Humanoid?)
	local model = hit:FindFirstAncestorOfClass("Model")
	if not model then
		return nil, nil
	end
	local player = Players:GetPlayerFromCharacter(model)
	if not player then
		return nil, nil
	end
	local humanoid = model:FindFirstChildOfClass("Humanoid")
	if not humanoid or humanoid.Health <= 0 then
		return nil, nil
	end
	return player, humanoid
end

-- 1234 -> "1,234"; 1234567 -> "1.23M".
function Util.Format(n: number): string
	local value = math.floor(n or 0)
	local absolute = math.abs(value)
	if absolute >= 1e9 then
		return string.format("%.2fB", value / 1e9)
	elseif absolute >= 1e6 then
		return string.format("%.2fM", value / 1e6)
	elseif absolute >= 1e4 then
		return string.format("%.1fK", value / 1e3)
	end
	local text = tostring(value)
	local formatted, count = text, 0
	repeat
		formatted, count = string.gsub(formatted, "^(-?%d+)(%d%d%d)", "%1,%2")
	until count == 0
	return formatted
end

-- Per-key cooldowns ("touched this pad less than 1 s ago").
function Util.Debouncer(seconds: number)
	local last: { [any]: number } = {}
	return function(key: any): boolean
		local now = os.clock()
		local previous = last[key]
		if previous and now - previous < seconds then
			return false
		end
		last[key] = now
		return true
	end
end

-- A tiny signal without BindableEvents (works with plain tables, so tests can use fake players).
function Util.Signal()
	local handlers = {}
	local signal = {}
	function signal:Connect(fn)
		table.insert(handlers, fn)
		return {
			Disconnect = function()
				local index = table.find(handlers, fn)
				if index then
					table.remove(handlers, index)
				end
			end,
		}
	end
	function signal:Fire(...)
		for _, fn in ipairs(table.clone(handlers)) do
			task.spawn(fn, ...)
		end
	end
	return signal
end

function Util.IsRealPlayer(player: any): boolean
	return typeof(player) == "Instance" and player:IsA("Player")
end

return Util
