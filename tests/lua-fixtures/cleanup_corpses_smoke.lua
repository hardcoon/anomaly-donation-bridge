local core_root = assert(arg[1])
local _ = assert(arg[2])
local optional_root = assert(arg[3])
local separator = package.config:sub(1, 1)
local ACTION_ID = "hardcoon.adb.cleanup_corpses"

local function load_module(root, name)
    local chunk = assert(loadfile(root .. separator .. name .. ".script"))
    local environment = setmetatable({}, { __index = _G })
    setfenv(chunk, environment)
    chunk()
    _G[name] = environment
    return environment
end

local now, paused, world, clients, events, bags, tips, loot_calls, release_calls
local level_name, bag_class, commands, transfers_per_step, refuse_transfer, no_bag_client
local flush_calls, flushed_logs
local function vec(x, y, z)
    local v = { x = x or 0, y = y or 0, z = z or 0 }
    function v:set(a, b, c) self.x, self.y, self.z = a, b, c; return self end
    function v:distance_to_sqr(other)
        return (self.x - other.x)^2 + (self.y - other.y)^2 + (self.z - other.z)^2
    end
    return v
end
function vector() return vec() end
function time_global() return now end
function device() return { is_paused = function() return paused end } end
function alife_on_limit() return false end
function alife_record(server, _) assert(server.online == false) end
function get_object_story_id(id) return world[id] and world[id].story or nil end
function se_load_var(id, _, key) return world[id] and world[id][key] end
function get_object_squad(object) return world[object:id()].squad end
function IsStalker(object) return object and object.kind == "npc" end
function IsMonster(object) return object and object.kind == "mutant" end
function IsItem(kind, section)
    assert(kind == "quest")
    return section == "item_quest" or section == "quest_bolt"
