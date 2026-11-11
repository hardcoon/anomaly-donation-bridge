local script_root = assert(arg[1], "pass game-addon/gamedata/scripts as arg[1]")
local separator = package.config:sub(1, 1)

local function load_module(name)
	local root = name:find("_actions_", 1, true) and
		(script_root .. separator .. ".." .. separator .. ".." .. separator ..
		"legacy-scripts") or script_root
	local chunk = assert(loadfile(root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

local function load_effect(root, name)
	local chunk = assert(loadfile(root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

anomaly_donation_bridge_common = load_module("anomaly_donation_bridge_common")
anomaly_donation_bridge_registry = load_module("anomaly_donation_bridge_registry")
local selected_squad
local anomaly_random_sections = { "zone_test", "zone_fire", "zone_acid" }
local anomaly_random_call = 0
anomaly_donation_bridge_catalog = {
	capable = function() return true end,
	validate_section = function(params, key, kind)
		if params.catalog_revision ~= "rev" then return nil, "catalog_revision_mismatch" end
		return { section_id = params[key], kind = kind, available = true,
			strength = "normal", faction = kind == "npc" and "bandit" or "monster" }
	end,
	validate_revision = function(params)
		return params.catalog_revision == "rev", "catalog_revision_mismatch"
	end,
	validate_sections = function(params, key, kind)
		if params.catalog_revision ~= "rev" then return nil, "catalog_revision_mismatch" end
		local selected = {}
		for section in tostring(params[key] or ""):gmatch("[^,]+") do
			selected[section] = true
		end
		local function filter(entries)
			local result = {}
			for _, entry in ipairs(entries) do
				if selected[entry.section_id] then result[#result + 1] = entry end
			end
			return result
		end
		if kind == "npc" then
			return filter({
				{ section_id = "npc_test", kind = kind, available = true,
					strength = "normal", faction = "bandit" },
				{ section_id = "npc_other", kind = kind, available = true,
					strength = "strong", faction = "monolith" }
			})
		end
		if kind == "mutant" then
			return filter({
				{ section_id = "mutant_test", kind = kind, available = true,
					strength = "normal", faction = "monster" },
				{ section_id = "mutant_other", kind = kind, available = true,
					strength = "strong", faction = "monster" }
			})
		end
		if kind == "stalker_squad" then
			return filter({
				{ section_id = "enemy_lite", kind = kind, available = true,
					strength = "lite", faction = "bandit" },
				{ section_id = "enemy_strong", kind = kind, available = true,
					strength = "strong", faction = "monolith" }
			})
		end
		return filter({
			{ section_id = "pack_lite", kind = kind, available = true,
				strength = "lite", faction = "monster" },
			{ section_id = "pack_strong", kind = kind, available = true,
				strength = "strong", faction = "monster" }
		})
	end,
	is_available = function() return true end,
	is_spawn_safe = function() return true end,
	resolve_squad_members = function(section, kind)
		selected_squad = section
		if kind == "mutant_squad" then
			return { "mutant_test" }, nil, "mutant"
		end
		return { "npc_test" }, nil, "npc"
	end,
	random_available_anomaly = function()
		anomaly_random_call = anomaly_random_call + 1
		local section = anomaly_random_sections[
			((anomaly_random_call - 1) % #anomaly_random_sections) + 1]
		return { section_id = section, kind = "anomaly", available = true }
	end
}

local support_ini = {}
function support_ini:section_for_each() end
function support_ini:section_exist(section)
	return section == "zone_test" or section == "zone_fire" or
		section == "zone_acid"
end
function support_ini:line_exist(section, key)
	return self:section_exist(section) and key == "class"
end
function support_ini:r_string(section, key)
	if self:section_exist(section) and key == "class" then return "ZS_GALAN" end
	return ""
end
function system_ini() return support_ini end

local function point(x, y, z)
	local value = { x = x or 0, y = y or 0, z = z or 0 }
	function value:set(nx, ny, nz) self.x, self.y, self.z = nx, ny, nz return self end
	function value:distance_to_sqr(other)
		local dx, dy, dz = self.x - other.x, self.y - other.y, self.z - other.z
		return dx * dx + dy * dy + dz * dz
	end
	return value
end
function vector() return point() end

local actor_position, actor_vertex = point(0, 0, 0), 1
local actor = {}
function actor:level_vertex_id() return actor_vertex end
function actor:game_vertex_id() return 10 end
function actor:position() return actor_position end
function actor:direction() return point(0, 0, 1) end
function actor:id() return 1 end
function actor:character_community() return "stalker" end
function actor:set_actor_position(destination)
	actor_position = point(destination.x, destination.y, destination.z)
end
db = { actor = actor, spawned_vertex_by_id = {}, offline_objects = {} }

local requested_navigation_radius = nil
local current_level_name = "l01_escape"
level = {
	name = function() return current_level_name end,
	vertex_in_direction = function(_, _, radius)
		requested_navigation_radius = radius
		return 2
	end,
	object_by_id = function() return nil end,
	vertex_id = function() return 1 end,
	vertex_position = function(id)
		if id ~= 1 and id ~= 2 then error("invalid level vertex") end
	return id == 1 and point(0, 0, 0) or point(20, candidate_height or 0, 0)
	end
}

local objects, released, next_id = {}, {}, 55
local squad_spawn_position, squad_relation, initialized_members
local strongest_goodwill, offline_actor_target, hostility_calls = 0, false, 0
local callback_members = 0
local life = {}
function life:actor() return { id = 1, position = actor_position, m_game_vertex_id = 10 } end
function life:switch_distance() return 150 end
function life:create(section, position, level_vertex_id)
	local object = { id = next_id, position = point(position.x, position.y, position.z),
		m_level_vertex_id = level_vertex_id or 2, _section = section }
	next_id = next_id + 1
	function object:section_name() return self._section end
	function object:set_character_name(value) self.character_name_value = value end
	function object:force_set_goodwill(value, actor_id)
		assert(value == -1000 or value == -5000)
		strongest_goodwill = math.min(strongest_goodwill, value)
		offline_actor_target = offline_actor_target or actor_id == 0
		hostility_calls = hostility_calls + 1
		self.hostile = value <= -1000
	end
	function object:make_object_visible_somewhen() self.visible = true end
	if section == "npc_test" or section == "mutant_test" then
		squad_spawn_position = point(position.x, position.y, position.z)
	end
	if section:find("^enemy_") or section:find("^pack_") then
		object._members = {}
		function object:create_npc(spawn_smart, spawn_position, level_vertex_id,
			game_vertex_id)
			assert(spawn_smart ~= nil, "stock squad creation requires a smart terrain")
			spawn_position = spawn_position or spawn_smart.position
			level_vertex_id = level_vertex_id or spawn_smart.m_level_vertex_id
			game_vertex_id = game_vertex_id or spawn_smart.m_game_vertex_id
			selected_squad = self._section
			local member = {
				id = next_id,
				position = point(spawn_position.x, spawn_position.y, spawn_position.z),
				m_level_vertex_id = level_vertex_id,
				_section = self._section .. "_member"
			}
			next_id = next_id + 1
			function member:section_name() return self._section end
			function member:force_set_goodwill(value, actor_id)
				assert(value == -1000 or value == -5000)
				strongest_goodwill = math.min(strongest_goodwill, value)
				offline_actor_target = offline_actor_target or actor_id == 0
				hostility_calls = hostility_calls + 1
				self.hostile = value <= -1000
			end
			objects[member.id] = member
			self._members[1] = member.id
		end
		function object:squad_members()
			local index = 0
			return function()
				index = index + 1
				local id = self._members[index]
				if id ~= nil then return { id = id, object = objects[id] } end
			end
		end
		function object:npc_count() return #self._members end
		function object:getSquadMemberId(index) return self._members[index + 1] end
		function object:set_squad_relation(relation)
			squad_relation = relation
			for _, id in ipairs(self._members) do
				objects[id]:force_set_goodwill(-1000, 1)
			end
		end
	end
	objects[object.id] = object
	return object
end
function life:object(id) return objects[id] end
function life:teleport_object(id, game_vertex_id, level_vertex_id, position)
	assert(game_vertex_id == 10 and level_vertex_id == 2)
	local object = assert(objects[id])
	object.position = point(position.x, position.y, position.z)
	object.m_level_vertex_id = level_vertex_id
end
function life:release(object)
	released[object.id] = true
	objects[object.id] = nil
end
function alife() return life end
function alife_create(section, position, level_vertex_id, game_vertex_id)
	assert(type(level_vertex_id) == "number" and level_vertex_id >= 1)
	assert(game_vertex_id == 10)
	return life:create(section, position, level_vertex_id)
end

local smart = { id = 900, position = point(80, 0, 0),
	m_level_vertex_id = 3, m_game_vertex_id = 10 }
local board = { smarts_by_names = { test_smart = smart }, smarts = { [900] = true } }
local detached_squads = 0
function board:setup_squad_and_group(member)
	assert(member ~= nil)
	initialized_members = (initialized_members or 0) + 1
end
function board:assign_squad_to_smart(squad, smart_id)
	assert(squad ~= nil and smart_id == nil)
	detached_squads = detached_squads + 1
end
function board:create_squad(spawn_smart, section)
	assert(spawn_smart == smart)
	local squad = life:create(section, spawn_smart.position,
		spawn_smart.m_level_vertex_id)
	squad:create_npc(spawn_smart)
	for member in squad:squad_members() do
		self:setup_squad_and_group(member.object)
		SendScriptCallback("squad_on_npc_creation", squad, member.object, spawn_smart)
	end
	return squad
end
function board:remove_squad(squad)
	for _, id in ipairs(squad._members or {}) do objects[id] = nil end
	objects[squad.id] = nil
end
sim_board = { get_sim_board = function() return board end }
simulation_objects = { is_on_the_same_level = function() return true end }
function TeleportSquad(squad, position, level_vertex_id, game_vertex_id)
	life:teleport_object(squad.id, game_vertex_id, level_vertex_id, position)
	for member in squad:squad_members() do
		life:teleport_object(member.id, game_vertex_id, level_vertex_id, position)
	end
	squad_spawn_position = point(position.x, position.y, position.z)
end
relation_registry = {
	community_relation = function(faction)
		return (faction == "bandit" or faction == "monolith") and -1000 or 0
	end,
	community_goodwill = function() return 0 end
}
function SendScriptCallback(name, _, member, spawn_smart)
	assert(name == "squad_on_npc_creation" and member ~= nil)
	assert(spawn_smart == smart)
	callback_members = callback_members + 1
end

-- Vanilla-compatible load_var variants may return nil instead of honoring the
-- supplied default when the pstor key has never existed. Anomaly spawning must
-- still initialize normally; F9 persistence is an optional enhancement.
local stored = arg[2] ~= nil and nil or ""
function save_var(_, _, value) stored = value end
function load_var(_, _, default_value)
	return stored
end
local initialized_shapes = {}
VEC_ZERO = point(0, 0, 0)
utils_stpk = {
	get_anom_zone_data = function(object)
		return initialized_shapes[object.id] or {}
	end,
	set_anom_zone_data = function(data, object)
		assert(data.object_flags == 31 and data.restrictor_type == 0)
		assert(#data.shapes == 1 and data.shapes[1].shtype == 0)
		assert(data.shapes[1].radius == 3 and data.shapes[1].offset ~= nil and
			data.shapes[1].center ~= nil)
		initialized_shapes[object.id] = data
	end
}
local global_time = 2000
function time_global() return global_time end

if arg[2] ~= nil then
	anomaly_donation_bridge_effects_direct_spawn = load_module(
		"anomaly_donation_bridge_effects_direct_spawn")
	local starter = {
		"spawn_mutants", "spawn_npcs", "spawn_anomaly"
	}
	for _, id in ipairs(starter) do
		load_effect(arg[2], "anomaly_donation_bridge_effect_" .. id).on_game_start()
	end
	local optional = {
		"spawn_npc_squads", "spawn_mutant_squads",
		"spawn_random_hostile_squad", "spawn_random_mutant_pack",
		"teleport_random", "spawn_random_npcs", "spawn_random_mutants"
	}
	for _, id in ipairs(optional) do
		load_effect(assert(arg[3]),
			"anomaly_donation_bridge_effect_" .. id).on_game_start()
	end
else
	local actions = load_module("anomaly_donation_bridge_actions_spawn")
	actions.register_all()
end
assert(#anomaly_donation_bridge_registry.ids() == (arg[2] ~= nil and 10 or 8))

local function dispatch(action_id, params)
	if arg[2]~=nil and (action_id=="spawn_mutants" or action_id=="spawn_npcs" or
		action_id=="spawn_random_mutants" or action_id=="spawn_random_npcs") then
		params.spawn_point_method=params.spawn_point_method or "legacy"
	end
	return anomaly_donation_bridge_registry.dispatch(
		action_id, { actor = actor, now = global_time }, params or {})
end

local normal_create = life.create
life.create = function() error("simulated create failure") end
local failed, failed_reason = dispatch("spawn_mutants", {
	section_id = "mutant_test", count = "1", catalog_revision = "rev"
})
assert(failed == "rejected" and failed_reason == "mutant_spawn_failed_rolled_back",
	tostring(failed) .. ":" .. tostring(failed_reason))
life.create = normal_create

assert(level.valid_vertex_id == nil)
local mutant_status, mutant_reason = dispatch("spawn_mutants", {
	section_id = "mutant_test", count = "1", catalog_revision = "rev"
})
assert(mutant_status == "executed" and
	mutant_reason == "spawned_mutants_1_groups_1",
	tostring(mutant_status) .. ":" .. tostring(mutant_reason))
local npc_status, npc_reason = dispatch("spawn_npcs", {
	section_id = "npc_test", count = "1", catalog_revision = "rev"
})
assert(npc_status == "executed" and
	npc_reason == "spawned_npcs_1_groups_1",
	tostring(npc_status) .. ":" .. tostring(npc_reason))

if arg[2] ~= nil then
	local named_direct_start = next_id
	local named_npc_status, named_npc_reason = dispatch("spawn_npcs", {
		section_id = "npc_test", count = "2", use_donor_name = "true",
		donor_name = "Донатёр", catalog_revision = "rev"
	})
	assert(named_npc_status == "executed" and
		named_npc_reason:find("_donor_named", 1, true) ~= nil,
		tostring(named_npc_status) .. ":" .. tostring(named_npc_reason))
	for id = named_direct_start, next_id - 1 do
		assert(objects[id].character_name_value ==
			anomaly_donation_bridge_common.game_text_cp1251("Донатёр"),
			"every directly spawned NPC must receive the donor name")
	end

	local original_random = math.random
	math.random = function(a, b)
		if b == nil and a == 2 then return 2 end
		return original_random(a, b)
	end
	local group_context = { actor = actor, now = 2000 }
	local group_handler = anomaly_donation_bridge_registry.get("spawn_mutants")
	assert(group_handler.can_execute(group_context, {
		groups = "mutant_test*any*3;mutant_other*any*2",
		random_group = "true", radius_meters = "40", catalog_revision = "rev",spawn_point_method="legacy"
	}) == true)
	math.random = original_random
	assert(group_context.options.total == 2 and group_context.options.radius == 40 and
		group_context.options.rows[1].section == "mutant_other")
	local group_status, group_detail = group_handler.execute(group_context)
	assert(group_status == "executed" and
		group_detail:find("spawned_mutants_2_groups_1_random_group", 1, true))
	local npc_random_status, npc_random_detail = dispatch("spawn_random_npcs", {
		npc_sections = "npc_test,npc_other", strength = "strong",
		minimum_count = "2", maximum_count = "2", radius_meters = "35",
		force_hostile = "true", use_donor_name = "true", donor_name = "Зритель",
		catalog_revision = "rev"
	})
	assert(npc_random_status == "executed" and
		npc_random_detail:find("spawned_npcs_2_groups_1", 1, true) and
		npc_random_detail:find("_donor_named", 1, true) and
		npc_random_detail:find("_random_npc_other_forced_hostile", 1, true))
	assert(objects[next_id - 1].character_name_value ==
		anomaly_donation_bridge_common.game_text_cp1251("Зритель") and
		objects[next_id - 2].character_name_value ==
		anomaly_donation_bridge_common.game_text_cp1251("Зритель"))
	local mutant_random_status, mutant_random_detail = dispatch("spawn_random_mutants", {
		mutant_sections = "mutant_test,mutant_other", strength = "normal",
		minimum_count = "1", maximum_count = "1", radius_meters = "30",
		force_hostile = "false", catalog_revision = "rev"
	})
	assert(mutant_random_status == "executed" and
		mutant_random_detail:find("_random_mutant_test_natural_hostility", 1, true))
end

local crowded_status, crowded_reason = dispatch("spawn_mutants", {
	section_id = "mutant_test", count = "2", catalog_revision = "rev"
})
assert(crowded_status == "executed" and
	crowded_reason:find("reused_nearby_vertex", 1, true) ~= nil,
	"multiple spawns may safely reuse the nearest navigation vertex")

local normal_vertex_in_direction = level.vertex_in_direction
level.vertex_in_direction = function() return 4294967295 end
local fallback_status, fallback_reason = dispatch("spawn_npcs", {
	section_id = "npc_test", count = "1", catalog_revision = "rev"
})
assert(fallback_status == "executed" and
	fallback_reason:find("actor_vertex_fallback", 1, true) ~= nil,
	"ordinary NPC spawn must fall back to the actor navigation vertex")
local random_fallback_status, random_fallback_reason = dispatch(
	"spawn_random_mutant_pack", {
		squad_sections = "pack_lite", strength = "lite", radius_meters = "25",
		force_hostile = "false", minimum_count = "1", maximum_count = "1",
		catalog_revision = "rev"
	})
assert(random_fallback_status == "executed" and
	random_fallback_reason:find("actor_vertex_fallback", 1, true) ~= nil,
	"random mutant spawn must use the same guaranteed fallback")
level.vertex_in_direction = normal_vertex_in_direction

local selected_npc_status, selected_npc_reason = dispatch("spawn_npc_squads", {
	groups = "enemy_strong*any*1", radius_meters = "25",
	force_hostile = "true", catalog_revision = "rev"
})
assert(selected_npc_status == "executed",
	tostring(selected_npc_status) .. ":" .. tostring(selected_npc_reason))
assert(selected_npc_reason:find("spawned_selected_stalker_squads_1", 1, true) ~= nil)
assert(selected_npc_reason:find("members_1_forced_hostile", 1, true) ~= nil)
assert(callback_members > 0 and initialized_members > 0,
	"selected squad must be created through the simulation board as a real squad entity")
assert(detached_squads > 0,
	"selected squad must be detached from the factory smart before it is teleported")

local selected_mutant_status, selected_mutant_reason = dispatch("spawn_mutant_squads", {
	groups = "pack_lite*strong*1", radius_meters = "25",
	force_hostile = "false", catalog_revision = "rev"
})
assert(selected_mutant_status == "executed" and
	selected_mutant_reason:find("spawned_selected_mutant_squads_1", 1, true) ~= nil,
	"legacy strength tokens must be accepted and ignored")

actor_vertex = 4294967295
local invalid_vertex, invalid_vertex_reason = dispatch("spawn_mutants", {
	section_id = "mutant_test", count = "1", catalog_revision = "rev"
})
assert(invalid_vertex == "deferred" and invalid_vertex_reason == "actor_vertex_invalid")
actor_vertex = 1

local pack_status, pack_reason = dispatch("spawn_random_mutant_pack", {
	squad_sections = "pack_strong",
	radius_meters = "25", force_hostile = "false",
	minimum_count = "1", maximum_count = "1",
	catalog_revision = "rev"
})
assert(pack_status == "executed", tostring(pack_status) .. ":" .. tostring(pack_reason))
assert(pack_reason:find("_pack_strong_", 1, true) ~= nil, tostring(pack_reason))
assert(squad_spawn_position ~= nil and squad_spawn_position.x == 20)

local hostile_status, hostile_reason = dispatch("spawn_random_hostile_squad", {
	squad_sections = "enemy_strong",
	radius_meters = "25", force_hostile = "true",
	minimum_count = "1", maximum_count = "1",
	catalog_revision = "rev"
})
assert(hostile_status == "executed" and
	hostile_reason:find("_enemy_strong_", 1, true) ~= nil)
assert(hostile_reason:find("near_actor_20m", 1, true) ~= nil)
assert(hostile_reason:find("forced_hostile", 1, true) ~= nil)
assert(squad_spawn_position ~= nil and squad_spawn_position.x == 20)
assert(strongest_goodwill == -5000 and offline_actor_target == true)
local calls_before_retry = hostility_calls
level.object_by_id = function(id) return objects[id] end
anomaly_donation_bridge_registry.get("spawn_random_hostile_squad").update(3000)
assert(hostility_calls > calls_before_retry,
	"forced hostility must be refreshed while squad members switch online")
local bad_strength, bad_strength_reason = dispatch("spawn_random_mutant_pack", {
	squad_sections = "pack_lite", strength = "impossible",
	radius_meters = "25", force_hostile = "false",
	minimum_count = "1", maximum_count = "1",
	catalog_revision = "rev"
})
assert(bad_strength == "executed",
	"obsolete strength parameter must be accepted and ignored: " .. tostring(bad_strength_reason))
local bad_radius, bad_radius_reason = dispatch("spawn_random_mutant_pack", {
	squad_sections = "pack_lite", strength = "lite", radius_meters = "101",
	force_hostile = "false", minimum_count = "1", maximum_count = "1",
	catalog_revision = "rev"
})
assert(bad_radius == "rejected" and bad_radius_reason == "squad_radius_invalid")
local bad_hostility, bad_hostility_reason = dispatch("spawn_random_mutant_pack", {
	squad_sections = "pack_lite", strength = "lite", radius_meters = "25",
	force_hostile = "sometimes", minimum_count = "1", maximum_count = "1",
	catalog_revision = "rev"
})
assert(bad_hostility == "rejected" and
	bad_hostility_reason == "force_hostile_invalid")

local normal_anomaly_vertex_in_direction = level.vertex_in_direction
local normal_anomaly_vertex_position = level.vertex_position
local ring_vertex_positions, next_ring_vertex = {}, 100
level.vertex_in_direction = function(start_vertex, direction, radius)
	assert(start_vertex == 1 and radius >= 0 and radius <= 100)
	local id = next_ring_vertex
	next_ring_vertex = next_ring_vertex + 1
	ring_vertex_positions[id] = point(direction.x * radius, 0, direction.z * radius)
	return id
end
level.vertex_position = function(id)
	return ring_vertex_positions[id] or normal_anomaly_vertex_position(id)
end

local before_anomaly_save
if arg[2] ~= nil then
	-- Simulate the automatic save made before the effect. Its actor pstor must
	-- contain an explicitly empty anomaly snapshot.
	anomaly_donation_bridge_registry.update(global_time)
	assert(stored == "2|", "the pre-effect save must contain no anomalies")
	before_anomaly_save = stored
	local long_lived_context = { actor = actor, now = global_time }
	local long_lived_handler = anomaly_donation_bridge_registry.get("spawn_anomaly")
	assert(long_lived_handler.can_execute(long_lived_context, {
		section_id = "zone_test", distance_meters = "5", count = "1",
		lifetime_seconds = "60000", catalog_revision = "rev"
	}) == true and long_lived_context.options.lifetime == 60000,
		"temporary anomalies must accept a 60000-second lifetime")
	local too_long_context = { actor = actor, now = global_time }
	local allowed, status, reason = long_lived_handler.can_execute(too_long_context, {
		section_id = "zone_test", distance_meters = "5", count = "1",
		lifetime_seconds = "60001", catalog_revision = "rev"
	})
	assert(allowed == false and status == "rejected" and
		reason == "anomaly_lifetime_invalid",
		"temporary anomalies must still reject lifetimes above 60000 seconds")
end

next_id = 55
local ring_status, ring_detail = dispatch("spawn_anomaly", {
	section_id = "zone_test", distance_meters = "5", count = "4",
	lifetime_seconds = "60", catalog_revision = "rev"
})
assert(ring_status == "executed" and
	ring_detail == "temporary_anomaly_ring_count_4_radius_5m_60s",
	tostring(ring_status) .. ":" .. tostring(ring_detail))
for id = 55, 58 do
	assert(stored:find(tostring(id) .. "|zone_test", 1, true))
	assert(initialized_shapes[id] ~= nil and initialized_shapes[id].shapes[1].radius == 3)
	local position = assert(objects[id]).position
	assert(math.abs(math.sqrt(position.x * position.x + position.z * position.z) - 5) < 0.001)
end
assert(math.abs(objects[55].position.x - 5) < 0.001 and
	math.abs(objects[56].position.z - 5) < 0.001 and
	math.abs(objects[57].position.x + 5) < 0.001 and
	math.abs(objects[58].position.z + 5) < 0.001,
	"four anomalies must form a square around the actor")

anomaly_random_call = 0
assert(select(1, dispatch("spawn_anomaly", {
	section_id = "__adb_random_anomaly__", distance_meters = "5", count = "3",
	lifetime_seconds = "60", catalog_revision = "rev"
})) == "executed")
assert(anomaly_random_call == 1)
for id = 59, 61 do assert(objects[id]._section == "zone_test") end

assert(select(1, dispatch("spawn_anomaly", {
	section_id = "__adb_each_random_anomaly__", distance_meters = "5", count = "3",
	lifetime_seconds = "60",
	catalog_revision = "rev"
})) == "executed")
assert(anomaly_random_call == 4 and objects[62]._section == "zone_fire" and
	objects[63]._section == "zone_acid" and objects[64]._section == "zone_test")
assert(select(1, dispatch("spawn_anomaly", {
	section_id = "zone_test", distance_meters = "7", count = "1",
	lifetime_seconds = "60", catalog_revision = "rev"
})) == "executed")
assert(math.abs(objects[65].position.x) < 0.001 and
	math.abs(objects[65].position.z - 7) < 0.001,
	"one anomaly must spawn in front of the actor")
if arg[2] ~= nil then
	local ring_vertex_in_direction = level.vertex_in_direction
	level.vertex_in_direction = function() return actor_vertex end
	local fallback_ring_status = dispatch("spawn_anomaly", {
		section_id = "zone_test", distance_meters = "10", count = "4",
		lifetime_seconds = "60", catalog_revision = "rev"
	})
	assert(fallback_ring_status == "executed",
		"a ring must still spawn when navigation returns the actor vertex")
	for id = 66, 69 do
		local position = assert(objects[id]).position
		assert(math.abs(math.sqrt(position.x * position.x +
			position.z * position.z) - 10) < 0.001,
			"fallback ring points must keep the requested radius")
	end
	level.vertex_in_direction = ring_vertex_in_direction
end
local too_many, too_many_reason = dispatch("spawn_anomaly", {
	section_id = "zone_test", distance_meters = "5", count = "101",
	lifetime_seconds = "60", catalog_revision = "rev"
})
assert(too_many == "rejected" and too_many_reason == "anomaly_count_invalid")

next_id = 70
local normal_anomaly_create = alife_create
local anomaly_create_call = 0
function alife_create(section, position, level_vertex_id, game_vertex_id)
	anomaly_create_call = anomaly_create_call + 1
	if anomaly_create_call == 3 then return nil end
	return normal_anomaly_create(section, position, level_vertex_id, game_vertex_id)
end
local batch_failed, batch_reason = dispatch("spawn_anomaly", {
	section_id = "zone_test", distance_meters = "5", count = "4",
	lifetime_seconds = "60", catalog_revision = "rev"
})
assert(batch_failed == "rejected" and
	batch_reason == "anomaly_spawn_failed_rolled_back")
assert(released[70] == true and released[71] == true and objects[70] == nil and
	objects[71] == nil and stored:find("55|zone_test", 1, true) and
	not stored:find("70|zone_test", 1, true))
alife_create = normal_anomaly_create
local after_anomaly_save = stored
anomaly_donation_bridge_registry.on_level_changing()
for id = 55, (arg[2] ~= nil and 69 or 65) do assert(released[id] == true) end
if arg[2] ~= nil then
	assert(after_anomaly_save:sub(1, 2) == "2|" and
		after_anomaly_save:find("|l01_escape", 1, true),
		"temporary anomaly records must retain their level and deadline")

	-- Loading BEFORE must use only that save's actor pstor. It must not inherit
	-- the anomalies that existed later in the same game process.
	stored = before_anomaly_save
	global_time = 4000
	anomaly_donation_bridge_registry.on_game_load()
	global_time = 6000
	anomaly_donation_bridge_registry.update(global_time)
	for _, object in pairs(objects) do
		assert(object._section ~= "zone_test" and object._section ~= "zone_fire" and
			object._section ~= "zone_acid",
			"the pre-effect save must not restore later temporary anomalies")
	end
	assert(stored == "2|", "the pre-effect save must remain anomaly-free")

	-- Loading AFTER must recreate its own anomaly set and continue the saved
	-- remaining lifetime.
	stored = after_anomaly_save
	global_time = 7000
	anomaly_donation_bridge_registry.on_game_load()
	global_time = 9000
	anomaly_donation_bridge_registry.update(global_time)
	local restored_ids = {}
	for id, object in pairs(objects) do
		if object._section == "zone_test" or object._section == "zone_fire" or
			object._section == "zone_acid" then restored_ids[#restored_ids + 1] = id end
	end
	assert(#restored_ids == 15,
		"the post-effect save must recreate all active temporary anomalies")
	global_time = 68999
	anomaly_donation_bridge_registry.update(global_time)
	for _, id in ipairs(restored_ids) do assert(objects[id] ~= nil) end
	global_time = 69000
	anomaly_donation_bridge_registry.update(global_time)
	for _, id in ipairs(restored_ids) do assert(objects[id] == nil and released[id] == true) end
	assert(stored == "2|", "expired restored anomalies must clear persisted tracking")
	global_time = 70000
	assert(select(1, dispatch("spawn_anomaly", {
		section_id = "zone_test", distance_meters = "5", count = "1",
		lifetime_seconds = "60", catalog_revision = "rev"
	})) == "executed")
	anomaly_donation_bridge_registry.on_level_changing()
	current_level_name = "l02_garbage"
	global_time = 71000
	anomaly_donation_bridge_registry.on_game_load()
	global_time = 73000
	anomaly_donation_bridge_registry.update(global_time)
	for _, object in pairs(objects) do
		assert(object._section ~= "zone_test" and object._section ~= "zone_fire" and
			object._section ~= "zone_acid",
			"temporary anomalies must not migrate to another level")
	end
	assert(stored == "2|", "cross-level load must clear temporary anomaly tracking")
	current_level_name = "l01_escape"
else
	assert(stored == "")
end
level.vertex_in_direction = normal_anomaly_vertex_in_direction
level.vertex_position = normal_anomaly_vertex_position

local normal_random = math.random
local normal_teleport_vertex_in_direction = level.vertex_in_direction
local normal_teleport_vertex_position = level.vertex_position
local requested_navigation_radii = {}
local teleport_vertex_count = 2000
level.vertex_in_direction = function(start_vertex, direction, radius)
	requested_navigation_radii[#requested_navigation_radii + 1] = radius
	assert(radius == 0, "random teleport must sample actual level vertices directly")
	return start_vertex >= 0 and start_vertex < teleport_vertex_count and
		start_vertex or 4294967295
end
level.vertex_position = function(id)
	assert(id >= 0 and id < teleport_vertex_count, "invalid teleport test vertex")
	return point(id, 15, 0)
end
local random_values, random_call = { 0.371, 0.742, 0.15 }, 0
math.random = function()
	random_call = random_call + 1
	return random_values[random_call] or 0.15
end
local teleport_status, teleport_reason = dispatch("teleport_random", {
	minimum_meters = "500", maximum_meters = "1000"
})
assert(teleport_status == "executed" and teleport_reason == "teleported_randomly_742m",
	tostring(teleport_status) .. ":" .. tostring(teleport_reason))
assert(actor_vertex == 1 and math.abs(actor_position.x - 742) < 0.001 and
	math.abs(actor_position.y - 15.10) < 0.001,
	"long teleport must select a real vertex from the complete level AI grid")
local repeated_status, repeated_reason = dispatch("teleport_random", {
	minimum_meters = "500", maximum_meters = "1000"
})
assert(repeated_status == "executed" and repeated_reason == "teleported_randomly_742m",
	tostring(repeated_status) .. ":" .. tostring(repeated_reason))
assert(math.abs(actor_position.x - 1484) < 0.001,
	"repeated teleport must sample the full level instead of the nearest ray")
assert(#requested_navigation_radii > 2 and #requested_navigation_radii < 100,
	"level vertex count discovery must remain logarithmic and cached")
for _, radius in ipairs(requested_navigation_radii) do
	assert(radius == 0, "vertex probes must not perform movement rays")
end
actor_position = point(0, 0, 0)
level.vertex_position = function(id)
	assert(id >= 0 and id < teleport_vertex_count, "invalid teleport fallback vertex")
	return point(id % 400, 15, 0)
end
local fallback_status, fallback_reason = dispatch("teleport_random", {
	minimum_meters = "500", maximum_meters = "1000"
})
assert(fallback_status == "executed" and actor_position.x > 300 and
	actor_position.x < 500 and fallback_reason:find("teleported_randomly_", 1, true),
	"a small level must use its closest real vertex instead of rejecting the effect")
math.random = normal_random
level.vertex_in_direction = normal_teleport_vertex_in_direction
level.vertex_position = normal_teleport_vertex_position

anomaly_donation_bridge_catalog.resolve_squad_members = function()
	return nil, "spawn_section_unsafe"
end
local unsupported_squad, unsupported_squad_reason = dispatch("spawn_random_mutant_pack", {
	squad_sections = "pack_lite", strength = "lite", radius_meters = "25",
	force_hostile = "false", catalog_revision = "rev"
})
assert(unsupported_squad == "rejected" and
	unsupported_squad_reason == "spawn_section_unsafe")

print("spawn action smoke passed")
