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
	capable = function() return true end,
	validate_revision = function(params)
		return params.catalog_revision == "rev", "catalog_revision_mismatch"
	end,
	validate_section = function(params)
		if params.catalog_revision ~= "rev" then return nil, "catalog_revision_mismatch" end
		return { section_id = params.section_id, kind = "item", available = true,
			display_name = params.section_id == "item_test" and "" or "Тестовый предмет" }
	end,
	random_available_weapon = function()
		return { section_id = "wpn_pm", kind = "item", available = true,
			display_name = "Пистолет ПМ" }
	end,
	random_available_outfit = function()
		return { section_id = "novice_outfit", kind = "item", available = true,
			display_name = "Куртка новичка" }
	end,
	random_available_helmet = function()
		return { section_id = "helm_m40", kind = "item", available = true,
			display_name = "Шлем М40" }
	end,
	random_available_artifact = function()
		return { section_id = "af_test", kind = "item", available = true,
			display_name = "Артефакт" }
	end,
	compatible_ammo = function(section)
		return section == "wpn_pm" and "ammo_9x18_fmj" or nil
	end,
	is_weapon = function(section) return section == "wpn_pm" end,
	is_available = function(section)
		return section == "ordinary" or section == "item_test" or
			section == "wpn_pm" or section == "novice_outfit" or
			section == "helm_m40" or section == "af_test" or
			section == "ammo_9x18_fmj"
	end
}

local definitions = {
	item_test = { class = "II_ATTCH", can_trade = true },
	ordinary = { class = "II_ATTCH", can_trade = true },
	af_test = { class = "ARTEFACT", can_trade = true },
	medkit = { class = "II_FOOD", can_trade = true },
	bread = { class = "S_FOOD", can_trade = true },
	wpn_pm = { class = "WP_PM", slot = 2, can_trade = true,
		ammo_class = "ammo_9x18_fmj" },
	ammo_9x18_fmj = { class = "AMMO", can_trade = true },
	novice_outfit = { class = "E_STLK", slot = 7, can_trade = true },
	helm_m40 = { class = "EQU_HLMET", slot = 12, can_trade = true }
}
local ini = {}
function ini:section_exist(section) return definitions[section] ~= nil end
function ini:line_exist(section, key)
	return definitions[section] ~= nil and definitions[section][key] ~= nil
end
function ini:r_string(section, key) return tostring(definitions[section][key]) end
function ini:r_bool(section, key) return definitions[section][key] == true end
function ini:section_for_each(callback)
	for section in pairs(definitions) do callback(section) end
end
function system_ini() return ini end
function get_object_story_id() return 0 end

local function item(id, section, slot)
	local value = {
		id = function() return id end,
		section = function() return section end,
		slot = function() return slot or 0 end
	}
	function value:get_remaining_uses() return self.uses or 1 end
	return value
end
local ordinary, artefact = item(1, "ordinary", 0), item(2, "af_test", 13)
local inventory, dropped = { ordinary, artefact }, {}
local slotted = {}
local item_in_slot_calls = 0
local actor = {}
function actor:position() return { x = 0, y = 0, z = 0 } end
function actor:level_vertex_id() return 1 end
function actor:game_vertex_id() return 2 end
function actor:id() return 3 end
function actor:item_in_slot(slot)
	item_in_slot_calls = item_in_slot_calls + 1
	return slotted[slot]
end
function actor:active_item() return nil end
function actor:is_on_belt(value) return value:slot() == 13 end
function actor:iterate_inventory(callback, owner)
	for _, value in ipairs(inventory) do callback(owner, value) end
end
function actor:iterate_ruck(callback, owner)
	for _, value in ipairs(inventory) do
		if value:slot() ~= 13 then callback(owner, value) end
	end
end
local function record_drop(value)
	dropped[value:id()] = true
	for slot, current in pairs(slotted) do
		if current:id() == value:id() then slotted[slot] = nil end
	end
end
function actor:drop_item(value) record_drop(value) end
function actor:drop_item_and_teleport(value, position)
	assert(position ~= nil and position.x == 0 and position.y == 0 and position.z == 0)
	record_drop(value)
end
function actor:move_to_ruck(value)
	for slot, current in pairs(slotted) do
		if current:id() == value:id() then slotted[slot] = nil end
	end
end
function actor:is_in_ruck(value)
	for _, current in ipairs(inventory) do
		if current:id() == value:id() then
			for _, equipped in pairs(slotted) do
				if equipped:id() == value:id() then return false end
			end
			return true
		end
	end
	return false
end
function actor:move_to_slot(value)
	slotted[value:slot()] = value
end
local made_active = nil
function actor:make_item_active(value) made_active = value end
function actor:eat(value)
	value.uses = value:get_remaining_uses() - 1
	if value.uses <= 0 then
		for index, current in ipairs(inventory) do
			if current:id() == value:id() then table.remove(inventory, index) break end
		end
	end
end
db = { actor = actor }

local quick_slots = { slot_0 = "medkit", slot_1 = "", slot_2 = "", slot_3 = "" }
local console = {}
function console:get_string(name) return quick_slots[name] end
function get_console() return console end
function CInventory__eat() return true end

