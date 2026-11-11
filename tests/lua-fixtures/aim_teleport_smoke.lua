local core,optional=assert(arg[1]),assert(arg[3])
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_aim_teleport"
local ID="hardcoon.adb.aim_teleport"
local MEMORY="ADB_AIM_TELEPORT_PROCESS_V1"
local checks=0
local function check(value,why) checks=checks+1;assert(value,why) end
local function near(a,b) return math.abs(a-b)<.001 end
local function p(x,y,z) return {x=x,y=y,z=z} end
local function load_module(root,name)
    local env=setmetatable({},{__index=_G});local chunk=assert(loadfile(root.."/"..name..".script"))
    setfenv(chunk,env);chunk();return env
end
local now,pid,frame,paused,living,talking,holder,present,app,ui,busy,delta,precache
local process_env,callbacks,descriptor,module,position,camera,look,boxes,moves,logs
local writable,readable,ffi_available,setter_failure,queries,write_count,ray_hook,ground
local function box(min,max,flags,object)
    boxes[#boxes+1]={min=min,max=max,flags=flags or 0,object=object}
end
local kernel={
    GetCurrentProcessId=function() return pid end,
    GetEnvironmentVariableA=function(name,buffer,size)
        assert(name==MEMORY and size==128);if not readable then error("read unavailable") end
        local value=process_env[name];if not value then return 0 end
        buffer.text=value;return #value
    end,
    SetEnvironmentVariableA=function(name,value)
        assert(name==MEMORY);write_count=write_count+1;if not writable then return 0 end
        process_env[name]=value;return 1
    end
}
local real_require=require
function require(name)
    if name=="ffi" then
        if not ffi_available then error("ffi unavailable") end
        return {cdef=function() end,load=function(name) assert(name=="kernel32");return kernel end,
            new=function() return {} end,string=function(buffer,count) return buffer.text:sub(1,count) end}
    end
    return real_require(name)
end
function vector() return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end} end
-- Unchanged garbage on a miss models old native rq_result semantics.
function rq_result() return {range=77,object="stale",material_flags=128} end
rq_target={rqtBoth=3}
function time_global() return now end
function device() return {frame=frame,is_paused=function() return paused end,precache_frame=precache,
    f_time_delta=delta,cam_pos=camera,cam_dir=look} end
