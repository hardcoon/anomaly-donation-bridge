local script_root = assert(arg[1], "pass game-addon/gamedata/scripts as arg[1]")
local separator = package.config:sub(1, 1)
local manifest_path = script_root .. separator .. ".." .. separator .. ".." .. separator .. "addon-manifest.json"
local manifest_file = assert(io.open(manifest_path, "rb"))
local manifest_text = manifest_file:read("*a")
manifest_file:close()
local expected_addon_version = assert(manifest_text:match('"addonVersion"%s*:%s*"([^"]+)"'))
local runtime_logs = {}
function printf(format, ...) runtime_logs[#runtime_logs+1] = string.format(format, ...) end

local function load_module(name)
	local root = name:find("_actions_", 1, true) and
		(arg[2] or (script_root .. separator .. ".." .. separator .. ".." .. separator ..
		"legacy-scripts")) or script_root
	local chunk = assert(loadfile(root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

anomaly_donation_bridge_common = load_module("anomaly_donation_bridge_common")
anomaly_donation_bridge_protocol = load_module("anomaly_donation_bridge_protocol")
anomaly_donation_bridge_registry = load_module("anomaly_donation_bridge_registry")

local dispatch_count, level_change_called = 0, false
assert(anomaly_donation_bridge_registry.register("author.test.action", {
	can_execute = function(_, params)
		return params.value == "1", "rejected", "bad_value"
	end,
	execute = function()
		dispatch_count = dispatch_count + 1
		return "executed", "extension_ok"
	end,
	on_level_changing = function() level_change_called = true end
}))

local invalidations = 0
local catalog_should_fail = false
anomaly_donation_bridge_catalog = {
	capable = function() return false end,
	invalidate = function() invalidations = invalidations + 1 end,
	snapshot = function()
		if catalog_should_fail then return nil, "catalog_api_unavailable" end
		return {
			revision = "revision-1", count = 1,
			content_hash = string.rep("a", 64),
			monster_communities="actor,255,1,0;custom_species,77,-1,0",
			entries = { {
				section_id = "item_test", kind = "item", display_name = "Тест",
				category = "unclassified", strength = "unclassified", faction = "",
				available = true, quarantine_reason = "none"
			} }
		}
	end
}

local connected, queued, sent, connect_count = false, {}, {}, 0
local fail_next_kind = nil
anomaly_donation_bridge_transport = {
	is_connected = function() return connected end,
	connect = function() connected = true connect_count = connect_count + 1 return true end,
	disconnect = function() connected = false end,
	process_id = function() return 1234 end,
	send = function(fields)
		if fail_next_kind == fields[1] then
			fail_next_kind = nil
			return false, "simulated_send_failure"
		end
		sent[#sent + 1] = fields
		return true
	end,
	poll = function()
		local events = queued
		queued = {}
		return events, nil
	end
}

local now = 2000
local save_observer
function time_global() return now end
local hud_messages, pda_messages, save_commands, created_save_paths = {}, {}, {}, {}
local save_test_root = os.getenv("TEMP") or "."
function getFS()
	return {
		update_path = function(_, root, relative)
			assert(root == "$game_saves$")
			return save_test_root .. separator .. "adb-runtime-smoke-" .. relative
		end
	}
end
actor_menu = {
	set_msg = function(_, text)
		hud_messages[#hud_messages + 1] = text
	end
}
news_manager = {
	send_tip = function(_, text)
		pda_messages[#pda_messages + 1] = text
		return true
	end
}
function exec_console_cmd(command)
	if save_observer then save_observer(command) end
	save_commands[#save_commands + 1] = command
	local save_name = command:match("^save (.+)$")
	if save_name ~= nil then
		local path = getFS():update_path("$game_saves$", save_name .. ".scop")
		local file = assert(io.open(path, "wb"))
		file:write("runtime smoke save")
		file:close()
		created_save_paths[#created_save_paths + 1] = path
	end
end
function user_name() return "Tester" end
local actor = {}
local actor_alive = true
function actor:alive() return actor_alive end
function actor:IsInCar() return false end
function actor:is_talking() return false end
function actor:has_info() return false end
db = { actor = actor, storage = {} }
level = { present = function() return true end }
function device() return { f_time_delta = 0.016 } end
GAME_VERSION = "1.5.3"

anomaly_donation_bridge_actions_actor = load_module(
	"anomaly_donation_bridge_actions_actor")
anomaly_donation_bridge_actions_inventory = load_module(
	"anomaly_donation_bridge_actions_inventory")
anomaly_donation_bridge_actions_spawn = load_module(
	"anomaly_donation_bridge_actions_spawn")
anomaly_donation_bridge_actions_actor.register_all()
anomaly_donation_bridge_actions_inventory.register_all()
anomaly_donation_bridge_actions_spawn.register_all()

local runtime = load_module("anomaly_donation_bridge_runtime")
assert(select(1, runtime.detect_engine_profile()) == "anomaly_1_5_3_vanilla")
get_modded_exes_version = function() return 20260722 end
assert(select(1, runtime.detect_engine_profile()) == "unknown")
get_modded_exes_name = function() return "Modded Exes" end
assert(select(1, runtime.detect_engine_profile()) == "xray_monolith_standard")
get_modded_exes_name = function() return "Modded Exes MT-TEST" end
assert(select(1, runtime.detect_engine_profile()) == "xray_monolith_mt_test")
get_modded_exes_name = function() return "Unrecognized Custom Exes" end
assert(select(1, runtime.detect_engine_profile()) == "unknown")
get_modded_exes_name = function()
	return string.char(0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2)
end
local cp_profile, cp_label = runtime.detect_engine_profile()
assert(cp_profile == "unknown" and cp_label:find("Привет", 1, true))
assert(anomaly_donation_bridge_common.is_valid_utf8(cp_label))
get_modded_exes_name = function() return string.char(0x98) .. "\tbroken" end
local invalid_profile, invalid_label = runtime.detect_engine_profile()
assert(invalid_profile == "unknown")
assert(anomaly_donation_bridge_common.is_valid_utf8(invalid_label))
assert(not invalid_label:find(string.char(0x98), 1, true))
get_modded_exes_name, get_modded_exes_version = nil, nil
runtime.update()
assert(sent[1][1] == "HELLO" and sent[1][2] == "1" and sent[1][3] == expected_addon_version)
assert(sent[1][4] == "1234")

queued = { { "WELCOME", "1", "1.0.0" } }
now = now + 100
runtime.update()
assert(sent[2][1] == "CAPABILITIES")
assert(sent[2][2] == "anomaly_1_5_3_vanilla")
assert(sent[2][5]:find("author.test.action", 1, true))
local feature_ids = ";" .. sent[2][4] .. ";"
assert(feature_ids:find(";correlated_ping;", 1, true))
assert(not feature_ids:find(";dynamic_catalog;", 1, true))
assert(not feature_ids:find(";catalog_sha256;", 1, true))
local advertised = ";" .. sent[2][5] .. ";"
assert(advertised:find(";random_active;", 1, true))
assert(advertised:find(";add_radiation;", 1, true))
assert(advertised:find(";spawn_items;", 1, true))
assert(advertised:find(";spawn_mutants;", 1, true))
assert(advertised:find(";trigger_emission;", 1, true))
assert(sent[3][1] == "STATUS" and sent[3][2] == "ready")

queued = { { "GET_CATALOG_INFO" } }
now = now + 100
runtime.update()
assert(sent[4][1] == "CATALOG_INFO" and sent[4][2] == "revision-1")
assert(sent[4][3] == "1" and #sent[4][4] == 64)

queued = { { "GET_CATALOG" } }
now = now + 100
runtime.update()
assert(sent[5][1] == "CATALOG_BEGIN" and #sent[5][4] == 64)
assert(sent[5][5]=="actor,255,1,0;custom_species,77,-1,0","native species metadata was not sent with the catalog transaction")
assert(sent[6][1] == "CATALOG_ENTRY" and #sent[6] == 11)
assert(sent[6][2] == "revision-1" and sent[6][3] == "0")
assert(sent[7][1] == "CATALOG_END" and sent[7][4] == sent[5][4])

local command = { "COMMAND", "cmd-1", "donation-1", "author.test.action",
	"100", "RUB", "value=1" }
queued = { command }
now = now + 100
runtime.update()
assert(sent[8][1] == "RESULT" and sent[8][2] == "cmd-1")
assert(sent[8][3] == "executed" and sent[8][4] == "extension_ok")
assert(dispatch_count == 1)

queued = { command }
now = now + 100
runtime.update()
assert(sent[9][1] == "RESULT" and sent[9][3] == "executed")
assert(dispatch_count == 1)

local before_probe = #sent
queued = { { "PING", "probe-123" } }
now = now + 100
runtime.update()
assert(#sent == before_probe + 1)
assert(sent[#sent][1] == "PONG" and sent[#sent][2] == "probe-123")

catalog_should_fail = true
queued = { { "GET_CATALOG" } }
now = now + 100
runtime.update()
assert(sent[#sent][1] == "STATUS" and sent[#sent][2] == "ready" and
	sent[#sent][3] == "catalog_api_unavailable")
catalog_should_fail = false
queued = { { "GET_CATALOG" } }
now = now + 100
runtime.update()
assert(sent[#sent][1] == "STATUS" and sent[#sent][2] == "ready" and
	sent[#sent][3] == "ok")

runtime.on_level_changing()
assert(connected and level_change_called and invalidations == 0)
assert(sent[#sent][1] == "STATUS" and sent[#sent][2] == "waiting" and
	sent[#sent][3] == "level_changing")
local before_connect = connect_count
now = now + 1000
runtime.update()
assert(connect_count == before_connect and connected)

runtime.on_game_load()
assert(invalidations == 0)
runtime.update()
assert(connect_count == before_connect and connected)
assert(sent[#sent][1] == "STATUS" and sent[#sent][2] == "waiting" and
	sent[#sent][3] == "game_loading")
now = now + 1600
runtime.update()
assert(connect_count == before_connect and connected)
assert(sent[#sent][1] == "STATUS" and sent[#sent][2] == "ready")

queued = { { "PING" } }
fail_next_kind = "PONG"
now = now + 100
runtime.update()
assert(not connected)
local before_reconnect = connect_count
now = now + 1000
runtime.update()
assert(connect_count == before_reconnect + 1 and connected)
assert(sent[#sent][1] == "HELLO")

queued = { { "WELCOME", "1", "1.0.0" } }
now = now + 100
runtime.update()
assert(sent[#sent - 1][1] == "CAPABILITIES" and sent[#sent][1] == "STATUS")

local result_recovery_command = { "COMMAND", "cmd-result-recovery", "donation-2",
	"author.test.action", "100", "RUB", "value=1" }
local dispatches_before_result_failure = dispatch_count
fail_next_kind = "RESULT"
queued = { result_recovery_command }
now = now + 100
runtime.update()
assert(dispatch_count == dispatches_before_result_failure + 1)
assert(not connected)

local connects_before_result_recovery = connect_count
now = now + 1000
runtime.update()
assert(connect_count == connects_before_result_recovery + 1 and connected)
assert(sent[#sent][1] == "HELLO")
queued = { { "WELCOME", "1", "1.0.0" } }
now = now + 100
runtime.update()
queued = { result_recovery_command }
now = now + 100
runtime.update()
assert(sent[#sent][1] == "RESULT" and sent[#sent][2] == "cmd-result-recovery")
assert(sent[#sent][3] == "executed" and sent[#sent][4] == "extension_ok")
assert(dispatch_count == dispatches_before_result_failure + 1)

local batch_command_1 = { "COMMAND", "cmd-batch-1", "donation-batch",
	"author.test.action", "100", "RUB", "value=1", "Донатчик", "Первый эффект",
	"1", "2", "3" }
local batch_command_2 = { "COMMAND", "cmd-batch-2", "donation-batch",
	"author.test.action", "100", "RUB", "value=1", "Донатчик", "Второй эффект",
	"2", "2", "3" }
queued = { batch_command_1 }
now = now + 100
runtime.update()
assert(#save_commands == 1 and save_commands[1]:find("before", 1, true))
assert(#hud_messages == 0)
now = now + 100
runtime.update()
assert(#hud_messages == 1)
assert(anomaly_donation_bridge_common.game_text_utf8(hud_messages[1]) ==
	"Донатчик: Первый эффект")
queued = { batch_command_2 }
now = now + 100
runtime.update()
assert(#save_commands == 2 and save_commands[2]:find("after", 1, true))
assert(#hud_messages == 1)
now = now + 3300
runtime.update()
assert(#save_commands == 2, "the save pair must not create any extra saves")
assert(#hud_messages == 2)
assert(anomaly_donation_bridge_common.game_text_utf8(hud_messages[2]) ==
	"Донатчик: Второй эффект")

local hidden_log_command = { "COMMAND", "cmd-hidden-log", "donation-hidden-log",
	"author.test.action", "100", "RUB", "value=1", "Донатчик", "Скрытый эффект",
	"1", "1", "4" }
queued = { hidden_log_command }
now = now + 100
runtime.update()
assert(sent[#sent][1] == "RESULT" and sent[#sent][2] == "cmd-hidden-log")
assert(#hud_messages == 2)
now = now + 3300
runtime.update()
assert(#hud_messages == 2)

local pda_command = { "COMMAND", "cmd-pda", "donation-pda",
	"author.test.action", "100", "RUB", "value=1", "Донатчик", "КПК эффект",
	"1", "1", "12" }
queued = { pda_command }
now = now + 100
runtime.update()
assert(sent[#sent][1] == "RESULT" and sent[#sent][2] == "cmd-pda")
assert(#hud_messages == 2 and #pda_messages == 1)
assert(anomaly_donation_bridge_common.game_text_utf8(pda_messages[1]) ==
	"%c[255,238,153,26]Донатчик%c[default]: КПК эффект")

-- Stock actor_on_update STOPS after death. The before-death callback must
-- publish waiting immediately, while native alive() can still be true.
local callbacks = {}
function RegisterScriptCallback(name, fn) callbacks[name] = fn end
local main = load_module("anomaly_donation_bridge_main")
main.on_game_start()
assert(callbacks.actor_on_before_death and callbacks.actor_on_update)
local cancelled = {ret_value=false}
local prior = #sent
callbacks.actor_on_before_death(42, cancelled)
assert(#sent==prior and cancelled.ret_value==false, "cancelled death was modified or announced")
local death_flags = {ret_value=true}
callbacks.actor_on_before_death(42, death_flags)
assert(death_flags.ret_value==true and actor_alive, "death observer changed the actor/flags")
assert(sent[#sent][1]=="STATUS" and sent[#sent][2]=="waiting" and sent[#sent][3]=="actor_dead",
	"death must invalidate stale ready WITHOUT another actor update")
assert(runtime.state_snapshot().reason=="actor_dead")
actor_alive=false
local death_dispatches, death_saves, death_hud, death_pda = dispatch_count,#save_commands,#hud_messages,#pda_messages
local death_command = {"COMMAND","cmd-during-death","donation-during-death","author.test.action",
	"100","RUB","value=1","Донатчик","Не выполнять при смерти","1","1","15"}
queued={death_command}; now=now+100; runtime.update()
assert(dispatch_count==death_dispatches and #save_commands==death_saves and
	#hud_messages==death_hud and #pda_messages==death_pda, "dead actor received an effect/save/notification")
local deferred
for i=prior+1,#sent do if sent[i][1]=="RESULT" and sent[i][2]=="cmd-during-death" then deferred=sent[i] end end
assert(deferred and deferred[3]=="deferred" and deferred[4]=="actor_dead")
actor_alive=true; callbacks.on_game_load()
queued={death_command}; now=now+100; runtime.update()
assert(dispatch_count==death_dispatches and sent[#sent][3]~="ready", "load grace was bypassed")
queued={death_command}; now=now+1600; runtime.update()
assert(dispatch_count==death_dispatches+1, "deferred death command was lost instead of retried")
queued={death_command}; now=now+100; runtime.update()
assert(dispatch_count==death_dispatches+1, "death recovery duplicated a command")
-- A mod may cancel death AFTER our callback. Do not latch a false death until F9.
callbacks.actor_on_before_death(42,{ret_value=true})
now=now+100; runtime.update()
assert(runtime.state_snapshot().status=="ready", "cancelled death left a living actor blocked")
local save_phases={}
local action_descriptor=anomaly_donation_bridge_registry.get("author.test.action")
action_descriptor.on_effect_save=function(stage,name,success)
    save_phases[#save_phases+1]=stage
    assert(name:find(" - ADB - ",1,true))
    if stage=="after" then assert(success==true) end
end
save_observer=function() assert(save_phases[#save_phases]=="before","Service guard did not precede native save") end
queued={{"COMMAND","cmd-save-guard","don-save-guard","author.test.action","100","RUB","value=1","Donor","Spawn","1","1","3"}}
now=now+100;runtime.update()
assert(table.concat(save_phases,",")=="before,after,before,after","Before/after service saves did not both use guards")
save_observer=nil;action_descriptor.on_effect_save=nil

local original_prune=anomaly_donation_bridge_common.prune_effect_saves
local retained={}
anomaly_donation_bridge_common.prune_effect_saves=function(limit,name)
    retained[#retained+1]={limit=limit,name=name};return true,0,0
end
local retention_command={"COMMAND","cmd-retention","don-retention","author.test.action","100","RUB","value=1","Donor","Spawn","1","1","3","7"}
queued={retention_command};now=now+100;runtime.update()
assert(#retained==2 and retained[1].limit==7 and retained[2].limit==7,"before+after saves receive the configured shared limit")
queued={retention_command};now=now+100;runtime.update()
assert(#retained==2,"duplicate delivery cannot repeat a save/cleanup")
anomaly_donation_bridge_common.prune_effect_saves=function()error("simulated cleanup failure")end
queued={{"COMMAND","cmd-retention-fail","don-retention-fail","author.test.action","100","RUB","value=1","Donor","Spawn","1","1","3","7"}}
local before_failure=dispatch_count;now=now+100;runtime.update()
assert(dispatch_count==before_failure+1,"cleanup failure cannot fail or replay the real effect")
anomaly_donation_bridge_common.prune_effect_saves=original_prune

-- Coarse READY remains open for allowed commands; conflicts stay deferred
-- before saves/messages, and can be retried after the interaction ends.
local limited_busy=true;local camera_calls=0
assert(anomaly_donation_bridge_registry.register("author.test.interaction",{
    is_blocking=function() return limited_busy end,allow_while_blocking={["author.test.action"]=true},
    execute=function() return "executed","ok" end}))
assert(anomaly_donation_bridge_registry.register("author.test.camera",{
    execute=function() camera_calls=camera_calls+1;return "executed","camera_ok" end}))
now=now+100;runtime.update();assert(runtime.state_snapshot().status=="ready","Allowlisted form closed transport")
local concurrent_before=dispatch_count
queued={{"COMMAND","cmd-concurrent","don-concurrent","author.test.action","100","RUB","value=1","Donor","Spawn","1","1","0"}}
now=now+100;runtime.update();assert(dispatch_count==concurrent_before+1,"Allowed spawn-like command did not execute")
local prior_saves=#save_commands
queued={{"COMMAND","cmd-conflict","don-conflict","author.test.camera","100","RUB","value=1","Donor","Camera","1","1","15"}}
now=now+100;runtime.update();assert(camera_calls==0 and #save_commands==prior_saves,"Conflicting command caused side effects")
local conflict
for _,line in ipairs(sent) do if line[1]=="RESULT" and line[2]=="cmd-conflict" then conflict=line end end
assert(conflict and conflict[3]=="deferred" and conflict[4]=="effect_in_progress")
limited_busy=false
queued={{"COMMAND","cmd-conflict","don-conflict","author.test.camera","100","RUB","value=1","Donor","Camera","1","1","0"}}
now=now+100;runtime.update();assert(camera_calls==1,"Deferred conflict could not run after form ended")

assert(anomaly_donation_bridge_registry.register("author.test.failure",{
    execute=function() error("intentional_runtime_smoke_failure") end}))
queued={{"COMMAND","cmd-error-log","don-error-log","author.test.failure","100","RUB","value=1","Donor","Failure","1","1","0"}}
now=now+100;runtime.update()
local saw_failure=false
for _,message in ipairs(runtime_logs) do
    assert(not message:find("command begin:",1,true) and not message:find("command complete:",1,true),"Routine command traces returned")
    if message:find("intentional_runtime_smoke_failure",1,true) then saw_failure=true end
end
assert(saw_failure,"Removing command traces also hid real execution errors")
for _, path in ipairs(created_save_paths) do os.remove(path) end
print("runtime protocol smoke passed")
