local script_root = assert(arg[1], "pass game-addon/gamedata/scripts as arg[1]")
local separator = package.config:sub(1, 1)

if io ~= nil and type(io.open) == "function" then
	local transport_file = assert(io.open(
		script_root .. separator .. "anomaly_donation_bridge_transport.script", "rb"))
	local transport_source = transport_file:read("*a")
	transport_file:close()
	assert(transport_source:find("SetNamedPipeHandleState", 1, true))
	assert(transport_source:find("ADB_DWORD%[1%]", 1) and
		transport_source:find("0x1", 1, true))
end

-- LuaJIT/X-Ray is Lua 5.1. Fengari is also used in CI as an independent
-- semantic runner and needs the standard 5.2+ _ENV compatibility shim.
if setfenv == nil then
	function setfenv(chunk, environment)
		local index = 1
		while true do
			local name = debug.getupvalue(chunk, index)
			if name == nil then break end
			if name == "_ENV" then
				debug.upvaluejoin(chunk, index, function() return environment end, 1)
				break
			end
			index = index + 1
		end
		return chunk
	end
end

local function load_module(name)
	local chunk = assert(loadfile(script_root .. separator .. name .. ".script"))
	local environment = setmetatable({}, { __index = _G })
	setfenv(chunk, environment)
	chunk()
	_G[name] = environment
	return environment
end

local common = load_module("anomaly_donation_bridge_common")
local protocol = load_module("anomaly_donation_bridge_protocol")
local sha256 = load_module("anomaly_donation_bridge_sha256")

local portable_semantic_runner = io == nil or type(io.open) ~= "function"
if portable_semantic_runner then
	-- Fengari uses a different numeric model than LuaJIT. SHA-256 is covered by
	-- the LuaJIT fixture and .NET protocol tests; keep this run focused on the
	-- real catalog code paths that caused the stock spawn regression.
	sha256.digest = function(value)
		if value == "" then
			return "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
		end
		return string.rep("a", 64)
	end
