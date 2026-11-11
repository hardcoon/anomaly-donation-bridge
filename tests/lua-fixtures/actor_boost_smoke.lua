local root,optional=assert(arg[1]),assert(arg[3])
local checks=0
local function check(value,message) checks=checks+1;assert(value,message) end
local function copy(value)
    if type(value)~="table" then return value end
    local result={};for k,v in pairs(value) do result[k]=copy(v) end;return result
end
local MODULE="anomaly_donation_bridge_effect_actor_boost"
local STATE="__anomaly_donation_bridge_actor_boost_v1"
local ENV="ADB_ACTOR_BOOST_SESSION_V2"
local defaults={max_weight=50,max_walk_weight=60,jump_speed=5,sprint_koef=1.2,run_coef=1.1}
local values=copy(defaults)
local clock,pid,encoded=1000,123,nil
local fail_session,fail_field,silent_field,ffi_missing=false,nil,nil,false
local saved,save_writes={},0
local kernel={
    GetCurrentProcessId=function() return pid end,
    GetEnvironmentVariableA=function(name,buffer)
        check(name==ENV,"Only the private process environment variable may be read")
        if not encoded then return 0 end
        if not buffer then return #encoded+1 end
        buffer.value=encoded;return #encoded
    end,
    SetEnvironmentVariableA=function(name,value)
        check(name==ENV,"Only private process memory may be written")
        if fail_session then return 0 end
        encoded=value;return 1
    end
}
local fake_ffi={cdef=function() end,load=function() return kernel end,
    new=function() return {} end,string=function(buffer) return buffer.value end}
local actor={id=function() return 0 end,section=function() return "custom_actor" end,get_actor_walk_accel=function() return 11.5 end}
for key in pairs(defaults) do
    actor["get_actor_"..key]=function() return values[key] end
    actor["set_actor_"..key]=function(_,value)
        if silent_field==key then return end
        values[key]=value
        if fail_field==key then fail_field=nil;error("setter failed AFTER mutation") end
    end
end
db={actor=actor}
function time_global() return clock end
function se_load_var(id,name,key) check(id==0 and name==nil,"Actor journal only");return saved[key] end
function se_save_var(id,name,key,value)
    check(id==0 and name==nil and (key=="adb_actor_boost_v1" or key=="adb_actor_parameters_v1") and value==nil,
        "Only migration may REMOVE the old journal; new boosts must never be serialized")
    save_writes=save_writes+1;saved[key]=value
end
local handler
anomaly_donation_bridge_registry={register_builtin=function(id,descriptor)
    check(id=="actor_boost","Only this template may change");handler=descriptor;return true
end}
local function load_module(name,folder)
    local env=setmetatable({require=function(name)
        check(name=="ffi","No file/config persistence dependency")
        if ffi_missing then error("ffi unavailable") end
        return fake_ffi
    end},{__index=_G})
    local chunk=assert(loadfile(folder.."/"..name..".script"));setfenv(chunk,env);chunk();return env
end
anomaly_donation_bridge_common=load_module("anomaly_donation_bridge_common",root)
local function load(fresh)
    if fresh then rawset(_G,STATE,nil) end
    local module=load_module(MODULE,optional);module.on_game_start();return module
end
local function params(jump,speed,weight,duration)
    return {jump_percent=tostring(jump or 50),speed_percent=tostring(speed or 0),
        carry_weight_kg=tostring(weight or 25),duration_seconds=tostring(duration or 10)}
end
local function start(options)
    local context={actor=actor,now=clock}
    local ok,status,reason=handler.can_execute(context,options or params())
    check(ok,tostring(status)..":"..tostring(reason))
    return handler.execute(context)
end
local function assert_base()
    for key,value in pairs(defaults) do check(math.abs(values[key]-value)<0.0001,"Baseline changed: "..key) end
end
local function reset()
    clock,pid,encoded=1000,123,nil;fail_session,fail_field,silent_field,ffi_missing=false,nil,nil,false
    values=copy(defaults);saved={};save_writes=0;db.actor=actor;load(true)
end
reset()
check(start()=="executed","Boost starts")
check(encoded:match("^2|123|11000|"),"Deadline and original values exist only in this process")
check(saved.adb_actor_boost_v1==nil and save_writes==0,"No bonus/timer enters the save")
check(values.max_weight==75 and values.jump_speed==7.5,"Temporary runtime values apply")
clock=2500;handler.update(clock)
-- Rebuild the whole Lua VM on F9 / location change; native fields reset.
values=copy(defaults);load(true);handler.on_game_load()
check(values.max_weight==75 and values.jump_speed==7.5,"F9 reapplies process lease without a saved timer")
check(rawget(_G,STATE).expires_at==11000,"F9 must not extend the deadline")
clock=10999;handler.update(clock);check(values.max_weight==75,"Lease lasts until original expiry")
clock=11000;handler.update(clock);assert_base()
check(encoded==nil and not rawget(_G,STATE).active,"Expiry clears only process memory")
check(save_writes==0,"Expiry must not write a save")

