local core,optional=assert(arg[1]),assert(arg[3])
local function load_module(root,name)
    local env=setmetatable({},{__index=_G});local chunk=assert(loadfile(root.."/"..name..".script"))
    setfenv(chunk,env);chunk();return env
end
local function copy(t) if type(t)~="table" then return t end;local r={};for k,v in pairs(t) do r[k]=copy(v) end;return r end
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_slow_time"
local KEY="adb_slow_time_v1";local LEASE="ADB_SLOW_TIME_PROCESS_LEASE_V1"
local memory,callbacks,process_env,descriptor={},{},{},nil
local now,scale,paused,living,command_available,write_works,env_works,writes=0,1,false,true,true,true,true,0
local function near(a,b) return math.abs(a-b)<0.001 end
USE_MARSHAL=true;DEV_DEBUG=false
db={actor={alive=function() return living end}}
function device() return {time_global=function() return now end,is_paused=function() return paused end} end
function get_console() return {
    get_string=function(_,name) assert(name=="time_factor");return command_available and tostring(scale) or nil end,
    get_float=function() error("CCC_TimeFactor is not CCC_Float") end,
    execute=function(_,command)
        local number=assert(command:match("^time_factor (%d+%.%d+)$"),"Unsafe or unrelated console command")
        writes=writes+1;if write_works then scale=tonumber(number) end
    end
} end
alife_storage_manager={get_state=function() return memory end}
function RegisterScriptCallback(name,f) assert(not callbacks[name],"Duplicate callback");callbacks[name]=f end
local kernel={
    GetEnvironmentVariableA=function(name,buffer,size)
        assert(name==LEASE and size==128);local value=process_env[name]
        if not value then return 0 end;buffer.text=value;return #value
    end,
    SetEnvironmentVariableA=function(name,value)
        assert(name==LEASE);if not env_works then return 0 end;process_env[name]=value;return 1
    end
}
-- Never touch the test runner's actual OS environment. Reinitializing Lua
-- preserves only this mock game-process marker and native time scale.
local real_require=require
function require(name)
    if name=="ffi" then return {cdef=function() end,load=function(name) assert(name=="kernel32");return kernel end,
        new=function() return {} end,string=function(buffer,count) return buffer.text:sub(1,count) end} end
    return real_require(name)
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.slow_time");descriptor=d end}
local module
local function reload()
    callbacks={};module=load_module(optional,MODULE);module.on_game_start();assert(descriptor.is_supported())
end
local function fresh()
    memory={};process_env={};now=0;scale=1;paused=false;living=true;command_available=true;write_works=true;env_works=true
    reload()
end
local function start(seconds,percent)
    local context={};local ok,status,why=descriptor.can_execute(context,{duration_seconds=tostring(seconds or 10),slowdown_percent=tostring(percent or 75)})
    assert(ok,tostring(status)..":"..tostring(why));assert(descriptor.execute(context)=="executed")
    assert(memory[KEY] and process_env[LEASE]);return memory[KEY]
end
local function advance(real_ms)
    if not paused then now=now+real_ms*scale end
    callbacks.actor_on_update()
end
-- New defaults apply only to missing parameters. Existing explicit 10/75
-- settings and saved in-flight effects retain their own values.
fresh();local defaults={};assert(descriptor.can_execute(defaults,{}))
assert(descriptor.execute(defaults)=="executed")
assert(near(scale,0.35) and memory[KEY].remaining_ms==30000 and memory[KEY].slowdown_percent==65)
advance(30000);assert(near(scale,1) and not memory[KEY])
fresh();start();assert(near(scale,0.25) and DEV_DEBUG==false)
advance(3000);assert(near(memory[KEY].remaining_ms,7000))
-- Saving does NOT cancel, restart or consume the rest; console/autosave uses
-- the same save_state path and its own immutable remaining-time snapshot.
callbacks.on_before_save_input();callbacks.save_state(memory)
local during=copy(memory);assert(near(scale,0.25) and near(during[KEY].remaining_ms,7000))
advance(2000);assert(near(memory[KEY].remaining_ms,5000))
callbacks.on_before_load_input();assert(near(scale,1) and near(memory[KEY].remaining_ms,5000))
memory=copy(during);descriptor.on_game_load();callbacks.actor_on_update()
assert(near(scale,0.25) and near(memory[KEY].remaining_ms,7000),"F9 did not load the saved remaining time")
advance(6999);assert(near(scale,0.25));advance(1)
assert(near(scale,1) and not memory[KEY] and not process_env[LEASE],"Expiry left slow motion behind")