local create_count, fail_create_at, released = 0, 2, false
local created_by_section = {}
local online_objects = {}
local life = {}
function life:create()
	create_count = create_count + 1
	if create_count == fail_create_at then return nil end
	return { id = 99 }
end
function life:release() released = true end
function life:object(id)
	for _, value in ipairs(inventory) do
		if value:id() == id then return { id = id } end
	end
	return nil
end
function alife() return life end
function alife_create_item(section)
	create_count = create_count + 1
	if create_count == fail_create_at then return nil end
	created_by_section[section] = (created_by_section[section] or 0) + 1
	local server = { id = 90 + create_count }
	online_objects[server.id] = item(server.id, section, section == "wpn_pm" and 2 or 0)
	return server
end
level = { object_by_id = function(id) return online_objects[id] end }
local last_tip = nil
news_manager = {
	send_tip = function(_, text)
		last_tip = text
		return true
	end
}

if arg[2] ~= nil then
	load_effect(arg[2], "anomaly_donation_bridge_effect_spawn_items").on_game_start()
	for _, id in ipairs({ "damage_equipment", "remove_equipment",
		"drop_random_items", "use_random_consumable" }) do
		load_effect(assert(arg[3]),
			"anomaly_donation_bridge_effect_" .. id).on_game_start()
	end
else
	local actions = load_module("anomaly_donation_bridge_actions_inventory")
	actions.register_all()
