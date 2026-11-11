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

local function position(x, y, z)
	return { x = x, y = y, z = z }
end

local function npc(id, x, story_id, online)
	local object = {
		id = id,
		position = position(x, 0, 0),
		m_game_vertex_id = 100,
		story_id = story_id,
		name_value = "NPC " .. id,
		online = online
	}
	function object:clsid() return 11 end
	function object:alive() return true end
	function object:section_name() return "stalker_test" end
	function object:spawn_ini() return nil end
	function object:character_name() return self.name_value end
	function object:set_character_name(value) self.name_value = value end
	return object
end

local server_actor = {
	id = 0,
	position = position(0, 0, 0),
	m_game_vertex_id = 100
}
local ordinary_near = npc(10, 2, nil, true)
local important_offline = npc(20, 12, "important_story_npc", false)
local important_far = npc(30, 30, "other_story_npc", true)
local companion = npc(35, 1, "companion_story_npc", true)
companion.group_id = 900
local other_level = npc(40, 1, "other_level_story_npc", true)
other_level.m_game_vertex_id = 200
local dead = npc(50, 0.5, "dead_story_npc", true)
function dead:alive() return false end

local life = {
	all = { ordinary_near, important_offline, important_far, companion, other_level, dead }
}
function life:actor() return server_actor end
function life:objects() return self.all end
function life:max_id() return 50 end
function life:object(id)
	for _, object in ipairs(self.all) do
		if object.id == id then return object end
	end
	return nil
end
function alife() return life end

local graph = {}
function graph:vertex(id)
	local level_id = id == 200 and 2 or 1
	return { level_id = function() return level_id end }
end
function game_graph() return graph end

clsid = { script_stalker = 11, script_actor = 12, stalker = 13, actor = 14 }
function IsStalker(object) return object:clsid() == clsid.script_stalker end
function get_object_story_id(id)
	for _, object in ipairs(life.all) do
		if object.id == id then return object.story_id end
	end
	return nil
end
function system_ini()
	return {
		section_exist = function() return true end,
		line_exist = function() return false end
	}
end
db = {
	actor = {},
	storage = {
		[10] = { object = ordinary_near },
		[30] = { object = important_far },
		[35] = { object = companion },
		[40] = { object = other_level },
		[50] = { object = dead }
	}
}
axr_companions = {
	non_task_companions = {},
	companion_squads = { [900] = { id = 900 } }
}
local tips = {}
news_manager = {
	send_tip = function(actor, text, sender, icon, duration)
		tips[#tips + 1] = { actor = actor, text = text, duration = duration }
		return true
	end
}

local effect = load_module(optional_root,
	"anomaly_donation_bridge_effect_hardcoon_adb_rename_nearest_npc")
effect.on_game_start()
local handler = assert(anomaly_donation_bridge_registry.get(
	"hardcoon.adb.rename_nearest_npc"))
assert(select(1, handler.is_supported({ actor = db.actor })) == true)

local context = { actor = db.actor }
local can_execute, status, reason = handler.can_execute(context, {
	donor_name = "  Offline Donor  "
})
assert(can_execute == true, tostring(status) .. ":" .. tostring(reason))
local execute_status, detail = handler.execute(context)
assert(execute_status == "executed", tostring(execute_status) .. ":" .. tostring(detail))
assert(important_offline.name_value == "Offline Donor",
	"the nearest important offline NPC was not renamed")
assert(ordinary_near.name_value == "NPC 10",
	"an ordinary but closer NPC incorrectly won over an important NPC")
assert(companion.name_value == "NPC 35", "a companion was renamed")
assert(detail == "npc_renamed_20_important_offline_pda", detail)
local phrase = assert(anomaly_donation_bridge_common.game_text_cp1251(
	" стал известен как: "))
assert(#tips == 1 and tips[1].duration == 8000 and tips[1].text ==
	"%c[255,100,255,100]NPC 20%c[default]" .. phrase ..
	"%c[255,238,153,26]Offline Donor%c[default]",
	"the PDA rename message or its colors are incorrect")

important_offline.story_id = nil
important_far.story_id = nil
other_level.story_id = nil
companion.story_id = nil
local fallback_context = { actor = db.actor }
assert(handler.can_execute(fallback_context, { donor_name = "Fallback Donor" }) == true)
local fallback_status, fallback_detail = handler.execute(fallback_context)
assert(fallback_status == "executed" and ordinary_near.name_value == "Fallback Donor")
assert(fallback_detail == "npc_renamed_10_ordinary_online_pda", fallback_detail)
assert(companion.name_value == "NPC 35", "a companion won the ordinary fallback")

local invalid_context = { actor = db.actor }
local invalid_ok, invalid_status, invalid_reason = handler.can_execute(invalid_context, {
	donor_name = "\n"
})
assert(invalid_ok == false and invalid_status == "rejected" and
	invalid_reason == "rename_npc_donor_name_invalid")

print("rename nearest NPC smoke passed")
