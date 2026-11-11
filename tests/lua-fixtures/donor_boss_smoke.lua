local core_root = assert(arg[1], "pass core scripts as arg[1]")
local optional_root = assert(arg[2], "pass optional scripts as arg[2]")
local separator = package.config:sub(1, 1)

local function load_module(root, name)
	local chunk = assert(loadfile(root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

local function make_vector()
	return {
		set = function(self, x, y, z)
			self.x, self.y, self.z = x, y, z
			return self
		end
	}
end

-- Luabind exposes vector as a callable object on the real engine rather than
-- a plain Lua function. Keep the smoke fixture faithful to that API shape.
vector = setmetatable({}, { __call = function() return make_vector() end })

local actor = { paid = 0 }
function actor:id() return 0 end
function actor:character_community() return "actor_stalker" end
function actor:get_task() return nil end
function actor:level_vertex_id() return 10 end
function actor:game_vertex_id() return 20 end
function actor:position() return self.current_position or vector():set(0, 0, 0) end
function actor:give_money(amount) self.paid = self.paid + amount end
db = { actor = actor }

local saved = {}
function save_var(_, key, value) saved[key] = value end
function load_var(_, key) return saved[key] end
function se_save_var() end
local function boss_save_key(id)
	return "adb_donor_boss_state_v2_" .. tostring(id):sub(-2)
end

task_functor = {}
task_status_functor = {}
xr_effects = {}

local manager = { task_info = {} }
function manager:give_task(id, giver_id)
	local title = task_functor.adb_donor_boss_text(id, "title")
	assert(type(title) == "string" and title:find("BossDonor", 1, true))
	assert(task_functor.adb_donor_boss_target(id) == giver_id)
	self.task_info[id] = { giver_id = giver_id }
end
task_manager = { get_task_manager = function() return manager end }

local callbacks = {}
function RegisterScriptCallback(name, callback)
	callbacks[name] = callbacks[name] or {}
	table.insert(callbacks[name], callback)
end

game_object = { enemy = 2 }
game_relations = { set_npcs_relation = function(npc, target, relation)
	assert(target == actor and relation == "enemy")
	npc.relation_applied = true
end }

local client = {
	community = "bandit", object_id = 101, is_stalker = true,
	current_position = vector():set(300, 0, 0), level_vertex = 777,
	game_vertex = 20
}
local stalker_set_enemy_calls = 0
function client:id() return self.object_id end
function client:position() return self.current_position end
function client:level_vertex_id() return self.level_vertex end
function client:game_vertex_id() return self.game_vertex end
function client:set_character_community(value) self.community = value end
function client:force_set_goodwill(value, target)
	assert(value == -5000 and target == actor)
	self.goodwill_applied = true
end
function client:set_relation(value, target)
	assert(value == game_object.enemy and target == actor)
	self.enemy_relation_applied = true
end
function client:enable_memory_object(target, enabled)
	assert(target == actor and enabled == true)
	self.actor_remembered = true
end
function client:make_object_visible_somewhen(target)
	assert(target == actor)
	self.actor_visible = true
	-- Stock fictitious visual memory fills position only for a NEW record.
	if self.last_actor_position == nil then
		local position = target:position()
		self.last_actor_position = vector():set(position.x, position.y, position.z)
	end
end
function client:see(target)
	assert(target == actor)
	return self.sees_actor == true
end
function client:memory_remove_links(target)
	assert(target == actor)
	self.memory_refresh_count = (rawget(self, "memory_refresh_count") or 0) + 1
	self.last_actor_position = nil
end
function client:set_enemy(target)
	assert(target == actor)
	stalker_set_enemy_calls = stalker_set_enemy_calls + 1
end

function IsStalker(object, class_id)
	if object ~= nil then return object.is_stalker == true end
	return class_id == 12345
end

local current_client = client
local respawn_vertex_positions = {
	[778] = vector():set(321, 4, 5),
	[779] = vector():set(323, 4, 5)
}
level = {
	object_by_id = function(id)
		return current_client ~= nil and id == current_client:id() and current_client or nil
	end,
	vertex_id = function(position)
		if position ~= nil and position.x == 321 and position.y == 4 and
			position.z == 5 then return 778 end
		return 4294967295
	end,
	vertex_position = function(id)
		return assert(respawn_vertex_positions[id],
			"invalid respawn vertex " .. tostring(id))
	end,
	vertex_in_direction = function(id, _, distance)
		if id == 778 and distance >= 0.5 and distance <= 3 then return 779 end
		return 4294967295
	end
}

local server = {
	id = 101, alive_value = true, object_name = "donor_boss_101",
	section = "sim_default_stalker_1", class_id = 12345
}
function server:set_character_name(value) self.character_name = value end
function server:force_set_goodwill(value) assert(value == -5000) end
function server:name() return self.object_name end
function server:alive() return self.alive_value end
function server:section_name() return self.section end
function server:clsid() return self.class_id end

local current_server = server
local next_spawn_server = nil
local next_spawn_client = nil
local expected_spawn_x = 300
local expected_level_vertex = 777
local expected_game_vertex = 20

local life = { forced_online = false, released = false, release_calls = 0,
	switch_calls = 0, keep_released_visible = false }
function life:object(id)
	return current_server ~= nil and id == current_server.id and
		(not self.released or self.keep_released_visible) and current_server or nil
end
function life:set_switch_online(id, value)
	assert(current_server ~= nil and id == current_server.id)
	self.switch_calls = self.switch_calls + 1
	self.forced_online = value
end
function life:set_switch_offline(id, value)
	assert(current_server ~= nil and id == current_server.id and value == false)
end
function life:release(object)
	assert(object == current_server)
	self.release_calls = self.release_calls + 1
	self.released = true
end
function alife() return life end
function alife_create(section, position, level_vertex, game_vertex)
	assert(section == "sim_default_stalker_1")
	assert(position.x == expected_spawn_x and level_vertex == expected_level_vertex and
		game_vertex == expected_game_vertex)
	local result = next_spawn_server or server
	current_server = result
	current_client = next_spawn_client or client
	next_spawn_server, next_spawn_client = nil, nil
	life.released = false
	return result
end

anomaly_donation_bridge_catalog = {
	capable = function() return true end,
	validate_revision = function(params)
		return params.catalog_revision == "catalog-1", "catalog_revision_mismatch"
	end,
	validate_sections = function(params, key, kind)
		assert(kind == "npc" and key == "npc_sections" and
			params.npc_sections == "sim_default_stalker_1")
		return { { section_id = "sim_default_stalker_1" } }
	end,
	is_spawn_safe = function(section, kind)
		return section == "sim_default_stalker_1" and kind == "npc"
	end
}

anomaly_donation_bridge_common = load_module(core_root,
	"anomaly_donation_bridge_common")
local live_level = level
local vertex_positions = {
	[0] = vector():set(0, 0, 0),
	[1] = vector():set(100, 0, 0),
	[2] = vector():set(250, 0, 0),
	[3] = vector():set(450, 0, 0),
	[4] = vector():set(700, 0, 0)
}
level = {
	name = function() return "tiny_test_level" end,
	vertex_position = function(id)
		assert(vertex_positions[id], "invalid vertex " .. tostring(id))
		return vertex_positions[id]
	end,
	vertex_in_direction = function(id, _, distance)
		if distance == 0 and vertex_positions[id] then return id end
		return 4294967295
	end
}
local probe_actor = {
	level_vertex_id = function() return 0 end,
	position = function() return vector():set(0, 0, 0) end
}
local farthest = assert(anomaly_donation_bridge_common.random_safe_point(
	probe_actor, 899, 999, 6000, math.huge))
assert(farthest.distance == 700 and farthest.vertex_id == 4,
	"small levels must use their farthest safe vertex")
level = live_level
anomaly_donation_bridge_common.random_safe_point = function(_, minimum, maximum,
	attempts, height_delta)
	assert(minimum == 270 and maximum == 300 and attempts == 2400 and
		height_delta == math.huge)
	return { position = vector():set(300, 0, 0), vertex_id = 777, distance = 300 }
end
anomaly_donation_bridge_registry = load_module(core_root,
	"anomaly_donation_bridge_registry")

local previous_hit_calls = 0
CAI_Stalker__BeforeHitCallback = function()
	previous_hit_calls = previous_hit_calls + 1
	return true
end

local effect = load_module(optional_root,
	"anomaly_donation_bridge_effect_spawn_donor_boss")
effect.on_game_start()
local descriptor = assert(anomaly_donation_bridge_registry.get("spawn_donor_boss"))

local context = { actor = actor }
local allowed, disposition, reason = descriptor.can_execute(context, {
	npc_sections = "sim_default_stalker_1",
	reward_money = "25000", health_percent = "999", radius_meters = "300",
	donor_name = "BossDonor", catalog_revision = "catalog-1"
})
assert(allowed == true and disposition == nil and reason == nil)
local status, detail = descriptor.execute(context)
assert(status == "executed" and detail:find("spawned_donor_boss_101", 1, true),
	tostring(status) .. ":" .. tostring(detail))
assert(server.character_name == "BossDonor")
assert(life.forced_online == true)
assert(client.community == "arena_enemy" and client.relation_applied and
	client.enemy_relation_applied and client.actor_remembered and
	client.actor_visible and stalker_set_enemy_calls == 0)
local task_id = "adb_donor_boss_01"
local state_key = boss_save_key(task_id)
local description = task_functor.adb_donor_boss_text(task_id, "descr")
local expected_description = anomaly_donation_bridge_common.game_text_cp1251(
	"Он идёт за мной, нужно срочно уносить ноги или дать бой.")
assert(description == expected_description, description)

assert(saved[state_key].target_id == 101 and
	saved[state_key].reward_money == 25000 and
	saved[state_key].health_percent == 999 and
	saved[state_key].object_name == "donor_boss_101")
assert(saved[state_key].always_follow == true, "follow must default to enabled")
assert(task_status_functor.adb_donor_boss_status(nil, task_id) == nil)

-- Invisible actor: replace the stale native memory record once a second.
local refreshes = client.memory_refresh_count
actor.current_position = vector():set(80, 0, 10)
descriptor.update(500)
assert(client.memory_refresh_count == refreshes and client.last_actor_position.x == 0)
descriptor.update(1000)
assert(client.memory_refresh_count == refreshes + 1 and client.last_actor_position.x == 80)
actor.current_position = vector():set(140, 0, 20)
descriptor.update(1500)
assert(client.memory_refresh_count == refreshes + 1 and client.last_actor_position.x == 80)
client.sees_actor = true
descriptor.update(2000)
assert(client.memory_refresh_count == refreshes + 1, "live combat vision must not be cleared")
client.sees_actor = false
descriptor.update(2500)
assert(client.memory_refresh_count == refreshes + 1)
descriptor.update(3000)
assert(client.memory_refresh_count == refreshes + 2 and client.last_actor_position.x == 140)

-- Compatible engines without this optional method retain the old behavior.
local remove_memory = client.memory_remove_links
client.memory_remove_links = false
descriptor.update(4000)
client.memory_remove_links = remove_memory

-- F9/runtime rebuild refreshes immediately and keeps the saved checkbox.
actor.current_position = vector():set(220, 0, 30)
effect.on_game_start()
descriptor.update(4500)
assert(saved[state_key].always_follow == true and client.last_actor_position.x == 220)
actor.current_position = nil

local invalid_follow, invalid_follow_state, invalid_follow_reason = descriptor.can_execute({actor = actor}, {
	npc_sections = "sim_default_stalker_1", donor_name = "BossDonor",
	catalog_revision = "catalog-1", always_follow = "invalid"
})
assert(invalid_follow == false and invalid_follow_state == "rejected" and
	invalid_follow_reason == "donor_boss_follow_invalid")

local hit = { power = 9.99 }
assert(CAI_Stalker__BeforeHitCallback(client, hit, 0) == true)
assert(math.abs(hit.power - 1) < 0.0001 and previous_hit_calls == 1)

local enemy_flags = { override = false, result = false }
for _, callback in ipairs(callbacks.on_enemy_eval or {}) do
	callback(client, actor, enemy_flags)
end
assert(enemy_flags.override == true and enemy_flags.result == true)
local other_enemy = { id = function() return 404 end }
enemy_flags = { override = false, result = true }
for _, callback in ipairs(callbacks.on_enemy_eval or {}) do
	callback(client, other_enemy, enemy_flags)
end
assert(enemy_flags.override == true and enemy_flags.result == false)

server.alive_value = false
for _, callback in ipairs(callbacks.npc_on_death_callback or {}) do callback(client, actor) end
assert(task_status_functor.adb_donor_boss_status(nil, task_id) == "complete")
xr_effects.adb_donor_boss_complete(actor, actor, { task_id })
assert(actor.paid == 25000 and life.forced_online == false)

-- A-Life may reuse the numeric ID of a finished boss for an unrelated mutant.
-- An old task must not call stalker-only methods, scale damage, or delete it.
local bloodsucker_target_calls = 0
local bloodsucker = { id = function() return 101 end }
function bloodsucker:set_enemy()
	bloodsucker_target_calls = bloodsucker_target_calls + 1
end
local bloodsucker_server = { id = 101 }
function bloodsucker_server:name() return "bloodsucker_101" end
function bloodsucker_server:alive() return true end
function bloodsucker_server:section_name() return "bloodsucker_normal" end
function bloodsucker_server:clsid() return 54321 end
current_client = bloodsucker
current_server = bloodsucker_server
life.released = false
saved.adb_donor_boss_02 = {
	target_id = 101, donor_name = "OldBoss", reward_money = 100,
	health_percent = 3000, community = "arena_enemy",
	section = "sim_default_stalker_1", dead = false
}
effect.on_game_start()
local switch_calls_before = life.switch_calls
descriptor.update(1000)
descriptor.update(1600)
assert(bloodsucker_target_calls == 0 and life.switch_calls == switch_calls_before)
assert(saved.adb_donor_boss_02.invalid_reason ==
	"donor_boss_target_identity_changed" or
	saved[boss_save_key("adb_donor_boss_02")].invalid_reason ==
	"donor_boss_target_identity_changed")
assert(task_status_functor.adb_donor_boss_status(nil,
	"adb_donor_boss_02") == "fail")
local release_calls_before = life.release_calls
xr_effects.adb_donor_boss_cleanup(actor, actor, { "adb_donor_boss_02" })
assert(life.release_calls == release_calls_before and life.released == false)

-- Multiple lives survive a script/runtime rebuild. The first death completes
-- the quest and pays the reward, while an intermediate corpse is removed
-- before the replacement is created on a nearby accessible AI vertex.
manager.task_info[task_id] = nil
local life_one_client = setmetatable({
	object_id = 201, is_stalker = true, community = "bandit",
	current_position = vector():set(321, 4, 5), level_vertex = 778,
	game_vertex = 20
}, { __index = client })
local life_one_server = setmetatable({
	id = 201, alive_value = true, object_name = "donor_boss_201",
	section = "sim_default_stalker_1", class_id = 12345
}, { __index = server })
next_spawn_server, next_spawn_client = life_one_server, life_one_client
expected_spawn_x, expected_level_vertex, expected_game_vertex = 300, 777, 20
local multiple_context = { actor = actor }
local multiple_allowed = descriptor.can_execute(multiple_context, {
	npc_sections = "sim_default_stalker_1", reward_money = "50000",
	health_percent = "1000000", lives = "2", radius_meters = "300",
	always_follow = "false",
	donor_name = "BossDonor", catalog_revision = "catalog-1"
})
assert(multiple_allowed == true)
local multiple_status, multiple_detail = descriptor.execute(multiple_context)
assert(multiple_status == "executed" and
	multiple_detail:find("_lives_2", 1, true), tostring(multiple_detail))
assert(saved[state_key].total_lives == 2 and
	saved[state_key].lives_remaining == 2 and saved[state_key].always_follow == false)

local life_two_client = setmetatable({
	object_id = 202, is_stalker = true, community = "bandit",
	current_position = vector():set(321, 4, 5), level_vertex = 778,
	game_vertex = 20
}, { __index = client })
local life_two_server = setmetatable({
	id = 202, alive_value = true, object_name = "donor_boss_202",
	section = "sim_default_stalker_1", class_id = 12345
}, { __index = server })
life_one_server.alive_value = false
next_spawn_server, next_spawn_client = life_two_server, life_two_client
for _, callback in ipairs(callbacks.npc_on_death_callback or {}) do
	callback(life_one_client, actor)
end
assert(saved[state_key].quest_complete == true and
	saved[state_key].respawn_pending == true and
	saved[state_key].lives_remaining == 1 and life.released == false)
assert(task_status_functor.adb_donor_boss_status(nil, task_id) == "complete")
xr_effects.adb_donor_boss_complete(actor, actor, { task_id })
-- This is what the stock task_manager does immediately after on_complete.
-- Boss lives must not share that task-owned key.
saved[task_id] = nil
assert(actor.paid == 75000 and saved[state_key].reward_granted == true)

effect.on_game_start()
assert(saved[state_key].always_follow == false, "load reset the disabled checkbox")
expected_spawn_x, expected_level_vertex, expected_game_vertex = 323, 779, 20
-- Some compatible engine builds keep returning the released corpse from
-- alife():object for several updates. That stale handle must not block respawn.
life.keep_released_visible = true
descriptor.update(5000)
life.keep_released_visible = false
assert(saved[state_key].target_id == 202 and
	saved[state_key].respawn_pending == false and
	saved[state_key].current_dead == false and life.released == false)
assert(saved[state_key].always_follow == false and
	rawget(life_two_client, "memory_refresh_count") == nil,
	"respawn enabled following despite the disabled checkbox")
local durable_hit = { power = 10000 }
assert(CAI_Stalker__BeforeHitCallback(life_two_client, durable_hit, 0) == true)
assert(math.abs(durable_hit.power - 1) < 0.0001)
assert(life.forced_online == true)

life_two_server.alive_value = false
local final_release_calls = life.release_calls
for _, callback in ipairs(callbacks.npc_on_death_callback or {}) do
	callback(life_two_client, actor)
end
assert(saved[state_key].finished == true and saved[state_key].dead == true and
	saved[state_key].lives_remaining == 0 and
	saved[state_key].respawn_pending == false)
assert(life.release_calls == final_release_calls and life.released == false,
	"the final corpse must remain")

print("donor boss smoke passed")
