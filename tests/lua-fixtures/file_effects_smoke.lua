local core_root = assert(arg[1], "pass game-addon/gamedata/scripts as arg[1]")
local effect_root = assert(arg[2], "pass effects/gamedata/scripts as arg[2]")
local optional_root = assert(arg[3], "pass optional-effects/gamedata/scripts as arg[3]")
local separator = package.config:sub(1, 1)
local script_callbacks = {}

function RegisterScriptCallback(name, callback)
	assert(type(name) == "string" and type(callback) == "function")
	script_callbacks[name] = script_callbacks[name] or {}
	table.insert(script_callbacks[name], callback)
end

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
anomaly_donation_bridge_effects_direct_spawn = load_module(core_root,
	"anomaly_donation_bridge_effects_direct_spawn")

assert(#anomaly_donation_bridge_registry.ids() == 0,
	"the installed core must not register any preset")

anomaly_donation_bridge_named_companion=load_module(core_root,"anomaly_donation_bridge_named_companion")

local effect_names = {
	"anomaly_donation_bridge_effect_spawn_npcs",
	"anomaly_donation_bridge_effect_spawn_mutants",
	"anomaly_donation_bridge_effect_spawn_items",
	"anomaly_donation_bridge_effect_spawn_anomaly"
}
for _, name in ipairs(effect_names) do
	local effect = load_module(effect_root, name)
	assert(type(effect.on_game_start) == "function", name)
	effect.on_game_start()
end

