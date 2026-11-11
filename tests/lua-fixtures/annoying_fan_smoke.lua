local core_root = assert(arg[1], "pass game-addon/gamedata/scripts as arg[1]")
local _ = assert(arg[2], "pass effects/gamedata/scripts as arg[2]")
local optional_root = assert(arg[3], "pass optional-effects/gamedata/scripts as arg[3]")
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

local current_time = 0
function time_global() return current_time end

local function position(x, y, z)
	local value = { x = x, y = y, z = z }
	function value:distance_to(other)
		local dx, dy, dz = self.x - other.x, self.y - other.y, self.z - other.z
		return math.sqrt(dx * dx + dy * dy + dz * dz)
	end
	return value
end

local ui = { mode = 0, close_calls = 0 }
_GUIs = {}
pda = { dialog_closed = true }
bind_container = { curBoxID = nil }
function Check_UI() return next(_GUIs) ~= nil end
actor_menu = {
	dead_body_searching = false,
	is_hud_free = function()
		if ui.probe_error then error("UI probe unavailable") end
		return ui.mode == 0 and pda.dialog_closed and not Check_UI()
	end
}
function device() return { is_paused = function() return ui.paused == true end } end
local function finish_inventory_close()
	ui.mode, actor_menu.dead_body_searching, bind_container.curBoxID = 0, false, nil
	_GUIs.UIInventory = nil
end
local script_inventory = {
	IsShown = function() return ui.script_open == true end,
	message_box = { IsShown = function() return ui.modal_open == true end }
}
function script_inventory:HideDialog() error("bypassed the script inventory Close routine") end
function script_inventory:Close()
	ui.close_calls = ui.close_calls + 1
	if ui.close_error then error("close failed") end
	if ui.close_refused then return end
	ui.script_open = false
	if not ui.delayed_cleanup then finish_inventory_close() end
end
ui_inventory = { GUI = script_inventory }
ActorMenu = {
	get_actor_menu = function()
		return { IsShown = function() return ui.native_open == true end }
	end,
	get_pda_menu = function()
		return {
			IsShown = function() return ui.pda_open == true end,
			HideDialog = function()
				ui.close_calls = ui.close_calls + 1
				if not ui.close_refused then ui.pda_open = false end
			end
		}
	end
}
function get_hud()
	return { HideActorMenu = function()
		ui.close_calls = ui.close_calls + 1
		if not ui.close_refused then ui.native_open = false; finish_inventory_close() end
	end }
end

local actor = { talking = false }
function actor:alive() return ui.dead ~= true end
function actor:character_community() return "actor_stalker" end
function actor:game_vertex_id() return 20 end
function actor:position() return position(0, 0, 0) end
function actor:is_talking() return self.talking end
function actor:run_talk_dialog(npc, allow_break)
	assert(npc.start_dialog == "anomaly_donation_bridge_annoying_fan_dialog")
	assert(allow_break == false)
	self.talking = true
	npc.talking = true
	npc.dialog_open_count = npc.dialog_open_count + 1
end

local member = {
	id = 101, m_smart_terrain_id = 65535, character_name_value = "Initial Fan"
}
function member:clsid() return 12345 end
function member:section_name() return "sim_default_stalker_1" end
function member:name() return "annoying_fan_101" end
function member:set_character_name(value) self.character_name_value = value end
function member:force_set_goodwill() end
function member:alive() return true end

local server_weapon = { id = 901, weapon = true }
function server_weapon:clsid() return 22222 end
local online_weapon = { id = 902, weapon = true }
local supplies = { id = 903, weapon = false }
local inventory_reads, weapon_releases, drop_calls, fatal_binding_calls = 0, 0, 0, 0
local client = { talking = false, dialog_open_count = 0 }
function client:id() return 101 end
function client:position() return position(1, 0, 0) end
function client:is_talking() return self.talking end
function client:set_start_dialog(value) self.start_dialog = value end
function client:restore_default_start_dialog() self.start_dialog = nil end
function client:set_character_community(value) self.community = value end
function client:force_set_goodwill() end
function client:inactualize_patrol_path() end
function client:iterate_inventory(callback, context)
	inventory_reads = inventory_reads + 1
	callback(context, online_weapon)
	callback(context, supplies)
end
function client:drop_item_and_teleport(item, point)
	if point == nil then
		fatal_binding_calls = fatal_binding_calls + 1
		error("native luabind signature error: destination vector missing")
	end
	drop_calls = drop_calls + 1
	item.dropped = true
