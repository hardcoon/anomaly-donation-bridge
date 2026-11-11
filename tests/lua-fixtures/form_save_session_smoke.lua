-- Process API failures/restarts are simulated; all serialization is the actual
-- common helper. Full native-object/form lifecycles run in the six form suites.
local core=assert(arg[1])
local clock,pid,env,unavailable,write_failed,level_name=1000,71,{},false,false,"l01_escape"
local kernel={
    GetCurrentProcessId=function() return pid end,
    GetEnvironmentVariableA=function(name,buffer)
        assert(name=="ADB_FORM_SAVE_OWNER_V1","foreign process setting accessed")
        local text=env[name];if not text then return 0 end
        if not buffer then return #text+1 end
        buffer.value=text;return #text
    end,
    SetEnvironmentVariableA=function(name,value)
        assert(name=="ADB_FORM_SAVE_OWNER_V1","foreign process setting changed")
        if write_failed then return 0 end
        env[name]=value;return 1
    end
}
local ffi={cdef=function() end,new=function() return {} end,string=function(value) return value.value end,
    load=function() if unavailable then error("missing process API") end;return kernel end}
package.loaded.ffi=ffi
time_global=function() return clock end
level={name=function() return level_name end}
local function common()
    local module=setmetatable({},{__index=_G})
    local f=assert(loadfile(core.."/anomaly_donation_bridge_common.script"));setfenv(f,module);f();return module
end
local a={health=0.77,id=function() return 0 end,alive=function() return true end,get_visual_name=function() return "actor" end}
local s={params={mutant_sections="dog_weak",duration_seconds="20"},section="dog_weak",
    remaining=20000,last=clock,actor_health=0.77,actor_visual="actor",form_health=1.5,form_max_health=2,
    yaw=0,pitch=0,camera_mode="center",cooldowns={melee=clock+1500},anomaly_type=4,
    body=setmetatable({},{__index=function() error("native object was serialized") end})}
local c=common()
local saved=assert(c.form_save_snapshot(s,"form.test",clock,false))
assert(saved.body==nil and saved.actor_id==nil and saved.cooldowns.melee==1500)
local restored=assert(c.form_load_snapshot(saved,"form.test",a))
assert(restored.remaining==20000 and restored.form_health==1.5 and restored.anomaly_type==4)
assert(restored.shield==0 and not restored.cloaked and #restored.zones==0 and #restored.servants==0)
clock=clock+1500
local paused=assert(c.form_save_snapshot(s,"form.test",clock,true))
assert(paused.remaining==20000,"paused UI consumed the form duration")
assert(c.form_save_snapshot(s,"form.test",clock,false).remaining==18500)
local fresh=common();assert(fresh.form_load_snapshot(saved,"form.test",a),"Lua VM generation lost the same process permission")
pid=72;assert(not common().form_load_snapshot(saved,"form.test",a),"inherited parent environment revived a form in a child process")
pid=71;env={};assert(not common().form_load_snapshot(saved,"form.test",a),"PID reuse after process death revived a saved form")
c=common();local current=assert(c.form_save_snapshot(s,"form.test",clock,false))
assert(current.owner~=saved.owner and not c.form_load_snapshot(saved,"form.test",a))
level_name="l02_garbage";assert(not c.form_load_snapshot(current,"form.test",a),"form crossed the level-transition boundary")
level_name="l01_escape";a.health=0.1;assert(not c.form_load_snapshot(current,"form.test",a),"form bypassed the loaded save's cleanup boundary")
a.health=0.77
assert(c.form_load_snapshot(current,"form.test",a))
c.form_save_session_end();assert(not c.form_load_snapshot(current,"form.test",a),"quit did not revoke saved forms")
env={};write_failed=true;assert(common().form_save_snapshot(s,"form.test",clock,false)==nil,"failed RAM permission store allowed a persistent form")
write_failed=false;unavailable=true;assert(common().form_save_snapshot(s,"form.test",clock,false)==nil,"missing native process APIs did not fail closed")
unavailable=false
print("Form save session smoke passed: process/VM generations, PID reuse, quit/crash invalidation, unavailable APIs, relative timers, HP and no native object serialization.")