local expected = {
	spawn_npcs = true, spawn_mutants = true,
	spawn_items = true, spawn_anomaly = true
}
local registered = anomaly_donation_bridge_registry.ids()
assert(#registered == 4, "each starter effect must register exactly once")
for _, id in ipairs(registered) do
	assert(expected[id], "unexpected core effect " .. tostring(id))
	local descriptor = anomaly_donation_bridge_registry.get(id)
	assert(type(descriptor.can_execute) == "function")
	assert(type(descriptor.execute) == "function")
end

local optional_ids = {
	"add_radiation", "drop_active_weapon", "block_inventory", "change_health", "change_needs",
	"change_money", "get_drunk", "god_mode", "trigger_emission", "sleep_hours",
	"random_active", "random_all_presets", "change_player_faction",
	"damage_equipment", "remove_equipment", "drop_random_items",
	"use_random_consumable", "spawn_npc_squads", "spawn_mutant_squads",
	"spawn_random_hostile_squad", "spawn_random_mutant_pack", "teleport_random",
	"spawn_random_npcs", "spawn_random_mutants", "delete_recent_saves",
	"teleport_random_level", "kill_nearby_npcs", "spawn_named_companion",
	"spawn_mutant_companion", "spawn_donor_boss",
	"kill_companions", "give_random_stash",
	"actor_boost", "actor_parameters", "tell_joke", "hide_hud",
	"change_player_name"
}
for _, id in ipairs(optional_ids) do
	local effect = load_module(optional_root,
		"anomaly_donation_bridge_effect_" .. id)
	assert(type(effect.on_game_start) == "function", id)
	effect.on_game_start()
	assert(anomaly_donation_bridge_registry.get(id) ~= nil,
		"optional effect did not register independently: " .. id)
end
assert(#anomaly_donation_bridge_registry.ids() == 41,
    "the optional pack must add exactly 37 independent templates")

-- The sleep template keeps the original stock path by default, while its
-- opt-in fallback must work without ui_sleep_dialog and always restore input.
local function run_sleep_smoke()
	local sleep = assert(_G.anomaly_donation_bridge_effect_sleep_hours)
	local handler = assert(anomaly_donation_bridge_registry.get("sleep_hours"))
	local events = {}
	local now = 5000
	local slot_item = {}
	local actor = { power = 0, slot = 3, sleeping = false }
	function actor:active_slot() return self.slot end
	function actor:activate_slot(slot) self.slot = slot end
	function actor:item_in_slot(slot) return slot == 3 and slot_item or nil end
	function actor:is_talking() return false end
	function actor:stop_talk() events[#events + 1] = { "stop_talk" } end
	function actor:give_info_portion(info)
		if info == "actor_is_sleeping" then self.sleeping = true end
	end
	function actor:disable_info_portion(info)
		if info == "actor_is_sleeping" then self.sleeping = false end
	end
	db = { actor = actor }
	function time_global() return now end
	function SendScriptCallback(name, hours)
		events[#events + 1] = { "callback", name, hours }
	end
	function get_console_cmd(_, name)
		return name == "snd_volume_music" and "0.8" or "0.9"
	end
	function exec_console_cmd(command)
		events[#events + 1] = { "console", command }
	end
	function hide_hud_all() events[#events + 1] = { "hide_hud" } end
	level = {
		add_cam_effector = function(path, id, cyclic, callback)
			events[#events + 1] = { "cam", path, id, cyclic, callback }
		end,
		add_pp_effector = function(path, id, cyclic)
			events[#events + 1] = { "pp", path, id, cyclic }
		end,
		remove_cam_effector = function(id) events[#events + 1] = { "remove_cam", id } end,
		remove_pp_effector = function(id) events[#events + 1] = { "remove_pp", id } end,
		change_game_time = function(days, hours, minutes)
			events[#events + 1] = { "time", days, hours, minutes }
		end,
		disable_input = function() events[#events + 1] = { "input", false } end,
		enable_input = function() events[#events + 1] = { "input", true } end,
		show_weapon = function(value) events[#events + 1] = { "weapon", value } end,
		hide_indicators_safe = function() events[#events + 1] = { "hud", false } end,
		show_indicators = function() events[#events + 1] = { "hud", true } end
	}
	local stock_hours = nil
	ui_sleep_dialog = {
		sleep_forced = function(hours) stock_hours = hours end
	}
	local stock_context = { actor = actor, now = now }
	assert(handler.can_execute(stock_context, { hours = "6" }) == true)
	assert(stock_context.options.alternative == false)
	assert(select(1, handler.execute(stock_context)) == "executed")
	assert(stock_hours == 6, "the default path must still call stock sleep_forced")

	ui_sleep_dialog = nil
	local alternative_context = { actor = actor, now = now }
	assert(handler.can_execute(alternative_context, {
		hours = "7", alternative_sleep = "true"
	}) == true)
	assert(select(1, handler.execute(alternative_context)) == "executed")
	assert(actor.slot == 0 and actor.sleeping == true,
		"the alternative path must hide the weapon and mark the actor sleeping")
	local sleep_state = assert(rawget(_G, "__anomaly_donation_bridge_sleep_hours_v2"))
	assert(sleep_state.active == true and sleep_state.time_applied == false)
	sleep.alternative_sleep_midpoint()
	assert(sleep_state.time_applied == true and actor.power == 1)
	local time_changes, callbacks = 0, 0
	for _, event in ipairs(events) do
		if event[1] == "time" then
			time_changes = time_changes + 1
			assert(event[2] == 0 and event[3] == 7 and event[4] == 0)
		elseif event[1] == "callback" then
			callbacks = callbacks + 1
			assert(event[2] == "actor_on_sleep" and event[3] == 7)
		end
	end
	assert(time_changes == 1 and callbacks == 1,
		"time and standard sleep consequences must be applied exactly once")
	sleep.alternative_sleep_midpoint()
	sleep.alternative_sleep_finish()
	assert(sleep_state.active == false and actor.slot == 3 and actor.sleeping == false,
		"the alternative path must restore the previous slot and sleeping flag")
	for _, event in ipairs(events) do
		assert(event[1] ~= "remove_cam" and event[1] ~= "remove_pp",
			"a native effector must never be removed re-entrantly from its callback")
	end

	-- Loading a save during the animation must only clean up the transient UI;
	-- it may not apply sleep time to the loaded save.
	now = 25000
	local interrupted_context = { actor = actor, now = now }
	assert(handler.can_execute(interrupted_context, {
		hours = "4", alternative_sleep = "true"
	}) == true)
	assert(select(1, handler.execute(interrupted_context)) == "executed")
	local changes_before_load = time_changes
	handler.on_game_load()
	assert(sleep_state.active == false and actor.slot == 3 and actor.sleeping == false)
	local changes_after_load = 0
	for _, event in ipairs(events) do
		if event[1] == "time" then changes_after_load = changes_after_load + 1 end
	end
	assert(changes_after_load == changes_before_load,
		"F9 cleanup must not advance time in the loaded save")
end

run_sleep_smoke()

-- File extensions are discovered without changing the executable or the
-- built-in catalog.  Dementia must remap the player's current action binds in
-- memory, never edit USER.ltx, never leave a fixed point and keep its mapping
-- and absolute deadline across an F9 quick-load in the same game process.
local function run_dementia_smoke()
local dementia = load_module(optional_root,
	"anomaly_donation_bridge_effect_hardcoon_adb_dementia")
key_bindings = {
	kCROUCH = 1, kACCEL = 2, kJUMP = 3, kSPRINT_TOGGLE = 4,
	kQUICK_USE_1 = 5, kQUICK_USE_2 = 6, kQUICK_USE_3 = 7, kQUICK_USE_4 = 8,
	kINVENTORY = 9, kACTIVE_JOBS = 10, kCUSTOM20 = 11, kDETECTOR = 12,
	kTORCH = 13, kWPN_FIRE = 14, kWPN_ZOOM = 15,
	kL_LOOKOUT = 16, kR_LOOKOUT = 17,
	kFWD = 18, kBACK = 19, kL_STRAFE = 20, kR_STRAFE = 21,
	kUSE = 22, kWPN_RELOAD = 23
}
local dementia_diks = {}
for _, action_id in pairs(key_bindings) do dementia_diks[action_id] = action_id + 100 end
-- One empty assignment must be ignored without rejecting the remaining group.
dementia_diks[key_bindings.kSPRINT_TOGGLE] = 0
function bind_to_dik(action_id) return dementia_diks[action_id] or 0 end
local dementia_events = {}
level = {
	press_action = function(dik) dementia_events[#dementia_events + 1] = { "press", dik } end,
	hold_action = function(dik) dementia_events[#dementia_events + 1] = { "hold", dik } end,
	release_action = function(dik) dementia_events[#dementia_events + 1] = { "release", dik } end
}
local dementia_now = 1000
function time_global() return dementia_now end
db = { actor = {
	id = function() return 1 end
} }
dementia.on_game_start()
assert(#anomaly_donation_bridge_registry.ids() == 42)
local dementia_handler = assert(anomaly_donation_bridge_registry.get("hardcoon.adb.dementia"))
assert(select(1, dementia_handler.is_supported({ actor = db.actor })) == true)
local dementia_context = { actor = db.actor, now = dementia_now }
assert(dementia_handler.can_execute(dementia_context, {
	duration_seconds = "30", mix_stance = "true", mix_quick_slots = "false",
	mix_equipment = "false", swap_fire_aim = "false",
	swap_lookout = "false", invert_movement = "false"
}) == true)
assert(select(1, dementia_handler.execute(dementia_context)) == "executed")
local press_callback = script_callbacks.on_before_key_press[#script_callbacks.on_before_key_press]
local hold_callback = script_callbacks.on_before_key_hold[#script_callbacks.on_before_key_hold]
local release_callback = script_callbacks.on_before_key_release[#script_callbacks.on_before_key_release]
for _, source_action in ipairs({
	key_bindings.kCROUCH, key_bindings.kACCEL, key_bindings.kJUMP
}) do
	local source_dik = dementia_diks[source_action]
	local flags = { ret_value = true }
	press_callback(source_dik, source_action, false, flags)
	assert(flags.ret_value == false)
	local event = dementia_events[#dementia_events]
	assert(event[1] == "press" and event[2] ~= source_dik,
		"a shuffled action must never keep its original key")
	local hold_flags = { ret_value = true }
	hold_callback(source_dik, source_action, false, hold_flags)
	assert(hold_flags.ret_value == false and dementia_events[#dementia_events][1] == "hold")
	local release_flags = { ret_value = true }
	release_callback(source_dik, source_action, false, release_flags)
	assert(release_flags.ret_value == false and
		dementia_events[#dementia_events][1] == "release")
end
local disabled_flags = { ret_value = true }
press_callback(dementia_diks[key_bindings.kCROUCH], key_bindings.kCROUCH,
	true, disabled_flags)
assert(disabled_flags.ret_value == true, "disabled engine input must stay untouched")

-- Swapped fire/aim must allow one physical button to stay held while the
-- other is clicked. In particular, firing may not release synthetic aim.
local mouse_context = { actor = db.actor, now = 1500 }
assert(dementia_handler.can_execute(mouse_context, {
	duration_seconds = "30", mix_stance = "false", mix_quick_slots = "false",
	mix_equipment = "false", swap_fire_aim = "true",
	swap_lookout = "false", invert_movement = "false"
}) == true)
assert(select(1, dementia_handler.execute(mouse_context)) == "executed")
local fire_dik = dementia_diks[key_bindings.kWPN_FIRE]
local aim_dik = dementia_diks[key_bindings.kWPN_ZOOM]
local aim_flags = { ret_value = true }
local before_aim = #dementia_events
press_callback(fire_dik, key_bindings.kWPN_FIRE, false, aim_flags)
assert(aim_flags.ret_value == false and #dementia_events == before_aim + 1 and
	dementia_events[#dementia_events][1] == "press" and
	dementia_events[#dementia_events][2] == aim_dik)
local fire_flags = { ret_value = true }
local before_fire = #dementia_events
press_callback(aim_dik, key_bindings.kWPN_ZOOM, false, fire_flags)
assert(fire_flags.ret_value == false and #dementia_events == before_fire + 1 and
	dementia_events[#dementia_events][1] == "press" and
	dementia_events[#dementia_events][2] == fire_dik,
	"firing must not release the synthetic aim key")
release_callback(aim_dik, key_bindings.kWPN_ZOOM, false, { ret_value = true })
release_callback(fire_dik, key_bindings.kWPN_FIRE, false, { ret_value = true })

-- The rejected local F/R and mouse experiment must stay out of the release.
assert(dementia_handler.can_execute({ actor = db.actor }, {
	swap_use_reload = "true"
}) == false, "the canceled F/R option must not return in the release")
assert(dementia_handler.can_execute({ actor = db.actor }, {
	invert_mouse_vertical = "true"
}) == false, "the canceled mouse option must not return in the release")
local unchanged_use = { ret_value = true }
press_callback(dementia_diks[key_bindings.kUSE], key_bindings.kUSE, false, unchanged_use)
assert(unchanged_use.ret_value == true, "interaction must stay untouched")

local f9_context = { actor = db.actor, now = 2000 }
assert(dementia_handler.can_execute(f9_context, {
	duration_seconds = "30", mix_stance = "true", mix_quick_slots = "false",
	mix_equipment = "false", swap_fire_aim = "false",
	swap_lookout = "false", invert_movement = "false"
}) == true)
assert(select(1, dementia_handler.execute(f9_context)) == "executed")
local f9_source_dik = dementia_diks[key_bindings.kCROUCH]
local before_f9_flags = { ret_value = true }
press_callback(f9_source_dik, key_bindings.kCROUCH, false, before_f9_flags)
assert(before_f9_flags.ret_value == false and
	dementia_events[#dementia_events][1] == "press")
local target_before_f9 = dementia_events[#dementia_events][2]
local events_before_load = #dementia_events
local process_state = assert(rawget(_G, "__anomaly_donation_bridge_dementia_v1"))
process_state.active = false
process_state.expires_at = 0
process_state.mapping = {}
process_state.pressed = {}
dementia_handler.on_game_load()
assert(process_state.active == true and process_state.expires_at == 32000,
	"F9 must restore the timer after the Lua state was rebuilt")
local target_was_released = false
for index = events_before_load + 1, #dementia_events do
	local event = dementia_events[index]
	if event[1] == "release" and event[2] == target_before_f9 then
		target_was_released = true
	end
end
assert(target_was_released,
	"F9 must release synthetic actions after restoring volatile process state")
local after_f9_flags = { ret_value = true }
press_callback(f9_source_dik, key_bindings.kCROUCH, false, after_f9_flags)
assert(after_f9_flags.ret_value == false and
	dementia_events[#dementia_events][1] == "press" and
	dementia_events[#dementia_events][2] == target_before_f9,
	"F9 must preserve the active mapping for the remaining duration")
dementia_handler.update(31999)
local before_expiry_flags = { ret_value = true }
release_callback(f9_source_dik, key_bindings.kCROUCH, false, before_expiry_flags)
assert(before_expiry_flags.ret_value == false,
	"the effect must remain active until its absolute deadline")
dementia_handler.update(32000)
local expired_flags = { ret_value = true }
press_callback(f9_source_dik, key_bindings.kCROUCH, false, expired_flags)
assert(expired_flags.ret_value == true,
	"the in-memory F9 timer must still expire at the original deadline")
end
run_dementia_smoke()

-- The WASD warmup uses the player's current action bindings, blocks every
-- participating action until completion, resets on a wrong key and restores
-- the active flag after an F9-style Lua state rebuild. F9 starts a fresh
-- sequence at step one; native UI objects are detached before HUD teardown.
local function run_wasd_warmup_smoke()
local warmup = load_module(optional_root,
	"anomaly_donation_bridge_effect_hardcoon_adb_wasd_warmup")
key_bindings = {
	kFWD = 1, kBACK = 2, kL_STRAFE = 3, kR_STRAFE = 4, kUSE = 5,
	kACCEL = 6, kCROUCH = 7, kJUMP = 8, kSPRINT_TOGGLE = 9,
	kQUICK_LOAD = 10
}
local warmup_diks = {
	[1] = 17, [2] = 31, [3] = 30, [4] = 32, [5] = 33,
	[6] = 42, [7] = 29, [8] = 57, [9] = 45, [10] = 67
}
function bind_to_dik(action_id) return warmup_diks[action_id] or 0 end
DIK_keys = {
    DIK_END = 207,
    DIK_PRIOR = 201, DIK_NEXT = 209,
	DIK_W = 17, DIK_S = 31, DIK_A = 30, DIK_D = 32, DIK_F = 33,
	DIK_LSHIFT = 42, DIK_LCONTROL = 29, DIK_SPACE = 57, DIK_X = 45,
	DIK_F9 = 67
}
local warmup_events = {}
local warmup_now = 40000
function time_global() return warmup_now end
level = {
	release_action = function(dik)
		warmup_events[#warmup_events + 1] = { "release", dik }
	end
}
local warmup_rendered = nil
local warmup_children = {}
local warmup_hud = {
	AddDialogToRender = function(_, window)
		assert(window ~= nil and warmup_rendered == nil)
		warmup_rendered = window
	end,
	RemoveDialogToRender = function(_, window)
		assert(window == warmup_rendered)
		warmup_rendered = nil
		warmup_children = {}
	end
}
function get_hud() return warmup_hud end
function vector2()
	return { set = function(self, x, y) self.x, self.y = x, y end }
end
function GetARGB(a, r, g, b) return a * 16777216 + r * 65536 + g * 256 + b end
function GetFontGraffiti22Russian() return "warmup-prompt-font" end
function GetFontGraffiti19Russian() return "warmup-progress-font" end
CGameFont = { alCenter = 2 }
function CUIScriptWnd()
	local window = { position = nil, size = nil, shown = nil }
	function window:SetAutoDelete(value) assert(value == false) end
	function window:SetWndPos(position)
		self.position = { x = position.x, y = position.y }
	end
	function window:SetWndSize(size)
		self.size = { x = size.x, y = size.y }
	end
	function window:AttachChild(child) warmup_children[#warmup_children + 1] = child end
	function window:Show(value) self.shown = value end
	return window
end
function CUITextWnd()
	local control = {
		text = "", color = nil, complex = nil, width = 0,
		position = nil, size = nil, font = nil, alignment = nil
	}
	function control:SetAutoDelete(value) assert(value == true) end
	function control:SetFont(value) self.font = value end
	function control:SetTextComplexMode(value) self.complex = value end
	function control:SetTextColor(value) self.color = value end
	function control:SetTextAlignment(value) self.alignment = value end
	function control:SetText(value) self.text = value end
	function control:SetWndSize(size)
		self.size = { x = size.x, y = size.y }
		self.width = size.x
	end
	function control:AdjustWidthToText() self.width = #self.text * 12 end
	function control:GetWidth() return self.width end
	function control:SetWndPos(position)
		self.position = { x = position.x, y = position.y }
	end
	return control
end
db = { actor = { id = function() return 1 end } }
warmup.on_game_start()
assert(#anomaly_donation_bridge_registry.ids() == 43)
local warmup_handler = assert(
	anomaly_donation_bridge_registry.get("hardcoon.adb.wasd_warmup"))
assert(select(1, warmup_handler.is_supported({ actor = db.actor })) == true)
local warmup_default_context = { actor = db.actor }
assert(warmup_handler.can_execute(warmup_default_context, {}) == true and
	warmup_default_context.options.count == 10,
	"the WASD warmup must default to ten required presses")
local warmup_context = { actor = db.actor }
assert(warmup_handler.can_execute(warmup_context, { press_count = "6" }) == true)
local original_random = math.random
math.random = function(maximum)
	assert(maximum == 9)
	return 1
end
local warmup_status, warmup_detail = warmup_handler.execute(warmup_context)
math.random = original_random
assert(warmup_status == "executed" and
	warmup_detail == "wasd_warmup_presses_6_actions_9")
local warmup_state = assert(rawget(_G, "__anomaly_donation_bridge_wasd_warmup_v1"))
assert(warmup_state.active and #warmup_state.sequence == 6 and
	warmup_state.index == 1 and warmup_rendered ~= nil and #warmup_children == 4 and
	warmup_rendered.position.x == 0 and warmup_rendered.position.y == 0 and
	warmup_rendered.size.x == 1024 and warmup_rendered.size.y == 768 and
	warmup_rendered.shown == true)
local prefix_text = anomaly_donation_bridge_common.game_text_utf8(warmup_children[1].text)
local key_text = anomaly_donation_bridge_common.game_text_utf8(warmup_children[2].text)
local suffix_text = anomaly_donation_bridge_common.game_text_utf8(warmup_children[3].text)
local progress_text = anomaly_donation_bridge_common.game_text_utf8(warmup_children[4].text)
local white = GetARGB(255, 240, 240, 240)
local gold = GetARGB(255, 238, 153, 26)
assert(prefix_text:find("Нажмите", 1, true) and key_text == "W" and
	suffix_text == " »" and progress_text == "Прогресс 1/6" and
	not prefix_text:find("%c", 1, true) and
	warmup_children[1].color == white and warmup_children[2].color == gold and
	warmup_children[3].color == white and warmup_children[4].color == white and
	warmup_children[1].position.y == 330 and warmup_children[2].position.y == 330 and
	warmup_children[3].position.y == 330 and warmup_children[4].position.y == 362 and
	warmup_children[1].font == "warmup-prompt-font" and
	warmup_children[2].font == "warmup-prompt-font" and
	warmup_children[4].font == "warmup-progress-font" and
	warmup_children[1].alignment == 2 and warmup_children[4].alignment == 2,
	"the autonomous HUD must use separate lines and a separate gold key control")
local warmup_press = script_callbacks.on_before_key_press[
	#script_callbacks.on_before_key_press]
local warmup_hold = script_callbacks.on_before_key_hold[
	#script_callbacks.on_before_key_hold]
local warmup_release = script_callbacks.on_before_key_release[
	#script_callbacks.on_before_key_release]
local hold_flags, release_flags = { ret_value = true }, { ret_value = true }
warmup_hold(warmup_diks[1], 1, false, hold_flags)
warmup_release(warmup_diks[1], 1, false, release_flags)
assert(hold_flags.ret_value == false and release_flags.ret_value == false,
	"movement must stay blocked while the warmup is active")
local wrong_flags = { ret_value = true }
warmup_press(warmup_diks[3], 3, false, wrong_flags)
assert(wrong_flags.ret_value == false and warmup_state.index == 1,
	"a wrong movement key must reset progress and stay blocked")
local correct_flags = { ret_value = true }
warmup_press(warmup_diks[warmup_state.sequence[1]],
	warmup_state.sequence[1], false, correct_flags)
assert(correct_flags.ret_value == false and warmup_state.index == 2)
local unrelated_flags = { ret_value = true }
warmup_press(250, 250, false, unrelated_flags)
assert(unrelated_flags.ret_value == true and warmup_state.index == 1,
	"a wrong unrelated key resets progress without blocking its normal action")
warmup_press(warmup_diks[warmup_state.sequence[1]],
	warmup_state.sequence[1], false, { ret_value = true })
assert(warmup_state.index == 2)
local quickload_flags = { ret_value = true }
warmup_press(warmup_diks[key_bindings.kQUICK_LOAD],
	key_bindings.kQUICK_LOAD, false, quickload_flags)
assert(quickload_flags.ret_value == true and warmup_state.index == 2,
	"F9 must remain available and may not count as a wrong key")
assert(warmup_rendered == nil,
	"F9 must detach the native dialog before the engine starts loading")
warmup_handler.on_level_changing()
assert(warmup_rendered == nil,
	"level changing must detach the autonomous HUD before engine teardown")
warmup_state.active = false
warmup_state.sequence = {}
warmup_state.index = 1
warmup_state.blocked_actions = {}
warmup_state.blocked_diks = {}
warmup_now = 50000
warmup_handler.on_game_load()
assert(warmup_state.active and warmup_state.index == 1 and
	warmup_state.press_count == 6 and #warmup_state.sequence == 6,
	"F9 must restart an unfinished warmup with a fresh full sequence")
assert(warmup_rendered == nil,
	"on_game_load must not create native UI while CUIGameCustom is rebuilding")
warmup_handler.update(51499)
assert(warmup_rendered == nil,
	"the autonomous HUD must observe its post-load safety delay")
warmup_handler.update(51500)
assert(warmup_rendered ~= nil and #warmup_children == 4,
	"the autonomous HUD must be recreated after the post-load safety delay")
while warmup_state.active do
	local action_id = warmup_state.sequence[warmup_state.index]
	local flags = { ret_value = true }
	warmup_press(warmup_diks[action_id], action_id, false, flags)
	assert(flags.ret_value == false)
end
assert(warmup_rendered == nil and #warmup_state.sequence == 0,
	"the final correct key must remove the block and HUD prompt")
warmup_handler.on_game_load()
assert(not warmup_state.active, "completed warmup state must not return after F9")
end
run_wasd_warmup_smoke()

-- Temporary actor boosts must change all requested capabilities and restore
-- the exact previous values after the duration expires.
local boost_values = {
	max_weight = 50, max_walk_weight = 60, jump_speed = 5,
	sprint_koef = 1.2, run_coef = 1.1
}
local boost_now_value = 1000
function time_global() return boost_now_value end
local pda_messages = {}
news_manager = {
	send_tip = function(actor, text)
		assert(actor == db.actor)
		pda_messages[#pda_messages + 1] = text
		return true
	end
}
local boost_actor = {}
function boost_actor:id() return 1 end
function boost_actor:get_actor_max_weight() return boost_values.max_weight end
function boost_actor:set_actor_max_weight(value) boost_values.max_weight = value end
function boost_actor:get_actor_max_walk_weight() return boost_values.max_walk_weight end
function boost_actor:set_actor_max_walk_weight(value) boost_values.max_walk_weight = value end
function boost_actor:get_actor_jump_speed() return boost_values.jump_speed end
function boost_actor:set_actor_jump_speed(value) boost_values.jump_speed = value end
function boost_actor:get_actor_sprint_koef() return boost_values.sprint_koef end
function boost_actor:set_actor_sprint_koef(value) boost_values.sprint_koef = value end
function boost_actor:get_actor_run_coef() return boost_values.run_coef end
function boost_actor:set_actor_run_coef(value) boost_values.run_coef = value end
function boost_actor:get_actor_walk_accel() return 11.5 end
db = { actor = boost_actor }

-- Random stash rewards delegate selection, content and map marking to the
-- stock treasure manager, then add the actual location to the PDA message.
local stash_server = { id = 501, m_game_vertex_id = 77 }
local stash_get_calls, stash_set_calls = 0, 0
treasure_manager = {
	get_random_stash = function()
		stash_get_calls = stash_get_calls + 1
		return 501, "Тайник", "Описание", "Подсказка"
	end,
	set_random_stash = function(no_spot, marker, bonus, id)
		assert(no_spot == nil and bonus == nil and id == 501 and
			type(marker) == "string" and marker:find("%c[", 1, true) ~= nil)
		stash_set_calls = stash_set_calls + 1
		return id
	end
}
dynamic_news_helper = {
	GetPointDescription = function(object)
		assert(object == stash_server)
		return "Кордон, возле деревни новичков"
	end
}
local stash_life = {
	object = function(_, id) return id == 501 and stash_server or nil end,
	level_name = function(_, level_id)
		assert(level_id == 3)
		return "l01_escape"
	end
}
function alife() return stash_life end
function game_graph()
	return { vertex = function(_, id)
		assert(id == 77)
		return { level_id = function() return 3 end }
	end }
end
game = { translate_string = function(key) return key end }
local stash_handler = anomaly_donation_bridge_registry.get("give_random_stash")
local stash_context = { actor = boost_actor }
assert(stash_handler.can_execute(stash_context, {}) == true)
local stash_status, stash_detail = stash_handler.execute(stash_context)
assert(stash_status == "executed" and
	stash_detail == "random_stash_501_level_l01_escape")
assert(stash_get_calls == 1 and stash_set_calls == 1 and
	pda_messages[#pda_messages] == anomaly_donation_bridge_common.game_text_cp1251(
		"Получена наводка на тайник: Кордон, возле деревни новичков"))

local persisted_actor_values = {}
function se_save_var(id, name, key, value)
	assert(id == 1 and name == nil)
	persisted_actor_values[key] = value
end
function se_load_var(id, name, key)
	assert(id == 1 and name == nil)
	return persisted_actor_values[key]
end

-- A timed weapon drop must start even with empty hands, drop every weapon
-- equipped during the interval and survive F9 using bounded remaining time.
local timer_now = 1000
function time_global() return timer_now end
local held_weapon = nil
local dropped_sections = {}
local weapon_serial = 100
local function make_weapon(section)
	weapon_serial = weapon_serial + 1
	local item = { object_id = weapon_serial, section_id = section }
	function item:id() return self.object_id end
	function item:section() return self.section_id end
	return item
end
function boost_actor:active_slot() return held_weapon ~= nil and 2 or 0 end
function boost_actor:active_item() return held_weapon end
function boost_actor:drop_item(item)
	assert(item == held_weapon)
	dropped_sections[#dropped_sections + 1] = item:section()
	held_weapon = nil
end
function get_object_story_id() return -1 end
function system_ini()
	return {
		section_exist = function(_, section)
			return section == "wpn_test" or section == "wpn_second" or
				section == "wpn_third"
		end,
		line_exist = function() return false end
	}
end

local drop_handler = anomaly_donation_bridge_registry.get("drop_active_weapon")
held_weapon = make_weapon("wpn_test")
local one_drop_context = {
	actor = boost_actor, now = timer_now,
	command = { donation_id = "one-drop" }
}
assert(drop_handler.can_execute(one_drop_context, {
	wait_seconds = "0", duration_seconds = "0"
}) == true)
assert(select(1, drop_handler.execute(one_drop_context)) == "executed")
assert(#dropped_sections == 1 and persisted_actor_values.adb_weapon_drop_timer_v1 == nil)
assert(drop_handler.can_execute({ actor = boost_actor, now = timer_now }, {
	wait_seconds = "0", duration_seconds = "9"
}) == false)

timer_now = 2000
local timed_drop_context = { actor = boost_actor, now = timer_now }
assert(drop_handler.can_execute(timed_drop_context, {
	wait_seconds = "0", duration_seconds = "30"
}) == true)
assert(select(1, drop_handler.execute(timed_drop_context)) == "executed")
assert(persisted_actor_values.adb_weapon_drop_timer_v1.remaining_ms == 30000)
held_weapon = make_weapon("wpn_second")
drop_handler.update(2100)
assert(#dropped_sections == 2 and dropped_sections[2] == "wpn_second")
drop_handler.update(7000)
assert(persisted_actor_values.adb_weapon_drop_timer_v1.remaining_ms == 25000)
timer_now = 9000
drop_handler.on_game_load()
held_weapon = make_weapon("wpn_third")
drop_handler.update(9100)
assert(#dropped_sections == 3 and dropped_sections[3] == "wpn_third")
drop_handler.update(34000)
assert(persisted_actor_values.adb_weapon_drop_timer_v1 == nil)
persisted_actor_values.adb_weapon_drop_timer_v1 = {
	version = 1, active = true, remaining_ms = 600001
}
timer_now = 35000
drop_handler.on_game_load()
assert(persisted_actor_values.adb_weapon_drop_timer_v1 == nil)

-- The stock ActorMenu callback blocks only the normal inventory. The timer is
-- restored from a save as remaining time and malformed values are discarded.
local inventory_handler = anomaly_donation_bridge_registry.get("block_inventory")
local inventory_callbacks = script_callbacks.ActorMenu_on_before_init_mode
assert(type(inventory_callbacks) == "table" and #inventory_callbacks == 1)
timer_now = 40000
local inventory_context = { actor = boost_actor, now = timer_now }
assert(inventory_handler.can_execute(inventory_context, {
	duration_seconds = "30"
}) == true)
assert(select(1, inventory_handler.execute(inventory_context)) == "executed")
assert(persisted_actor_values.adb_inventory_lock_v1.remaining_ms == 30000)
local stock_inventory_closed = 0
ui_inventory = { GUI = { mode = "inventory" } }
function ui_inventory.GUI:Close() stock_inventory_closed = stock_inventory_closed + 1 end
inventory_handler.update(40100)
assert(stock_inventory_closed == 1)
ui_inventory = nil
local flags = {}
inventory_callbacks[1]("inventory", flags)
assert(flags.ret == true)
local trade_flags = {}
inventory_callbacks[1]("trade", trade_flags)
assert(trade_flags.ret == nil)
inventory_handler.update(50000)
assert(persisted_actor_values.adb_inventory_lock_v1.remaining_ms == 20000)
timer_now = 55000
inventory_handler.on_game_load()
flags = {}
inventory_callbacks[1]("inventory", flags)
assert(flags.ret == true)
inventory_handler.update(75000)
assert(persisted_actor_values.adb_inventory_lock_v1 == nil)
flags = {}
inventory_callbacks[1]("inventory", flags)
assert(flags.ret == nil)
persisted_actor_values.adb_inventory_lock_v1 = {
	version = 1, active = true, remaining_ms = 700000
}
timer_now = 80000
inventory_handler.on_game_load()
assert(persisted_actor_values.adb_inventory_lock_v1 == nil)

boost_now_value = 1000
function time_global() return boost_now_value end
local boost_handler = anomaly_donation_bridge_registry.get("actor_boost")
local boost_context = { actor = boost_actor, now = 1000 }
assert(boost_handler.can_execute(boost_context, {
	jump_percent = "100", speed_percent = "50", carry_weight_kg = "25",
	duration_seconds = "5"
}) == true)
assert(select(1, boost_handler.execute(boost_context)) == "executed")
assert(boost_values.max_weight == 75 and boost_values.max_walk_weight == 85 and
	boost_values.jump_speed == 10 and math.abs(boost_values.sprint_koef - 1.8) < 0.0001 and
	math.abs(boost_values.run_coef - 1.65) < 0.0001)
assert(pda_messages[#pda_messages] == anomaly_donation_bridge_common.game_text_cp1251(
	"Параметры героя временно изменены на 5 сек."))
assert(persisted_actor_values.adb_actor_boost_v1 == nil,
	"Temporary boosts must not store bonuses/timers in the game save")
boost_values.max_weight, boost_values.max_walk_weight, boost_values.jump_speed = 50, 60, 5
boost_values.sprint_koef, boost_values.run_coef = 1.2, 1.1
boost_now_value = 2500
boost_handler.on_game_load()
assert(boost_values.max_weight == 75 and boost_values.max_walk_weight == 85 and
	boost_values.jump_speed == 10 and math.abs(boost_values.sprint_koef - 1.8) < 0.0001 and
	math.abs(boost_values.run_coef - 1.65) < 0.0001)
assert(persisted_actor_values.adb_actor_boost_v1 == nil)
boost_handler.update(5999)
assert(boost_values.max_weight == 75)
boost_handler.update(6000)
assert(boost_values.max_weight == 50 and boost_values.max_walk_weight == 60 and
	boost_values.jump_speed == 5 and boost_values.sprint_koef == 1.2 and
	boost_values.run_coef == 1.1)
assert(persisted_actor_values.adb_actor_boost_v1 == nil)

-- Negative temporary changes are valid as long as every resulting runtime
-- value remains positive.
local negative_context = { actor = boost_actor, now = 7000 }
assert(boost_handler.can_execute(negative_context, {
	jump_percent = "-50", speed_percent = "-50", carry_weight_kg = "-10",
	duration_seconds = "5"
}) == true)
assert(select(1, boost_handler.execute(negative_context)) == "executed")
assert(boost_values.max_weight == 40 and boost_values.max_walk_weight == 50 and
	boost_values.jump_speed == 2.5 and math.abs(boost_values.sprint_koef - 0.6) < 0.0001 and
	math.abs(boost_values.run_coef - 0.55) < 0.0001)
boost_handler.update(12000)

-- Profiles created by older releases may still contain +300% speed. They
-- remain executable, but both runtime coefficients are reduced together so
-- the resulting sprint velocity stays below the 13 m/s safety ceiling.
local legacy_boost_context = { actor = boost_actor, now = 13000 }
assert(boost_handler.can_execute(legacy_boost_context, {
	jump_percent = "0", speed_percent = "300", carry_weight_kg = "0",
	duration_seconds = "5"
}) == true)
local legacy_boost_status, legacy_boost_detail = boost_handler.execute(legacy_boost_context)
assert(legacy_boost_status == "executed" and
	legacy_boost_detail:find("speed_capped", 1, true))
assert(boost_values.run_coef * boost_values.sprint_koef * 11.5 / 10 <= 13.0001)
boost_handler.update(18000)
assert(boost_values.sprint_koef == 1.2 and boost_values.run_coef == 1.1)

-- Permanent actor parameters are stored in the save and reapplied after F9.
local parameters_handler = anomaly_donation_bridge_registry.get("actor_parameters")
local parameters_context = { actor = boost_actor }
assert(parameters_handler.can_execute(parameters_context, {
	jump_percent = "100", speed_percent = "50", carry_weight_kg = "25",
	-- Legacy 1.0.63 profiles may still send this key. It must be ignored rather
	-- than blocking the supported parameter changes.
	max_health_percent = "100"
}) == true)
assert(select(1, parameters_handler.execute(parameters_context)) == "executed")
assert(boost_values.max_weight == 75 and boost_values.max_walk_weight == 85 and
	boost_values.jump_speed == 10 and math.abs(boost_values.sprint_koef - 1.8) < 0.0001 and
	math.abs(boost_values.run_coef - 1.65) < 0.0001)
assert(type(persisted_actor_values.adb_actor_parameters_v1) == "table" and
	persisted_actor_values.adb_actor_parameters_v1.version == 2 and
	persisted_actor_values.adb_actor_parameters_v1.target.max_health == nil)
boost_values.max_weight, boost_values.max_walk_weight, boost_values.jump_speed = 50, 60, 5
boost_values.sprint_koef, boost_values.run_coef = 1.2, 1.1
parameters_handler.on_game_load()
parameters_handler.update()
assert(boost_values.max_weight == 75 and boost_values.max_walk_weight == 85 and
	boost_values.jump_speed == 10)
assert(pda_messages[#pda_messages] == anomaly_donation_bridge_common.game_text_cp1251(
	"Параметры героя изменены и сохранены: прыжок 100%, скорость 50%, вес 25 кг."))

-- Permanent changes may be triggered repeatedly, but must stop growing at
-- the same ceiling instead of eventually breaking movement physics.
local legacy_parameters_context = { actor = boost_actor }
assert(parameters_handler.can_execute(legacy_parameters_context, {
	jump_percent = "0", speed_percent = "300", carry_weight_kg = "0"
}) == true)
local legacy_parameters_status, legacy_parameters_detail =
	parameters_handler.execute(legacy_parameters_context)
assert(legacy_parameters_status == "executed" and
	legacy_parameters_detail:find("speed_capped", 1, true))
assert(boost_values.run_coef * boost_values.sprint_koef * 11.5 / 10 <= 13.0001)
local capped_run, capped_sprint = boost_values.run_coef, boost_values.sprint_koef
local repeated_parameters_context = { actor = boost_actor }
assert(parameters_handler.can_execute(repeated_parameters_context, {
	jump_percent = "0", speed_percent = "300", carry_weight_kg = "0"
}) == true)
assert(select(1, parameters_handler.execute(repeated_parameters_context)) == "executed")
assert(math.abs(boost_values.run_coef - capped_run) < 0.0001 and
	math.abs(boost_values.sprint_koef - capped_sprint) < 0.0001)

-- Timed intoxication is restored after F9 and keeps the original deadline.
local effect_now_value, drunk_adds, drunk_removes = 1000, 0, 0
function time_global() return effect_now_value end
level = {
	add_complex_effector = function(section, id)
		assert(section == "effector_alcohol" and id == 29781)
		drunk_adds = drunk_adds + 1
	end,
	remove_complex_effector = function(id)
		assert(id == 29781)
		drunk_removes = drunk_removes + 1
	end
}
function system_ini()
	return { section_exist = function(_, section) return section == "effector_alcohol" end }
end
local drunk_handler = anomaly_donation_bridge_registry.get("get_drunk")
local drunk_context = { actor = boost_actor, now = effect_now_value }
assert(drunk_handler.can_execute(drunk_context, { duration_seconds = "5" }) == true)
assert(select(1, drunk_handler.execute(drunk_context)) == "executed")
assert(drunk_adds == 1 and
	type(persisted_actor_values.adb_drunk_effect_v1) == "table" and
	persisted_actor_values.adb_drunk_effect_v1.remaining_ms == 5000)
effect_now_value = 2500
drunk_handler.on_game_load()
anomaly_donation_bridge_registry.get("god_mode").on_game_load()
assert(drunk_adds == 2 and persisted_actor_values.adb_drunk_effect_v1.remaining_ms == 3500)
drunk_handler.update(5999)
assert(persisted_actor_values.adb_drunk_effect_v1 ~= nil)
drunk_handler.update(6000)
assert(persisted_actor_values.adb_drunk_effect_v1 == nil and drunk_removes >= 3)

-- HUD hiding uses the stock console command, survives a level load and then
-- restores the user's original HUD state when the saved timer expires.
local hud_enabled, god_enabled = true, false
local hud_console = {}
function hud_console:get_bool(name)
	if name == "hud_draw" then return hud_enabled end
	if name == "g_god" then return god_enabled end
	if name == "mouse_invert" then return false end
	error("unexpected boolean console command " .. tostring(name))
end
function hud_console:get_float(name)
	if name == "mouse_sens" then return 0.12 end
	if name == "mouse_sens_vertical" then return 1 end
	error("unexpected float console command " .. tostring(name))
end
function hud_console:execute(command)
	if command == "hud_draw on" or command == "hud_draw off" then
		hud_enabled = command == "hud_draw on"
		return
	end
	if command == "g_god on" or command == "g_god off" then
		god_enabled = command == "g_god on"
		return
	end
	error("unexpected console command " .. tostring(command))
end
function get_console() return hud_console end
db = { actor = boost_actor }
effect_now_value = 20000
local hud_handler = anomaly_donation_bridge_registry.get("hide_hud")
local hud_context = { actor = boost_actor, now = effect_now_value }
assert(hud_handler.can_execute(hud_context, { duration_seconds = "5" }) == true)
local hud_status, hud_detail = hud_handler.execute(hud_context)
assert(hud_status == "executed", tostring(hud_status) .. ":" .. tostring(hud_detail))
assert(not hud_enabled and type(persisted_actor_values.adb_hide_hud_v1) == "table" and
	persisted_actor_values.adb_hide_hud_v1.remaining_ms == 5000)
effect_now_value = 22000
hud_handler.on_level_changing()
assert(hud_enabled and persisted_actor_values.adb_hide_hud_v1.remaining_ms == 3000)
hud_handler.on_game_load()
assert(not hud_enabled)
hud_handler.update(24999)
assert(not hud_enabled)
hud_handler.update(25000)
assert(hud_enabled and persisted_actor_values.adb_hide_hud_v1 == nil)

-- Joke files are discovered by walking the actual sound VFS. This exercises
-- the packed-archive path used by Anomaly instead of the broken recursive mask.
FS = { FS_ListFiles = 1, FS_ListFolders = 2, FS_RootOnly = 8 }
local joke_folders = {
	["characters_voice\\"] = { "human\\" },
	["characters_voice\\human\\"] = { "stalker_1\\", "mod_voice\\" },
	["characters_voice\\human\\stalker_1\\"] = { "talk\\" },
	["characters_voice\\human\\stalker_1\\talk\\"] = { "jokes\\" },
	["characters_voice\\human\\mod_voice\\"] = { "talk\\" },
	["characters_voice\\human\\mod_voice\\talk\\"] = { "jokes\\" }
}
local joke_files = {
	["characters_voice\\human\\stalker_1\\talk\\jokes\\"] =
		{ "joke_1.ogg", "joke_2.ogg", "unrelated.ogg" },
	["characters_voice\\human\\mod_voice\\talk\\jokes\\"] =
		{ "joke_modded.ogg" }
}
local joke_fs, freed_lists = {}, 0
local function joke_list(values)
	values = values or {}
	return {
		Size = function() return #values end,
		GetAt = function(_, index) return values[index + 1] end,
		Free = function() freed_lists = freed_lists + 1 end
	}
end
function joke_fs:file_list_open(alias, folder, flags)
	assert(alias == "$game_sounds$")
	if flags == FS.FS_ListFolders + FS.FS_RootOnly then
		return joke_list(joke_folders[folder])
	end
	assert(flags == FS.FS_ListFiles + FS.FS_RootOnly)
	return joke_list(joke_files[folder])
end
function getFS() return joke_fs end
local joke_playing, joke_path, joke_actor = false, nil, boost_actor
sound_object = setmetatable({ s3d = 0, s2d = 2 }, { __call = function(_, path)
	joke_path = path
	return {
		play = function(_, actor, delay, kind)
			-- Actor voice is first-person audio: stock Anomaly uses s2d so the
			-- source is audible to the actor who is also the listener.
			assert(actor == joke_actor and delay == 0 and kind == sound_object.s2d)
			joke_playing = true
		end,
		playing = function() return joke_playing end,
		stop_deffered = function() joke_playing = false end
	}
end })
local joke_handler = anomaly_donation_bridge_registry.get("tell_joke")
local joke_context = { actor = joke_actor }
assert(joke_handler.can_execute(joke_context, {}) == true)
assert(select(1, joke_handler.execute(joke_context)) == "executed")
assert(joke_path:find("characters_voice\\human\\", 1, true) == 1 and
	joke_path:find("talk\\jokes\\joke_", 1, true) and
	not joke_path:find(".ogg", 1, true))
assert(freed_lists > 0)
assert(select(2, joke_handler.can_execute({ actor = joke_actor }, {})) == "deferred")
joke_playing = false
joke_handler.update()
assert(joke_handler.can_execute({ actor = joke_actor }, {}) == true)

-- The stock character-name API lives on the server actor object and persists
-- the same way as a name entered in Anomaly's new-game options.
local player_name_actor = { character_name_value = "Old name" }
function player_name_actor:character_name() return self.character_name_value end
function player_name_actor:set_character_name(value) self.character_name_value = value end
local player_name_life = {}
function player_name_life:actor() return player_name_actor end
function alife() return player_name_life end
local player_name_handler = anomaly_donation_bridge_registry.get("change_player_name")
assert(select(1, player_name_handler.is_supported()) == true)
local player_name_context = { actor = boost_actor }
assert(player_name_handler.can_execute(player_name_context, {
	donor_name = "  Локальный тест  "
}) == true)
local player_name_status, player_name_detail = player_name_handler.execute(player_name_context)
assert(player_name_status == "executed" and player_name_detail == "actor_name_changed")
assert(player_name_actor.character_name_value ==
	anomaly_donation_bridge_common.game_text_cp1251("Локальный тест"))

-- Named companions are created as persistent companion squads rather than as
-- loose NPC objects, so the stock companion system carries them between maps.
local original_spawn_points = anomaly_donation_bridge_common.spawn_points
anomaly_donation_bridge_common.spawn_points = function()
	return { { position = { x = 10, y = 0, z = 10 }, vertex_id = 7 } },
		"requested_ring"
end
local companion_member = { id = 42, character_name_value = "" }
function companion_member:name() return "sim_default_stalker_0_42" end
function companion_member:community() return "army" end
function companion_member:set_character_name(value) self.character_name_value = value end
function companion_member:force_set_goodwill(value, id)
	assert(value == 5000 and id == 0)
end
local companion_squad = { id = 100 }
function companion_squad:add_squad_member(section, position, level_vertex, game_vertex)
	assert(section == "sim_default_stalker_0" and level_vertex == 7 and game_vertex == 9)
	return companion_member.id
end
function companion_squad:set_squad_relation(relation)
	assert(relation == "friend")
	self.relation_set = true
end
function companion_squad:refresh() self.refreshed = true end
local fake_life = {}
function fake_life:actor() return {} end
function fake_life:create(section)
	assert(section == "online_offline_group")
	return companion_squad
end
function fake_life:object(id) return id == 42 and companion_member or nil end
function fake_life:release() end
function alife() return fake_life end
local client_actor = {
	id = function() return 1 end,
	level_vertex_id = function() return 1 end,
	game_vertex_id = function() return 9 end,
	position = function() return { x = 0, y = 0, z = 0 } end,
	team = function() return 5 end,
	character_community = function() return "actor_stalker" end
}
anomaly_donation_bridge_catalog = {
	capable = function() return true end,
	validate_revision = function(params)
		return params.catalog_revision == "rev", "catalog_revision_mismatch"
	end,
	random_available = function(kind)
		assert(kind == "npc")
		return { section_id = "sim_default_stalker_0" }
	end,
	validate_section = function(params, key, kind)
		assert(params.catalog_revision == "rev" and key == "section_id" and kind == "npc")
		return { section_id = params.section_id }
	end,
	is_spawn_safe = function(section, kind)
		return section == "sim_default_stalker_0" and kind == "npc"
	end
}
axr_companions = { companion_squads = {}, non_task_companions = {} }
SIMBOARD = {
	assign_squad_to_smart = function(_, squad, smart)
		assert(squad == companion_squad and smart == nil)
	end,
	setup_squad_and_group = function(_, member) assert(member == companion_member) end
}
local saved_companion, saved_companion_community = false, nil
local saved_companion_nick={}
function se_save_var(id, name, key, value)
	assert(id == 42 and name == companion_member:name())
	if key == "companion" then
		saved_companion = value == true
	elseif key == "adb_companion_community_v1" then
		saved_companion_community = value
	elseif key:find("adb_companion_3d_nick_v1",1,true)==1 then
        saved_companion_nick[key]=value
	elseif key=="adb_companion_damage_percent_v1" then
        saved_companion_nick.damage_percent=value
	else
		error("unexpected companion save key " .. tostring(key))
	end
end
do
local previous_companion_load=se_load_var
function se_load_var(id,name,key)
    if id==1 then return previous_companion_load(id,name,key) end
    if id~=42 or name~=companion_member:name() then return nil end
    if key=="adb_companion_damage_percent_v1" then return saved_companion_nick.damage_percent end
    if key=="adb_companion_community_v1" then return saved_companion_community end
    return saved_companion_nick[key]
end
end
local online_companion = { community_value = "army" }
function online_companion:id()return 42 end
function online_companion:name()return self.identity or companion_member:name()end
function online_companion:set_character_community(community, squad, group)
	assert(squad == 0 and group == 0)
	self.community_value = community
end
function online_companion:character_community() return self.community_value end
local companion_is_online = false
level = { object_by_id = function(id)
	return companion_is_online and id == companion_member.id and online_companion or nil
end }
local companion_handler = anomaly_donation_bridge_registry.get("spawn_named_companion")
local companion_context = { actor = client_actor }
assert(companion_handler.can_execute(companion_context, {
	section_id = "", catalog_revision = "rev", random_npc = "true",
	use_player_faction = "true", radius_meters = "25", donor_name = "Локальный тест"
}) == true)
assert(companion_context.options.incoming_damage_percent==100,"default NPC damage must remain unchanged")
local companion_status, companion_detail = companion_handler.execute(companion_context)
assert(companion_status == "executed", tostring(companion_detail))
assert(online_companion:character_community() == "army")
companion_is_online = true
companion_handler.update(1000)
assert(companion_member.character_name_value == anomaly_donation_bridge_common.game_text_cp1251(
		"Локальный тест") and type(companion_member.community) == "function" and
	companion_squad.player_id == "stalker" and
	online_companion:character_community() == "stalker" and
	saved_companion_community == "stalker" and
	saved_companion and axr_companions.non_task_companions[42] == true and
	axr_companions.companion_squads[100] == companion_squad and
	companion_squad.scripted_target == "actor")

do
local function companion_hit(target,power,cancelled)
    local hit={power=power};local flags={ret_value=not cancelled}
    for _,callback in ipairs(script_callbacks.npc_on_before_hit or {})do callback(target,hit,0,flags)end
    return hit,flags
end
assert(saved_companion_nick.damage_percent==100 and companion_hit(online_companion,.8).power==.8,
    "100 percent must preserve native damage exactly")
for _,invalid in ipairs({"0","-1","101","1.5","nan"})do
    assert(not companion_handler.can_execute({actor=client_actor},{incoming_damage_percent=invalid}),
        "invalid NPC damage setting accepted")
end
local function spawn_protected(percent)
    local context={actor=client_actor}
    assert(companion_handler.can_execute(context,{section_id="sim_default_stalker_0",catalog_revision="rev",
        donor_name="Локальный тест",incoming_damage_percent=tostring(percent)}))
    assert(companion_handler.execute(context)=="executed")
end
spawn_protected(50)
assert(companion_hit(online_companion,.8).power==.4,"named NPC must receive half damage at 50 percent")
spawn_protected(10)
assert(math.abs(companion_hit(online_companion,.8).power-.08)<1e-12 and saved_companion_nick.damage_percent==10,
    "named NPC must receive a tenth of incoming damage and save its setting")
assert(companion_hit(online_companion,.8,true).power==.8,"do not modify hits cancelled by another handler")
assert(companion_hit(online_companion,0).power==0 and companion_hit(online_companion,-1).power==-1,
    "zero/healing power is untouched")
local untracked={id=function()return 43 end,name=function()return "foreign_npc_43"end}
axr_companions.non_task_companions[43]=true
assert(companion_hit(untracked,.8).power==.8,"ordinary or quest companion must not inherit ADB protection")
axr_companions.non_task_companions[42]=nil
assert(companion_hit(online_companion,.8).power==.8,"dismissed NPC no longer receives companion protection")
axr_companions.non_task_companions[42]=true
online_companion.identity="unrelated_reused_id_42"
assert(companion_hit(online_companion,.8).power==.8,"reused object ID cannot protect an unrelated NPC")
online_companion.identity=nil
companion_handler.on_game_load()
assert(math.abs(companion_hit(online_companion,.8).power-.08)<1e-12,
    "saved protection applies even before the first actor update after F9")
companion_handler.on_game_load();companion_handler.update(2000)
assert(math.abs(companion_hit(online_companion,.8).power-.08)<1e-12,
    "companion synchronization preserves saved damage setting")
saved_companion_nick.damage_percent=nil;companion_handler.on_game_load()
assert(companion_hit(online_companion,.8).power==.8,"old NPC saves without a setting retain native damage")
spawn_protected(1)
assert(math.abs(companion_hit(online_companion,.8).power-.008)<1e-12,"minimum setting remains finite, not invulnerability")
spawn_protected(100)
assert(companion_hit(online_companion,.8).power==.8,"new NPC with 100 percent remains an ordinary companion")
end

-- Only human members returned by Anomaly's stock companion roster are valid
-- targets. The experimental mutant companion and unrelated NPCs are ignored.
local human_a = { alive_value = true }
function human_a:alive() return self.alive_value end
function human_a:kill() self.alive_value = false end
local human_b = { alive_value = true }
function human_b:alive() return self.alive_value end
function human_b:kill() self.alive_value = false end
local offline_human = { alive_value = true }
function offline_human:clsid() return "stalker_cls" end
function offline_human:alive() return self.alive_value end
function offline_human:kill() self.alive_value = false end
local experimental_mutant = { alive_value = true }
function experimental_mutant:alive() return self.alive_value end
function experimental_mutant:kill() self.alive_value = false end
db.storage = {
	[201] = { object = human_a }, [202] = { object = human_b },
	[777] = { object = experimental_mutant }
}
local companion_life_object = fake_life.object
function fake_life:object(id)
	if id == 203 then return offline_human end
	return companion_life_object(self, id)
end
axr_companions.list_actor_squad_by_id = function() return { 201, 202, 203 } end
IsStalker = function(object, class_id)
	return object == human_a or object == human_b or class_id == "stalker_cls"
end
local kill_companions = anomaly_donation_bridge_registry.get("kill_companions")
local one_context = { actor = client_actor }
assert(kill_companions.can_execute(one_context, { count = "1" }) == true)
assert(select(1, kill_companions.execute(one_context)) == "executed")
local killed_one = (human_a.alive_value and 0 or 1) +
	(human_b.alive_value and 0 or 1) + (offline_human.alive_value and 0 or 1)
assert(killed_one == 1)
local all_context = { actor = client_actor }
assert(kill_companions.can_execute(all_context, { count = "99" }) == true)
assert(select(1, kill_companions.execute(all_context)) == "executed")
assert(not human_a.alive_value and not human_b.alive_value and
	not offline_human.alive_value and
	experimental_mutant.alive_value,
	"kill_companions must never touch the experimental mutant companion")

anomaly_donation_bridge_common.spawn_points = original_spawn_points

-- Mutant companions are direct single ALife objects. They never create a
-- simulation squad, keep their donor label in the save, follow the actor,
-- assist against the actor's enemy and move with the actor between levels.
local mutant_saved_state = {}
alife_storage_manager = { get_state = function() return mutant_saved_state end }
local mutant_server = { id = 77, team = 33, squad = 4, group = 2 }
local mutant_teleport = nil
local mutant_life = {}
function mutant_life:actor() return { m_game_vertex_id = 91 } end
function mutant_life:object(id) return id == 77 and mutant_server or nil end
function mutant_life:release() end
function mutant_life:teleport_object(id, gvid, lvid, position)
	assert(id == 77)
	mutant_teleport = { gvid = gvid, lvid = lvid, position = position }
end
function alife() return mutant_life end
local mutant_distance = 13
local mutant_actor_position = { distance_to = function(_, other)
	return other == mutant_actor_position and 0 or mutant_distance
end }
local actor_combat_enemy = nil
client_actor.position = function() return mutant_actor_position end
client_actor.best_enemy = function() return actor_combat_enemy end
client_actor.force_set_goodwill = function() end
client_actor.set_relation = function() end
local mutant_position = { distance_to = function() return mutant_distance end }
local mutant_alive = true
local mutant_action = nil
local mutant_restrictions_cleared = 0
local mutant_home_removed = 0
local mutant_client = { health = 0.64, assigned_enemy = nil, current_enemy = client_actor,
	forgotten = {}, team_value = 33, squad_value = 4, group_value = 2 }
function mutant_client:id() return 77 end
function mutant_client:alive() return mutant_alive end
function mutant_client:position() return mutant_position end
function mutant_client:get_enemy() return self.current_enemy end
function mutant_client:action() return mutant_action end
function mutant_client:accessible(vertex_id) return vertex_id == 1 end
function mutant_client:remove_all_restrictions()
	mutant_restrictions_cleared = mutant_restrictions_cleared + 1
end
function mutant_client:remove_home()
	mutant_home_removed = mutant_home_removed + 1
end
function mutant_client:team() return self.team_value end
function mutant_client:squad() return self.squad_value end
function mutant_client:group() return self.group_value end
function mutant_client:change_team(team, squad, group)
	self.team_value, self.squad_value, self.group_value = team, squad, group
end
function mutant_client:enable_memory_object(object, enabled)
	assert(enabled == false)
	self.forgotten[object:id()] = true
	if self.current_enemy == object then self.current_enemy = nil end
end
local combat_enemy = { alive = function() return true end, force_set_goodwill = function() end }
function combat_enemy:id() return 88 end
local aimed_object = mutant_client
local actor_follow_position = { x = 2, y = 0, z = 2 }
local mutant_map_spots = {}
level = {
	object_by_id = function(id) return id == 77 and mutant_client or nil end,
	get_target_obj = function() return aimed_object end,
	vertex_position = function(vertex_id)
		assert(vertex_id == 1)
		return actor_follow_position
	end,
	map_has_object_spot = function(id, spot)
		return mutant_map_spots[id] and mutant_map_spots[id].spot == spot and 1 or 0
	end,
	map_add_object_spot = function(id, spot, hint)
		mutant_map_spots[id] = { spot = spot, hint = hint }
	end,
	map_remove_object_spot = function(id, spot)
		assert(mutant_map_spots[id] == nil or mutant_map_spots[id].spot == spot)
		mutant_map_spots[id] = nil
	end
}
game_object = { friend = 0, neutral = 1, enemy = 2 }
game_relations = { set_npcs_relation = function() end }
local human_companion = { forgotten = {}, current_enemy = mutant_client }
function human_companion:id() return 42 end
function human_companion:alive() return true end
function human_companion:has_info(info) return info == "npcx_is_companion" end
function human_companion:force_set_goodwill() end
function human_companion:set_relation() end
function human_companion:enable_memory_object()
	error("companion memory must not be changed from the mutant callback")
end
local neutral_stalker = { forgotten = {} }
function neutral_stalker:id() return 43 end
function neutral_stalker:alive() return true end
function neutral_stalker:has_info() return false end
function neutral_stalker:relation() return game_object.neutral end
function neutral_stalker:enable_memory_object()
	error("neutral stalker memory must not be changed from the mutant callback")
end
local hostile_stalker = {}
function hostile_stalker:id() return 44 end
function hostile_stalker:alive() return true end
function hostile_stalker:has_info() return false end
function hostile_stalker:relation() return game_object.enemy end
function hostile_stalker:enable_memory_object()
	error("hostile stalker memory must not be mutated during an AI update")
end
IsStalker = function(object)
	return object == human_companion or object == neutral_stalker or
		object == hostile_stalker
end
xr_logic = {
	mob_capture = function(object) assert(object == mutant_client) end,
	mob_release = function(object) assert(object == mutant_client) end
}
move = setmetatable({ run_fwd = "run_fwd" }, {
	__call = function(_, mode, vertex_id, position)
		assert(mode == "run_fwd" and vertex_id == 1 and
			position == actor_follow_position)
		return { kind = "move", vertex_id = vertex_id, position = position }
	end
})
act = setmetatable({ attack = "attack", rest = "rest" }, {
	__call = function(_, mode, target)
		if mode == "attack" then
			assert(target == combat_enemy)
			return { kind = "attack", target = target }
		end
		assert(mode == "rest" and target == nil)
		return { kind = "rest" }
	end
})
cond = setmetatable({ move_end = "move_end", time_end = "time_end" }, {
	__call = function(_, value, duration)
		assert(value == "move_end" or
			(value == "time_end" and (duration == 1000 or duration == 8000)))
		return value
	end
})
local attack_orders = 0
local rest_orders = 0
local follow_orders = 0
action = function(object, command)
	assert(object == mutant_client)
	mutant_action = command
	if command.kind == "attack" then
		attack_orders = attack_orders + 1
		mutant_client.assigned_enemy = command.target
	elseif command.kind == "rest" then
		rest_orders = rest_orders + 1
	elseif command.kind == "move" then
		follow_orders = follow_orders + 1
	end
end
local health_label = ""
local health_static = nil
function get_hud()
	return {
		AddCustomStatic = function(_, key)
			assert(key == "bar_measurement")
			health_static = { wnd = function()
				return {
					GetWidth = function() return 1024 end,
					SetWndPos = function() end,
					TextControl = function()
						return {
							SetTextColor = function() end,
							SetText = function(_, text) health_label = text end
						}
					end
				}
			end }
		end,
		GetCustomStatic = function(_, key)
			assert(key == "bar_measurement")
			return health_static
		end,
		RemoveCustomStatic = function(_, key)
			assert(key == "bar_measurement")
			health_static = nil
		end
	}
end
function game_graph()
	return { vertex = function(_, gvid)
		assert(gvid == 91)
		return {
			level_vertex_id = function() return 19 end,
			level_point = function() return { x = 1, y = 2, z = 3 } end
		}
	end }
end
anomaly_donation_bridge_common.spawn_points = function()
	return { { position = { x = 5, y = 0, z = 5 }, vertex_id = 17 } },
		"requested_ring"
end
anomaly_donation_bridge_catalog = {
	capable = function() return true end,
	validate_sections = function(params, key, kind)
		assert(params.catalog_revision == "mutant-rev" and key == "mutant_sections" and
			kind == "mutant")
		return {
			{ section_id = "mutant_dog" },
			{ section_id = "mutant_cat" }
		}
	end,
	is_spawn_safe = function(section, kind)
		return (section == "mutant_dog" or section == "mutant_cat") and
			kind == "mutant"
	end
}
local direct_spawn_section = nil
function alife_create(section, position, lvid, gvid)
	direct_spawn_section = section
	assert(position.x == 5 and lvid == 17 and gvid == 9)
	return mutant_server
end
local mutant_companion = anomaly_donation_bridge_registry.get("spawn_mutant_companion")
local mutant_context = { actor = client_actor }
local original_random = math.random
math.random = function(maximum)
	assert(maximum == 2)
	return 2
end
assert(mutant_companion.can_execute(mutant_context, {
	mutant_sections = "mutant_dog,mutant_cat", random_mutant = "false",
	radius_meters = "25", catalog_revision = "mutant-rev",
	donor_name = "Барсик"
}) == true)
math.random = original_random
local mutant_status, mutant_detail = mutant_companion.execute(mutant_context)
assert(mutant_status == "executed" and direct_spawn_section == "mutant_cat" and
	mutant_detail:find("spawned_mutant_companion_77_mutant_cat_random", 1, true))
assert(mutant_saved_state.adb_mutant_companions_v1[77].donor_name == "Барсик")
assert(mutant_server.team == 5)
assert(mutant_map_spots[77].spot == "ui_pda2_companion_location" and
	mutant_map_spots[77].hint == anomaly_donation_bridge_common.game_text_cp1251("Барсик"))
db = { actor = client_actor, storage = {
	[42] = { object = human_companion }, [43] = { object = neutral_stalker },
	[44] = { object = hostile_stalker }
},
	offline_objects = {}, spawned_vertex_by_id = {} }
mutant_companion.update(1000)
assert(mutant_teleport == nil and follow_orders == 1 and health_label ==
	anomaly_donation_bridge_common.game_text_cp1251("Барсик") .. ": 64% HP")
assert(mutant_restrictions_cleared == 1 and mutant_home_removed == 1)
assert(mutant_client:team() == 5 and mutant_client:squad() == 4 and
	mutant_client:group() == 2)
assert(mutant_client.forgotten[1] and mutant_client.current_enemy == nil)
assert(type(script_callbacks.on_enemy_eval) == "table" and
	#script_callbacks.on_enemy_eval >= 1)
local neutral_flags = { override = false, result = true }
local hostile_flags = { override = false, result = false }
for _, callback in ipairs(script_callbacks.on_enemy_eval) do
	callback(neutral_stalker, mutant_client, neutral_flags)
	callback(hostile_stalker, mutant_client, hostile_flags)
end
assert(neutral_flags.override == true and neutral_flags.result == false)
assert(hostile_flags.override == false)
mutant_client.current_enemy = human_companion
human_companion.current_enemy = mutant_client
assert(type(script_callbacks.monster_on_update) == "table" and
	#script_callbacks.monster_on_update >= 1)
for _, callback in ipairs(script_callbacks.monster_on_update) do
	callback(mutant_client, {})
end
assert(mutant_client.current_enemy == nil)
mutant_distance = 9
mutant_companion.update(1250)
assert(follow_orders == 1 and rest_orders == 0)
actor_combat_enemy = combat_enemy
mutant_companion.update(1500)
assert(attack_orders == 1 and mutant_client.assigned_enemy == combat_enemy)
mutant_distance = 101
mutant_client.current_enemy = combat_enemy
mutant_companion.update(1750)
assert(attack_orders == 1 and follow_orders == 2 and
	mutant_client.current_enemy == nil and mutant_client.forgotten[88])
actor_combat_enemy = nil
mutant_distance = 3
mutant_companion.update(2000)
assert(rest_orders == 1 and mutant_restrictions_cleared == 1 and
	mutant_home_removed == 1)
mutant_companion.on_level_changing()
assert(mutant_teleport.gvid == 91 and mutant_teleport.lvid == 19 and
	mutant_teleport.position.x == 1)
mutant_companion.on_game_load()
assert(mutant_saved_state.adb_mutant_companions_v1[77].section == "mutant_cat")
local pda_count_before_mutant_death = #pda_messages
mutant_alive = false
mutant_companion.update(2250)
assert(mutant_saved_state.adb_mutant_companions_v1[77] == nil and
	mutant_map_spots[77] == nil and
	#pda_messages == pda_count_before_mutant_death + 1 and
	anomaly_donation_bridge_common.game_text_utf8(pda_messages[#pda_messages]) ==
	"%c[255,238,153,26]Барсик%c[default] погиб")

anomaly_donation_bridge_common.spawn_points = original_spawn_points

-- Fake filesystem only: this fixture never touches the user's savedgames.
local fake_files = {
	["older.scop"] = true, ["older.scoc"] = true,
	["newer.scop"] = true, ["newer.scoc"] = true, ["newer.dds"] = true
}
local original_io_open = io.open
io.open = function(path)
	local name = path:match("^fake/(.+)$")
	if fake_files[name] then return { close = function() end } end
	return nil
end
FS = { FS_ListFiles = 1, FS_RootOnly = 2, FS_sort_by_modif_down = 3 }
local fake_fs = {}
function fake_fs:file_list_open_ex(alias, flags, mask)
	assert(alias == "$game_saves$" and flags == 3 and mask == "*.scop")
	local names = {}
	for _, name in ipairs({ "newer.scop", "older.scop", "failure.scop" }) do
		if fake_files[name] then names[#names + 1] = name end
	end
	return {
		Sort = function(_, mode) assert(mode == 3) end,
		Size = function() return #names end,
		GetAt = function(_, index)
			return { NameShort = function() return names[index + 1] end }
		end
	}
end
function fake_fs:exist(alias, name)
	assert(alias == "$game_saves$")
	return fake_files[name] or nil
end
function fake_fs:update_path(alias, name)
	assert(alias == "$game_saves$")
	return "fake/" .. name
end
local fail_main_move = false
function fake_fs:file_rename(source, destination, overwrite)
	assert(overwrite == false)
	local old, new = source:match("^fake/(.+)$"), destination:match("^fake/(.+)$")
	if fail_main_move and old == "failure.scop" then error("simulated rename failure") end
	assert(fake_files[old] and not fake_files[new])
	fake_files[old], fake_files[new] = nil, true
end
function getFS() return fake_fs end
local save_handler = anomaly_donation_bridge_registry.get("delete_recent_saves")
local save_context = {}
assert(save_handler.can_execute(save_context, { count = "1" }) == true)
local save_status, save_detail = save_handler.execute(save_context)
assert(save_status == "executed" and save_detail:find("recent_saves_archived_1", 1, true))
assert(not fake_files["newer.scop"] and fake_files["older.scop"])
assert(not fake_files["newer.scoc"] and not fake_files["newer.dds"])
assert(fake_files["ADB_Removed\\1\\newer.scop"] and
	fake_files["ADB_Removed\\1\\newer.scoc"] and
	fake_files["ADB_Removed\\1\\newer.dds"])
local all_context = {}
assert(save_handler.can_execute(all_context, { count = "999" }) == true)
assert(select(1, save_handler.execute(all_context)) == "executed")
assert(not fake_files["older.scop"] and not fake_files["older.scoc"] and
	fake_files["ADB_Removed\\1\\older.scop"] and
	fake_files["ADB_Removed\\1\\older.scoc"])
fake_files["failure.scop"], fake_files["failure.scoc"] = true, true
fail_main_move = true
local failure_context = {}
assert(save_handler.can_execute(failure_context, { count = "1" }) == true)
local failed_status, failed_detail = save_handler.execute(failure_context)
assert(failed_status == "uncertain" and
	failed_detail:find("save_archive_failed_rolled_back", 1, true))
assert(fake_files["failure.scop"] and fake_files["failure.scoc"] and
	not fake_files["ADB_Removed\\1\\failure.scoc"])
io.open = original_io_open

-- The effect delegates to JumpToLevel even when direct graph bindings are
-- unavailable. That wrapper owns the level entry-point lookup.
local fake_now, jumps = 1000, {}
function time_global() return fake_now end
level = { name = function() return "l01_escape" end }
game_graph, alife, vector, ChangeLevel, CreateTimeEvent = nil, nil, nil, nil, nil
function JumpToLevel(name)
	jumps[#jumps + 1] = name
	return true
end
local visited_calls = {}
game_statistics = {
	has_actor_visited_level = function(level_name)
		visited_calls[#visited_calls + 1] = level_name
		return level_name == "l01_escape" or level_name == "l03_agroprom"
	end
}
anomaly_donation_bridge_catalog = {
	validate_sections = function(params, key, kind)
		assert(params.catalog_revision == "rev" and key == "level_sections" and kind == "level")
		local entries = {}
		for section in tostring(params.level_sections or ""):gmatch("[^,]+") do
			if section == "adb_level_excluded" then
				return nil, "catalog_selection_stale_or_quarantined"
		elseif section == "adb_level_l01_escape" then
				entries[#entries + 1] = { faction = "l01_escape" }
		elseif section == "adb_level_l02_garbage" then
				entries[#entries + 1] = { faction = "l02_garbage" }
		elseif section == "adb_level_l03_agroprom" then
				entries[#entries + 1] = { faction = "l03_agroprom" }
		end
		end
		return entries
	end
}
local level_handler = anomaly_donation_bridge_registry.get("teleport_random_level")
assert(select(1, level_handler.is_supported()) == true)
local level_context = {}
assert(level_handler.can_execute(level_context, {
	level_sections = "adb_level_l01_escape,adb_level_l02_garbage",
	level_options = "", catalog_revision = "rev"
}) == true)
assert(level_context.options.target == "l02_garbage")
local level_status, level_detail = level_handler.execute(level_context)
assert(level_status == "executed" and level_detail:find("l02_garbage", 1, true))
assert(#jumps == 1 and jumps[1] == "l02_garbage")
local already_status = select(1, level_handler.execute(level_context))
assert(already_status == "rejected")

-- The pipeline is: selected list -> safe catalog/exclusions -> visited filter
-- -> current-level removal. Only the one visited destination survives.
fake_now = 12000
visited_calls = {}
local visited_context = {}
assert(level_handler.can_execute(visited_context, {
	level_sections = "adb_level_l01_escape,adb_level_l02_garbage,adb_level_l03_agroprom",
	level_options = "visited_only", catalog_revision = "rev"
}) == true)
assert(visited_context.options.target == "l03_agroprom" and #visited_calls == 3)
assert(select(1, level_handler.execute(visited_context)) == "executed")
assert(#jumps == 2 and jumps[2] == "l03_agroprom")

fake_now = 23000
visited_calls = {}
local excluded_context = {}
local excluded_ok = level_handler.can_execute(excluded_context, {
	level_sections = "adb_level_excluded,adb_level_l03_agroprom",
	level_options = "visited_only", catalog_revision = "rev"
})
assert(excluded_ok == true and excluded_context.options.target == "l03_agroprom" and
	#visited_calls == 1 and visited_calls[1] == "l03_agroprom")
local all_excluded_ok, _, all_excluded_reason = level_handler.can_execute({}, {
	level_sections = "adb_level_excluded", level_options = "visited_only",
	catalog_revision = "rev"
})
assert(all_excluded_ok == false and all_excluded_reason == "no_safe_level_selected")
local no_visited_ok, _, no_visited_reason = level_handler.can_execute({}, {
	level_sections = "adb_level_l02_garbage", level_options = "visited_only",
	catalog_revision = "rev"
})
assert(no_visited_ok == false and no_visited_reason == "no_visited_level_selected")
local current_only_ok, _, current_only_reason = level_handler.can_execute({}, {
	level_sections = "adb_level_l01_escape", level_options = "visited_only",
	catalog_revision = "rev"
})
assert(current_only_ok == false and current_only_reason == "no_other_level_selected")
local bad_option_ok, _, bad_option_reason = level_handler.can_execute({}, {
	level_sections = "adb_level_l03_agroprom", level_options = "unknown",
	catalog_revision = "rev"
})
assert(bad_option_ok == false and bad_option_reason == "level_options_invalid")
local saved_game_statistics = game_statistics
game_statistics = nil
local unrestricted_without_api = {}
assert(level_handler.can_execute(unrestricted_without_api, {
	level_sections = "adb_level_l02_garbage", level_options = "",
	catalog_revision = "rev"
}) == true)
local missing_api_ok, _, missing_api_reason = level_handler.can_execute({}, {
	level_sections = "adb_level_l03_agroprom", level_options = "visited_only",
	catalog_revision = "rev"
})
assert(missing_api_ok == false and missing_api_reason == "visited_levels_api_unavailable")
game_statistics = saved_game_statistics

JumpToLevel = function() return false end
assert(select(1, level_handler.execute(unrestricted_without_api)) == "rejected")
JumpToLevel = function() error("simulated travel failure") end
assert(select(1, level_handler.execute(unrestricted_without_api)) == "rejected")

-- In-memory NPCs only. A default effect never kills story characters,
-- companions or mutants; checked options explicitly widen its target set.
local game_object_mt = {
    __eq = function() error("game_object does not define __eq") end
}
local function npc(id, kind, x, relation, story)
    local object = { id_value = id, kind = kind, x = x,
        relation_value = relation, story_value = story, living = true }
    function object:id() return self.id_value end
    function object:section() return "ordinary_npc" end
    function object:position() return { x = self.x, y = 0, z = 0 } end
    function object:alive() return self.living end
    function object:relation() return self.relation_value end
    function object:kill(killer)
        assert(killer == self)
        self.living = false
    end
    return setmetatable(object, game_object_mt)
end
local actor = npc(1, "actor", 0, 0, nil)
function actor:position()
    return { distance_to_sqr = function(_, other)
        return other.x * other.x + other.y * other.y + other.z * other.z
    end }
end
local ordinary = npc(2, "npc", 10, 0, nil)
assert(not pcall(function() return ordinary == actor end))
local important = npc(3, "npc", 15, -1, "story_character")
local mutant = npc(4, "mutant", 20, -1, nil)
local distant = npc(5, "npc", 90, -1, nil)
local companion = npc(6, "npc", 22, -1, nil)
db = { actor = actor, storage = {
    [1] = { object = actor }, [2] = { object = ordinary },
    [3] = { object = important }, [4] = { object = mutant },
    [5] = { object = distant }, [6] = { object = companion }
} }
axr_companions = { non_task_companions = {}, companion_squads = { [100] = true } }
function get_object_squad(object)
    return object:id() == 6 and { id = 100 } or nil
end
game_object = { enemy = -1 }
function IsStalker(object) return object.kind == "npc" or object.kind == "actor" end
function IsMonster(object) return object.kind == "mutant" end
function get_object_story_id(id)
    return db.storage[id].object.story_value
end
function system_ini()
    return {
        section_exist = function() return true end,
        line_exist = function() return false end
    }
end
local kill_handler = anomaly_donation_bridge_registry.get("kill_nearby_npcs")
assert(select(1, kill_handler.is_supported()) == true)
local kill_context = { actor = actor }
assert(kill_handler.can_execute(kill_context, { radius_meters = "25" }) == true)
local kill_status, kill_detail = kill_handler.execute(kill_context)
assert(kill_status == "executed" and
    kill_detail:find("nearby_npcs_killed_1_failed_0", 1, true))
assert(not ordinary.living and important.living and mutant.living and
    companion.living and distant.living)
local neutral = npc(7, "npc", 12, 0, nil)
db.storage[7] = { object = neutral }
local widened_context = { actor = actor }
assert(kill_handler.can_execute(widened_context, {
    radius_meters = "25", kill_important_npcs = "true",
    kill_mutants = "true", only_enemies = "true"
}) == true)
local widened_status, widened_detail = kill_handler.execute(widened_context)
assert(widened_status == "executed" and
    widened_detail:find("nearby_npcs_killed_3_failed_0", 1, true))
assert(not important.living and not mutant.living and
    not companion.living and neutral.living and distant.living)
local failing = npc(8, "npc", 18, -1, nil)
function failing:kill() error("simulated NPC death failure") end
local another_enemy = npc(9, "npc", 19, -1, nil)
db.storage[8], db.storage[9] = { object = failing }, { object = another_enemy }
local partial_context = { actor = actor }
assert(kill_handler.can_execute(partial_context, {
    radius_meters = "25", only_enemies = "true"
}) == true)
local partial_status, partial_detail = kill_handler.execute(partial_context)
assert(partial_status == "uncertain" and
    partial_detail:find("nearby_npcs_killed_1_failed_1", 1, true))
assert(failing.living and not another_enemy.living and neutral.living)
assert(kill_handler.can_execute({ actor = actor }, {
    radius_meters = "0"
}) == false)
assert(kill_handler.can_execute({ actor = actor }, {
    kill_mutants = "yes"
}) == false)

print("file effects smoke passed")