end
function client:drop_item(item)
	drop_calls = drop_calls + 1
	item.dropped = true
end

local squad = { id = 200 }
function squad:add_squad_member(section, point, vertex, game_vertex)
	assert(section == "sim_default_stalker_1")
	assert(vertex == 10 and game_vertex == 20)
	return member.id
end
function squad:set_squad_relation(value) assert(value == "friend") end
function squad:refresh() end

local life = { objects = { [101] = member, [200] = squad, [901] = server_weapon } }
function life:create(section, point, vertex, game_vertex)
	assert(section == "online_offline_group")
	assert(vertex == 10 and game_vertex == 20)
	return squad
end
function life:object(id) return self.objects[id] end
function life:get_children()
	local yielded = false
	return function()
		if yielded then return nil end
		yielded = true
		return 901
	end
end
function life:release(item)
	if item.weapon then weapon_releases = weapon_releases + 1 end
	self.objects[item.id] = nil
end
function life:set_switch_online(id, value) assert(id == 101 and value == true) end
function life:set_switch_offline(id, value) assert(id == 101 and value == false) end
function life:actor() return { m_game_vertex_id = 20 } end
function alife() return life end

level = { object_by_id = function(id) return id == 101 and client or nil end }
db = { actor = actor, storage = { [101] = { object = client } } }
dialogs = { who_is_npc = function(a, b) return a == actor and b or a end }
modules = { stype_stalker = 1 }
ini_file = setmetatable({}, { __call = function(_, path)
	assert(path == "scripts\\anomaly_donation_bridge_annoying_fan.ltx")
	return {}
end })
xr_logic = {
	configure_schemes = function(npc, ini, path)
		assert(npc == client and path == "scripts\\anomaly_donation_bridge_annoying_fan.ltx")
	end,
	determine_section_to_activate = function() return "beh@fan" end,
	activate_by_section = function()
		db.storage[101].ini_filename = "scripts\\anomaly_donation_bridge_annoying_fan.ltx"
		db.storage[101].active_section = "beh@fan"
	end
}

function IsStalker(object, class_id)
	if object ~= nil then return object == member or object == client end
	return class_id == 12345
end
function IsWeapon(item, class_id)
	return item ~= nil and item.weapon == true or class_id == 22222
end
game_relations = { set_npcs_relation = function(npc, target, relation)
	assert(npc == client and target == actor and relation == "friend")
end }
SIMBOARD = { assign_squad_to_smart = function(_, target_squad, smart)
	assert(target_squad == squad and smart == nil)
end }
function TeleportSquad() end

local callbacks = {}
function RegisterScriptCallback(name, callback) callbacks[name] = callback end

anomaly_donation_bridge_catalog = {
	capable = function() return true end,
	validate_sections = function(params, key, kind)
		assert(params.catalog_revision == "catalog-1")
		assert(key == "npc_sections" and kind == "npc")
		return { { section_id = "sim_default_stalker_1" } }
	end,
	is_spawn_safe = function(section, kind)
		return section == "sim_default_stalker_1" and kind == "npc"
	end
}

anomaly_donation_bridge_common.spawn_points = function()
	return { { position = position(2, 0, 0), vertex_id = 10 } }, "requested_ring"
end

local effect = load_module(optional_root,
	"anomaly_donation_bridge_effect_hardcoon_adb_annoying_fan")
effect.on_game_start()
local handler = assert(anomaly_donation_bridge_registry.get(
	"hardcoon.adb.annoying_fan"))

assert(select(1, handler.is_supported({ actor = actor })) == true)
local default_context = { actor = actor }
assert(handler.can_execute(default_context, {
	npc_sections = "sim_default_stalker_1",
	catalog_revision = "catalog-1",
	donor_name = "Default Fan"
}) == true)
assert(default_context.options.dialogue_count == 999 and
	default_context.options.interval_ms == 60000 and
	default_context.options.close_stock_dialogs == false and
	default_context.options.community == "stalker",
	"the template runtime defaults are not 999 talks every 60 seconds")
local context = { actor = actor }
local allowed, status, reason = handler.can_execute(context, {
	npc_sections = "sim_default_stalker_1",
	use_trader_faction = "true",
	dialogue_count = "3",
	interval_seconds = "3",
	catalog_revision = "catalog-1",
	donor_name = " Фанат Донатера ",
	donation_message = " Сообщение 😀 "
})
assert(allowed == true, tostring(status) .. ":" .. tostring(reason))
assert(context.options.community == "trader",
	"the technical trader faction mode was not selected")
