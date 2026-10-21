-- OmniKit · Effects
-- Juice from the server: a burst of sparkles where something happened, "+25 🪙" floating up from it, a
-- sound. The server only sends a small message; each client draws the effect itself (particles, a
-- billboard, a sound), so effects cost the server nothing and never replicate parts.
--
--   Effects.Burst(position, Color3.fromRGB(255, 205, 64))   -- everyone sees it
--   Effects.Float(player, position, "+25 🪙")               -- only that player
--   Effects.Sound(player, "coin")                            -- a UI sound for one player (nil: everyone)

local Config = require(script.Parent.Config)
local Net = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Net"))
local Util = require(game:GetService("ReplicatedStorage"):WaitForChild("OmniKit"):WaitForChild("Util"))

local Effects = {}

local testing = Config.Testing == true

-- What was sent during an engine test (specs can check that a sale made a burst, say).
Effects.Sent = {} :: { any }

local function send(player: any?, message: { [string]: any })
	if testing then
		if #Effects.Sent < 500 then
			table.insert(Effects.Sent, message)
		end
		return
	end
	local remote = Net.Event("OmniFx")
	if player == nil then
		remote:FireAllClients(message)
	elseif Util.IsRealPlayer(player) then
		remote:FireClient(player, message)
	end
end

-- Sparkles bursting out at a point (count 8-40).
function Effects.Burst(position: Vector3, color: Color3?, count: number?, player: any?)
	send(player, { kind = "burst", at = position, color = color or Color3.fromRGB(255, 220, 90), count = math.clamp(count or 16, 4, 60) })
end

-- Text that rises and fades above a point ("+25 🪙", "LEVEL UP!").
function Effects.Float(player: any?, position: Vector3, text: string, color: Color3?)
	send(player, { kind = "float", at = position, text = string.sub(text, 1, 40), color = color or Color3.fromRGB(255, 220, 90) })
end

-- A named sound (see Sounds) for one player, or everyone; at a position when given.
function Effects.Sound(player: any?, name: string, position: Vector3?)
	send(player, { kind = "sound", name = name, at = position })
end

-- A toast message for one player.
function Effects.Toast(player: any, text: string, kind: string?)
	if testing then
		send(player, { kind = "toast", text = text, style = kind })
		return
	end
	if Util.IsRealPlayer(player) then
		Net.Event("Notify"):FireClient(player, { text = text, kind = kind or "info" })
	end
end

-- Everything at once for a reward: a burst at the spot, the amount floating up, a coin sound.
function Effects.Reward(player: any, position: Vector3?, text: string, color: Color3?)
	local at = position
	if not at then
		local root = Util.RootOf(player)
		at = if root then root.Position + Vector3.new(0, 3, 0) else nil
	end
	if at then
		Effects.Burst(at, color, 14)
		Effects.Float(player, at, text, color)
	end
	Effects.Sound(player, "coin")
end

return Effects
