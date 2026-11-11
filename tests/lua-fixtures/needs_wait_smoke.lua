local core, effects, optional = assert(arg[1]), assert(arg[2]), assert(arg[3])
local sep = package.config:sub(1, 1)
local function load(root, name)
    local env = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(root .. sep .. name .. '.script'))
    setfenv(chunk, env); chunk(); _G[name] = env; return env
end
anomaly_donation_bridge_common = load(core, 'anomaly_donation_bridge_common')
anomaly_donation_bridge_registry = load(core, 'anomaly_donation_bridge_registry')
local food, generic_writes, native_calls, queued_delta, broken_setter = 0, 0, 0, 0, false
local condition = {}
function condition:GetSatiety() return food end
function condition:ChangeSatiety(delta)
    if broken_setter then error('native change failed') end
    native_calls = native_calls + 1; queued_delta = queued_delta + delta
end
local function engine_tick()
    food = math.max(0, math.min(1, food + queued_delta)); queued_delta = 0
end
-- Reproduce the user's engine: generic satiety is the base-class stub (1
-- even when actual HUD food is 0), and its setter does not change real food.
local actor = setmetatable({}, {
    __index = function(_, key) if key == 'satiety' then return 1 end end,
    __newindex = function(self, key, value)
        if key ~= 'satiety' then rawset(self, key, value); return end
        generic_writes = generic_writes + 1
    end
})
function actor:cast_Actor() return { conditions = function() return condition end } end
function actor:change_satiety() error('must use actual CActorCondition') end
db = { actor = actor }
local last_drink = 10000
actor_status_thirst = { get_water_deprivation = function() return last_drink / 10000 end }
load(optional, 'anomaly_donation_bridge_effect_change_needs').on_game_start()
local function dispatch(hunger, thirst, wait, before)
    local params = { hunger_percent = tostring(hunger), thirst_percent = tostring(thirst) }
    if wait ~= nil then params.wait_if_insufficient = wait end
    return anomaly_donation_bridge_registry.dispatch('change_needs',
        { actor = actor, now = 1000, command = { command_id = 'original', donation_id = 'donation' } }, params, before)
end
local function close(a, b) assert(math.abs(a - b) < 0.000001, tostring(a) .. ' ~= ' .. tostring(b)) end
local saves = 0
local function save() saves = saves + 1; return true end
assert(actor.satiety == 1 and condition:GetSatiety() == 0)
for i = 1, 20 do
    local status, reason = dispatch(-50, 20, nil, save)
    assert(status == 'deferred' and reason == 'needs_waiting_food')
    assert(food == 0 and last_drink == 10000 and native_calls == 0 and saves == 0 and queued_delta == 0)
end
food = 0.49; assert(select(1, dispatch(-50, 0)) == 'deferred'); close(food, 0.49)
food = 0.5; assert(select(1, dispatch(-50, 0)) == 'executed')
close(food, 0.5); close(queued_delta, -0.5); assert(native_calls == 1)
engine_tick(); close(food, 0)
-- Native change becomes visible only on the next engine update. A successful
-- submission must not be retried or marked uncertain while waiting for it.
assert(select(1, dispatch(50, 0)) == 'executed'); close(food, 0); close(queued_delta, 0.5)
engine_tick(); close(food, 0.5)

-- Only negatives wait. Full positive scales clamp immediately, including 100%.
food = 1; last_drink = 0
assert(select(1, dispatch(15, 15)) == 'executed'); engine_tick(); close(food, 1); assert(last_drink == 0)
food = 0.2; last_drink = 8000
assert(select(1, dispatch(100, 100)) == 'executed'); engine_tick(); close(food, 1); assert(last_drink == 0)
food = 0; last_drink = 10000
assert(select(1, dispatch(-15, -15, 'false')) == 'executed'); engine_tick(); close(food, 0); assert(last_drink == 10000)

-- For negative thirst the reserve is hydration, inverse of deprivation.
food = 0.5; last_drink = 8600
local status, reason = dispatch(20, -15)
assert(status == 'deferred' and reason == 'needs_waiting_water'); close(food, 0.5); assert(last_drink == 8600)
last_drink = 8500
assert(select(1, dispatch(20, -15)) == 'executed'); engine_tick(); close(food, 0.7); assert(last_drink == 10000)
food = 0; last_drink = 10000
status, reason = dispatch(-15, -15)
assert(status == 'deferred' and reason == 'needs_waiting_food_and_water')
food = 0.15; last_drink = 8500
assert(select(1, dispatch(-15, -15)) == 'executed'); engine_tick(); close(food, 0); assert(last_drink == 10000)

-- Save callbacks can change stats. Recheck before any write, and never apply
-- the positive half on repeated failed preconditions of the same command.
food = 0.2; last_drink = 5000
local old_calls = native_calls
status, reason = dispatch(-15, 20, 'true', function() food = 0; return true end)
assert(status == 'deferred' and reason == 'needs_waiting_food' and native_calls == old_calls)
assert(last_drink == 5000)
assert(select(1, dispatch(0, 0)) == 'executed' and native_calls == old_calls)
assert(select(1, dispatch(-15, 0, 'invalid')) == 'rejected')
food = 0.5; last_drink = 5000; broken_setter = true
status, reason = dispatch(-15, 20)
assert(status == 'uncertain' and reason == 'hunger_change_failed' and last_drink == 5000)
broken_setter = false
assert(queued_delta == 0)
food = 0 / 0
assert(select(1, dispatch(-15, 0)) == 'rejected', 'Invalid real food fell back to the generic stub')
assert(generic_writes == 0, 'The unreliable generic setter was used')
print('needs wait smoke passed: HUD food=0 with generic stub=1, native next-frame change exactly once, positive/disabled modes and rollback')