reset();start();clock=2000;check(start(params(100,0,10))=="executed","Repeated boost replaces the previous one")
check(values.max_weight==60 and values.jump_speed==10,"Repeated boosts do not compound temporary baselines")
clock=12000;handler.update(clock);assert_base()

-- Unexpected process exit: no quit callback, no save hook or rollback runs.
reset();start();local game_save=copy(saved);encoded=nil;pid=456;values=copy(defaults)
load(true);saved=game_save;handler.on_game_load();handler.update(clock)
assert_base();check(not rawget(_G,STATE).active and next(saved)==nil,"Crash/new process cannot resurrect a boost")
-- Even a spawned child inheriting the environment must not inherit the lease.
reset();start();pid=789;values=copy(defaults);load(true);handler.on_game_load()
assert_base();check(encoded==nil and not rawget(_G,STATE).active,"Inherited foreign PID lease is discarded")

reset();start();clock=12000;load(true);handler.on_game_load();assert_base()
check(encoded==nil and not rawget(_G,STATE).active,"Expired lease after VM rebuild restores exact baseline")

reset();fail_session=true
check(start()=="rejected","Session-store failure is fail-closed")
assert_base();check(not rawget(_G,STATE).active and encoded==nil,"No untracked changes on persistence failure")
reset();fail_field="jump_speed"
check(start()=="uncertain","Partial native mutation is reported")
assert_base();check(encoded==nil and not rawget(_G,STATE).active,"Failed native apply rolls back all fields")
reset();silent_field="jump_speed"
check(start()=="uncertain","Silently ignored setter is detected")
assert_base();check(encoded==nil,"Silent setter failure does not retain a lease")

-- Legacy saved bonuses are migrated by cancelling, not by importing their timer.
reset();saved.adb_actor_boost_v1={version=1,active=true,remaining_ms=3500000,
    baseline=copy(defaults),target={max_weight=500,max_walk_weight=600,jump_speed=15,sprint_koef=2,run_coef=2}}
values=copy(saved.adb_actor_boost_v1.target);handler.on_game_load();assert_base()
check(saved.adb_actor_boost_v1==nil and save_writes==1 and encoded==nil,"Old bonus is removed once, never resumed")
handler.update(clock);check(save_writes==1,"Migration must not repeat")
reset();start();local current=copy(values)
saved.adb_actor_boost_v1={baseline={max_weight=1,max_walk_weight=1,jump_speed=1,sprint_koef=1,run_coef=1}}
handler.on_game_load();check(values.max_weight==current.max_weight,"Loaded legacy journal cannot replace live session lease")
check(saved.adb_actor_boost_v1==nil,"Stale legacy record is removed without affecting current effect")

reset();encoded="2|123|999999999999999999999|bad|bad";handler.on_game_load();assert_base()
check(encoded==nil,"Malformed process state is discarded without touching actor stats")
reset();ffi_missing=true;load(true)
local ok,status,reason=handler.can_execute({actor=actor},params())
check(not ok and status=="rejected" and reason=="actor_boost_session_unavailable","No FFI means no save-based fallback")
assert_base()
reset();start();db.actor=nil;handler.on_game_load();check(rawget(_G,STATE).load_pending,"Actor-less load defers safely")
db.actor=actor;values=copy(defaults);handler.update(clock)
check(values.max_weight==75 and not rawget(_G,STATE).load_pending,"Deferred actor rebuild retains process lease")
clock=11000;handler.update(clock);assert_base()

-- Explicit manual reset uses the installed game's merged INI defaults,
-- including actor-section and condition-section overrides, not fixed numbers.
local ini_values
local unsafe_reads=0
function system_ini()
    return {
        line_exist=function(_,s,k) return ini_values[s]~=nil and ini_values[s][k]~=nil end,
        r_float=function(_,s,k)
            if not ini_values[s] or ini_values[s][k]==nil then unsafe_reads=unsafe_reads+1;error("missing native INI key") end
            return ini_values[s][k]
        end,
        r_string=function(_,s,k)
            if not ini_values[s] or ini_values[s][k]==nil then unsafe_reads=unsafe_reads+1;error("missing native INI string") end
            return ini_values[s][k]
        end
    }
end
local function reset_ini(override)
    ini_values={inventory={max_weight=37},custom_actor={condition_sect="custom_condition",jump_speed=6.8,sprint_koef=2.1,run_coef=2.4},custom_condition={max_walk_weight=97}}
    if override then ini_values.custom_actor.inv_max_weight=43 end
    unsafe_reads=0