local execute_status, detail = handler.execute(context)
assert(execute_status == "executed", tostring(execute_status) .. ":" .. tostring(detail))
assert(squad.player_id == "trader",
	"the spawned fan squad did not receive the trader faction")
assert(member.character_name_value ==
	anomaly_donation_bridge_common.game_text_cp1251("Фанат Донатера"),
	"the spawned fan did not receive the donor name")
assert(axr_companions == nil, "the smoke unexpectedly registered a stock companion")
local enemy_flags = { override = false, result = true }
assert(type(callbacks.on_enemy_eval) == "function")
callbacks.on_enemy_eval(client, {}, enemy_flags)
assert(enemy_flags.override == true and enemy_flags.result == false,
	"the fan did not ignore a combat target")

current_time = 2750
handler.update(current_time)
assert(weapon_releases == 0 and life.objects[901] == server_weapon,
	"fan spawn stripped the stock NPC weapon")
assert(inventory_reads == 0 and drop_calls == 0 and fatal_binding_calls == 0 and
	online_weapon.dropped ~= true and supplies.dropped ~= true,
	"fan update touched equipment or attempted the fatal native drop binding")
assert(client.community == "trader",
	"the online fan did not receive the trader faction")
assert(client.dialog_open_count == 0, "the first dialog ignored its interval")
current_time = 3000
handler.update(current_time)
assert(client.dialog_open_count == 1, "the first stock dialog was not opened")
actor.talking, client.talking = false, false
current_time = 3250
handler.update(current_time)
current_time = 6250
handler.update(current_time)
assert(client.dialog_open_count == 2, "the second stock dialog was not opened")

local saved = {}
assert(type(callbacks.save_state) == "function" and type(callbacks.load_state) == "function")
callbacks.save_state(saved)
assert(type(saved.anomaly_donation_bridge_annoying_fans_v1) == "table",
	"fan state was not persisted")
assert(saved.anomaly_donation_bridge_annoying_fans_v1[101].dialogue_text ==
	anomaly_donation_bridge_common.game_text_cp1251("Сообщение ?"),
	"the donation message was not persisted in Windows-1251")
assert(saved.anomaly_donation_bridge_annoying_fans_v1[101].close_stock_dialogs == false,
	"old/default fan actions must not automatically close interfaces")