function app_ready() return app end
function Check_UI() return ui end
actor_menu={is_hud_free=function() return not ui end}
function RegisterScriptCallback(name,f) assert(not callbacks[name],"Duplicate callback: "..name);callbacks[name]=f end
function printf(format,...) logs[#logs+1]=string.format(format,...) end
alife_storage_manager={get_state=function() error("Series must not read/write a saved-game snapshot") end}
USE_MARSHAL=true
DEV_DEBUG=false
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_registry={register=function(id,d) assert(id==ID);descriptor=d end,
    busy_reason=function(id) assert(id==ID);return busy and "other_effect" or nil end}
local function forbidden() error("Aim teleport must not use NPC AI navigation") end
level={present=function() return present end,vertex_id=forbidden,vertex_position=forbidden,
    get_target_pos=forbidden}
local function box_hit(from,dir,length,b)
    local enter,leave=-math.huge,math.huge
    for _,axis in ipairs({"x","y","z"}) do
        if math.abs(dir[axis])<1e-9 then
            if from[axis]<b.min[axis] or from[axis]>b.max[axis] then return end
        else
            local a,c=(b.min[axis]-from[axis])/dir[axis],(b.max[axis]-from[axis])/dir[axis]
            if a>c then a,c=c,a end
            enter=math.max(enter,a);leave=math.min(leave,c)
        end
    end
    if enter>leave then return end
    local hit=enter>=0 and enter or leave
    if hit>=0 and hit<=length then return hit end
end
function level.ray_pick(from,dir,length,target,result,ignore)
    queries=queries+1
    assert(present and app and not paused and living and not ui and not holder and precache==0 and delta>0,
        "Native geometry queried outside playable scene")
    assert(ignore==db.actor and target==rq_target.rqtBoth)
    local distance,flags,object
    if ground and dir.y<0 then
        local t=-from.y/dir.y
        if t>=0 and t<=length then distance,flags=t,0 end
    end
    for _,b in ipairs(boxes) do
        local hit=box_hit(from,dir,length,b)
        if hit and (not distance or hit<distance) then distance,flags,object=hit,b.flags,b.object end
    end
    if ray_hook then local hook=ray_hook;ray_hook=nil;hook() end
    if not distance then return false end
    result.range,result.material_flags,result.object=distance,flags,object
    return true
end
local function aim(x,y,z,origin)
    camera=origin or p(position.x,position.y+1.7,position.z)
    local dx,dy,dz=x-camera.x,y-camera.y,z-camera.z
    local n=math.sqrt(dx*dx+dy*dy+dz*dz);look=p(dx/n,dy/n,dz/n)
end
local function reload()
    callbacks={};module=load_module(optional,MODULE);module.on_game_start()
end
local function step(ms,frames)
    now=now+ms;frame=frame+(frames or math.max(1,math.ceil(ms/16)))
    descriptor.update()
end
local function warm()
    step(0,1);step(0,15)
end
local function fresh()
    now,pid,frame,paused,living,talking,holder,present,app,ui,busy,delta,precache=0,73,0,false,true,false,nil,true,true,false,false,1/60,0
    process_env,boxes,moves,logs={}, {}, {}, {}
    writable,readable,ffi_available,setter_failure,queries,write_count,ray_hook,ground=true,true,true,nil,0,0,nil,true
    position=p(0,0,0);_GUIs={};actor_effects={item_not_in_use=true}
    db={storage={},actor={id=function() return 7 end,alive=function() return living end,
        is_talking=function() return talking end,get_current_holder=function() return holder end,
        accessible=forbidden,level_vertex_id=forbidden,position=function() return position end,
        set_actor_position=function(_,target)
            assert(not ui and not holder and living and present and not paused)
            moves[#moves+1]=p(target.x,target.y,target.z)
            if setter_failure=="throw" then error("native setter rejected position") end
            if setter_failure~="no_move" then position=p(target.x,target.y,target.z) end
        end}}
    aim(20,0,0);reload();check(descriptor.is_supported(),"Native API not detected");warm()
end
local function state()
    local version,process,count,interval,remaining=(process_env[MEMORY] or ""):match("^(%d+)|(%d+)|(%d+)|(%d+)|(%d+)$")
    return tonumber(count),tonumber(remaining),tonumber(interval),tonumber(process),tonumber(version)
end
local function start(count,interval)
    local params={};if count then params.repeat_count=tostring(count) end
    if interval then params.interval_seconds=tostring(interval) end
    local context={};local ok,status,why=descriptor.can_execute(context,params)
    check(ok,tostring(status)..":"..tostring(why));check(descriptor.execute(context)=="executed","Series did not start")
end

-- Defaults, delayed first attempt, fresh aim at every repetition, exactly N.
fresh();start();check(#moves==0 and state()==3,"Activation moved actor immediately")
step(2999);check(#moves==0,"First interval was shortened");step(1)
check(#moves==1 and near(position.x,20) and near(position.y,.05),"First aim point was not reached")
aim(40,0,4);step(3000);check(#moves==2 and near(position.x,40) and near(position.z,4),"Stale aim was reused")
aim(5,0,-7);step(3000);check(#moves==3 and near(position.x,5) and state()==0,"Series did not finish")
step(90000);descriptor.on_game_load();warm();step(90000)
reload();warm();step(90000);check(#moves==3,"Completed series replayed after load/Lua reset")

-- F5/F9 retain LIVE process progress, not the older saved snapshot. No actor
-- pointer, old coordinates or wall-clock loading time can leak into a move.
fresh();start(3,3);step(1000);local saved_memory=process_env[MEMORY]
check(not callbacks.save_state,"Aim series unexpectedly writes game saves")
step(2000);step(500);local left,remaining=state();check(left==2 and remaining==2500,"Incorrect live checkpoint")
callbacks.on_before_load_input();callbacks.actor_on_net_destroy();now=now+120000;frame=0
reload();descriptor.on_game_load();warm();step(2499)
check(#moves==1 and state()==2 and process_env[MEMORY]~=saved_memory,"F9 restored spent repeats or counted loading")
aim(30,0,10);step(1);check(#moves==2 and near(position.z,10),"Series failed to resume after Lua reset")
descriptor.on_game_load();warm();step(3000);check(#moves==3 and state()==0,"Duplicate load notification restarted series")
-- A new process ignores inherited process memory; actual close/relaunch with
-- no inherited record also starts idle. Saves never arm a new session.
fresh();start(4,2);pid=74;reload();warm();step(100000)
check(#moves==0,"A different game process resumed a stale series")
process_env={};reload();warm();step(100000);check(#moves==0,"Fresh process unexpectedly resumed")

-- Menu, native pause, load, transport, dialogue and other actor effects must
-- suspend both movement and the countdown, including long gaps in updates.
for _,mode in ipairs({"pause","menu","holder","talk","precache","loading","no_frame","dead","no_actor","level",
    "input_lock","registered_gui","animation","blocking_effect"}) do
    fresh();start(2,2);step(500)
    if mode=="pause" then paused=true
    elseif mode=="menu" then ui=true;callbacks.GUI_on_show()
    elseif mode=="holder" then holder={}
    elseif mode=="talk" then talking=true
    elseif mode=="precache" then precache=20
    elseif mode=="loading" then app=false;callbacks.on_before_load_input()
    elseif mode=="no_frame" then delta=0
    elseif mode=="dead" then living=false;callbacks.actor_on_before_death()
    elseif mode=="no_actor" then db.saved_actor=db.actor;db.actor=nil
    elseif mode=="level" then present=false;descriptor.on_level_changing()
    elseif mode=="input_lock" then db.storage[7]={disable_input_idle=1}
    elseif mode=="registered_gui" then _GUIs.blocker=true
    elseif mode=="animation" then actor_effects.item_not_in_use=false
    else busy=true end
    local before_queries=queries;step(60000);local count,ms=state()
    check(#moves==0 and count==2 and ms==1500 and queries==before_queries,"Unsafe update during "..mode)
    paused,ui,holder,talking,precache,app,delta,living,present,busy=false,false,nil,false,0,true,1/60,true,true,false
    if db.saved_actor then db.actor=db.saved_actor end
    db.storage={};_GUIs={};actor_effects.item_not_in_use=true
    warm();step(1499);check(#moves==0,"Resume skipped interval: "..mode)
    step(1);check(#moves==1,"Resume stalled: "..mode)
end
fresh();start(2,2);step(500);callbacks.main_menu_on_init();now=now+999999
callbacks.main_menu_on_quit();warm();step(1500);check(#moves==1,"Menu with no actor updates counted real time")
fresh();start(2,2);step(500);callbacks.on_console_execute("load");now=0;frame=0
descriptor.on_game_load();warm();step(1500);check(#moves==1,"Console load/clock reset broke timer")

-- Large update gaps cause a single attempt, not N stale-camera teleports.
fresh();start(3,1);step(600000);check(#moves==1 and state()==2,"Backlog replayed several moves at once")
descriptor.update();check(#moves==1,"Same rendered frame moved actor twice")
step(999);check(#moves==1,"Next attempt lacked full delay");step(1);check(#moves==2)

-- Physical destination checks: no NPC navigation calls. Valid flat ground,
-- an unmeshed roof, a near-side wall landing, and passable foliage.
fresh();box(p(8,3,-3),p(15,3.2,3));aim(10,3.2,0,p(0,8,0));start(1,1);step(1000)
check(#moves==1 and near(position.y,3.25),"Roof was snapped down to ground/AI mesh")
fresh();box(p(8,0,-5),p(8.2,4,5));aim(20,1.7,0);start(1,1);step(1000)
check(#moves==1 and position.x<7.7,"Wall target landed inside/behind wall")
fresh();box(p(5,0,-5),p(5.1,5,5),128);start(1,1);step(1000)
check(#moves==1 and near(position.x,20),"Passable foliage displaced destination")
-- Invalid/unsafe targets consume their scheduled slot and leave the player
-- where they are. The next slot retries the NEW aim, not that unsafe point.
for _,mode in ipairs({"sky","no_ground","ledge","low_ceiling","layer_limit","nan_camera","zero_direction"}) do
    fresh()
    if mode=="sky" then look=p(0,1,0)
    elseif mode=="no_ground" then ground=false
    elseif mode=="ledge" then box(p(19.9,0,-.1),p(20.1,3,.1));aim(20,3,0,p(0,8,0))
    elseif mode=="low_ceiling" then box(p(17,1.2,-5),p(25,1.4,5))
    elseif mode=="layer_limit" then for i=1,7 do box(p(i,0,-5),p(i+.05,5,5),128) end
    elseif mode=="nan_camera" then camera.x=0/0
    else look=p(0,0,0) end
    start(2,1);step(1000);check(#moves==0 and state()==1,"Unsafe aim accepted: "..mode)
    boxes={};ground=true;aim(30,0,0);step(1000)
    check(#moves==1 and near(position.x,30) and state()==0,"Next aim did not recover: "..mode)
end

-- Maxima/minima, rejection instead of clamping, and isolated overlapping
-- commands. No successful native mutation without a committed checkpoint.
fresh();for _,params in ipairs({{repeat_count="0"},{repeat_count="100"},{repeat_count="2.5"},
    {interval_seconds="0"},{interval_seconds="601"},{interval_seconds="nan"},{interval_seconds="1;quit"},{bad="1"}}) do
    local ok,status=descriptor.can_execute({},params);check(not ok and status=="rejected" and #moves==0,"Invalid bounds accepted")
end
start(99,600);step(599999);check(#moves==0);step(1);check(#moves==1 and state()==98)
local ok,status,reason=descriptor.can_execute({},{});check(not ok and status=="deferred" and reason=="aim_teleport_series_active")
check(descriptor.is_blocking==nil,"Idle waiting series blocked unrelated effects")
fresh();start(1,1);writable=false;step(1000);check(#moves==0 and state()==1,"Moved without durable process checkpoint")
writable=true;step(1000);check(#moves==1 and state()==0,"Memory recovery did not resume")
fresh();local context={};check(descriptor.can_execute(context,{}));writable=false
check(descriptor.execute(context)=="rejected" and not process_env[MEMORY],"Activation ignored memory error")
fresh();context={};check(descriptor.can_execute(context,{}));ui=true
check(descriptor.execute(context)=="deferred" and not process_env[MEMORY],"Activation ignored UI opened after preflight")
for _,failure in ipairs({"throw","no_move"}) do
    fresh();start(2,1);setter_failure=failure;step(1000);check(state()==1 and #moves==1)
    descriptor.update();check(#moves==1,"Setter failure retried in same frame")
    setter_failure=nil;step(1000);check(state()==0 and #moves==2,"Setter failure broke remaining series")
end
-- Readiness reset occurring inside a scene query must not consume an attempt.
fresh();start(1,1);ray_hook=function() callbacks.GUI_on_show() end;step(1000)
check(#moves==0 and state()==1,"Suspended geometry query still teleported")
warm();step(1);check(#moves==1,"Suspended destination did not retry")
for _,record in ipairs({"broken","1|73|100|1000|1000","1|73|1|999|999","1|73|1|1000|1001","1|73|1|1001|1000"}) do
    fresh();process_env[MEMORY]=record;reload();warm();step(600000)
    check(#moves==0,"Malformed process record resumed: "..record)
end
fresh();ffi_available=false;reload();check(not descriptor.is_supported(),"Missing session memory API was advertised")
ok,status=descriptor.can_execute({},{});check(not ok and status=="rejected")
print("Aim teleport smoke passed (mock native API): "..checks.." checks; bounds/defaults, fresh crosshair, physical landing, process memory, F5/F9/Lua reset, pauses, transitions and failures.")
