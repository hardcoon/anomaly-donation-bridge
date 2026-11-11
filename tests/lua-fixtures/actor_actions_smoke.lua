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
anomaly_donation_bridge_catalog = {
	validate_sections = function(params)
		if params.catalog_revision ~= "rev" then
			return nil, "catalog_revision_mismatch"
		end
		local entries = {}
		for section in (params.faction_communities .. ","):gmatch("(.-),") do
			entries[#entries + 1] = {
				section_id = section,
				faction = section:gsub("^adb_faction_", "")
			}
		end
		return entries
	end
}

local actor_values = { radiation = 0.5, health = 0.5, satiety = 0.5, money = 1000,
	invulnerable = false }
local actor = setmetatable({}, {
	__index = function(_, key)
		if actor_values[key] ~= nil then return actor_values[key] end
	end,
	__newindex = function(self, key, value)
		if key == "satiety" then
			actor_values.satiety = math.max(0, math.min(1, value))
		else rawset(self, key, value) end
	end
})
function actor:change_radiation(delta)
	actor_values.radiation = math.max(0, math.min(1, actor_values.radiation + delta))
end
function actor:change_health(delta)
	actor_values.health = math.max(0, math.min(1, actor_values.health + delta))
end
function actor:change_satiety(delta)
	actor_values.satiety = math.max(0, math.min(1, actor_values.satiety + delta))
end
local native_food_condition = {}
function native_food_condition:GetSatiety() return actor_values.satiety end
function native_food_condition:ChangeSatiety(delta)
	actor_values.satiety = math.max(0, math.min(1, actor_values.satiety + delta))
end
function actor:cast_Actor()
	return { conditions = function() return native_food_condition end }
end
function actor:money() return actor_values.money end
function actor:give_money(delta) actor_values.money = actor_values.money + delta end
local active_slot_value, active_item_value, dropped_weapon = 0, nil, nil
function actor:active_slot() return active_slot_value end
function actor:active_item() return active_item_value end
function actor:drop_item(item) dropped_weapon = item end
local actor_community = "actor_stalker"
function actor:character_community() return actor_community end
function actor:set_character_community(community) actor_community = community end
local persistent_faction = "stalker"
alife_storage_manager = {
	get_state = function()
		return { default_faction = persistent_faction }
	end
}
function set_actor_true_community(community, now)
	assert(now == true)
	persistent_faction = community
	actor:set_character_community("actor_" .. community)
end
function get_actor_true_community() return persistent_faction end
local goodwill_faction = nil
game_relations = {
	set_community_goodwill_for_faction = function(community)
		goodwill_faction = community
	end
}
local pda_messages = {}
local pda_lost = string.char(207, 238, 242, 229, 240, 255, 237, 238, 58, 32) ..
	"1 000" .. string.char(32, 240, 243, 225, 235, 229, 233)
local pda_received = string.char(207, 238, 235, 243, 247, 229, 237, 238, 58, 32) ..
	"123 456" .. string.char(32, 240, 243, 225, 235, 229, 233)
local pda_faction_changed = string.char(195, 240, 243, 239, 239, 232, 240, 238,
	226, 234, 224, 32, 232, 231, 236, 229, 237, 229, 237, 224)
local pda_god_started = string.char(195, 238, 228, 45, 236, 238, 228, 32, 226,
	234, 235, 254, 247, 184, 237, 32, 237, 224, 32) .. "5" ..
	string.char(32, 241, 229, 234, 46)
local pda_god_finished = string.char(195, 238, 228, 45, 236, 238, 228, 32, 226,
	251, 234, 235, 254, 247, 229, 237, 46)