local phrases = {}
local dialog = {}
function dialog:AddPhrase(text, id, parent, priority)
	local script = { actions = {} }
	function script:SetScriptText(value) self.text = value end
	function script:AddAction(value) self.actions[#self.actions + 1] = value end
	local phrase = { text = text, id = id, parent = parent, priority = priority,
		script = script }
	function phrase:GetPhraseScript() return self.script end
	phrases[#phrases + 1] = phrase
	return phrase
end
effect.init_dialog(dialog)
assert(#phrases == 2 and phrases[1].id == "0" and phrases[1].parent == "" and
	phrases[2].id == "1" and phrases[2].parent == "0")
assert(phrases[2].script.actions[1] == "dialogs.break_dialog")
assert(effect.dialog_fan_text(actor, client) ==
	anomaly_donation_bridge_common.game_text_cp1251("Сообщение ?"))
assert(effect.dialog_fan_text() ==
	anomaly_donation_bridge_common.game_text_cp1251("*молчит*"))
assert(effect.dialog_actor_text() ==
	anomaly_donation_bridge_common.game_text_cp1251("Отстань"))

local fan_state = rawget(_G, "__anomaly_donation_bridge_annoying_fan_v1")
local function current_fan() return assert(fan_state.fans[101]) end
local function reset_ui_case(close_dialogs)
	actor.talking, client.talking = false, false
	ui = { mode = 0, close_calls = 0 }
	_GUIs, pda.dialog_closed = {}, true
	actor_menu.dead_body_searching, bind_container.curBoxID = false, nil
	ui_inventory.GUI = script_inventory
	local fan = current_fan()
	fan.remaining, fan.next_due, fan.waiting_for_close = 3, current_time, false
	fan.close_stock_dialogs, fan.ui_close_retry_at = close_dialogs, nil
	fan_state.next_update, fan_state.ui_quiet_until = 0, 0
end
local function tick_ui()
	current_time = current_time + 250
	handler.update(current_time)
end
local function open_inventory(mode, native)
	ui.mode = mode
	if native then ui.native_open = true else
		ui.script_open = true
		_GUIs.UIInventory = { path = "ui_inventory" }
	end
	actor_menu.dead_body_searching = mode == 4
	bind_container.curBoxID = mode == 4 and 456 or nil
end

-- Default mode never interrupts inventory, corpse/box loot, trade or repair.
-- Expired calls are retained, not consumed or rescheduled by a busy interface.
for mode = 1, 4 do
	reset_ui_case(false)
	open_inventory(mode, false)
	local before, due = client.dialog_open_count, current_fan().next_due
	for _ = 1, 4 do tick_ui() end
	assert(client.dialog_open_count == before and current_fan().remaining == 3 and
		current_fan().next_due == due and ui.close_calls == 0 and ui.script_open,
		"default mode interrupted an inventory or consumed the pending call")
	script_inventory:Close() -- player closes it through its normal procedure
	tick_ui()
	assert(client.dialog_open_count == before, "dialogue raced inventory cleanup")
	tick_ui()
	assert(client.dialog_open_count == before + 1 and current_fan().remaining == 2)
end

-- Opt-in uses the script's Close (never raw HideDialog), or the native stock
-- close procedure. Opening a conversation in the same update is forbidden.
for _, native in ipairs({ false, true }) do
	for mode = 1, 4 do
		reset_ui_case(true)
		open_inventory(mode, native)
		local before = client.dialog_open_count
		tick_ui()
		assert(ui.close_calls == 1 and ui.mode == 0 and not Check_UI() and
			not actor_menu.dead_body_searching and bind_container.curBoxID == nil)
		assert(client.dialog_open_count == before and current_fan().remaining == 3,
			"fan opened talk in the same update as closing an inventory")
		tick_ui()
		assert(client.dialog_open_count == before, "UI settling interval was ignored")
		tick_ui()
		assert(client.dialog_open_count == before + 1 and current_fan().remaining == 2)
	end
end

-- Native PDA visibility is independent of actor_menu's inventory mode.
for _, close_dialogs in ipairs({ false, true }) do
	reset_ui_case(close_dialogs)
	ui.pda_open = true
	local before = client.dialog_open_count
	tick_ui()
	assert(client.dialog_open_count == before and current_fan().remaining == 3)
	if close_dialogs then
		assert(not ui.pda_open and ui.close_calls == 1)
		tick_ui(); tick_ui()
		assert(client.dialog_open_count == before + 1)
	else
		assert(ui.pda_open and ui.close_calls == 0)
	end
end

-- Unknown add-on windows, renamed/replaced inventory registrations and stock
-- confirmation modals must not be closed underneath their own UI lifecycle.
for _, blocked in ipairs({ "addon", "replacement", "modal", "pause", "dead", "probe" }) do
	reset_ui_case(true)
	open_inventory(4, false)
	if blocked == "addon" then _GUIs.Addon = { path = "some_addon" }
	elseif blocked == "replacement" then _GUIs.UIInventory.path = "replacement_inventory"
	elseif blocked == "modal" then ui.modal_open = true
	elseif blocked == "pause" then ui.paused = true
	elseif blocked == "dead" then ui.dead = true
	elseif blocked == "probe" then ui.probe_error = true end
	local before = client.dialog_open_count
	for _ = 1, 5 do tick_ui() end
	assert(ui.close_calls == 0 and ui.script_open and client.dialog_open_count == before and
		current_fan().remaining == 3, "unsafe UI was interrupted: " .. blocked)
end

-- No auto-close while far away, before the timer or during another dialogue.
for _, blocked in ipairs({ "distance", "timer", "talking" }) do
	reset_ui_case(true)
	open_inventory(4, false)
	local original_position = client.position
	if blocked == "distance" then client.position = function() return position(6, 0, 0) end
	elseif blocked == "timer" then current_fan().next_due = current_time + 10000
	else actor.talking = true end
	tick_ui()
	client.position = original_position
	assert(ui.close_calls == 0 and current_fan().remaining == 3)
end

-- A failed/refused close must never become a conversation. Retrying is
-- throttled, and eventual success still waits for the UI to settle.
for _, failure in ipairs({ "close_refused", "close_error" }) do
	reset_ui_case(true)
	open_inventory(4, false)
	ui[failure] = true
	local before = client.dialog_open_count
	for _ = 1, 4 do tick_ui() end
	assert(ui.close_calls == 1 and client.dialog_open_count == before and current_fan().remaining == 3)
	ui[failure] = false
	tick_ui()
	assert(ui.close_calls == 2 and client.dialog_open_count == before)
	tick_ui(); tick_ui()
	assert(client.dialog_open_count == before + 1)
end

-- A window can disappear before its closing callbacks finish: keep waiting
-- until the original mode/registration/box flags have actually been cleared.
reset_ui_case(true)
open_inventory(4, false)
ui.delayed_cleanup = true
local before = client.dialog_open_count
tick_ui()
assert(not ui.script_open and actor_menu.dead_body_searching and bind_container.curBoxID == 456)
for _ = 1, 4 do tick_ui() end
assert(client.dialog_open_count == before and current_fan().remaining == 3 and ui.close_calls == 1)
finish_inventory_close()
tick_ui(); tick_ui()
assert(client.dialog_open_count == before + 1)

-- The setting survives save/F9; transient UI deadlines/handles never enter
-- the save. Saves from before this checkbox safely load with it disabled.
reset_ui_case(true)
current_fan().ui_close_retry_at = current_time + 1000
local ui_saved = {}
callbacks.save_state(ui_saved)
local saved_fan = ui_saved.anomaly_donation_bridge_annoying_fans_v1[101]
assert(saved_fan.close_stock_dialogs == true and saved_fan.ui_close_retry_at == nil)
callbacks.load_state(ui_saved)
handler.on_game_load()
assert(current_fan().close_stock_dialogs == true and current_fan().ui_close_retry_at == nil)
saved_fan.close_stock_dialogs = nil
callbacks.load_state(ui_saved)
handler.on_game_load()
assert(current_fan().close_stock_dialogs == false, "legacy save enabled auto-close")

local close_context = { actor = actor }
assert(handler.can_execute(close_context, {
	npc_sections = "sim_default_stalker_1", catalog_revision = "catalog-1",
	donor_name = "Opt-in fan", close_stock_dialogs = "true"
}) == true and close_context.options.close_stock_dialogs == true)
assert(handler.execute(close_context) == "executed" and current_fan().close_stock_dialogs == true,
	"the checkbox did not reach the newly spawned fan")
local invalid_close, invalid_close_status, invalid_close_reason = handler.can_execute({ actor = actor }, {
	npc_sections = "sim_default_stalker_1", catalog_revision = "catalog-1",
	donor_name = "Invalid flag", close_stock_dialogs = "maybe"
})
assert(invalid_close == false and invalid_close_status == "rejected" and
	invalid_close_reason == "annoying_fan_close_dialogs_invalid")

local invalid_context = { actor = actor }
local invalid_ok, invalid_status, invalid_reason = handler.can_execute(invalid_context, {
	npc_sections = "sim_default_stalker_1", dialogue_count = "3",
	interval_seconds = "3", catalog_revision = "catalog-1", donor_name = "\n"
})
assert(invalid_ok == false and invalid_status == "rejected" and
	invalid_reason == "annoying_fan_donor_name_invalid")

local invalid_faction_ok, invalid_faction_status, invalid_faction_reason =
	handler.can_execute({ actor = actor }, {
		npc_sections = "sim_default_stalker_1", use_trader_faction = "maybe",
		catalog_revision = "catalog-1", donor_name = "Invalid Faction"
	})
assert(invalid_faction_ok == false and invalid_faction_status == "rejected" and
	invalid_faction_reason == "annoying_fan_faction_mode_invalid")

local legacy_armed_save = {}
callbacks.save_state(legacy_armed_save)
legacy_armed_save.anomaly_donation_bridge_annoying_fans_v1[101].disarmed = false
callbacks.load_state(legacy_armed_save)
handler.on_game_load()
current_time = current_time + 1000
handler.update(current_time)
assert(weapon_releases == 0 and inventory_reads == 0 and drop_calls == 0 and fatal_binding_calls == 0,
	"loading a legacy fan re-enabled disarming or the invalid native call")

print("annoying fan smoke passed: stock weapons retained, no disarm or fatal drop binding after spawn/load; safe default wait, opt-in stock inventory/PDA close, delayed cleanup, modal/add-on guards, failures, quotas and save/F9 compatibility (mock API)")
