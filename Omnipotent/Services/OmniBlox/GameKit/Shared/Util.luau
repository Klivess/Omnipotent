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

-- A numeric setting (text such as "12" counts; anything else falls back to the default).
function Util.Number(inst: Instance, name: string, default: number): number
	local value = Util.Setting(inst, name, default)
	return tonumber(value) or default
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

-- The root part of a player's character, if they have one right now.
function Util.RootOf(player: any): BasePart?
	if not Util.IsRealPlayer(player) then
		return nil
	end
	local character = (player :: Player).Character
	local root = character and character:FindFirstChild("HumanoidRootPart")
	return if root and root:IsA("BasePart") then root else nil
end

-- 1234 -> "1,234"; 12345 -> "12.3K"; 1234567 -> "1.23M".
function Util.Format(n: number): string
	local value = math.floor(n or 0)
	local absolute = math.abs(value)
	if absolute >= 1e12 then
		return string.format("%.2fT", value / 1e12)
	elseif absolute >= 1e9 then
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
function Util.Debouncer(seconds: number): (any) -> boolean
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

export type Connection = { Disconnect: () -> () }
export type Signal = {
	Connect: (self: Signal, fn: (...any) -> ()) -> Connection,
	Fire: (self: Signal, ...any) -> (),
	Wait: (self: Signal) -> ...any,
}

-- A tiny signal without BindableEvents (works with plain tables, so tests can use fake players).
function Util.Signal(): Signal
	local handlers: { (...any) -> () } = {}
	local waiting: { thread } = {}
	local signal = {}
	function signal:Connect(fn: (...any) -> ()): Connection
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
	function signal:Fire(...: any)
		for _, fn in ipairs(table.clone(handlers)) do
			task.spawn(fn, ...)
		end
		local resume = waiting
		waiting = {}
		for _, thread in ipairs(resume) do
			task.spawn(thread, ...)
		end
	end
	function signal:Wait(): ...any
		table.insert(waiting, coroutine.running())
		return coroutine.yield()
	end
	return (signal :: any) :: Signal
end

function Util.IsRealPlayer(player: any): boolean
	return typeof(player) == "Instance" and player:IsA("Player")
end

-- Picks a random entry of a list (nil for an empty list).
function Util.Pick<T>(list: { T }): T?
	if #list == 0 then
		return nil
	end
	return list[math.random(1, #list)]
end

-- A colour from "#RRGGBB" (or a Color3 passed through); white for anything else.
function Util.Color(value: any, default: Color3?): Color3
	if typeof(value) == "Color3" then
		return value
	end
	if type(value) == "string" then
		local hex = string.match(value, "^#?(%x%x%x%x%x%x)$")
		if hex then
			return Color3.fromHex(hex)
		end
	end
	return default or Color3.new(1, 1, 1)
end

return Util