else
	assert(sha256.digest("") ==
		"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
	assert(sha256.digest("abc") ==
		"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
end
assert(protocol.percent_encode("A B\tЖ") == "A%20B%09%D0%96")
assert(protocol.percent_decode("A%20B%09%D0%96") == "A B\tЖ")
assert(select(1, protocol.percent_decode("%98")) == nil)
assert(select(1, protocol.encode_fields({ "STATUS", string.char(0x98) })) == nil)
local cp1251_hello = string.char(0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2)
assert(common.game_text_utf8(cp1251_hello) == "Привет")
assert(common.is_valid_utf8(common.game_text_utf8(cp1251_hello)))
assert(common.game_text_utf8(string.char(0x98)) == nil)
assert(common.game_text_utf8(string.char(0xC0, 0xAF)) == "АЇ")
assert(not common.is_valid_utf8(string.char(0xC0, 0xAF)))

local sections = {
	stock_item = { inv_name = "st_stock", cost = "100", class = "II_ATTCH",
		can_trade = true, quest_item = false, story_id = "0" },
	mod_food = { inv_name = "st_food", cost = "0", class = "II_FOOD",
		["$spawn"] = "food", inv_weight = "0.2", eat_health = "0.1" },
	ui_template = { inv_name = "st_template", cost = "0" },
	no_trade = { inv_name = "st_no_trade", cost = "50", class = "II_ATTCH",
		can_trade = false },
	ai_fake = { inv_name = "st_ai", cost = "100", class = "AI_TRD_S" }
}

local ini = {}
function ini:section_exist(section) return sections[section] ~= nil end
function ini:line_exist(section, key)
	return sections[section] ~= nil and sections[section][key] ~= nil
end
function ini:r_string(section, key) return tostring(sections[section][key]) end
function ini:r_bool(section, key) return sections[section][key] == true end
function ini:section_for_each(callback)
	for section in pairs(sections) do
		local stop = callback(section)
		assert(type(stop) == "boolean", "section_for_each callback must return bool")
		if stop then return end
	end
end

function system_ini() return ini end
local stock_spawner_blacklist = { zat_b5_stalker_raider_1 = true }
local user_exclusions = {}
function ini_file(path)
	if path == "plugins\\anomaly_donation_bridge_exclusions.ltx" then
		return {
			line_exist = function(_, section, key)
				return section == "excluded_sections" and user_exclusions[key] == true
			end
		}
	end
	assert(path == "plugins\\spawner_blacklist.ltx")
	return {
		line_exist = function(_, section, key)
			return section == "ignore_sections" and stock_spawner_blacklist[key] == true
		end
	}
end
game = { translate_string = function(token)
	if token == "st_stock" then return cp1251_hello end
	if token == "st_template" then return string.char(0x98) end
	if token == "st_faction_stalker" then return "Одиночки" end
	if token == "st_faction_bandit" then return "Бандиты" end
	if token == "st_faction_ecolog" then return "Учёные" end
	return token
end }
anomaly_donation_bridge_transport = { process_id = function() return 42 end }

local catalog = load_module("anomaly_donation_bridge_catalog")
assert(catalog.capable())
assert(catalog.rebuild())
local snapshot = assert(catalog.snapshot())
assert(snapshot.content_hash:match("^[0-9a-f]+$") and #snapshot.content_hash == 64)
-- Independently reproduced with .NET UTF8 + SHA256 over the canonical lines.
if not portable_semantic_runner then
	assert(snapshot.content_hash ==
		"772ab2746873a4d17ad9d82447a6056c99600256155fdc8c1e1fe56ed7baba0d")
end
sections.game_relations = {
	communities = "actor, 0, actor_stalker, 1, actor_bandit, 2, stalker, 3, bandit, 4"
}
sections.actor_communities = {
	actor_stalker = "stalker, bandit", actor_bandit = "bandit, stalker"
}
sections.dog_normal = {
	name = "st_mutant_dog", class = "SM_DOG_S", species = "dog",
	visual = "monsters\\dog\\dog", ["$spawn"] = "\"monsters\\dogs\\dog_02_normal\""
}
sections.dog_arena = {
	name = "st_mutant_dog", class = "SM_DOG_S", species = "dog",
	visual = "monsters\\dog\\dog", ["$spawn"] = "monsters\\arena\\dog_arena"
}
sections.dog_without_spawn = {
	name = "st_mutant_dog", class = "SM_DOG_S", species = "dog",
	visual = "monsters\\dog\\dog"
}
sections.wpn_pm = {
	inv_name = "st_wpn_pm", cost = "5000", class = "WP_PM", slot = "2",
	can_trade = true, quest_item = false, story_id = "0",
	ammo_class = "ammo_9x18_fmj, ammo_9x18_pmm"
}
sections.ammo_9x18_fmj = {
	inv_name = "st_ammo_9x18_fmj", cost = "100", class = "AMMO_S",
	can_trade = true, quest_item = false, story_id = "0"
}
sections.novice_outfit = {
	inv_name = "st_novice_outfit", cost = "5000", class = "EQU_STLK", slot = "6", kind = "o_light",
	can_trade = true, quest_item = false, story_id = "0"
}
sections.helm_m40 = {
	inv_name = "st_helm_m40", cost = "5000", class = "E_HLMET", slot = "11", kind = "o_helmet",
	can_trade = true, quest_item = false, story_id = "0"
}
-- Real LTX slots are zero-based: PDA=7, backpack=12, outfit=6, helmet=11.
-- Mods can move equipment to other slots or give ordinary items misleading names/kinds.
local outfit_sections = {novice_outfit="EQU_STLK", neutral_scientific="EQU_SCIE",
	neutral_military="EQU_MLTR", neutral_exo="EQU_EXO", neutral_scripted="E_STLK"}
local helmet_sections = {helm_m40="E_HLMET", neutral_headgear="EQ_HLMET"}
for section, class_name in pairs(outfit_sections) do
	if not sections[section] then sections[section] = {
		inv_name=section, cost="5000", class=class_name, slot="19",
		can_trade=true, quest_item=false, story_id="0"} end
end
sections.neutral_headgear = {inv_name="Headgear", cost="1000", class="EQ_HLMET",
	slot="7", can_trade=true, quest_item=false, story_id="0"}
local non_equipment = {
	device_pda={class="D_PDA",slot="7",kind="i_device"},
	equ_small_pack={class="EQ_BAKPK",slot="12",kind="i_backpack"},
	misleading_outfit={class="II_ATTCH",slot="6",kind="o_heavy"},
	misleading_helm={class="II_ATTCH",slot="11",kind="o_helmet"},
	misleading_slot_outfit={class="II_ATTCH",slot="7"},
	misleading_slot_helm={class="II_ATTCH",slot="12"}
}
for section, item in pairs(non_equipment) do
	item.inv_name=section; item.cost="1000"; item.can_trade=true
	item.quest_item=false; item.story_id="0"; sections[section]=item
end
sections.inventory_sort_registry = {device_pda="outfits",equ_small_pack="helmets"}
sections.af_fire = {
	inv_name = "st_af_fire", cost = "10000", class = "ARTEFACT",
	can_trade = true, quest_item = false, story_id = "0"
}
sections.malformed_mutant = { class = "SM_DOG_S" }
sections.sim_default_bandit_novice = {
	name = "st_bandit", class = "AI_STL_S",
	character_profile = "sim_default_bandit_novice", community = "bandit",
	["$spawn"] = "\"respawn\\sim_default_bandit_novice\""
}
sections.sim_default_army_trader = {
	name = "st_army_trader", class = "AI_STL_S",
	character_profile = "sim_default_military_2", community = "army",
	["$spawn"] = "\"respawn\\sim_default_army_trader\""
}
sections.sim_default_stalker_pri_a16 = {
	name = "st_stalker", class = "AI_STL_S",
	character_profile = "sim_default_stalker_2", community = "stalker",
	["$spawn"] = "\"respawn\\sim_default_stalker_pri_a16\""
}
sections.companion_survival_army = {
	name = "st_military", class = "AI_STL_S",
	character_profile = "sim_default_military_4", community = "army",
	["$spawn"] = "\"respawn\\companion_survival_army\""
}
sections.cat_weak_a = {
	name = "st_mutant_cat", class = "SM_CAT_S", species = "cat",
	visual = "monsters\\cat\\cat_weak", ["$spawn"] = "\"monsters\\cat\\cat_weak_a\""
}
sections.simulation_cat = {
	class = "ON_OFF_S", faction = "monster", common = true,
	npc_random = "cat_weak_a", npc_in_squad = "1, 3"
}
sections.army_sim_squad_10 = {
	class = "ON_OFF_S", faction = "army", common = true,
	npc = table.concat({
		"companion_survival_army", "companion_survival_army",
		"companion_survival_army", "companion_survival_army",
		"companion_survival_army", "companion_survival_army",
		"companion_survival_army", "companion_survival_army",
		"companion_survival_army", "companion_survival_army"
	}, ", ")
}
sections.zat_b5_stalker_raider_1 = {
	name = "st_story_raider", class = "AI_STL_S",
	character_profile = "zat_b5_stalker_raider_1", community = "bandit"
}
sections.unverified_mod_npc = {
	name = "st_mod_npc", class = "AI_STL_S",
	character_profile = "unverified_mod_npc", community = "bandit"
}
sections.bandit_sim_squad_safe = {
	class = "ON_OFF_S", faction = "bandit", common = true,
	npc_random = "sim_default_bandit_novice", npc_in_squad = "1, 3"
}
sections.bandit_sim_squad_unsafe = {
	class = "ON_OFF_S", faction = "bandit", common = true,
	npc_random = "zat_b5_stalker_raider_1", npc_in_squad = "1, 3"
}
sections.bandit_sim_squad_hidden = {
	class = "ON_OFF_S", faction = "bandit",
	npc_random = "sim_default_bandit_novice", npc_in_squad = "1, 3"
}
sections.bandit_sim_squad_mixed_unsafe = {
	class = "ON_OFF_S", faction = "bandit", common = true,
	npc = "sim_default_bandit_novice", npc_random = "zat_b5_stalker_raider_1",
	npc_in_squad = "1, 3"
}
sections.bandit_sim_squad_oversized = {
	class = "ON_OFF_S", faction = "bandit", common = true,
	npc_random = "sim_default_bandit_novice", npc_in_squad = "1, 40"
}
sections.bandit_sim_squad_directed = {
	class = "ON_OFF_S", faction = "bandit", common = true,
	npc_random = "sim_default_bandit_novice", npc_in_squad = "1, 3",
	spawn_point = "story_patrol"
}
sections.fireball_electric_zone = { class = "ZS_TORRD", name = "fireball_electric_zone" }
sections.fireball_acidic_zone = { class = "ZS_TORRD", name = "fireball_acidic_zone" }
sections.mincer_zone = { class = "ZS_MINCE", name = "mincer_zone" }
sections.mod_heli_custom={class="C_HLCP_S",visual="modded\\rotor_custom.ogf",name="Custom rotor"}
sections.mod_heli_missing={class="C_HLCP_S"}
sections.story_helicopter={class="C_HLCP_S",visual="model.ogf",story_id="story_heli"}
sections.adb_helicopter_boss={class="C_HLCP_S",visual="private.ogf"}
sections.adb_helicopter_companion={class="C_HLCP_S",visual="private_support.ogf"}
sections.adb_stationary_mgun={class="W_STMGUN",inv_name="MountedWeapon",["$spawn"]="weapons\\stationary_mgun"}
sections.adb_stationary_mgun_r020={class="W_STMGUN",inv_name="MountedWeapon",["$spawn"]="weapons\\stationary_mgun"}
sections.adb_stationary_mgun_blast={class="G_RGD5_S",inv_name="grenade",["$spawn"]="weapons\\grenades",cost="0",inv_weight="0.3"}
-- Private pet sections must not affect the public catalog, including item-like metadata.
sections.adb_mc2_private_normal = { class="SM_DOG_S", species="dog", visual="dog",
    ["$spawn"]="monsters\\dog", inv_name="Private", cost="100", can_trade=true }
game_relations = {
	factions_table = { "stalker", "bandit", "ecolog" },
	factions_table_all = { "actor_stalker", "actor_bandit", "actor_ecolog" }
}
assert(catalog.rebuild())
snapshot = assert(catalog.snapshot())
local saved_r_bool = ini.r_bool
ini.r_bool = nil
assert(not catalog.capable())
ini.r_bool = saved_r_bool

local entries = {}
for _, entry in ipairs(snapshot.entries) do entries[entry.section_id] = entry end
assert(entries.stock_item and entries.stock_item.available)
assert(entries.stock_item.category == "unclassified")
assert(entries.stock_item.display_name == "Привет")
assert(protocol.percent_encode(entries.stock_item.display_name) ==
	"%D0%9F%D1%80%D0%B8%D0%B2%D0%B5%D1%82")
assert(common.protected_section("stock_item") == false)
local ordinary_object = {
	section = function() return "stock_item" end,
	id = function() return 77 end
}
get_object_story_id = nil
assert(common.protected_object(ordinary_object) == true)
get_object_story_id = function() error("story API failure") end
assert(common.protected_object(ordinary_object) == true)
get_object_story_id = function() return 0 end
assert(common.protected_object(ordinary_object) == false)
get_object_story_id = function() return "story_unique" end
assert(common.protected_object(ordinary_object) == true)
assert(entries.mod_food and entries.mod_food.available)
assert(entries.mod_food.category == "consumables")
assert(entries.ui_template and not entries.ui_template.available)
assert(entries.ui_template.quarantine_reason == "invalid_structure")
assert(entries.ui_template.display_name == "ui_template")
assert(entries.no_trade and entries.no_trade.quarantine_reason == "non_tradeable")
assert(entries.ai_fake and entries.ai_fake.quarantine_reason == "unsupported_class")
assert(entries.dog_normal and entries.dog_normal.kind == "mutant" and
	entries.dog_normal.available and entries.dog_normal.strength == "normal")
assert(entries.dog_normal.native_class == "SM_DOG_S")
assert(entries.adb_mc2_private_normal == nil)
assert(entries.adb_helicopter_boss==nil)
assert(entries.adb_helicopter_companion==nil)
assert(entries.adb_stationary_mgun==nil and entries.adb_stationary_mgun_r020==nil and entries.adb_stationary_mgun_blast==nil,
    "Private stationary gun/temporary explosion leaked into ordinary item spawning")
assert(not catalog.is_available("adb_stationary_mgun","item") and not catalog.is_available("adb_stationary_mgun_blast","item"))
assert(entries.mod_heli_custom and entries.mod_heli_custom.kind=="helicopter" and
    entries.mod_heli_custom.available and entries.mod_heli_custom.native_class=="C_HLCP_S")
assert(entries.mod_heli_missing.quarantine_reason=="missing_required_fields" and not entries.story_helicopter.available)
local heli_selection=catalog.validate_sections({catalog_revision=snapshot.revision,helicopter_sections="mod_heli_custom"},"helicopter_sections","helicopter")
assert(heli_selection and #heli_selection==1 and heli_selection[1].section_id=="mod_heli_custom")
assert(not catalog.is_available("adb_helicopter_boss","helicopter"))
assert(entries.dog_arena and not entries.dog_arena.available and
	entries.dog_arena.quarantine_reason == "scripted")
assert(entries.dog_without_spawn and not entries.dog_without_spawn.available and
	entries.dog_without_spawn.quarantine_reason == "invalid_structure")
assert(entries.wpn_pm and entries.wpn_pm.kind == "item" and
	entries.wpn_pm.available and entries.wpn_pm.category == "weapons")
assert(catalog.is_weapon("wpn_pm"))
assert(catalog.random_available_weapon().section_id == "wpn_pm")
user_exclusions.wpn_pm = true
catalog.refresh_command_exclusions()
assert(not catalog.is_weapon("wpn_pm"))
local excluded_weapon, excluded_weapon_error = catalog.random_available_weapon()
assert(excluded_weapon == nil and excluded_weapon_error == "random_weapon_pool_empty")
user_exclusions.wpn_pm = nil
catalog.refresh_command_exclusions()
assert(catalog.is_weapon("wpn_pm"))
assert(entries.novice_outfit and entries.novice_outfit.available and
	entries.novice_outfit.category == "outfits" and catalog.is_outfit("novice_outfit"))
assert(entries.helm_m40 and entries.helm_m40.available and
	entries.helm_m40.category == "helmets" and catalog.is_helmet("helm_m40"))
for section in pairs(non_equipment) do
	assert(entries[section] and entries[section].available, "ordinary non-equipment must stay in the item catalog")
	assert(entries[section].category ~= "outfits" and entries[section].category ~= "helmets",
		"slot, registry, kind or name misclassified " .. section)
	assert(not catalog.is_outfit(section) and not catalog.is_helmet(section),
		"random equipment accepts " .. section)
end
for section in pairs(outfit_sections) do
	assert(entries[section].category == "outfits" and catalog.is_outfit(section) and not catalog.is_helmet(section),
		"native outfit class was lost or assigned by a modded slot: " .. section)
end
for section in pairs(helmet_sections) do
	assert(entries[section].category == "helmets" and catalog.is_helmet(section) and not catalog.is_outfit(section),
		"native helmet class was lost: " .. section)
end
for _=1,64 do
	assert(outfit_sections[catalog.random_available_outfit().section_id], "non-outfit selected")
	assert(helmet_sections[catalog.random_available_helmet().section_id], "non-helmet selected")
end
-- Recheck the actual class immediately before selection, even if an older cache says outfit/helmet.
sections.novice_outfit.class="D_PDA"
assert(not catalog.is_outfit("novice_outfit"))
sections.novice_outfit.class="EQU_STLK"
sections.helm_m40.class="EQ_BAKPK"
assert(not catalog.is_helmet("helm_m40"))
sections.helm_m40.class="E_HLMET"
for section in pairs(outfit_sections) do user_exclusions[section]=true end
for section in pairs(helmet_sections) do user_exclusions[section]=true end
catalog.refresh_command_exclusions()
local no_outfit,outfit_error=catalog.random_available_outfit()
local no_helmet,helmet_error=catalog.random_available_helmet()
assert(no_outfit==nil and outfit_error=="random_outfit_pool_empty")
assert(no_helmet==nil and helmet_error=="random_helmet_pool_empty")
for section in pairs(outfit_sections) do user_exclusions[section]=nil end
for section in pairs(helmet_sections) do user_exclusions[section]=nil end
catalog.refresh_command_exclusions()
assert(entries.af_fire and entries.af_fire.available and
	entries.af_fire.category == "artefacts" and catalog.is_artifact("af_fire"))
assert(catalog.random_available_artifact().section_id == "af_fire")
assert(catalog.compatible_ammo("wpn_pm") == "ammo_9x18_fmj")
assert(entries.malformed_mutant and entries.malformed_mutant.kind == "mutant" and
	not entries.malformed_mutant.available and
	entries.malformed_mutant.quarantine_reason == "missing_required_fields")
assert(entries.sim_default_bandit_novice and
	entries.sim_default_bandit_novice.kind == "npc" and
	entries.sim_default_bandit_novice.available)
assert(entries.sim_default_army_trader and
	not entries.sim_default_army_trader.available and
	entries.sim_default_army_trader.quarantine_reason == "scripted")
assert(entries.sim_default_stalker_pri_a16 and
	not entries.sim_default_stalker_pri_a16.available and
	entries.sim_default_stalker_pri_a16.quarantine_reason == "scripted")
assert(entries.companion_survival_army and
	entries.companion_survival_army.kind == "npc" and
	entries.companion_survival_army.available)
assert(entries.cat_weak_a and entries.cat_weak_a.kind == "mutant" and
	entries.cat_weak_a.available)
assert(entries.simulation_cat and entries.simulation_cat.kind == "mutant_squad" and
	entries.simulation_cat.available)
assert(entries.army_sim_squad_10 and
	entries.army_sim_squad_10.kind == "stalker_squad" and
	entries.army_sim_squad_10.available)
assert(entries.fireball_electric_zone and
	entries.fireball_electric_zone.kind == "anomaly" and
	not entries.fireball_electric_zone.available and
	entries.fireball_electric_zone.quarantine_reason == "missing_required_fields")
assert(entries.fireball_acidic_zone and
	not entries.fireball_acidic_zone.available)
assert(not catalog.is_spawn_safe("fireball_electric_zone", "anomaly"))
assert(not catalog.is_spawn_safe("fireball_acidic_zone", "anomaly"))
assert(entries.mincer_zone and entries.mincer_zone.kind == "anomaly" and
	entries.mincer_zone.available and catalog.is_spawn_safe("mincer_zone", "anomaly"))
local available_counts = { npc = 0, mutant = 0, stalker_squad = 0, mutant_squad = 0 }
for _, entry in pairs(entries) do
	if entry.available and available_counts[entry.kind] ~= nil then
		available_counts[entry.kind] = available_counts[entry.kind] + 1
	end
end
for kind, count in pairs(available_counts) do
	assert(count > 0, "stock spawn category became empty: " .. kind)
end
assert(entries.zat_b5_stalker_raider_1 and
	entries.zat_b5_stalker_raider_1.kind == "npc" and
	not entries.zat_b5_stalker_raider_1.available and
	entries.zat_b5_stalker_raider_1.quarantine_reason == "blacklisted")
assert(entries.unverified_mod_npc and not entries.unverified_mod_npc.available and
	entries.unverified_mod_npc.quarantine_reason == "scripted")
assert(entries.bandit_sim_squad_safe and entries.bandit_sim_squad_safe.available)
assert(entries.bandit_sim_squad_unsafe and
	not entries.bandit_sim_squad_unsafe.available and
	entries.bandit_sim_squad_unsafe.quarantine_reason == "invalid_structure")
assert(entries.bandit_sim_squad_hidden and
	not entries.bandit_sim_squad_hidden.available and
	entries.bandit_sim_squad_hidden.quarantine_reason == "scripted")
assert(entries.bandit_sim_squad_mixed_unsafe and
	not entries.bandit_sim_squad_mixed_unsafe.available and
	entries.bandit_sim_squad_mixed_unsafe.quarantine_reason == "invalid_structure")
assert(entries.bandit_sim_squad_oversized and
	not entries.bandit_sim_squad_oversized.available and
	entries.bandit_sim_squad_oversized.quarantine_reason == "invalid_structure")
assert(entries.bandit_sim_squad_directed and
	not entries.bandit_sim_squad_directed.available and
	entries.bandit_sim_squad_directed.quarantine_reason == "scripted")
assert(catalog.is_spawn_safe("sim_default_bandit_novice", "npc"))
assert(not catalog.is_spawn_safe("sim_default_army_trader", "npc"))
assert(not catalog.is_spawn_safe("sim_default_stalker_pri_a16", "npc"))
assert(not catalog.is_spawn_safe("zat_b5_stalker_raider_1", "npc"))
local safe_stalkers, stalker_error, stalker_kind = catalog.resolve_squad_members(
	"bandit_sim_squad_safe", "stalker_squad")
assert(stalker_error == nil and stalker_kind == "npc" and
	#safe_stalkers >= 1 and #safe_stalkers <= 3)
for _, section in ipairs(safe_stalkers) do
	assert(section == "sim_default_bandit_novice" and
		catalog.is_spawn_safe(section, "npc"))
end
local safe_mutants, mutant_error, mutant_kind = catalog.resolve_squad_members(
	"simulation_cat", "mutant_squad")
assert(mutant_error == nil and mutant_kind == "mutant" and
	#safe_mutants >= 1 and #safe_mutants <= 3)
for _, section in ipairs(safe_mutants) do
	assert(section == "cat_weak_a" and catalog.is_spawn_safe(section, "mutant"))
end
local fixed_stalkers = assert(catalog.resolve_squad_members(
	"army_sim_squad_10", "stalker_squad"))
assert(#fixed_stalkers == 10)
assert(catalog.resolve_squad_members(
	"bandit_sim_squad_unsafe", "stalker_squad") == nil)
assert(entries.adb_faction_actor_stalker and
	entries.adb_faction_actor_stalker.kind == "faction" and
	entries.adb_faction_actor_stalker.faction == "actor_stalker" and
	entries.adb_faction_actor_stalker.display_name == "Одиночки")
assert(entries.adb_faction_actor_bandit and
	entries.adb_faction_actor_bandit.display_name == "Бандиты")
assert(entries.adb_faction_actor_ecolog and
	entries.adb_faction_actor_ecolog.display_name == "Учёные")
assert(not entries.adb_faction_actor)

-- A live graph, not a compiled location list, supplies cross-level options.
local graph = {}
function graph:valid_vertex_id(id) return id < 3 end
function graph:accessible(id) return id ~= 1 end
function graph:vertex(id)
    return {
        level_id = function() return id == 0 and 1 or 2 end,
        level_point = function() return { x = id, y = 0, z = 0 } end,
        level_vertex_id = function() return id + 10 end
    }
end
function game_graph() return graph end
function alife()
	return { level_name = function(_, id)
		return id == 1 and "l01_escape" or "l02_garbage"
	end }
end
-- Catalog enumeration must not depend on the transport's optional callbacks
-- or on the ALife accessibility mask for other levels.
level = {}
assert(catalog.rebuild())
local level_snapshot = assert(catalog.snapshot())
local level_entries = {}
for _, entry in ipairs(level_snapshot.entries) do
	if entry.kind == "level" then level_entries[entry.section_id] = entry end
end
assert(level_entries.adb_level_l01_escape and
	level_entries.adb_level_l02_garbage and
	level_entries.adb_level_l02_garbage.faction == "l02_garbage")
assert(catalog.is_available("adb_level_l02_garbage", "level"))
local selected_levels = assert(catalog.validate_sections({
	catalog_revision = level_snapshot.revision,
	level_sections = "adb_level_l01_escape,adb_level_l02_garbage"
}, "level_sections", "level"))
assert(#selected_levels == 2)
graph.vertex = function(_, id)
    if id == 2 then return { level_id = function() return 2 end } end
    return {
        level_id = function() return id == 0 and 1 or 2 end,
        level_point = function() return { x = id, y = 0, z = 0 } end,
        level_vertex_id = function() return id + 10 end
    }
end
assert(catalog.validate_sections({
    catalog_revision = level_snapshot.revision,
    level_sections = "adb_level_l02_garbage"
}, "level_sections", "level") ~= nil)

local registry = load_module("anomaly_donation_bridge_registry")
assert(registry.register("add_radiation", { execute = function() end }) == false)
assert(registry.register("vendor.action", { execute = function() end }) == false)
assert(registry.register("author..weather", { execute = function() end }) == false)
assert(registry.register("author.modpack.", { execute = function() end }) == false)
assert(registry.register("author." .. string.rep("m", 33) .. ".weather",
	{ execute = function() end }) == false)
local interaction_busy=false
assert(registry.register("author.modpack.weather", {
	can_execute = function() return true end,
	is_blocking=function() return interaction_busy end,
	execute = function() return "executed", "ok" end
}) == true)
assert(registry.busy_reason()==nil)
interaction_busy=true;assert(registry.busy_reason()=="effect_in_progress")
interaction_busy=false;assert(registry.busy_reason()==nil)
assert(not registry.register("author.modpack.bad_allowlist",{allow_while_blocking={spawn_npcs="yes"},execute=function() end}))
local weather=registry.get("author.modpack.weather")
weather.allow_while_blocking={spawn_mutants=true}
local concurrent_count=0
assert(registry.register_builtin("spawn_mutants",{execute=function() concurrent_count=concurrent_count+1;return "executed","spawn_ok" end}))
interaction_busy=true
assert(registry.busy_reason()==nil and registry.busy_reason("spawn_mutants")==nil,
    "A limited interaction must not close the entire command transport")
assert(registry.busy_reason("author.modpack.weather")=="effect_in_progress")
local concurrent_status=registry.dispatch("spawn_mutants",{},{})
assert(concurrent_status=="executed" and concurrent_count==1)
local boundary_ran=false
local blocked_status=registry.dispatch("author.modpack.weather",{},{},function() boundary_ran=true end)
assert(blocked_status=="deferred" and not boundary_ran,"Blocked command reached its side-effect boundary")
local hard_busy=true
assert(registry.register_builtin("spawn_npcs",{is_blocking=function() return hard_busy end,execute=function() return "executed","ok" end}))
assert(registry.busy_reason()=="effect_in_progress" and registry.dispatch("spawn_mutants",{},{})=="deferred",
    "An allowlist bypassed a second fully exclusive interaction")
hard_busy=false;weather.allow_while_blocking={}
assert(registry.busy_reason()=="effect_in_progress","Empty allowlist must remain fully exclusive")
interaction_busy=false;weather.allow_while_blocking=nil
assert(registry.register("author.modpack.weather", { execute = function() end }) == false)
local before_execute_count = 0
local status, reason = registry.dispatch("author.modpack.weather", {}, {}, function()
	before_execute_count = before_execute_count + 1
	return true
end)
assert(status == "executed" and reason == "ok")
assert(before_execute_count == 1)
assert(registry.register("author.modpack.localized", {
	execute = function() return "executed", cp1251_hello end
}) == true)
local localized_status, localized_reason = registry.dispatch(
	"author.modpack.localized", {}, {})
assert(localized_status == "executed" and localized_reason == "Привет")
assert(common.is_valid_utf8(localized_reason))
assert(registry.register_builtin("add_radiation", {
	is_supported = function() return false, "probe_hint_only" end,
	support_probe_is_hint = true,
	can_execute = function() return true end,
	execute = function() return "executed", "builtin_actual_check_won" end
}) == true)
local builtin_status, builtin_reason = registry.dispatch("add_radiation", {}, {})
assert(builtin_status == "executed" and builtin_reason == "builtin_actual_check_won")
assert(registry.register("author.modpack.boundary", {
	execute = function() return "executed", string.rep("a", 239) .. "Ж" end
}) == true)
local boundary_status, boundary_reason = registry.dispatch(
	"author.modpack.boundary", {}, {})
assert(boundary_status == "executed" and #boundary_reason == 239)
assert(common.is_valid_utf8(boundary_reason))
assert(registry.register("author.modpack.invalid_reason", {
	execute = function() return "executed", string.char(0x98) end
}) == true)
local invalid_status, invalid_reason = registry.dispatch(
	"author.modpack.invalid_reason", {}, {})
assert(invalid_status == "executed" and invalid_reason == "invalid_reason_encoding")
local maximum_id = string.rep("a", 32) .. "." .. string.rep("b", 32) .. "." ..
	string.rep("c", 32) .. "." .. string.rep("d", 29)
assert(#maximum_id == 128)
assert(registry.register(maximum_id, {
	execute = function() return "executed", "long_id_ok" end
}) == true)
assert(registry.register(maximum_id .. "c", { execute = function() end }) == false)
local function maximum_extension_id(index)
	return string.rep("v", 32) .. "." .. string.rep("m", 32) .. "." ..
		("a" .. string.format("%02d", index) .. string.rep("x", 29)) .. "." ..
		string.rep("z", 29)
end
for index = 1, 59 do
	assert(#maximum_extension_id(index) == 128)
	assert(registry.register(maximum_extension_id(index), {
		execute = function() return "executed", "limit_ok" end
	}) == true)
end
local overflow_ok, overflow_reason = registry.register(maximum_extension_id(60), {
	execute = function() return "executed", "must_not_register" end
})
assert(overflow_ok == false and overflow_reason == "extension_action_limit_reached")
local capability_line = assert(protocol.encode_fields({ "CAPABILITIES", "unknown",
	"test", "named_pipe", table.concat(registry.supported_ids(), ";") }))
assert(#capability_line <= 16384)

-- Real merged table export: arbitrary mod species, non-vanilla order/teams.
sections.monster_communities={communities="mod_beast,77,actor,255,human,255,dog,43"}
sections.monster_relations={mod_beast="1,-2,0,0",actor="-1,1,1,-1",human="-1,0,1,-1",dog="0,-1,-1,1"}
sections.sim_default_test_novice={class="AI_STL_S",species="human",character_profile="sim_default_test_novice",["$spawn"]="respawn\\test"}
assert(catalog.rebuild())
local before_private=assert(catalog.snapshot())
local metadata="mod_beast,77,-2,0;actor,255,1,0;human,255,0,1;dog,43,-1,0"
assert(before_private.monster_communities==metadata and not catalog.companion_relations_ready())
-- Installing the private last row/column does not change the exported baseline
-- or catalog identity, so repeated scans cannot recursively add species.
sections.monster_communities.communities=sections.monster_communities.communities..",adb_mc2_companion,255"
sections.monster_relations.mod_beast="1,-2,0,0,-2"
sections.monster_relations.actor="-1,1,1,-1,1"
sections.monster_relations.human="-1,0,1,-1,-1"
sections.monster_relations.dog="0,-1,-1,1,-1"
sections.monster_relations.adb_mc2_companion="-1,0,-1,-1,1"
sections.adb_mc2_relations={profile="5",members="mod_beast,actor,human,dog"}
for i=1,3 do
    assert(catalog.rebuild())
    local after=assert(catalog.snapshot())
    assert(after.monster_communities==metadata and after.content_hash==before_private.content_hash)
    assert(catalog.companion_relations_ready())
end
sections.adb_mc2_relations.profile="3"
assert(catalog.rebuild() and catalog.snapshot().monster_communities==metadata and not catalog.companion_relations_ready(),
    "legacy matrix either blocked catalog recovery or bypassed native-combat preparation")
sections.adb_mc2_relations.profile="5"
sections.monster_relations.adb_mc2_companion="0,0,-1,-1,1"
assert(catalog.rebuild())
assert(not catalog.companion_relations_ready(),"private species still neutral to a modded mutant")
assert(catalog.rebuild() and catalog.snapshot().monster_communities==metadata,"our stale patch could not be regenerated")
sections.monster_relations.adb_mc2_companion="-1,0,-1,-1,1"
-- A mod adds a species; our old row/marker and the new species' row are short.
-- Export the NEW baseline for repair but do not allow a spawn before restart.
sections.monster_communities.communities="mod_beast,77,actor,255,human,255,dog,43,unexpected_mod,9,adb_mc2_companion,255"
sections.monster_relations.mod_beast="1,-2,0,0,0,-2"
sections.monster_relations.actor="-1,1,1,-1,-1,1"
sections.monster_relations.human="-1,0,1,-1,-1,-1"
sections.monster_relations.dog="0,-1,-1,1,0,-1"
sections.monster_relations.unexpected_mod="0,-1,-1,0,1"
assert(catalog.rebuild())
assert(catalog.snapshot().monster_communities==metadata..";unexpected_mod,9,-1,0" and not catalog.companion_relations_ready(),
    "modpack update either bypassed readiness or trapped catalog regeneration")
sections.monster_relations.dog="0,-1"
assert(catalog.rebuild() and catalog.snapshot().monster_communities=="","incomplete baseline matrix accepted")
sections.monster_communities=nil;sections.monster_relations=nil;sections.adb_mc2_relations=nil;sections.sim_default_test_novice=nil
assert(catalog.rebuild() and catalog.snapshot().monster_communities=="","legacy games/catalogs stopped working")
-- VFS scan: non-contiguous/mod-added tracks, a live custom theme directory,
-- deduplication, stable IDs, exclusion and removal after catalog refresh.
FS={FS_ListFiles=1,FS_RootOnly=8};bit_or=function(x,y) return x+y end
sound_theme={theme={play_guitar={path=[[custom\camp\strum_]],prefix=false}}}
local sound_files={[[music\guitar_1.ogg]],[[music\guitar_19.ogg]],[[characters_voice\custom\camp\strum_mod.ogg]]}
local frees=0
function getFS() return {
    exist=function(_,alias,path) assert(alias=="$game_sounds$");for _,file in ipairs(sound_files) do if file==path then return true end end end,
    file_list_open=function(_,alias,dir,flags)
        assert(alias=="$game_sounds$" and flags==9)
        local names={}
        for _,file in ipairs(sound_files) do if file:sub(1,#dir)==dir then names[#names+1]=file:sub(#dir+1) end end
        names[#names+1]="other_song.ogg";names[#names+1]="guitar_../bad.ogg"
        return {Size=function() return #names end,GetAt=function(_,i) return names[i+1] end,Free=function() frees=frees+1 end}
    end} end
sections.adb_guitar_double={class="AI_STL_S",character_profile="sim_default_test_novice"}
assert(catalog.rebuild());local guitar_snapshot=assert(catalog.snapshot());local song_ids={}
for _,entry in ipairs(guitar_snapshot.entries) do
    assert(entry.section_id~="adb_guitar_double","Private guitar double leaked into spawn picker")
    if entry.kind=="guitar_music" then
        song_ids[#song_ids+1]=entry.section_id
        assert(catalog.guitar_sound(entry.section_id) and catalog.is_available(entry.section_id,"guitar_music"))
    end
end
assert(#song_ids==3 and frees==2,"VFS scan missed a non-contiguous/modded guitar track or leaked its list")
local chosen=song_ids[1]
assert(catalog.validate_sections({catalog_revision=guitar_snapshot.revision,song_sections=chosen},"song_sections","guitar_music"))
assert(catalog.validate_section({catalog_revision=guitar_snapshot.revision,song=chosen},"song","guitar_music"))
local old_path=assert(catalog.guitar_sound(chosen))
assert(catalog.rebuild() and catalog.guitar_sound(chosen)==old_path,"Refresh changed stable sound identity")
for index,file in ipairs(sound_files) do if file==old_path..".ogg" then table.remove(sound_files,index);break end end
assert(not catalog.guitar_sound(chosen) and not catalog.is_available(chosen,"guitar_music"),"Removed sound remained playable")
assert(catalog.rebuild() and not catalog.guitar_sound(chosen))
print("catalog/registry/protocol smoke passed")