end
clsid = { obj_bolt = 51, item_test = 52 }
local logs = {}
function printf(format, ...) logs[#logs + 1] = string.format(format, ...) end
function flush()
    flush_calls = flush_calls + 1
    flushed_logs = table.concat(logs, "\n")
end

local ini = {}
function ini:section_exist(section) return section == "inv_backpack" or section == "item_test" end
function ini:line_exist(section, key)
    -- The cleanup should use Anomaly's quest-item classification, not a
    -- second ad-hoc INI probe which can reject every normal corpse on errors.
    if section == "item_test" or section == "item_quest" then
        error("unexpected direct inventory INI probe")
    end
    return section == "inv_backpack" and key == "class"
end
function ini:r_string(section, key)
    assert(section == "inv_backpack" and key == "class")
    return bag_class
end
function ini:r_bool() return false end
function system_ini() return ini end

local life = {}
function life:object(id)
    local server = world[id]
    return server and not server.client_only and server or nil
end
function life:get_children(server)
    local ids = {}
    for id, item in pairs(world) do
        if item.parent_id == server.id then ids[#ids + 1] = id end
    end
    table.sort(ids)
    local index = 0
    return function() index = index + 1; return ids[index] end
end
function life:set_switch_online(id, value) world[id].switch_online = value end
function life:set_switch_offline(id, value) world[id].switch_offline_allowed = value end
function life:release(server, value)
    assert(value == true and server.online == false and clients[server.id] == nil,
        "release occurred before net_destroy")
    assert(not (db.storage[server.id] and db.storage[server.id].object),
        "release occurred before binder destruction")
    assert(self:get_children(server)() == nil, "a corpse was deleted with owned items")
    release_calls[#release_calls + 1] = server.id
    world[server.id] = nil
end
function alife() return life end

local function make_server(id, kind, name)
    local server = { id = id, kind = kind, name_value = name or ("object_" .. id),
        section_value = kind == "bag" and "inv_backpack" or kind == "item" and "item_test" or "test_npc",
        online = true, alive_value = false }
    function server:name() return self.name_value end
    function server:section_name() return self.section_value end
    function server:alive() return self.alive_value end
    function server:switch_offline()
        assert(self.switch_online == false and self.switch_offline_allowed == true)
        local id_to_destroy = self.id
        events[#events + 1] = function()
            if world[id_to_destroy] == self then
                self.online = false
                clients[id_to_destroy], db.storage[id_to_destroy] = nil, nil
                -- Native offline switching destroys NPC client-only bolts;
                -- they never existed in ALife and must not enter a loot stash.
                for child_id, child in pairs(world) do
                    if child.parent_id == id_to_destroy and child.client_only then
                        world[child_id], clients[child_id] = nil, nil
                    end
                end
            end
        end
    end
    world[id] = server
    return server
end

local function add_item(id, parent, attributes)
    local server = make_server(id, "item")
    server.parent_id = parent
    local object = { id_value = id, attributes = attributes or {} }
    function object:id() return self.id_value end
    function object:section() return server.section_value end
    function object:parent() return clients[server.parent_id] end
    function object:clsid()
        if server.class_value ~= nil then return server.class_value end
        return (server.section_value == "bolt" or server.section_value == "bolt_bullet") and
            clsid.obj_bolt or clsid.item_test
    end
    clients[id] = object
    return object
end

local function add_corpse(id, x, kind, flags)
    local server = make_server(id, kind or "npc")
    for key, value in pairs(flags or {}) do server[key] = value end
    local object = { id_value = id, kind = kind or "npc", position_value = vec(x, 0, 0) }
    function object:id() return self.id_value end
    function object:name() return server.name_value end
    function object:section() return server.section_value end
    function object:position() return self.position_value end
    function object:alive() return server.alive_value end
    function object:transfer_item(item, bag)
        transfers_per_step = transfers_per_step + 1
        assert(transfers_per_step <= 16, "the per-update item budget was exceeded")
        local item_server = assert(world[item:id()])
        assert(item_server.parent_id == self:id(), "an item was transferred from the wrong owner")
        if refuse_transfer == item:id() then return end
        events[#events + 1] = function()
            item_server.parent_id = bag:id()
        end
    end
    clients[id], db.storage[id] = object, { object = object }
    return object, server
end

function alife_create(section, position, vertex, game_vertex)
    assert(section == "inv_backpack" and vertex == 5 and game_vertex == 9)
    local id = 5000 + #bags
    local server = make_server(id, "bag")
    server.position = vec(position.x, position.y, position.z)
    local object = { id = function() return id end }
    if not no_bag_client then clients[id] = object end
    bags[#bags + 1] = server
    return server
end

level = {
    object_by_id = function(id) return clients[id] end,
    vertex_position = function(id)
        assert(id == 5, "a game vertex was incorrectly queried as a level vertex")
        return vec(0, 0, 0)
    end,
    name = function() return level_name end,
    map_add_object_spot_ser = function(id, spot, caption)
        assert(world[id] and spot == "treasure" and #caption > 0)
    end
}
release_body_manager = {
    get_release_body_manager = function()
        return { can_release = function(_, object)
            return not world[object:id()].known_info
        end }
    end
}
utils_item = { has_quest_item = function(object) return world[object:id()].quest end }
death_manager = { create_release_item = function(object)
    assert(object.kind == "npc", "cleanup tried to butcher a mutant")
    local server = world[object:id()]
    if server.death_dropped then return end
    server.death_dropped = true
    loot_calls[#loot_calls + 1] = object:id()
    if server.lazy_loot then
        events[#events + 1] = function() add_item(3000 + server.id, server.id) end
    end
    if server.lazy_quest then server.quest = true end
end }
news_manager = { send_tip = function(_, text) tips[#tips + 1] = text end }
ActorMenu = { get_actor_menu = function() return { IsShown = function() return paused end } end }

local handler
local function reset()
    now, paused, level_name, bag_class = 1000, false, "l01_escape", "S_INVBOX"
    world, clients, events, bags, tips, loot_calls, release_calls = {}, {}, {}, {}, {}, {}, {}
    commands, transfers_per_step, refuse_transfer, no_bag_client = 0, 0, nil, false
    logs = {}
    flush_calls, flushed_logs = 0, ""
    local actor = { position_value = vec(0, 0, 0) }
    function actor:position() return self.position_value end
    function actor:level_vertex_id() return 5 end
    function actor:game_vertex_id() return 9 end
    function actor:alive() return true end
    function actor:is_talking() return false end
    db = { actor = actor, storage = {} }
    axr_companions = { non_task_companions = {}, companion_squads = {} }
    anomaly_donation_bridge_common = load_module(core_root, "anomaly_donation_bridge_common")
    anomaly_donation_bridge_registry = load_module(core_root, "anomaly_donation_bridge_registry")
    local effect = load_module(optional_root,
        "anomaly_donation_bridge_effect_hardcoon_adb_cleanup_corpses")
    effect.on_game_start()
    handler = assert(anomaly_donation_bridge_registry.get(ACTION_ID))
    assert(handler.is_supported())
end

local function command(params)
    commands = commands + 1
    return { actor = db.actor, command = { command_id = "cleanup_" .. commands }, now = now }, params or {}
end
local function dispatch(context, params)
    context.now = now
    return anomaly_donation_bridge_registry.dispatch(ACTION_ID, context, params)
end
local function tick(apply_events)
    now = now + 100
    if apply_events ~= false then
        local queued = events
        events = {}
        for _, event in ipairs(queued) do event() end
    end
    transfers_per_step = 0
    anomaly_donation_bridge_registry.update(now)
end
local function complete(context, params)
    local status, detail = dispatch(context, params)
    assert(status == "deferred" or status == "executed", tostring(status) .. ":" .. tostring(detail))
    for index = 1, 3000 do
        if status ~= "deferred" then break end
        tick()
        status, detail = dispatch(context, params)
    end
    assert(status ~= "deferred", "cleanup never completed")
    return status, detail
end

-- Defaults: 100m, mutants disabled, safe exclusions, one stash, lazy stalker
-- loot generated only once, real object attributes/IDs survive transfers.
reset()
add_corpse(10, 99, "npc", { lazy_loot = true })
add_corpse(11, 100)
local outside = add_corpse(12, 100.1)
add_corpse(13, 1, "npc", { alive_value = true })
add_corpse(14, 2, "npc", { story = "quest_person" })
add_corpse(15, 3, "npc", { quest = true })
add_corpse(16, 4, "npc", { known_info = true })
add_corpse(17, 5, "npc", { dont_release = true })
add_corpse(18, 6)
axr_companions.non_task_companions[18] = true
add_corpse(19, 7, "mutant")
local attributes = { condition = 0.43, upgrades = "upgrade_a", ammo = 7, uses = 2 }
local original = add_item(100, 10, attributes)
local context, params = command()
local status, detail = complete(context, params)
assert(status == "executed" and detail:find("corpses_removed_2_items_2", 1, true), detail)
assert(#bags == 1 and bags[1].position.x == 0 and bags[1].position.y == 0.05)
assert(world[10] == nil and world[11] == nil and clients[12] == outside)
for id = 12, 19 do assert(world[id], "protected/live/outside body removed: " .. id) end
assert(clients[100] == original and clients[100].attributes == attributes)
assert(world[100].parent_id == bags[1].id and world[3010].parent_id == bags[1].id)
assert(#loot_calls == 2 and #release_calls == 2 and #tips == 1)
assert(dispatch(context, params) == "executed" and #bags == 1 and #loot_calls == 2,
    "a repeated command generated duplicate loot")
assert(flush_calls == 1 and flushed_logs:find("corpses_removed_2_items_2", 1, true),
    "cleanup must persist the log once, not per corpse or command retry")

-- Regression: 76 ordinary corpses with ordinary loot must not all be
-- misclassified as protected by the additional server-inventory check.
reset()
for id = 10, 85 do
    add_corpse(id, 1)
    add_item(1000 + id, id)
    add_item(2000 + id, id)
    world[2000 + id].client_only = true
    world[2000 + id].section_value = "bolt"
end
context, params = command()
status, detail = complete(context, params)
assert(status == "executed" and detail:find("corpses_removed_76_items_76", 1, true), detail)
assert(#bags == 1 and #release_calls == 76)
for id = 10, 85 do
    assert(world[id] == nil and world[1000 + id].parent_id == bags[1].id)
    assert(world[2000 + id] == nil and clients[2000 + id] == nil)
end

-- Regression from the game log: 163 bodies succeeded but 44 others carried
-- client-only bolt_bullet. All 207 ordinary bodies must now complete safely.
reset()
for id = 10, 216 do
    add_corpse(id, 1)
    add_item(1000 + id, id)
    add_item(2000 + id, id)
    world[2000 + id].client_only = true
    world[2000 + id].section_value = id < 173 and "bolt" or "bolt_bullet"
end
context, params = command()
status, detail = complete(context, params)
assert(status == "executed" and
    detail:find("corpses_removed_207_items_207_skipped_0_failed_0", 1, true), detail)
assert(#bags == 1 and #release_calls == 207)
for id = 10, 216 do
    assert(world[id] == nil and world[1000 + id].parent_id == bags[1].id)
    assert(world[2000 + id] == nil and clients[2000 + id] == nil)
end

-- Engine class covers add-on variants without trusting section names. Quest,
-- story, wrong-owner and wrong-class objects must still preserve the body.
reset()
for id = 10, 15 do add_corpse(id, 1) end
for id = 100, 105 do
    add_item(id, id - 90)
    world[id].client_only = true
    world[id].section_value = "addon_bolt"
    world[id].class_value = clsid.obj_bolt
end
world[101].section_value = "quest_bolt"
world[102].story = "important_addon_bolt"
world[103].section_value, world[103].class_value = "bolt_bullet", clsid.item_test
clients[104].parent = function() return clients[10] end
clients[105].clsid = function() error("class lookup unavailable") end
context, params = command()
status, detail = complete(context, params)
assert(status == "uncertain" and world[10] == nil and #bags == 0, detail)
assert(#release_calls == 1)
for id = 11, 15 do assert(world[id] and world[id + 90].parent_id == id) end

-- Compatibility for engines without the exported class identifier: accept
-- only the two known stock bolt sections, not arbitrary missing objects.
reset()
for id = 10, 12 do add_corpse(id, 1) end
for id = 100, 102 do add_item(id, id - 90); world[id].client_only = true end
world[100].section_value, world[101].section_value = "bolt", "bolt_bullet"
local stock_clsid = clsid
clsid = nil
context, params = command()
status, detail = complete(context, params)
clsid = stock_clsid
assert(status == "uncertain" and #release_calls == 2 and #bags == 0, detail)
assert(world[10] == nil and world[11] == nil and world[12] and world[102].parent_id == 12)

-- Only a positively identified client-only NPC bolt can be omitted. A real
-- ALife bolt is loot and must move; unknown missing/quest/story children stay.
reset()
for id = 10, 13 do add_corpse(id, 1) end
for id = 100, 103 do add_item(id, id - 90) end
world[100].section_value = "bolt" -- ALife-controlled, must be transferred
world[101].client_only = true -- unknown non-ALife loot, must not be discarded
world[102].client_only, world[102].section_value, world[102].story = true, "bolt", "important_bolt"
world[103].client_only, clients[103] = true, nil -- cannot positively identify
context, params = command()
status, detail = complete(context, params)
assert(status == "uncertain" and world[10] == nil, detail)
assert(world[100].parent_id == bags[1].id and #release_calls == 1)
for id = 11, 13 do assert(world[id] and world[id + 90].parent_id == id) end

reset()
add_corpse(10, 1)
add_item(100, 10)
world[100].section_value = "bolt_bullet" -- persistent variant is also real loot
context, params = command()
assert(complete(context, params) == "executed")
assert(world[10] == nil and world[100].parent_id == bags[1].id)

-- Stock quest classification and story IDs still protect inventory children
-- (including unusual mutant inventories which the stock NPC probe ignores).
reset()
for id = 10, 12 do add_corpse(id, 1, "mutant") end
add_item(100, 10)
world[100].section_value = "item_quest"
add_item(101, 11)
world[101].story = "important_item"
add_item(102, 12)
context, params = command({ delete_mutant_corpses = "true" })
status, detail = complete(context, params)
assert(status == "uncertain" and world[10] and world[11] and world[12] == nil, detail)
assert(world[100].parent_id == 10 and world[101].parent_id == 11)
assert(world[102].parent_id == bags[1].id and #release_calls == 1)

-- A genuine failed protection probe is diagnosed and stays fail-closed,
-- rather than being silently reported as a normal quest-item exclusion.
reset()
add_corpse(10, 1)
add_item(100, 10)
local stock_item_probe = IsItem
IsItem = function() error("quest lookup unavailable") end
context, params = command()
status, detail = complete(context, params)
IsItem = stock_item_probe
assert(status == "uncertain" and world[10] and world[100].parent_id == 10)
assert(#bags == 0 and #release_calls == 0)
assert(table.concat(logs, "\n"):find("quest lookup unavailable", 1, true),
    "the failed protection probe was not logged")
assert(detail:find("_reason_inventory_probe_failed_100", 1, true) and #detail <= 240,
    "the app history must receive the concrete failed probe within IPC limits")
assert(flush_calls == 1 and flushed_logs:find("quest lookup unavailable", 1, true),
    "the native API error must survive a buffered game log")

-- Custom radius and opted-in mutants: existing parts are moved, never butchered.
reset()
add_corpse(10, 10, "mutant")
add_corpse(11, 11)
local part = add_item(100, 10, { condition = 0.75 })
context, params = command({ radius_meters = "10", delete_mutant_corpses = "true" })
status, detail = complete(context, params)
assert(status == "executed" and world[10] == nil and world[11])
assert(#loot_calls == 0 and clients[100] == part and world[100].parent_id == bags[1].id)

-- Empty mutant corpse: no fictitious parts, no empty stash.
reset()
add_corpse(10, 10, "mutant")
context, params = command({ delete_mutant_corpses = "true" })
assert(complete(context, params) == "executed")
assert(#bags == 0 and #loot_calls == 0 and world[10] == nil)

-- Large inventory is bounded per frame, and late engine events are awaited.
reset()
add_corpse(10, 1)
for id = 100, 179 do add_item(id, 10) end
context, params = command()
assert(complete(context, params) == "executed")
assert(#bags == 1 and #release_calls == 1)
for id = 100, 179 do assert(world[id].parent_id == bags[1].id) end

-- A silent native transfer refusal must NEVER lead to deletion of owned loot.
reset()
add_corpse(10, 1)
add_item(100, 10)
refuse_transfer = 100
context, params = command()
status, detail = complete(context, params)
assert(status == "uncertain" and world[10] and world[100].parent_id == 10)
assert(#release_calls == 0 and detail:find("failed_1", 1, true), detail)
assert(detail:find("_reason_transfer_not_confirmed", 1, true), detail)

-- Partial success is counted accurately, but the remaining inventory/body is
-- never discarded after one of several native transfer events is refused.
reset()
add_corpse(10, 1)
add_item(100, 10)
add_item(101, 10)
refuse_transfer = 101
context, params = command()
status, detail = complete(context, params)
assert(status == "uncertain" and world[10] and #release_calls == 0)
assert(world[100].parent_id == bags[1].id and world[101].parent_id == 10)
assert(detail:find("items_1", 1, true), detail)

-- F9/before restore cancels old work, even if object IDs are reused. The data
-- loaded by the game are authoritative; no out-of-save job is replayed.
reset()
add_corpse(10, 1)
add_item(100, 10)
context, params = command()
assert(dispatch(context, params) == "deferred")
for index = 1, 4 do tick() end
anomaly_donation_bridge_registry.on_game_load()
events = {} -- native events are discarded/rebuilt by a real game load
local replacement = add_corpse(10, 1, "npc", { alive_value = true })
world[100].parent_id = 10
for index = 1, 20 do tick() end
status, detail = dispatch(context, params)
assert(status == "uncertain" and detail:find("cancelled_by_load", 1, true), detail)
assert(clients[10] == replacement and world[100].parent_id == 10 and #release_calls == 0)

-- Save/load after completion keeps actual stash/items and cannot duplicate them.
reset()
add_corpse(10, 1)
add_item(100, 10)
context, params = command()
assert(complete(context, params) == "executed")
anomaly_donation_bridge_registry.on_game_load()
assert(dispatch(context, params) == "executed" and #bags == 1)
assert(world[100].parent_id == bags[1].id)

-- Scene transition cancels without carrying targets to the next level.
reset()
add_corpse(10, 1)
context, params = command()
assert(dispatch(context, params) == "deferred")
anomaly_donation_bridge_registry.on_level_changing()
level_name = "l02_garbage"
for index = 1, 20 do tick() end
status, detail = dispatch(context, params)
assert(status == "uncertain" and world[10] and #release_calls == 0)

-- Do not touch corpses while the player is using an inventory/menu.
reset()
add_corpse(10, 1)
context, params = command()
assert(dispatch(context, params) == "deferred")
paused = true
for index = 1, 10 do tick() end
assert(#loot_calls == 0 and #bags == 0 and #release_calls == 0)
paused = false
assert(complete(context, params) == "executed")

-- Stock loot may reveal a quest item: keep that corpse intact.
reset()
add_corpse(10, 1, "npc", { lazy_quest = true })
context, params = command()
status = complete(context, params)
assert(status == "uncertain" and world[10] and #release_calls == 0)

-- Invalid config/unsafe stash class fails before any game mutation/save hook.
reset()
add_corpse(10, 1)
local before_calls = 0
context, params = command({ radius_meters = "0" })
assert(anomaly_donation_bridge_registry.dispatch(ACTION_ID, context, params,
    function() before_calls = before_calls + 1 end) == "rejected")
context, params = command({ delete_mutant_corpses = "maybe" })
assert(dispatch(context, params) == "rejected")
bag_class = "AI_STL"
context, params = command()
assert(dispatch(context, params) == "rejected")
assert(before_calls == 0 and #bags == 0 and #loot_calls == 0)

-- No bodies: successful no-op, no empty backpack.
reset()
context, params = command()
status, detail = dispatch(context, params)
assert(status == "executed" and detail:find("corpses_removed_0", 1, true) and #bags == 0)

-- Missing/never-online bag or a lost stash leaves bodies and loot alive.
reset()
add_corpse(10, 1)
add_item(100, 10)
no_bag_client = true
context, params = command()
status, detail = complete(context, params)
assert(status == "uncertain" and world[10] and world[100].parent_id == 10)
assert(#release_calls == 0 and detail:find("backpack_online_timeout", 1, true), detail)

reset()
add_corpse(10, 1)
add_item(100, 10)
context, params = command()
assert(dispatch(context, params) == "deferred")
for index = 1, 4 do tick() end
assert(#bags == 1)
world[bags[1].id], clients[bags[1].id] = nil, nil
tick()
status, detail = dispatch(context, params)
assert(status == "uncertain" and world[10] and #release_calls == 0)

print("Corpse cleanup smoke passed: 76 NPC bolts, 207 bodies including 44 bolt_bullet, add-on bolt classes, compatibility, protected children, radius/defaults, mutants, lazy loot, original item IDs, bounded transfers, offline release, failures, diagnostics, menus, F9 and level changes.")