end
assert(#anomaly_donation_bridge_registry.ids() == 5)

local function dispatch(action_id, params)
	return anomaly_donation_bridge_registry.dispatch(
		action_id, { actor = actor, now = 1000 }, params or {})
end

local condition_changed = false
local scaled_item = item(3, "ordinary", 7)
function scaled_item:condition() return 50 end
function scaled_item:set_condition() condition_changed = true end
slotted[7] = scaled_item
local scaled_status, scaled_reason = dispatch("damage_equipment", {
	target = "outfit", percent = "25"
})
assert(scaled_status == "rejected" and
	scaled_reason == "gear_condition_scale_unsupported")
assert(condition_changed == false)
slotted[7] = nil

local outfit = item(6, "ordinary", 7)
local helmet = item(7, "ordinary", 12)
inventory[#inventory + 1] = outfit
inventory[#inventory + 1] = helmet
slotted[7], slotted[12] = outfit, helmet
local normal_is_in_ruck = actor.is_in_ruck
actor.is_in_ruck = function() return false end
local remove_status, remove_reason = dispatch("remove_equipment", { target = "both" })
assert(remove_status == "executed" and
	remove_reason == "equipment_removed_to_inventory_both")
assert(slotted[7] == nil and slotted[12] == nil)
actor.is_in_ruck = normal_is_in_ruck
for index = #inventory, 1, -1 do
	local id = inventory[index]:id()
	if id == 6 or id == 7 then table.remove(inventory, index) end
end

local damage_condition = 0.8
local damage_outfit = item(5, "ordinary", 7)
function damage_outfit:condition() return damage_condition end
function damage_outfit:set_condition(value) damage_condition = value end
slotted[7] = damage_outfit
local damage_status, damage_reason = dispatch("damage_equipment", {
	target = "both", percent = "25"
})
assert(damage_status == "executed" and damage_reason == "equipment_damaged_both")
assert(math.abs(damage_condition - 0.55) < 0.001)
slotted[7] = nil

local helmet_condition = 0.9
local damage_helmet = item(8, "ordinary", 12)
function damage_helmet:condition() return helmet_condition end
function damage_helmet:set_condition(value) helmet_condition = value end
slotted[12] = damage_helmet
damage_status, damage_reason = dispatch("damage_equipment", {
	target = "both", percent = "10"
})
assert(damage_status == "executed" and damage_reason == "equipment_damaged_both")
assert(math.abs(helmet_condition - 0.8) < 0.001)
slotted[12] = nil

local single_outfit = item(9, "ordinary", 7)
inventory[#inventory + 1] = single_outfit
slotted[7] = single_outfit
remove_status, remove_reason = dispatch("remove_equipment", { target = "both" })
assert(remove_status == "executed" and
	remove_reason == "equipment_removed_to_inventory_both" and slotted[7] == nil)
for index = #inventory, 1, -1 do
	if inventory[index]:id() == 9 then table.remove(inventory, index) end
end

local spawn_status, spawn_reason = dispatch("spawn_items", {
	section_id = "item_test", count = "2", catalog_revision = "rev"
})
assert(spawn_status == "rejected" and spawn_reason == "item_spawn_failed_rolled_back",
	tostring(spawn_status) .. ":" .. tostring(spawn_reason))
assert(released == true)

create_count, fail_create_at = 0, nil
local random_weapon_status, random_weapon_reason = dispatch("spawn_items", {
	section_id = "__adb_random_weapon__", count = "1", catalog_revision = "rev",
	weapon_options = "equip"
})
assert(random_weapon_status == "executed" and
	random_weapon_reason == "spawned_item_groups_1_total_1_equip_pending")
assert(last_tip == anomaly_donation_bridge_common.game_text_cp1251(
	'Получено: "Пистолет ПМ" х1'))
assert(made_active == nil)
local inventory_state = assert(rawget(_G, "__anomaly_donation_bridge_inventory_actions_v1"))
assert(inventory_state.pending_equip ~= nil)
assert(online_objects[inventory_state.pending_equip.object_id] ~= nil)
assert(online_objects[inventory_state.pending_equip.object_id]:section() == "wpn_pm")
local equip_update = anomaly_donation_bridge_registry.get("spawn_items").update
local equip_ok, equip_error = pcall(equip_update, 1100)
assert(equip_ok, tostring(equip_error))
assert(made_active ~= nil and made_active:section() == "wpn_pm")

create_count, fail_create_at = 0, nil
local ammo_before = created_by_section.ammo_9x18_fmj or 0
local ammo_status, ammo_reason = dispatch("spawn_items", {
	items = "wpn_pm*2;ordinary*1", catalog_revision = "rev",
	weapon_options = "add_ammo", ammo_packs = "3"
})
assert(ammo_status == "executed" and
	ammo_reason == "spawned_item_groups_2_total_3_ammo_packs_6")
assert((created_by_section.ammo_9x18_fmj or 0) - ammo_before == 6)

create_count, fail_create_at = 0, nil
ammo_before = created_by_section.ammo_9x18_fmj or 0
local no_weapon_status, no_weapon_reason = dispatch("spawn_items", {
	section_id = "ordinary", count = "2", catalog_revision = "rev",
	weapon_options = "add_ammo", ammo_packs = "5"
})
assert(no_weapon_status == "executed" and
	no_weapon_reason == "spawned_item_groups_1_total_2")
assert((created_by_section.ammo_9x18_fmj or 0) == ammo_before)

create_count, fail_create_at = 0, nil
local outfit_status, outfit_reason = dispatch("spawn_items", {
	section_id = "__adb_random_outfit__", count = "1", catalog_revision = "rev"
})
assert(outfit_status == "executed" and
	outfit_reason == "spawned_item_groups_1_total_1")
assert((created_by_section.novice_outfit or 0) > 0)

create_count, fail_create_at = 0, nil
local helmet_status, helmet_reason = dispatch("spawn_items", {
	section_id = "__adb_random_helmet__", count = "1", catalog_revision = "rev"
})
assert(helmet_status == "executed" and
	helmet_reason == "spawned_item_groups_1_total_1")
assert((created_by_section.helm_m40 or 0) > 0)

create_count, fail_create_at = 0, nil
local artifact_status, artifact_reason = dispatch("spawn_items", {
	section_id = "__adb_random_artifact__", count = "1", catalog_revision = "rev"
})
assert(artifact_status == "executed" and
	artifact_reason == "spawned_item_groups_1_total_1")
assert((created_by_section.af_test or 0) > 0)

create_count, fail_create_at, last_tip = 0, nil, nil
local fallback_name_status, fallback_name_reason = dispatch("spawn_items", {
	section_id = "item_test", count = "3", catalog_revision = "rev"
})
assert(fallback_name_status == "executed" and
	fallback_name_reason == "spawned_item_groups_1_total_3")
assert(last_tip == anomaly_donation_bridge_common.game_text_cp1251(
	'Получено: "item_test" х3'))

item_in_slot_calls = 0
local invalid_drop_status, invalid_drop_reason = dispatch("drop_random_items", { count = "1000" })
assert(invalid_drop_status == "rejected" and invalid_drop_reason == "random_item_count_invalid")
local drop_status, drop_reason = dispatch("drop_random_items", { count = "999" })
assert(drop_status == "executed" and drop_reason == "random_items_dropped_1")
assert(dropped[1] == true and dropped[2] ~= true)
assert(item_in_slot_calls == 0,
	"random drop must enumerate the ruck instead of guessing engine slot bounds")

local hotbar_medkit = item(10, "medkit", 0)
hotbar_medkit.uses = 2
local inventory_food = item(11, "bread", 0)
inventory_food.uses = 1
inventory[#inventory + 1] = hotbar_medkit
inventory[#inventory + 1] = inventory_food
local hotbar_status, hotbar_reason = dispatch("use_random_consumable", {
	sources = "hotbar"
})
assert(hotbar_status == "executed" and
	hotbar_reason == "consumable_used_medkit" and hotbar_medkit.uses == 1)
CInventory__eat = nil
local inventory_status, inventory_reason = dispatch("use_random_consumable", {
	sources = "inventory"
})
assert(inventory_status == "executed" and
	inventory_reason == "consumable_used_bread")
for _, value in ipairs(inventory) do assert(value:id() ~= 11) end
local invalid_source, invalid_source_reason = dispatch("use_random_consumable", {
	sources = "hotbar,unknown"
})
assert(invalid_source == "rejected" and
	invalid_source_reason == "consumable_source_invalid")

print("inventory action smoke passed")
