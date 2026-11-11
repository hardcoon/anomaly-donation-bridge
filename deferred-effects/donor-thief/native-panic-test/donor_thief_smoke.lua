local core_root = assert(arg[1], "pass core scripts as arg[1]")
local optional_root = assert(arg[2], "pass optional scripts as arg[2]")
local separator = package.config:sub(1, 1)
local logs = {}
function printf(format, ...)
	logs[#logs + 1] = string.format(format, ...)
end

local function load_module(root, name)
	local chunk = assert(loadfile(root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

function vector()
	return {
		set = function(self, x, y, z)
			self.x, self.y, self.z = x, y, z
			return self
		end
	}
end

local function item(id, section)
	return {
		id = function() return id end,
		section = function() return section end,
		name = function() return section .. id end,
		condition = .87, ammo = 24
	}
end

local weapon = item(1, "wpn_pm")
local medkit = item(2, "medkit")
local ammo = item(3, "ammo_9x18_fmj")
local artifact = item(4, "af_medusa")
local quest = item(5, "quest_case")
local outfit = item(6, "stalker_outfit")
local helmet = item(7, "helm_resp")
local holstered_weapon = item(8, "wpn_ak74")
local belt_artefact = item(9, "af_belt")
local detector = item(10, "detector_simple")
local backpack = item(11, "backpack_equipped")
local spare_outfit = item(12, "stalker_outfit")
local equipped = { [1] = true, [6] = true, [7] = true, [8] = true,
	[9] = true, [10] = true, [11] = true }
local actor_inventory = { weapon, medkit, ammo, artifact, quest, outfit,
	helmet, holstered_weapon, belt_artefact, detector, backpack, spare_outfit }
local dropped = {}

local task_manager_instance
local actor = {}
function actor:id() return 0 end
function actor:position() return vector():set(0, 0, 0) end
function actor:direction() return vector():set(1, 0, 0) end
function actor:level_vertex_id() return 10 end
function actor:game_vertex_id() return 20 end
function actor:character_community() return "actor_stalker" end
function actor:get_task(id) return task_manager_instance and task_manager_instance.task_info[id] end
function actor:active_slot() return 2 end
function actor:active_item() return weapon end
function actor:character_reputation() return self.reputation or 150 end
function actor:set_character_reputation() error("never reset actor reputation globally") end
function actor:iterate_ruck(callback, owner)
	local snapshot = {}
	for _, value in ipairs(actor_inventory) do
		if not equipped[value:id()] then snapshot[#snapshot + 1] = value end
	end
	for _, value in ipairs(snapshot) do callback(owner, value) end
end
function actor:iterate_inventory(callback, owner)
	local snapshot = {}
	for index, value in ipairs(actor_inventory) do snapshot[index] = value end
	for _, value in ipairs(snapshot) do callback(owner, value) end
end
local function remove_item(target)
	for index, value in ipairs(actor_inventory) do
		if value == target then table.remove(actor_inventory, index) return true end
	end
	return false
end
function actor:drop_item(target)
	assert(remove_item(target))
	dropped[#dropped + 1] = target
end
function actor:transfer_item(target, recipient)
	assert(remove_item(target))
	recipient.inventory[#recipient.inventory + 1] = target
end
AC_ID = 0
db = {
	actor = actor,
	storage = {
		[AC_ID] = {},
		[101] = {
			abuse = { abuse_manager = {} },
			corpse_detection = {
				selected_corpse_id = 999,
				nearest_id = 999,
				corpse_detection_enabled = "true",
				mutant_corpse_analysis = "true"
			},
			gather_items = {
				selected_id = 998,
				gather_items_enabled = "true",
				gather_artefact_items_enabled = "true"
			}
		}
	}
}
xr_logic = {
	parse_condlist = function(_, _, field, source)
		assert(type(field) == "string" and source == "false")
		return { disabled = true, field = field }
	end
}

local saved = {}
local item_vars = {}
function save_var(obj, key, value)
    assert(obj:id()==0,'items have no online pstor storage');saved[key]=value
end
function load_var(obj, key)
    assert(obj:id()==0,'items have no online pstor storage');return saved[key]
end
function se_save_var(id,name,key,value)item_vars[id]=item_vars[id] or {};item_vars[id][key]=value end
function se_load_var(id,name,key)return item_vars[id] and item_vars[id][key] end
local ordinary_death_calls=0
death_manager={keep_item=function(npc, item)
    ordinary_death_calls=ordinary_death_calls+1
    item.condition=.1;item.ammo=0
end}

task_functor = {}
task_status_functor = {}
xr_effects = {}
task_manager_instance = { task_info = {} }
function task_manager_instance:give_task(id, giver_id)
	assert(giver_id == 101)
	local title = task_functor.adb_donor_thief_text(id, "title")
	local description = task_functor.adb_donor_thief_text(id, "descr")
	assert(title == anomaly_donation_bridge_common.game_text_cp1251(
		"Вернуть украденное"))
	assert(description:find(anomaly_donation_bridge_common.game_text_cp1251(
		"Подлый Воришка меня обокрал! Нужно догнать и вернуть."), 1, true))
	assert(task_functor.adb_donor_thief_target(id) == 101)
	self.task_info[id] = { giver_id = giver_id }
end
task_manager = { get_task_manager = function() return task_manager_instance end }

local ini_sections = {
	wpn_pm = true, medkit = true, ammo_9x18_fmj = true,
	af_medusa = true, quest_case = true, stalker_outfit = true, helm_resp = true,
	wpn_ak74 = true, af_belt = true, detector_simple = true, backpack_equipped = true
}
function system_ini()
	return {
		section_exist = function(_, section) return ini_sections[section] == true end,
		line_exist = function() return false end
	}
end
function get_object_story_id(id)
	return id == 5 and "test_quest_item" or nil
end

anomaly_donation_bridge_common = load_module(core_root,
	"anomaly_donation_bridge_common")
anomaly_donation_bridge_registry = load_module(core_root,
	"anomaly_donation_bridge_registry")

anomaly_donation_bridge_catalog = {
	validate_revision = function(params)
		return params.catalog_revision == "catalog-1", "catalog_revision_mismatch"
	end,
	random_available = function(kind)
		assert(kind == "npc")
		return { section_id = "sim_default_stalker_1" }
	end,
	is_spawn_safe = function(section, kind)
		return section == "sim_default_stalker_1" and kind == "npc"
	end
}

local vertices = {
	[10] = vector():set(0, 0, 0),
	[11] = vector():set(-1.5, 0, 0),
	[12] = vector():set(-11, 0, 0),
	[13] = vector():set(-3, 0, 10)
}
local client_objects = {}
level = {
	vertex_position = function(id)
		assert(vertices[id], "invalid fixture vertex " .. tostring(id))
		return vertices[id]
	end,
	vertex_in_direction = function(start, direction, distance)
		if start == 10 and direction.x < -0.5 and distance >= 1.25 and distance <= 1.8 then
			return 11
		end
		assert(start == 10, "only initial spawn may project a vertex, never escape")
		return 4294967295
	end,
	object_by_id = function(id) return client_objects[id] end,
	add_cam_effector = function(path, id, cyclic, callback)
		assert(path == "camera_effects\\fusker.anm" and id == 999 and
			cyclic == false and callback == "", "use the stock actor-punch camera effect")
		level.hit_effects = (level.hit_effects or 0) + 1
	end,
	enable_input = function()
		level.input_enabled = true
		level.enable_input_calls = (level.enable_input_calls or 0) + 1
	end,
	disable_input = function()
		level.input_enabled = false
	end
}

function set_inactivate_input_time(seconds)
	assert(seconds == 3600)
	db.storage[AC_ID].disable_input_time = time_global()
	db.storage[AC_ID].disable_input_idle = seconds
	level.disable_input()
end

local current_time = 1000
function time_global() return current_time end

local server = { id = 101, alive_value = true, character_name = nil }
function server:section_name() return "sim_default_stalker_1" end
function server:name() return "donor_thief_101" end
function server:clsid() return 12345 end
function server:alive() return self.alive_value end
function server:set_character_name(value) self.character_name = value end
function server:force_set_goodwill(value) assert(value == 5000) end

local life = { forced_online = false }
function life:object(id) return id == 101 and server or nil end
function life:set_switch_online(id, value)
	assert(id == 101)
	self.forced_online = value
end
function life:set_switch_offline(id, value) assert(id == 101 and value == false) end
function life:release(value) assert(value == server) end
function alife() return life end
function alife_create(section, spawn_position, level_vertex, game_vertex)
	assert(section == "sim_default_stalker_1")
	assert(spawn_position.x == -1.5 and level_vertex == 11 and game_vertex == 20,
		"spawn must be close behind the actor, inside native punch range")
	return server
end

local npc = {
	inventory = {}, current_position = vector():set(-1.5, 0, 0),
	current_vertex = 11, killed = false, companion_flag = true
}
function npc:id() return 101 end
function npc:position() return self.current_position end
function npc:level_vertex_id() return self.current_vertex end
function npc:set_character_community(value) self.community = value end
function npc:force_set_goodwill(value, target)
	assert((value == 5000 or value == -10000) and target == actor)
	self.personal_goodwill = value
end
function npc:set_relation(value, target)
	assert(value == game_object.enemy and target == actor)
	self.actor_relation = value
end
function npc:make_object_visible_somewhen(target)
	assert(target == actor)
	self.memory_refreshes = (self.memory_refreshes or 0) + 1
	self.best_enemy_value = target
end
function npc:best_enemy() return self.best_enemy_value end
function npc:invulnerable(value) self.invulnerable_value = value end
function npc:can_select_weapon(value) assert(value == false) end
function npc:hide_weapon()
	self.actor_weapon_blocked = true
	error("hide_weapon would block the ACTOR's weapon slots")
end
function npc:take_items_enabled(value) assert(value == false) end
function npc:enable_talk(value) assert(value == false) end
function npc:trade_enabled(value) assert(value == false) end
function npc:has_info(name)
	assert(name == "npcx_is_companion")
	return self.companion_flag
end
function npc:disable_info_portion(name)
	assert(name == "npcx_is_companion")
	self.companion_flag = false
	self.companion_clears = (self.companion_clears or 0) + 1
end
function npc:set_path_type() error("new thief must not script an approach/escape route") end
function npc:set_detail_path_type() error("native retreat owns its path") end
function npc:set_dest_level_vertex_id() error("no scripted approach/escape destination") end
function npc:set_desired_position() self.desired_position_cleared = true end
function npc:set_desired_direction() self.desired_direction_cleared = true end
function npc:clear_animations() self.animations_cleared = true end
function npc:set_movement_type(value)
	self.movement_type = value
	self.movement_commands = (self.movement_commands or 0) + 1
end
function npc:set_body_state(value) self.body_state = value end
function npc:set_mental_state(value) self.mental_state = value end
function npc:target_movement_type() return self.movement_type end
local function planner_action()
	return {
		preconditions = {}, effects = {},
		add_precondition = function(self, property)
			for _, existing in ipairs(self.preconditions) do
				assert(existing.id ~= property.id, "duplicate native precondition")
			end
			self.preconditions[#self.preconditions + 1] = property
		end,
		add_effect = function(self, property)
			self.effects[#self.effects + 1] = property
		end
	}
end
local planner = {
	evaluators = {}, removed = {}, actions = {
		[70001] = planner_action(), [70002] = planner_action(),
		[70003] = planner_action(), [70004] = planner_action(),
		[90005] = planner_action()
	}
}
function planner:initialized() return true end
function planner:current_action_id() return stalker_ids.action_combat_planner end
function planner:remove_evaluator() error("the main planner must remain untouched") end
function planner:add_evaluator() error("the main planner must remain untouched") end
function planner:evaluator(id) return self.evaluators[id] end
function planner:remove_action(id) self.actions[id] = nil end
function planner:add_action(id, action) self.actions[id] = action end
function planner:action(id) return self.actions[id] end
function npc:motivation_action_manager() return planner end
local native_retreat = planner_action()
native_retreat.initializations, native_retreat.executions, native_retreat.finalizations = 0, 0, 0
function native_retreat:initialize() self.initializations = self.initializations + 1 end
function native_retreat:execute()
	self.executions = self.executions + 1
	-- Native combat writes the slower danger/run after NPC update callbacks.
	npc.movement_type = move.run
	npc.mental_state = anim.danger
	npc.body_state = move.crouch -- a combat-body override must not slow panic sprint
	self.route_chosen_by_engine = true
end
function native_retreat:finalize() self.finalizations = self.finalizations + 1 end
local sprint_action_id = 19991031
local neighboring_action_id = sprint_action_id + 100
local neighboring_action = planner_action()
local combat = { evaluators = {}, evaluator_changes = 0,
	actions = { [neighboring_action_id] = neighboring_action }, additions = 0 }
function combat:initialized() return true end
local function applicable(action)
	if action == nil then return false end
	for _, property in ipairs(action.preconditions) do
		local evaluator = combat.evaluators[property.id]
		if evaluator == nil or type(evaluator.evaluate) ~= "function" or
			evaluator:evaluate() ~= property.value then return false end
	end
	return true
end
function combat:current_action_id()
	-- Model the observed engine choice: an unrestricted native retreat wins.
	-- Never hardcode our delegate as the current action, hiding a real AI bug.
	if applicable(native_retreat) then return stalker_ids.action_retreat_from_enemy end
	if applicable(self.actions[sprint_action_id]) then return sprint_action_id end
	return nil
end
function combat:action(id)
	if id == stalker_ids.action_retreat_from_enemy then return native_retreat end
	-- Exact X-Ray lower_bound behaviour: missing ID can return the next action!
	local selected_id
	for candidate_id in pairs(self.actions) do
		if candidate_id >= id and (selected_id == nil or candidate_id < selected_id) then
			selected_id = candidate_id
		end
	end
	return selected_id and self.actions[selected_id] or nil
end
function combat:add_action(id, action)
	assert(id == sprint_action_id and self.actions[id] == nil)
	-- Native add_operator calls setup: super(nil, name) must not assume an
	-- NPC is already available inside the base constructor.
	assert(action.super_constructed == true and action.object == nil)
	action:setup(npc, {})
	self.actions[id] = action
	self.additions = self.additions + 1
end
function combat:evaluator(id) return self.evaluators[id] end
function combat:remove_evaluator(id)
	assert(self.evaluators[id] ~= nil)
	self.evaluators[id] = nil
end
function combat:add_evaluator(id, evaluator)
	assert(self.evaluators[id] == nil)
	self.evaluators[id] = evaluator
	self.evaluator_changes = self.evaluator_changes + 1
end
function combat:remove_action() error("native actions must not be replaced") end
function combat:set_goal_world_state() error("native goals must not be replaced") end
function combat:update() error("the engine, not our script, updates the planner") end
function cast_planner(action)
	assert(action == planner.actions[70004])
	return combat
end
function npc:deadbody_can_take(value) assert(value == true) end
function npc:name() return 'donor_thief_101' end
function npc:kill(killer)
	assert(killer == self and self.invulnerable_value == false)
	-- Stock xr_motivator's companion-death penalty does not check the killer.
	if self:has_info("npcx_is_companion") then actor.reputation = 75 end
	assert(actor:character_reputation() == 150, "capture must not penalise companion death")
	for _,value in ipairs(self.inventory)do death_manager.keep_item(self,value)end
	self.killed = true
	server.alive_value = false
end
client_objects[101] = npc

function IsStalker(object, class_id)
	if object ~= nil then return object == npc end
	return class_id == 12345
end
game_object = { level_path = 77, enemy = 2 }
move = { line = 88, run = 89, standing = 91, crouch = 94 }
anim = { panic = 90, free = 92, danger = 93 }
xr_evaluators_id = { corpse_exist = 90004 }
xr_gather_items = { evaid = 90005, actid = 90005 }
xr_actions_id = {
	alife = 70001, corpse_exist = 70002, wounded_exist = 70003
}
stalker_ids = {
	property_alive = 1, action_combat_planner = 70004,
	action_retreat_from_enemy = 20, property_panic = 17,
	property_item_to_kill = 9, property_found_item_to_kill = 10,
	property_use_suddenness = 25, property_pure_enemy = 37
}
for _, id in ipairs({ 17, 9, 10, 25, 37 }) do combat.evaluators[id] = {} end
native_retreat:add_effect({ id = stalker_ids.property_pure_enemy, value = false })
function world_property(id, value) return { id = id, value = value } end
function property_evaluator_const(value)
	return { evaluate = function() return value end }
end
-- C++ constructors are userdata; action_base DOES NOT export __init. Match
-- the real action_base_script.cpp API instead of inventing a table method.
local function callable_userdata(constructor)
	local proxy = newproxy(true)
	getmetatable(proxy).__call = function(_, ...) return constructor(...) end
	return proxy
end
local action_base_methods = {}
local function construct_action(self, object, name)
	self.object, self.name = object, name
	self.preconditions, self.effects = {}, {}
	self.super_constructed = true
end
function action_base_methods:setup(object, storage) self.object, self.storage = object, storage end
function action_base_methods:initialize() assert(self.object == npc and self.storage ~= nil) end
function action_base_methods:execute() assert(self.object == npc and self.storage ~= nil) end
function action_base_methods:finalize() assert(self.object == npc and self.storage ~= nil) end
function action_base_methods:add_precondition(property)
	self.preconditions[#self.preconditions + 1] = property
end
function action_base_methods:add_effect(property)
	self.effects[#self.effects + 1] = property
end
function action_base_methods:set_weight(value) self.weight = value end
action_base = callable_userdata(function(object, name)
	local instance = setmetatable({}, { __index = action_base_methods })
	construct_action(instance, object, name)
	return instance
end)
getmetatable(action_base).__index = action_base_methods
assert(type(action_base) == "userdata" and action_base.__init == nil)
function class(name)
	return function(base)
		local definition = {}
		definition.__index = definition
		setmetatable(definition, { __index = base, __call = function(_, ...)
			local instance = setmetatable({}, definition)
			local previous_super = super
			super = function(...)
				local parent = base(...)
				for key, value in pairs(parent) do instance[key] = value end
			end
			local ok, message = pcall(instance.__init, instance, ...)
			super = previous_super
			if not ok then error(message, 0) end
			return instance
		end })
		_G[name] = definition
	end
end
-- Luabind exports C++ class constructors as callable USERDATA, not functions.
-- Model the real API so a type(..., "function") gate cannot pass mocks only.
world_property = callable_userdata(world_property)
property_evaluator_const = callable_userdata(property_evaluator_const)
assert(type(world_property) == "userdata" and type(property_evaluator_const) == "userdata")
xr_sound = {
	set_sound_play = function(id, sound)
		assert(id == 101 and sound == "panic_human")
		npc.panic_sound = true
	end
}
game_relations = {
	set_npcs_relation = function(object, target, relation)
		assert(object == npc and target == actor and relation == "friend")
	end
}
local commanded_states = {}
local commanded_state = nil
state_mgr = {
	set_state = function(object, name, _, _, look, options)
		assert(object == npc)
		assert(name == "idle", "no NPC approach/sprint/punch state; idle only for old-save cleanup")
		if commanded_state == name then return end
		commanded_states[#commanded_states + 1] = name
		commanded_state = name
	end,
	get_state = function(object)
		assert(object == npc)
		return commanded_state
	end
}
news_manager = {
	send_tip = function(target, message, timeout, sender, showtime, sender_id)
		assert(target == actor and timeout == 0 and sender == nil and
			showtime == 5000 and sender_id == nil)
		npc.robbery_news = message
	end
}
xr_abuse = {
	add_abuse = function(object, value)
		error("NPC must not approach or perform punch animations")
	end,
	clear_abuse = function(object)
		assert(object == npc)
		npc.abuse_cleared = (npc.abuse_cleared or 0) + 1
	end
}

local callbacks = {}
function RegisterScriptCallback(name, callback)
	callbacks[name] = callback
end

local effect = load_module(optional_root,
	"anomaly_donation_bridge_effect_hardcoon_adb_donor_thief")
effect.on_game_start()
local handler = assert(anomaly_donation_bridge_registry.get(
	"hardcoon.adb.donor_thief"))

local default_context = { actor = actor, now = current_time }
local default_allowed, default_disposition, default_reason =
	handler.can_execute(default_context, {})
assert(default_allowed == true and default_context.options.count == 99,
	tostring(default_disposition) .. ":" .. tostring(default_reason))
assert(default_context.options.sprint_meters == 200, "new thieves must default to 200m")
for _, invalid_distance in ipairs({ "0", "-1", "10001", "1.5", "not-a-number" }) do
	local distance_allowed, distance_status, distance_reason = handler.can_execute(
		{ actor = actor }, { sprint_meters = invalid_distance })
	assert(not distance_allowed and distance_status == "rejected" and
		distance_reason == "donor_thief_sprint_meters_invalid" and
		level.hit_effects == nil and #npc.inventory == 0,
		"invalid distance must reject BEFORE spawning/hitting/stealing")
end
for _, boundary in ipairs({ 1, 10000 }) do
	local boundary_context = { actor = actor }
	assert(handler.can_execute(boundary_context, { sprint_meters = tostring(boundary) }) and
		boundary_context.options.sprint_meters == boundary, "distance boundary rejected")
end
local saved_ruck = actor.iterate_ruck
local saved_hit_camera = level.add_cam_effector
level.add_cam_effector = nil
local hit_allowed, _, hit_reason = handler.can_execute({ actor = actor }, {})
assert(not hit_allowed and hit_reason == "donor_thief_hit_api_unavailable")
level.add_cam_effector = saved_hit_camera
actor.iterate_ruck = nil
local ruck_allowed, _, ruck_reason = handler.can_execute({ actor = actor }, {})
assert(not ruck_allowed and ruck_reason == "donor_thief_ruck_api_unavailable",
	"missing ruck API must not fall back to stealing equipped inventory")
actor.iterate_ruck = saved_ruck
local named_context = { actor = actor, command = { donor_name = "Тестовый донатер" } }
assert(handler.can_execute(named_context, {}))
assert(named_context.options.donor_name == "Тестовый донатер",
	"modular effects must read the existing command donor field without an EXE rebuild")
local saved_cast = cast_planner
cast_planner = nil
local unsupported, unsupported_status, unsupported_reason =
	handler.can_execute({ actor = actor }, {})
assert(not unsupported and unsupported_status == "rejected" and
	unsupported_reason == "donor_thief_native_panic_api_unavailable")
cast_planner = saved_cast
local saved_world_property = world_property
world_property = nil
local property_allowed, _, property_reason = handler.can_execute({ actor = actor }, {})
assert(not property_allowed and property_reason == "donor_thief_native_panic_api_unavailable",
	"actually missing world-property constructor must still reject safely")
world_property = saved_world_property
world_property = callable_userdata(function() error("simulate broken engine constructor") end)
local broken_property_allowed = handler.can_execute({ actor = actor }, {})
assert(not broken_property_allowed, "broken callable constructors must reject before spawning")
world_property = saved_world_property
local action_constructor = getmetatable(adb_donor_thief_sprint_action)
local saved_constructor = action_constructor.__call
action_constructor.__call = function() error("simulate unsupported native base constructor") end
local constructor_allowed, constructor_status, constructor_reason =
	handler.can_execute({ actor = actor }, {})
assert(not constructor_allowed and constructor_status == "rejected" and
	constructor_reason == "donor_thief_native_panic_api_unavailable" and
	level.hit_effects == nil and #npc.inventory == 0,
	"broken AI construction must reject BEFORE hit/stealing/spawning")
action_constructor.__call = saved_constructor

local context = { actor = actor, now = current_time }
local params = { item_count = "999" }
if arg[3] == "distance-jog" then params.sprint_meters = "100" end
if arg[3] == "custom-distance-jog" then params.sprint_meters = "75" end
local allowed, disposition, reason = handler.can_execute(context, params)
assert(allowed == true, tostring(disposition) .. ":" .. tostring(reason))
local status, detail = handler.execute(context)
assert(status == "executed" and detail:find("_behind_actor", 1, true), detail)
assert(server.character_name == anomaly_donation_bridge_common.game_text_cp1251("Воришка") and
	life.forced_online == true)
assert(saved.adb_donor_thief_01.phase == "robbery")
assert(saved.adb_donor_thief_01.sprint_meters == context.options.sprint_meters,
	"each thief must save the configured threshold at spawn time")
assert(combat:current_action_id() == stalker_ids.action_retreat_from_enemy)
assert(combat:action(sprint_action_id) == neighboring_action,
	"fixture must reproduce non-exact X-Ray lookup of a missing action ID")
local before_enemy_flags = {}
callbacks.on_enemy_eval(npc, actor, before_enemy_flags)
assert(before_enemy_flags.override == true and before_enemy_flags.result == false)

if arg[3] == "legacy-distance-jog" then
	saved.adb_donor_thief_01.sprint_meters = nil
	handler.on_game_load()
elseif arg[3] == "pending-load" then
	client_objects[101] = nil
	handler.update(current_time)
	assert(saved.adb_donor_thief_01.phase == "robbery" and #npc.inventory == 0 and
		level.hit_effects == nil, "wait for an online inventory before applying hit/loot")
	handler.on_game_load()
	client_objects[101] = npc
	current_time = current_time + 100
elseif arg[3] == "legacy-punch" or arg[3] == "legacy-approach" then
	saved.adb_donor_thief_01.phase = arg[3] == "legacy-punch" and "punch" or "approach"
	if arg[3] == "legacy-punch" then
		db.storage[AC_ID].disable_input_idle = 30
		level.input_enabled = false
	end
	handler.on_game_load()
	assert(saved.adb_donor_thief_01.phase == "robbery",
		"old unfinished approach/punch saves must migrate, not resurrect old AI logic")
	if arg[3] == "legacy-punch" then
		assert(level.input_enabled == true and db.storage[AC_ID].disable_input_idle == nil)
	end
elseif arg[3] == "actor-moved" then
	npc.current_position = vector():set(-30, 0, 0)
end

handler.update(current_time)
local robbed_at = current_time
assert(saved.adb_donor_thief_01.phase == "flee" and
	saved.adb_donor_thief_01.hit_mode == "instant" and level.hit_effects == 1,
	"FIRST online update must hit/drop/steal/start escape without NPC approach or animation")
assert(npc.community == "stalker" and npc.invulnerable_value == true and
	npc.actor_weapon_blocked ~= true and npc.route_commands == nil)
assert(arg[3] == "legacy-punch" and #commanded_states == 1 or #commanded_states == 0,
	"only an old punch save may use an idle state for cleanup")
assert(db.storage[101].corpse_detection.selected_corpse_id == nil and
	db.storage[101].corpse_detection.corpse_detection_enabled.disabled == true and
	db.storage[101].gather_items.selected_id == nil and
	db.storage[101].gather_items.gather_items_enabled.disabled == true)
assert(combat.evaluator_changes == 4 and
	combat.evaluators[stalker_ids.property_panic]:evaluate() == true and
	combat.evaluators[stalker_ids.property_item_to_kill]:evaluate() == false and
	combat.evaluators[stalker_ids.property_found_item_to_kill]:evaluate() == false and
	combat.evaluators[stalker_ids.property_use_suddenness]:evaluate() == false,
	table.concat(logs, "\n"))
assert(npc.companion_flag == false and npc.companion_clears == 1)
local sprint = assert(combat.actions[sprint_action_id])
assert(combat.additions == 1 and combat.actions[neighboring_action_id] == neighboring_action,
	"a neighboring mod action must not prevent binding or be replaced")
assert(sprint.retreat == native_retreat and sprint.weight == 1 and
	combat:action(stalker_ids.action_retreat_from_enemy) == native_retreat)
assert(#sprint.preconditions == 2 and #sprint.effects == 1 and
	sprint.effects[1].id == stalker_ids.property_pure_enemy and
	sprint.effects[1].value == false and #native_retreat.preconditions == 1 and
	native_retreat.preconditions[1].id == stalker_ids.property_panic and
	native_retreat.preconditions[1].value == false and
	combat:current_action_id() == sprint_action_id and not applicable(native_retreat))
sprint:initialize()
sprint:execute()
assert(native_retreat.initializations == 1 and native_retreat.executions == 1 and
	native_retreat.route_chosen_by_engine and npc.mental_state == anim.panic and
	npc.body_state == move.standing)
sprint:finalize()
assert(native_retreat.finalizations == 1)
assert(level.input_enabled == false and db.storage[AC_ID].disable_input_idle == 3600)
assert(npc.actor_relation == game_object.enemy and npc.personal_goodwill == -10000 and
	npc.memory_refreshes == 1)
assert(npc.robbery_news == anomaly_donation_bridge_common.game_text_cp1251("Вас обокрали"))
assert(task_manager_instance.task_info.adb_donor_thief_01 ~= nil and
	task_status_functor.adb_donor_thief_status(nil, "adb_donor_thief_01") == nil)
assert(saved.adb_donor_thief_01.stolen_count == 4 and #npc.inventory == 4 and
	#actor_inventory == 7 and #dropped == 1 and dropped[1] == weapon)
for _, stolen in ipairs(npc.inventory) do
	assert(not equipped[stolen:id()] and stolen ~= quest,
		"equipped slots/belt and quest items must never be stolen")
end

if arg[3] == "early-death" then
	current_time = robbed_at + 200
	server.alive_value = false
	handler.update(current_time)
	assert(saved.adb_donor_thief_01.phase == "dead" and
		level.input_enabled == true and db.storage[AC_ID].disable_input_idle == nil)
	print("donor thief early-death input safety smoke passed")
	return
end

if arg[3] == "distance-jog" or arg[3] == "default-distance-jog" or
	arg[3] == "custom-distance-jog" or arg[3] == "legacy-distance-jog" then
	local limit = arg[3] == "legacy-distance-jog" and 100 or context.options.sprint_meters
	-- Cumulative path, never distance away from the actor. F9 halfway must
	-- preserve 50m and must not count a position jump on the first loaded tick.
	for _, x in ipairs({ -11.5, -31.5, -11.5 }) do
		npc.current_position = vector():set(x, 0, 0)
		current_time = current_time + 100
		handler.update(current_time)
	end
	assert(saved.adb_donor_thief_01.escape_meters == 50 and
		saved.adb_donor_thief_01.escape_jogging == false)
	handler.on_game_load()
	current_time = current_time + 100
	handler.update(current_time)
	assert(saved.adb_donor_thief_01.escape_meters == 50)
	assert(saved.adb_donor_thief_01.sprint_meters ==
		(arg[3] ~= "legacy-distance-jog" and limit or nil), "F9 changed the saved threshold")
	while saved.adb_donor_thief_01.escape_meters < limit - 1 do
		local step = math.min(20, limit - 1 - saved.adb_donor_thief_01.escape_meters)
		npc.current_position = vector():set(npc.current_position.x - step, 0, 0)
		current_time = current_time + 100
		handler.update(current_time)
		combat.actions[sprint_action_id]:execute()
		assert(npc.mental_state == anim.panic, "sprint ended before configured distance")
	end
	npc.current_position = vector():set(npc.current_position.x - 2, 0, 0)
	current_time = current_time + 100
	handler.update(current_time)
	assert(saved.adb_donor_thief_01.escape_meters == limit and
		saved.adb_donor_thief_01.escape_jogging == true)
	combat.actions[sprint_action_id]:execute()
	assert(npc.mental_state == anim.free and npc.movement_type == move.run and
		npc.body_state == move.standing, "at the threshold use stock free/run jog, not panic sprint")
	handler.on_game_load()
	current_time = current_time + 100
	handler.update(current_time)
	combat.actions[sprint_action_id]:execute()
	assert(saved.adb_donor_thief_01.escape_meters == limit and
		saved.adb_donor_thief_01.escape_jogging == true and npc.mental_state == anim.free and
		level.hit_effects == 1 and #npc.inventory == 4 and #dropped == 1,
		"F9 must preserve permanent jogging without repeating robbery")
	print("donor thief " .. tostring(limit) ..
		"m cumulative sprint -> permanent jog / F9 smoke passed (mock API)")
	return
end

local commands_after_robbery = #commanded_states
current_time = robbed_at + 3000
handler.update(current_time)
assert(saved.adb_donor_thief_01.phase == "flee" and level.hit_effects == 1 and
	npc.route_commands == nil and #commanded_states == commands_after_robbery)
assert(level.input_enabled == false)
current_time = robbed_at + 4900
handler.update(current_time)
assert(level.input_enabled == false and combat.evaluator_changes == 4,
	"stun must last five REAL seconds, with no repeated AI rebinding")
local enemy_flags = { override = false, result = true }
callbacks.on_enemy_eval(npc, actor, enemy_flags)
assert(enemy_flags.override == true and enemy_flags.result == true)
local other_enemy = { id = function() return 202 end }
callbacks.on_enemy_eval(npc, other_enemy, enemy_flags)
assert(enemy_flags.override == true and enemy_flags.result == false)
local ordinary_flags = { override = false, result = true }
callbacks.on_enemy_eval(other_enemy, actor, ordinary_flags)
assert(ordinary_flags.override == false and ordinary_flags.result == true)

npc.current_position = vector():set(-6, 0, 0)
current_time = robbed_at + 5000
handler.update(current_time)
assert(saved.adb_donor_thief_01.catchable == true and not npc.killed and
	level.input_enabled == true and db.storage[AC_ID].disable_input_idle == nil)
local meters_before_f9 = saved.adb_donor_thief_01.escape_meters
handler.on_game_load()
current_time = robbed_at + 5200
handler.update(current_time)
assert(saved.adb_donor_thief_01.phase == "flee" and level.hit_effects == 1 and
	#npc.inventory == 4 and #actor_inventory == 7 and #dropped == 1 and
	combat.evaluator_changes == 8 and npc.route_commands == nil and combat.additions == 1 and
	saved.adb_donor_thief_01.escape_meters == meters_before_f9,
	"F9 must preserve theft and path progress, without duplicate delegate")
assert(level.input_enabled == true and npc.actor_weapon_blocked ~= true and
	#native_retreat.preconditions == 1 and combat:current_action_id() == sprint_action_id)
local rebound_changes = combat.evaluator_changes
current_time = robbed_at + 5500
handler.update(current_time)
assert(combat.evaluator_changes == rebound_changes)
combat.actions = { [neighboring_action_id] = neighboring_action }
native_retreat.preconditions = {}
handler.on_game_load()
current_time = robbed_at + 5600
handler.update(current_time)
assert(combat.additions == 2 and combat.actions[sprint_action_id].retreat == native_retreat and
	combat:current_action_id() == sprint_action_id and not applicable(native_retreat),
	"fresh planner must rebind despite non-nil neighboring action lookup")

npc.current_position = vector():set(-1, 0, 0)
npc.companion_flag = true
current_time = robbed_at + 5700
if arg[3] == "capture-flag-failure" then
	local saved_disable_info = npc.disable_info_portion
	npc.disable_info_portion = function() error("simulate unavailable flag cleanup") end
	handler.update(current_time)
	assert(not npc.killed and npc.invulnerable_value and
		saved.adb_donor_thief_01.phase == "flee" and actor:character_reputation() == 150)
	npc.disable_info_portion = saved_disable_info
	current_time = current_time + 100
elseif arg[3] == "capture-kill-failure" then
	local saved_kill = npc.kill
	npc.kill = function() error("simulate failed engine death") end
	handler.update(current_time)
	assert(not npc.killed and npc.invulnerable_value and
		saved.adb_donor_thief_01.phase == "flee")
	npc.kill = saved_kill
	current_time = current_time + 100
end
handler.update(current_time)
assert(npc.killed and saved.adb_donor_thief_01.phase == "dead" and
	npc.companion_flag == false and actor:character_reputation() == 150 and
	life.forced_online == false and
	task_status_functor.adb_donor_thief_status(nil, "adb_donor_thief_01") == "complete")
for _,value in ipairs(npc.inventory)do
    assert(value.condition==.87 and value.ammo==24,'native corpse processing altered stolen gear/ammunition')
end
assert(ordinary_death_calls==0,'stolen inventory reached ordinary NPC loot processing')
-- Corpse reload and finished/reused task slots must not lose item protection.
saved.adb_donor_thief_01=nil
handler.on_game_load()
for _,value in ipairs(npc.inventory)do death_manager.keep_item(npc,value)end
assert(ordinary_death_calls==0,'completed task/reload lost protection of stolen items')
local foreign={id=function()return 102 end,name=function()return 'other_npc' end}
death_manager.keep_item(foreign,ammo)
assert(ordinary_death_calls==1,'protection leaked to another NPC')
local reused=item(ammo:id(),'different_ammo')
death_manager.keep_item(npc,reused)
assert(ordinary_death_calls==2,'protection leaked to a reused item ID')
saved.adb_donor_thief_01={dead=true,phase='dead'}
saved.adb_donor_thief_01.actor_lock_active = true
db.storage[AC_ID].disable_input_idle = 3600
level.input_enabled = false
handler.on_game_load()
assert(level.input_enabled == true and saved.adb_donor_thief_01.actor_lock_active == false)
print("donor thief instant robbery / exact sprint binding smoke passed (mock API, not in-game AI)")