news_manager = {
	send_tip = function(_, text)
		pda_messages[#pda_messages + 1] = text
		return true
	end
}
function get_object_story_id() return 0 end

local god_enabled = false
local console = {}
function console:get_bool(name)
	assert(name == "g_god")
	return god_enabled
end
function console:execute(command)
	assert(command == "g_god on" or command == "g_god off")
	god_enabled = command == "g_god on"
end
function get_console() return console end

local last_drink = 5000
actor_status_thirst = {
	get_water_deprivation = function(visual)
		if visual then return 0 end
		return last_drink / 10000
	end
}

local manager = { started = false, _state_loaded = true, condlist = {} }
surge_manager = {
	get_surge_manager = function() return manager end,
	is_started = function() return manager.started end,
	start_surge = function() manager.started = true end
}
local current_level = "l01_escape"
local remove_effect_calls = 0
level = {
	name = function() return current_level end,
	add_complex_effector = function() end,
	remove_complex_effector = function()
		remove_effect_calls = remove_effect_calls + 1
		if remove_effect_calls == 2 then error("simulated cleanup failure") end
	end
}
level_weathers = { valid_levels = { l01_escape = true, l13_generators = true } }
ui_options = { get = function() return true end }
has_alife_info = function() return false end
xr_logic = { pick_section_from_condlist = function() return "true" end }
db = { actor = actor }
local ini = {}
function ini:section_exist(section)
	return section == "effector_alcohol" or section == "wpn_pm"
end
function system_ini() return ini end

local modular_callbacks = {}
if arg[2] ~= nil then
    function RegisterScriptCallback(event, fn) modular_callbacks[event] = fn end
	for _, id in ipairs({
		"add_radiation", "drop_active_weapon", "change_health", "change_needs",
		"change_money", "get_drunk", "god_mode", "trigger_emission",
		"sleep_hours", "random_active", "random_all_presets",
		"change_player_faction"
	}) do
		load_effect(arg[2], "anomaly_donation_bridge_effect_" .. id).on_game_start()
	end
else
	local actions = load_module("anomaly_donation_bridge_actions_actor")
	actions.register_all()
end
assert(#anomaly_donation_bridge_registry.ids() == 12)

local function dispatch(action_id, params, now, command)
	return anomaly_donation_bridge_registry.dispatch(
		action_id, { actor = actor, now = now or 1000,
			command = command or { command_id = action_id, donation_id = action_id } },
		params or {})
end

actor_values.radiation = 50
assert(select(1, dispatch("add_radiation", { percent = "25" })) == "rejected")
assert(actor_values.radiation == 50)
actor_values.radiation = 0.5
actor_values.health = 50
assert(select(1, dispatch("change_health", { percent = "25" })) == "rejected")
assert(actor_values.health == 50)
actor_values.health = 0.5
actor_values.satiety = 0 / 0
assert(select(1, dispatch("change_needs", {
	hunger_percent = "25", thirst_percent = "0"
})) == "rejected")
assert(actor_values.satiety ~= actor_values.satiety)
actor_values.satiety = 0.5
actor_values.money = 0 / 0
assert(select(1, dispatch("change_money", { amount = "100" })) == "rejected")
assert(actor_values.money ~= actor_values.money)
actor_values.money = 1000
assert(select(1, dispatch("add_radiation", { percent = "100" })) == "executed")
assert(actor_values.radiation == 1)
assert(select(1, dispatch("change_health", { percent = "-80" })) == "executed")
assert(actor_values.health == 0)
assert(select(1, dispatch("change_needs", {
	hunger_percent = "25", thirst_percent = "20"
})) == "executed")
assert(math.abs(actor_values.satiety - 0.75) < 0.001)
assert(math.abs(actor_status_thirst.get_water_deprivation(false) - 0.3) < 0.001)
assert(select(1, dispatch("change_money", { amount = "-2000" })) == "executed")
assert(actor_values.money == 0)
assert(pda_messages[#pda_messages] == pda_lost)
assert(select(1, dispatch("change_money", { amount = "123456" })) == "executed")
assert(actor_values.money == 123456)
assert(pda_messages[#pda_messages] == pda_received)

local weapon = {
	id = function() return 77 end,
	section = function() return "wpn_pm" end
}
local waiting_command = { command_id = "weapon-wait-1", donation_id = "donation-weapon-wait" }
local waiting, waiting_reason = dispatch("drop_active_weapon", { wait_seconds = "30" },
	1000, waiting_command)
assert(waiting == "deferred" and waiting_reason == "waiting_for_active_weapon")
waiting, waiting_reason = dispatch("drop_active_weapon", { wait_seconds = "30" },
	29000, waiting_command)
assert(waiting == "deferred" and waiting_reason == "waiting_for_active_weapon")
active_slot_value, active_item_value = 2, weapon
local weapon_status, weapon_reason = dispatch("drop_active_weapon",
	{ wait_seconds = "30" }, 29500, waiting_command)
assert(weapon_status == "executed" and weapon_reason == "weapon_dropped_wpn_pm")
assert(dropped_weapon == weapon)
active_slot_value, active_item_value = 0, nil
local expiring_command = { command_id = "weapon-wait-2", donation_id = "donation-weapon-expire" }
assert(select(1, dispatch("drop_active_weapon", { wait_seconds = "1" },
	1000, expiring_command)) == "deferred")
local expired, expired_reason = dispatch("drop_active_weapon", { wait_seconds = "1" },
	2000, expiring_command)
assert(expired == "rejected" and expired_reason == "active_weapon_wait_expired")

assert(select(1, dispatch("get_drunk", { duration_seconds = "5" })) == "executed")
assert(select(1, dispatch("god_mode", { duration_seconds = "5" }, 1000)) == "executed")
assert(pda_messages[#pda_messages] == pda_god_started)
if arg[2] ~= nil then
    local flags = {ret_value=true}
    modular_callbacks.actor_on_before_hit({}, 0, flags)
    assert(god_enabled == false and flags.ret_value == false,
        "Default modular god mode must veto hits without the console flag")
else
    assert(god_enabled == true)
end
anomaly_donation_bridge_registry.update(6000)
local expected_finished = arg[2] ~= nil and
    anomaly_donation_bridge_common.game_text_cp1251("Год-мод закончен") or pda_god_finished
assert(god_enabled == false and pda_messages[#pda_messages] == expected_finished)
-- God mode must no longer also run get_drunk's shared legacy cleanup.
-- The drunk handler retries its own failed removal on the next update.
assert(remove_effect_calls == 2, tostring(remove_effect_calls))
anomaly_donation_bridge_registry.update(6100)
assert(remove_effect_calls == 3)

actor_status_thirst = { get_water_deprivation = function() return 0.5 end }
local unsupported, unsupported_reason = dispatch("change_needs", {
	hunger_percent = "0", thirst_percent = "1"
})
assert(unsupported == "rejected" and unsupported_reason == "thirst_adapter_unsupported")

assert(select(1, dispatch("trigger_emission")) == "executed")
manager.started = false
current_level = "l13_generators"
local blocked, blocked_reason = dispatch("trigger_emission")
assert(blocked == "rejected" and blocked_reason == "surge_level_unsupported")
current_level = "l01_escape"

local meta_status, meta_reason = dispatch("random_active")
assert(meta_status == "rejected" and meta_reason == "meta_action_must_be_resolved_by_application")
local faction_status, faction_reason = dispatch("change_player_faction", {
	faction_communities = "adb_faction_actor_stalker,adb_faction_actor_bandit",
	catalog_revision = "rev"
})
assert(faction_status == "executed" and
	faction_reason == "player_faction_changed_actor_bandit")
assert(actor_community == "actor_bandit" and goodwill_faction == "actor_bandit")
assert(persistent_faction == "bandit")
assert(pda_messages[#pda_messages] == pda_faction_changed)
local same_faction, same_faction_reason = dispatch("change_player_faction", {
	faction_communities = "adb_faction_actor_bandit", catalog_revision = "rev"
})
assert(same_faction == "rejected" and
	same_faction_reason == "selected_factions_contain_no_change")
surge_manager = nil
local unsupported_surge, unsupported_surge_reason = dispatch("trigger_emission")
assert(unsupported_surge == "rejected" and
	unsupported_surge_reason == "stock_surge_unsupported")
anomaly_donation_bridge_registry.on_level_changing()

print("actor action smoke passed")