-- A complete Lua reset/console load: recover the previous live process lease
-- before reading THIS save, not the state from a future scene.
fresh();scale=0.8;start(20,50);advance(4000);callbacks.save_state(memory);during=copy(memory)
assert(near(scale,0.4));reload();assert(near(scale,0.8) and not process_env[LEASE])
memory=copy(during);descriptor.on_game_load();callbacks.actor_on_update()
assert(near(scale,0.4) and near(memory[KEY].remaining_ms,16000))
memory={};reload();descriptor.on_game_load();callbacks.actor_on_update()
assert(near(scale,0.8) and not memory[KEY] and not process_env[LEASE],"Loading before save leaked future slow motion")

-- New game process / exit: only game-save data resumes. Never inherit an OS
-- session lease, hard-code 1 on exit, or count hours spent out of the game.
fresh();scale=0.8;start(10,75);advance(2000);callbacks.save_state(memory);during=copy(memory)
callbacks.on_console_execute("quit");assert(near(scale,0.8) and not process_env[LEASE])
scale=1;process_env={};now=0;memory=copy(during);reload();descriptor.on_game_load();callbacks.actor_on_update()
assert(near(scale,0.25) and near(memory[KEY].remaining_ms,8000));advance(8000);assert(near(scale,1))

fresh();start(30,50);advance(5000);local remaining=memory[KEY].remaining_ms
paused=true;callbacks.main_menu_on_init();assert(near(scale,1))
for _=1,5 do advance(60000) end
assert(near(memory[KEY].remaining_ms,remaining))
paused=false;callbacks.main_menu_on_quit();advance(0)
assert(near(scale,0.5) and near(memory[KEY].remaining_ms,remaining),"Return from menu incorrectly cancelled the effect")
advance(1000);assert(near(memory[KEY].remaining_ms,remaining-1000))
-- A native pause with no Lua updates at all does not introduce a wall-clock gap.
paused=true;advance(600000);paused=false;advance(0)
assert(near(memory[KEY].remaining_ms,remaining-1000))

fresh();start();advance(1000);callbacks.actor_on_before_death();living=false
assert(near(scale,1));advance(60000);assert(near(memory[KEY].remaining_ms,9000))
fresh();start();advance(2000);callbacks.save_state(memory);during=copy(memory)
descriptor.on_level_changing();assert(near(scale,1));memory=during;descriptor.on_game_load();advance(0)
assert(near(scale,0.25) and near(memory[KEY].remaining_ms,8000))

-- Do not stack/fight another time-scale writer or touch weather/calendar/HP,
-- stamina/UI/settings. A changed scale relinquishes this effect.
fresh();start();local context={};local ok,status,why=descriptor.can_execute(context,{})
assert(not ok and status=="deferred" and why=="slow_time_already_active")
local before_writes=writes;scale=0.6;advance(1000)
assert(near(scale,0.6) and writes==before_writes and not memory[KEY] and not process_env[LEASE])
fresh();for _,p in ipairs({{duration_seconds="0"},{duration_seconds="3601"},{slowdown_percent="0"},
    {slowdown_percent="100"},{slowdown_percent="1.5"},{duration_seconds="1;quit"},{unrelated="x"}}) do
    ok,status=descriptor.can_execute({},p);assert(not ok and status=="rejected" and near(scale,1) and not memory[KEY])
end
start(1,1);advance(1000);assert(near(scale,1))
fresh();start(1,90);advance(1000);assert(near(scale,1))
fresh();command_available=false;ok,status=descriptor.can_execute({},{});assert(not ok and status=="deferred")
fresh();write_works=false;context={};assert(descriptor.can_execute(context,{}));assert(descriptor.execute(context)=="rejected")
assert(near(scale,1) and not memory[KEY] and not process_env[LEASE])
fresh();env_works=false;context={};assert(descriptor.can_execute(context,{}));assert(descriptor.execute(context)=="rejected")
assert(near(scale,1) and not memory[KEY])
fresh();memory[KEY]={version=1,remaining_ms=5000,previous=1,applied=0,slowdown_percent=100}
descriptor.on_game_load();advance(0);assert(near(scale,1) and not process_env[LEASE])
print("Slow time smoke passed (mock API): configurable percent/duration, scaled-clock real seconds, pause, F5/F9 snapshots, fresh process/console load, previous speed on exit, transitions, death, conflicts and failures.")