end
local function force_reset()
    local ctx={actor=actor,now=clock}
    local ready,status,why=handler.can_execute(ctx,{reset_to_defaults="true"})
    if not ready then return status,why end
    return handler.execute(ctx)
end
local function assert_config(override)
    check(values.max_weight==(override and 43 or 37) and values.max_walk_weight==97 and values.jump_speed==6.8 and values.sprint_koef==2.1 and values.run_coef==2.4,
        "Forced reset did not restore all five merged configuration values")
end
reset();reset_ini(false);start()
saved.adb_actor_boost_v1={version=1,active=true,baseline=copy(defaults),target=copy(values)}
saved.adb_actor_parameters_v1={version=2,target=copy(values)}
saved.unrelated_quest={keep=true}
rawset(_G,"__anomaly_donation_bridge_actor_parameters_v1",{target=copy(values),load_pending=true})
local status,reason=force_reset()
check(status=="executed" and reason=="actor_boost_reset_defaults","Forced reset failed")
assert_config(false)
check(encoded==nil and not rawget(_G,STATE).active and not rawget(_G,STATE).load_pending,"Reset left an active process lease")
check(saved.adb_actor_boost_v1==nil and saved.adb_actor_parameters_v1==nil and saved.unrelated_quest.keep,
    "Reset must clear only the two ADB parameter journals")
check(rawget(_G,"__anomaly_donation_bridge_actor_parameters_v1").target==nil and
    not rawget(_G,"__anomaly_donation_bridge_actor_parameters_v1").load_pending,"Permanent ADB cache would re-apply on load")
clock=50000;handler.update(clock);assert_config(false)
load(true);handler.on_game_load();handler.update(clock);assert_config(false)
check(encoded==nil and saved.adb_actor_parameters_v1==nil,"F9 resurrected reset parameters")

-- No original lease/journal is required: old saves with orphaned values work.
reset();reset_ini(true)
values={max_weight=700,max_walk_weight=900,jump_speed=.1,sprint_koef=.1,run_coef=.1}
check(force_reset()=="executed","Orphaned old-save values could not be reset")
assert_config(true)
check(unsafe_reads==0,"Reset entered a native INI read without checking the key")

reset();reset_ini(false);ini_values.custom_actor.jump_speed=nil
local before=copy(values)
status,reason=force_reset()
check(status=="rejected" and reason=="actor_boost_reset_defaults_unavailable" and unsafe_reads==0,
    "Missing configuration defaults must reject without unsafe native reads")
for k,v in pairs(before)do check(values[k]==v,"Missing INI key partially reset the actor")end
reset();reset_ini(false);start();fail_session=true
status,reason=force_reset()
check(status=="rejected" and reason=="actor_boost_reset_session_clear_failed" and rawget(_G,STATE).active,
    "An uncleared process lease must not report reset success")
reset();reset_ini(false);start();fail_field="jump_speed"
status,reason=force_reset()
check(status=="uncertain" and reason=="actor_boost_reset_apply_failed" and not rawget(_G,STATE).active and encoded==nil,
    "Partial setter failure must be visible and must not keep the temporary timer running")
check(force_reset()=="executed","Manual retry after a setter failure could not recover")
assert_config(false)

reset();reset_ini(false)
values.run_coef=0;values.sprint_koef=0;values.jump_speed=0
check(force_reset()=="executed","An immobile actor with zero/invalid old values cannot be reset")
assert_config(false)

-- Exercise the actual permanent-parameters module, not just its cached table:
-- old saved targets must not return after its real F9/load callback.
reset();reset_ini(false)
local permanent_handler
local old_register=anomaly_donation_bridge_registry.register_builtin
anomaly_donation_bridge_registry.register_builtin=function(id,descriptor)
    if id=="actor_parameters" then permanent_handler=descriptor;return true end
    return old_register(id,descriptor)
end
rawset(_G,"__anomaly_donation_bridge_actor_parameters_v1",nil)
local permanent_module=load_module("anomaly_donation_bridge_effect_actor_parameters",optional)
permanent_module.on_game_start()
saved.adb_actor_parameters_v1={version=2,target={max_weight=700,max_walk_weight=900,jump_speed=20,sprint_koef=4,run_coef=4}}
values=copy(saved.adb_actor_parameters_v1.target)
permanent_handler.on_game_load()
check(force_reset()=="executed","Reset failed with a real permanent-parameters module pending load")
permanent_handler.on_game_load();permanent_handler.update(clock)
assert_config(false)
check(saved.adb_actor_parameters_v1==nil,"Permanent-parameters load resurrected its erased old target")
anomaly_donation_bridge_registry.register_builtin=old_register

print("Actor boost session smoke passed: "..checks.." checks (mock native/process APIs; no game session).")
