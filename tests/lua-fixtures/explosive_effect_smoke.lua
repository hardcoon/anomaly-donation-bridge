local core_root = assert(arg[1], "pass game-addon/gamedata/scripts as arg[1]")
local effect_root = assert(arg[3], "pass optional-effects/gamedata/scripts as arg[3]")
local separator = package.config:sub(1, 1)

local function load_module(root, name)
	local chunk = assert(loadfile(root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

anomaly_donation_bridge_common = load_module(core_root,
	"anomaly_donation_bridge_common")
anomaly_donation_bridge_registry = load_module(core_root,
	"anomaly_donation_bridge_registry")

local ini_values = {
	grenade_f1 = { class = "G_F1", destroy_time = "2500" },
	grenade_rgd5 = { class = "G_RGD5_S", destroy_time = "3000" },
	medkit = { class = "II_MEDKI" }
}
function system_ini()
	return {
		section_exist = function(_, section) return ini_values[section] ~= nil end,
		line_exist = function(_, section, key)
			return ini_values[section] ~= nil and ini_values[section][key] ~= nil
		end,
		r_string = function(_, section, key) return ini_values[section][key] end
	}
end

local available = { grenade_f1 = true, grenade_rgd5 = true, medkit = true }
anomaly_donation_bridge_catalog = {
	capable = function() return true end,
	is_available = function(section, kind)
		return kind == "item" and available[section] == true
	end,
	validate_sections = function(params, key, kind)
		assert(kind == "item" and type(params.catalog_revision) == "string")
		local entries = {}
		for section in tostring(params[key] or ""):gmatch("[^,]+") do
			if not available[section] then return nil, "stale" end
			entries[#entries + 1] = { section_id = section }
		end
		return entries
	end
}

function vector()
	return {
		set = function(self, x, y, z)
			self.x, self.y, self.z = x, y, z
			return self
		end
	}
end

local actor = {}
function actor:position() return vector():set(0, 0, 0) end
function actor:direction() return vector():set(1, 0, 0) end
function actor:level_vertex_id() return 1 end
function actor:game_vertex_id() return 20 end
db = { actor = actor }

local current_time = 1000
function time_global() return current_time end
function device()
	return {
		cam_pos = vector():set(0, 1.65, 0),
		cam_dir = vector():set(1, -0.2, 0),
		time_global = function() return current_time end
	}
end

local ray_blocked = false
rq_target = { rqtStatic = 2 }
function ray_pick(_, _, distance, flags, ignored)
	assert(flags == 2 and ignored == actor)
	return {
		query = function() return ray_blocked end,
		get_distance = function() return math.min(1, distance) end
	}
end

local vertices = {
	[1] = vector():set(0, 0, 0),
	[2] = vector():set(5, 0, 0),
	[3] = vector():set(0, 0, 2)
}
local client_objects = {}
local callbacks = {}
function RegisterScriptCallback(name, callback)
	callbacks[name] = callback
end
level = {
	vertex_position = function(id)
		assert(vertices[id] ~= nil)
		return vertices[id]
	end,
	vertex_in_direction = function(_, direction, distance)
		if distance >= 2.5 and math.abs(direction.z) < 0.1 then return 2 end
		if direction.z > 0.1 then return 3 end
		return 1
	end,
	object_by_id = function(id) return client_objects[id] end
}

local released = {}
local servers = {}
local life = {
	object = function(_, id) return servers[id] end,
	release = function(_, server) released[server.id] = true end,
	set_switch_online = function() end,
	set_switch_offline = function() end
}
function alife() return life end
local next_id = 700
local created = {}
function alife_create(section, position, level_vertex_id, game_vertex_id)
	assert(available[section] and level_vertex_id == 2 and game_vertex_id == 20)
	assert(math.abs(position.x - 5) < 0.001 and
		math.abs(position.y - 1.65) < 0.001 and math.abs(position.z) < 0.001)
	local server = { id = next_id }
	function server:section_name() return section end
	servers[next_id] = server
	created[#created + 1] = { section = section, id = next_id }
	next_id = next_id + 1
	return server
end

local exploded = 0
local function client(section, id, parent)
	return {
		id = function() return id end,
		parent = function() return parent end,
		section = function() return section end,
		explode = function(_, value)
			assert(value == 0)
			exploded = exploded + 1
		end
	}
end

local effect = load_module(effect_root,
	"anomaly_donation_bridge_effect_hardcoon_adb_explosive")
effect.on_game_start()
local handler = assert(anomaly_donation_bridge_registry.get(
	"hardcoon.adb.explosive"))
assert(handler.is_supported == nil,
	"luabind engine APIs must be validated by the guarded real calls, not by type probes")

local invalid_context = { actor = actor }
assert(handler.can_execute(invalid_context, {
	grenade_sections = "grenade_f1,medkit",
	distance_meters = "3",
	catalog_revision = "live"
}) == false, "non-grenade catalog items must never enter the pool")

local context = { actor = actor, now = current_time }
local original_random = math.random
math.random = function(maximum) assert(maximum == 2) return 1 end
local can_spawn, can_status, can_reason = handler.can_execute(context, {
	grenade_sections = "grenade_f1,grenade_rgd5",
	catalog_revision = "live"
})
assert(can_spawn == true, tostring(can_status) .. ":" .. tostring(can_reason))
math.random = original_random
local status, detail = handler.execute(context)
assert(status == "executed" and
	detail == "explosive_grenade_grenade_f1_distance_5m_fuse_2500ms_in_front")
assert(#created == 1 and created[1].section == "grenade_f1")
client_objects[created[1].id] = client("grenade_f1", created[1].id)
local pickup_flags = { ret_value = true }
assert(type(callbacks.actor_on_item_before_pickup) == "function")
callbacks.actor_on_item_before_pickup(client_objects[created[1].id], pickup_flags)
assert(pickup_flags.ret_value == false,
	"a grenade created by the effect must not be pickable")
handler.update(2000)
assert(pickup_flags.ret_value == true,
	"the compatibility flag must be restored after the engine consumes the veto")
local unrelated_flags = { ret_value = true }
callbacks.actor_on_item_before_pickup(client("medkit", 999), unrelated_flags)
assert(unrelated_flags.ret_value == true,
	"the pickup veto must not affect unrelated items")
handler.update(3499)
assert(exploded == 0)
handler.update(3500)
assert(exploded == 1, "the live grenade must explode after its stock fuse")

assert(handler.can_execute({ actor = actor }, {
	grenade_sections = "grenade_f1",
	distance_meters = "11",
	catalog_revision = "live"
}) == false, "distance must stay within 0..10 metres")

print("explosive effect smoke passed")
