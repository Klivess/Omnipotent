-- OmniKit · Net
-- The Game Kit's remotes, in one folder. The server creates them on demand; clients wait for them.

local ReplicatedStorage = game:GetService("ReplicatedStorage")
local RunService = game:GetService("RunService")

local Net = {}

local FOLDER_NAME = "OmniKitRemotes"

local function folder(): Folder
	local existing = ReplicatedStorage:FindFirstChild(FOLDER_NAME)
	if existing then
		return existing
	end
	if RunService:IsServer() then
		local created = Instance.new("Folder")
		created.Name = FOLDER_NAME
		created.Parent = ReplicatedStorage
		return created
	end
	return ReplicatedStorage:WaitForChild(FOLDER_NAME)
end

local function get(className: string, name: string)
	local root = folder()
	local found = root:FindFirstChild(name)
	if found then
		return found
	end
	if RunService:IsServer() then
		local remote = Instance.new(className)
		remote.Name = name
		remote.Parent = root
		return remote
	end
	return root:WaitForChild(name)
end

function Net.Event(name: string): RemoteEvent
	return get("RemoteEvent", name)
end

function Net.Function(name: string): RemoteFunction
	return get("RemoteFunction", name)
end

return Net
