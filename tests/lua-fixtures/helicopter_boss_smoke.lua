local core,optional=assert(arg[1]),assert(arg[3])
local function load_module(root,name)
    local env=setmetatable({},{__index=_G})
    local chunk=assert(loadfile(root.."/"..name..".script"));setfenv(chunk,env);chunk();return env
end
local function copy(t)
    if type(t)~="table" then return t end
    local v={};for k,x in pairs(t) do v[k]=copy(x) end;return v
end
vector=setmetatable({},{__call=function() return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end} end})
local function vec(x,y,z) return vector():set(x,y,z) end
local now,is_paused,level_name,blocked,spawn_fail,task_fail=1000,false,"mod_surface_custom",false,false,false
local packet_fail,model_missing,motion_missing,save_during_spawn,sound_missing=false,false,false,false,false
local saved,servers,clients={}, {},{}
local serial,creates,task_calls,logs=0,0,0,0
local announcements={}
function time_global() return now end
function device() return {is_paused=function() return is_paused end} end
local a={living=true,paid=0,tasks={}}
function a:alive() return self.living end
function a:id() return 0 end
function a:position() return self.current_position or vec(0,0,0) end
function a:level_vertex_id() return 11 end
function a:game_vertex_id() return 22 end
function a:get_task(id,only_active) assert(only_active==true);return self.tasks[id] end
function a:give_money(value) self.paid=self.paid+value end
db={actor=a,storage={},heli={}}
news_manager={send_tip=function(target,message,_,_,duration)
    assert(target==a and duration==8000)
    assert(not message:find("\n",1,true),"PDA expects literal \\n tokens, not control LF characters")
    announcements[#announcements+1]=message;return true
end}
function db.add_obj(o) assert(db.storage[o:id()]);db.storage[o:id()].object=o end
function db.add_heli(o) db.heli[o:id()]=o end
function db.del_obj(o) db.storage[o:id()]=nil end
function db.del_heli(o) db.heli[o:id()]=nil end
alife_storage_manager={get_state=function() return saved end}
level_weathers={bLevelUnderground=false}
rq_target={rqtStatic=1};rq_result=setmetatable({},{__call=function() return {} end})
level={name=function() return level_name end,object_by_id=function(id) return clients[id] end,
    get_bounding_volume=function() return {min=vec(-2000,-50,-2000),max=vec(2000,15,2000)} end,
    ray_pick=function(_,_,_,kind) assert(kind==rq_target.rqtStatic);return blocked end}
local SECTION="adb_helicopter_boss"
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_helicopter_boss"
local sections={helicopter={class="C_HLCP_S",visual="mod/heli_base.ogf"},
    mod_heli_a={class="C_HLCP_S",visual="mod/heli_a.ogf"},
    mod_heli_b={class="C_HLCP_S",visual="mod/heli_b.ogf",startup_animation="rotor_loop"},
    [SECTION]={class="C_HLCP_S",adb_heli_profile="1",script_binding=MODULE..".bind"}}
game={motion_exists=function(visual,animation)
    assert(visual:find("mod/heli_",1,true))
    assert(animation=="idle" or animation=="rotor_loop")
    return not motion_missing
end}
function getFS() return {exist=function(_,alias,file)
    if alias=="$game_sounds$" then assert(file:match("%.ogg$"));return not sound_missing end
    assert(alias=="$game_meshes$" and file:match("%.ogf$"));return not model_missing
end} end
function system_ini() return {
    section_exist=function(_,s) return sections[s]~=nil end,
    line_exist=function(_,s,k) return sections[s] and sections[s][k]~=nil end,
    r_string=function(_,s,k) return sections[s][k] end} end
local life={}
function life:object(id) return servers[id] end
function life:set_switch_online(id,v)
    assert(servers[id])
    if v then assert(servers[id].packet_data.startup_animation~="$editor" and servers[id].packet_data.engine_sound~="",
        "Unprepared server must NEVER be permitted to become online") end
    servers[id].online=v
end
function life:set_switch_offline(id,v) assert(servers[id]);servers[id].offline=v end
function life:release(s) assert(s.section==SECTION);servers[s.id]=nil;clients[s.id]=nil end
function alife() return life end
function alife_create(section,p,lv,gv)
    assert(section==SECTION and lv==11 and gv==22 and p.y==55)
    creates=creates+1
    if spawn_fail then error("simulated native spawn failure") end
    serial=serial+1
    local s={id=serial,section=section,position=p,object_name=SECTION..serial,
        packet_data={visual_name="mod/heli_base.ogf",startup_animation="$editor",engine_sound="",motion_name="",skeleton_name=""}}
    function s:section_name() return self.section end
    function s:name() return self.object_name end
    servers[s.id]=s;return s
end
utils_stpk={get_heli_data=function(s) return copy(s.packet_data) end,
    set_heli_data=function(data,s)
        if packet_fail then error("simulated packet write failure") end
        assert(s.online==false,"Server must be blocked from going online while editing")
        s.packet_data=copy(data)
    end}
anomaly_donation_bridge_catalog={capable=function() return true end,
    validate_sections=function(params,key,kind)
        assert(key=="helicopter_sections" and kind=="helicopter")
        if params.catalog_revision~="heli-revision" then return nil,"catalog_revision_mismatch" end
        local entries={}
        for name in tostring(params[key] or ""):gmatch("[^,]+") do
            if not sections[name] or name==SECTION then return nil,"catalog_selection_stale_or_quarantined" end
            entries[#entries+1]={section_id=name}
        end
        if #entries==0 then return nil,"selection_empty" end
        return entries
    end,is_available=function(section,kind) return kind=="helicopter" and sections[section]~=nil end}
task_functor={};task_status_functor={};xr_effects={}
local manager={task_info={}}
function manager:give_task(id,giver)
    task_calls=task_calls+1;assert(giver==a:id(),"Heli must not be cast to inventory-owning task giver")
    if task_fail then self.task_info[id]={partial=true};error("simulated partially created task") end
    assert(task_functor.adb_helicopter_boss_text(id,"title")=="Боевой вертолёт")
    assert(task_functor.adb_helicopter_boss_text(id,"descr"):find(" запросил зачистку с воздуха. Требуется отбить атаку.",1,true))
    assert(task_functor.adb_helicopter_boss_target(id))
    self.task_info[id]={}
    a.tasks[id]={id=id}
end
task_manager={get_task_manager=function() return manager end}
function RegisterScriptCallback() end
function printf() logs=logs+1 end
callback={helicopter_on_hit=9};CHelicopter={}
object_binder={reinit=function() end,net_spawn=function(self)
    if save_during_spawn then self:save({w_u8=function() end,w_float=function() end}) end
    return true
end,net_destroy=function() end,
    save=function() end,load=function() end,update=function() end}
local constructing
function super(obj) constructing.object=obj end
function class(name)
    local env=getfenv(2)
    return function(base)
        local cls=setmetatable({},{__index=base,__call=function(c,obj)
            local self=setmetatable({},{__index=c});constructing=self;self:__init(obj);constructing=nil;return self
        end});env[name]=cls
    end
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_common.game_text_cp1251_lossy=function(v) return v end
anomaly_donation_bridge_common.rollback_server_objects=function(objects) for _,o in ipairs(objects) do life:release(o) end end
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.helicopter_boss");descriptor=d end}
local module=load_module(optional,MODULE);module.on_game_start();assert(descriptor.is_supported())
local KEY="adb_helicopter_boss_v1"
local function data() return saved[KEY] or {} end
local function reset()
    saved={};servers={};clients={};manager.task_info={};a.tasks={};a.living=true;a.paid=0;a.current_position=nil
    is_paused=false;blocked=false;spawn_fail=false;task_fail=false;level_name="mod_surface_custom"
    packet_fail=false;model_missing=false;motion_missing=false;save_during_spawn=false;sound_missing=false
    level_weathers.bLevelUnderground=false;descriptor.on_game_load()
end
local function advance(ms) now=now+ms;descriptor.update(now) end
local function schedule(params)
    params=copy(params or {});params.helicopter_sections=params.helicopter_sections or "mod_heli_a"
    params.catalog_revision=params.catalog_revision or "heli-revision"
    local ctx={};local ok,state,why=descriptor.can_execute(ctx,params)
    assert(ok,tostring(state)..":"..tostring(why))
    assert(descriptor.execute(ctx)=="executed")
    local id=ctx.options.slot;return data()[id],id
end
local function deferred(reason)
    local ok,state,why=descriptor.can_execute({},{helicopter_sections="mod_heli_a",catalog_revision="heli-revision"})
    assert(not ok and state=="deferred" and why==reason,tostring(why))
end
local function client_for(r)
    local h={hp=1,sets=0,m_dead=false,fire_trail=true,visible=true,
        m_min_rocket_dist=100,m_max_rocket_dist=250,m_min_mgun_dist=15,m_max_mgun_dist=300,
        m_time_between_rocket_attack=8000}
    function h:GetfHealth() return self.hp end
    function h:SetfHealth(v) self.hp=v;self.sets=self.sets+1 end
    function h:ClearEnemy() self.enemy=nil end
    function h:SetEnemy(o) assert(o==a);self.enemy=o end
    function h:LookAtPoint(p,active) self.look_point=p;self.look_active=active end
    function h:UseFireTrail(value) assert(value==false);self.fire_trail=value end
    function h:isVisible(o) assert(o==a);return self.visible end
    function h:TurnEngineSound() assert(self.native_ready,"Sound accessed before native net_Spawn completed") end
    function h:TurnLighting() assert(self.native_ready,"Light accessed before native net_Spawn completed") end
    function h:SetMaxVelocity(v) assert(v>0);self.max_velocity=v end
    function h:SetSpeedInDestPoint(v) assert(v>0);self.dest_velocity=v end
    function h:SetDestPosition(v) self.destination=v end
    function h:Die() self.m_dead=true end
    local o={object_id=r.id,object_name=r.identity,p=servers[r.id].position,h=h}
    function o:id() return self.object_id end
    function o:name() return self.object_name end
    function o:section() return SECTION end
    function o:position() return self.p end
    function o:get_helicopter() return self.h end
    function o:set_callback(_,f,binder) self.on_hit=f;self.hit_binder=binder end
    function o:bind_object(b) self.binder=b end
    clients[r.id]=o;module.bind(o);o.binder:reinit();assert(o.binder:net_spawn(servers[r.id]))
    h.native_ready=true
    return o,o.binder,h
end
local function packet()
    local p={values={},cursor=0}
    function p:w_u8(v) self.values[#self.values+1]=v end
    function p:w_float(v) self.values[#self.values+1]=v end
    function p:r_u8() self.cursor=self.cursor+1;return self.values[self.cursor] end
    p.r_float=p.r_u8;return p
end
local function hit(o,power)
    -- CHelicopter::Hit applies immunity before firing its callback; this is
    -- not a simulated script damage override. The coefficients are also
    -- independently checked against the shipped private LTX in app tests.
    o.h.hp=o.h.hp-power*0.01;o.on_hit(o.hit_binder,power,0,8,a:id())
end
local function orbit_radius(h)
    local center=a:position();local point=assert(h.destination)
    return math.sqrt((point.x-center.x)^2+(point.z-center.z)^2)
end
local function assert_orbit(h,expected)
    assert(math.abs(orbit_radius(h)-expected)<1e-6,"Wrong horizontal orbit radius: "..orbit_radius(h))
end

-- Fail closed in underground/unknown weather, covered maps, old installations.
level_weathers.bLevelUnderground=true;deferred("helicopter_boss_requires_surface")
level_weathers.bLevelUnderground=nil;deferred("helicopter_boss_requires_surface")
level_weathers.bLevelUnderground=false;blocked=true;deferred("helicopter_boss_open_sky_required");blocked=false
sections[SECTION].adb_heli_profile="0";deferred("helicopter_boss_update_addon_restart_game");sections[SECTION].adb_heli_profile="1"
local api=utils_stpk.set_heli_data;utils_stpk.set_heli_data=nil;deferred("helicopter_boss_spawn_api_unavailable");utils_stpk.set_heli_data=api
model_missing=true;descriptor.on_game_load();deferred("helicopter_boss_model_animation_unsupported")
model_missing=false;motion_missing=true;descriptor.on_game_load();deferred("helicopter_boss_model_animation_unsupported")
motion_missing=false;descriptor.on_game_load()
sound_missing=true;deferred("helicopter_boss_engine_sound_unavailable");sound_missing=false
for _,params in ipairs({{health_percent="9"},{health_percent="100000"},{reward_money="1000001"},{arrival_seconds="-1"},{use_rockets="yes"},{unknown="1"}}) do
    local ok,state=descriptor.can_execute({},params);assert(not ok and state=="rejected")
end

-- Timer belongs to the save, pauses while dead/on another level, no external
-- queue restoration over a before save, no saved absolute retry deadlines.
local before=copy(saved)
local r,id=schedule({donor_name="TimerDonor"});assert(r.remaining_ms==10000 and r.health_percent==50 and not r.id)
assert(#announcements==1 and announcements[1]:find("TimerDonor",1,true) and announcements[1]:find("10 секунд",1,true))
assert(announcements[1]:find("Штаб, это",1,true) and announcements[1]:find("\\n\\nВас понял",1,true))
local rendered=announcements[1]:gsub("\\n","\n");assert(rendered:find("\n\nВас понял",1,true))
local _,donor_count=announcements[1]:gsub("TimerDonor","");assert(donor_count==2)
advance(5000);assert(r.remaining_ms==5000)
local pending=copy(saved);advance(1000);assert(r.remaining_ms==4000)
saved=copy(pending);descriptor.on_game_load();r=data()[id];advance(5000);assert(r.remaining_ms==5000)
a.living=false;advance(10000);assert(r.remaining_ms==5000);a.living=true
is_paused=true;advance(5000);assert(r.remaining_ms==5000);is_paused=false
descriptor.on_level_changing();level_name="mod_other_surface";advance(10000);assert(r.remaining_ms==5000 and not r.id)
level_name="mod_surface_custom";descriptor.on_game_load();advance(1000);advance(5000);assert(r.stage=="arriving" and r.id)
local c,b,h=client_for(r);b:update(0);assert(h.hp==0.5 and h.m_use_mgun_on_attack==false and not r.task_created)
assert_orbit(h,120)
local spawn_count=creates
saved=copy(before);descriptor.on_game_load();advance(60000);b:update(0)
assert(creates==spawn_count and h.enemy==nil and #announcements==1,"Before save replayed a future effect/message")

-- Preparation failures cannot leave a fatal $editor server in ALife; cold
-- save during native net_Spawn must not touch not-yet-created light/sound.
reset();packet_fail=true;r,id=schedule({arrival_seconds="0"});advance(250)
assert(r.stage=="waiting" and not r.id and next(servers)==nil)
packet_fail=false;advance(5000);assert(r.id and servers[r.id].packet_data.startup_animation=="idle")
save_during_spawn=true;c,b,h=client_for(r);assert(not b.effects_started);b:update(0);assert(b.effects_started)
reset();r,id=schedule({helicopter_sections="mod_heli_b",arrival_seconds="0"});advance(250)
assert(r.source_section=="mod_heli_b" and servers[r.id].packet_data.visual_name=="mod/heli_b.ogf" and
    servers[r.id].packet_data.startup_animation=="rotor_loop")
local chosen={}
local real_random=math.random
for i=1,2 do
    math.random=function(minimum,maximum) assert(minimum==1 and maximum==2);return i end
    reset();r=schedule({helicopter_sections="mod_heli_a,mod_heli_b",arrival_seconds="0"});advance(250)
    assert(r.source_section=="mod_heli_a" or r.source_section=="mod_heli_b");chosen[r.source_section]=true
end
math.random=real_random
assert(chosen.mod_heli_a and chosen.mod_heli_b,"Multi-selection never chooses both approved models")

-- Orbit combat must retain body lock and direct-fire mode at every azimuth.
-- These are the actual stock round_update API contracts; they are NOT a
-- substitute for testing native turret traversal/weapon hits in the game.
reset();r,id=schedule({arrival_seconds="0",donor_name="OrbitDonor"});advance(250)
c,b,h=client_for(r)
for angle=0,330,30 do
    local rad=angle*math.pi/180;c.p=vec(math.cos(rad)*100,55,math.sin(rad)*100)
    advance(3100);b:update(0)
    assert(h.enemy==a and h.look_active and h.look_point.x==0 and h.look_point.z==0,
        "Orbit lost its body-facing-target lock")
    assert(not h.fire_trail and h.m_use_mgun_on_attack and not h.m_use_rocket_on_attack,
        "Orbit incorrectly retained finite flyby fire mode or stopped gun attacks")
    assert(h.max_velocity==15 and h.dest_velocity==15 and h.hp==0.5)
    assert_orbit(h,100)
end
h.visible=false;b:update(0);assert(h.enemy==nil and not h.m_use_mgun_on_attack)
h.visible=true;a.current_position=vec(20,0,25);b:update(0)
assert(h.enemy==a and h.look_point.x==20 and h.look_point.z==25,"Moving actor aim point was not updated")
is_paused=true;b:update(0);assert(h.enemy==nil and not h.look_active and not h.m_use_mgun_on_attack)
is_paused=false;level_weathers.bLevelUnderground=true;b:update(0);assert(h.enemy==nil and not h.look_active)
level_weathers.bLevelUnderground=false;b:update(0);assert(h.enemy==a and h.look_active)
a.living=false;b:update(0);assert(h.enemy==nil and not h.look_active and not h.m_use_mgun_on_attack)

-- Rockets need a wider horizontal orbit, inside the ACTUAL native ranges.
-- The checkbox changes the route, never native weapon limits or cooldowns.
reset();r,id=schedule({arrival_seconds="0",use_rockets="true"});advance(250)
c,b,h=client_for(r);b:update(0);assert(r.stage=="arriving");assert_orbit(h,120)
c.p=vec(100,55,0)
for angle=0,330,30 do
    a.current_position=vec(angle/10,0,-angle/20)
    advance(3100);b:update(0);assert_orbit(h,175)
    assert(r.stage=="active" and h.m_use_rocket_on_attack and h.m_use_mgun_on_attack)
    assert(orbit_radius(h)>h.m_min_rocket_dist and orbit_radius(h)<h.m_max_rocket_dist)
    assert(h.m_min_rocket_dist==100 and h.m_max_rocket_dist==250 and
        h.m_min_mgun_dist==15 and h.m_max_mgun_dist==300 and h.m_time_between_rocket_attack==8000)
end
h.visible=false;advance(3100);b:update(0);assert_orbit(h,175)
assert(not h.m_use_rocket_on_attack and not h.m_use_mgun_on_attack,"Wider orbit bypassed native visibility")
-- Another build may change either weapon's range. Prefer their intersection;
-- if it is empty, keep rockets in range rather than silently disabling them.
h.m_min_rocket_dist,h.m_max_rocket_dist=240,500
h.m_min_mgun_dist,h.m_max_mgun_dist=15,600
advance(3100);b:update(0);assert_orbit(h,370)
h.m_max_mgun_dist=300;advance(3100);b:update(0);assert_orbit(h,270)
h.m_max_mgun_dist=200;advance(3100);b:update(0);assert_orbit(h,370)
h.m_min_rocket_dist=0/0;advance(3100);b:update(0);assert_orbit(h,100)
h.m_min_rocket_dist,h.m_max_rocket_dist=500,240;advance(3100);b:update(0);assert_orbit(h,100)
h.m_min_rocket_dist,h.m_max_rocket_dist=100,250
h.m_min_mgun_dist,h.m_max_mgun_dist=nil,nil;advance(3100);b:update(0);assert_orbit(h,175)

-- High and low HP, early hit preserved, gun default, rockets opt-in, native
-- health restoration once (no regeneration), quest only when arriving nearby.
reset();r,id=schedule({health_percent="99999",arrival_seconds="0",reward_money="1000000",use_rockets="true",donor_name="RealDonor"});advance(250)
assert(announcements[#announcements]:find("0 секунд",1,true))
c,b,h=client_for(r);hit(c,10);assert(math.abs(h.hp-999.89)<1e-6)
c.p=vec(100,55,0);b:update(0);assert(r.stage=="active" and r.task_created and h.enemy==a)
assert(h.m_use_mgun_on_attack and h.m_use_rocket_on_attack)
assert_orbit(h,175)
assert(task_functor.adb_helicopter_boss_text(id,"descr"):find("RealDonor",1,true))
hit(c,10);local damaged=h.hp;local sets=h.sets
b:update(0);b:update(0);assert(h.hp==damaged and h.sets==sets)
local p=packet();b:save(p);local after=copy(saved);hit(c,20)
saved=copy(after);descriptor.on_game_load();r=data()[id];r.health=999.99 -- deliberately stale actor storage
c,b,h=client_for(r);b:load(p);b:update(0);assert(h.hp==damaged and h.sets==1)
assert(not h.fire_trail and h.look_active and h.enemy==a,"Load did not restore direct aimed combat")
assert(r.rockets==true);assert_orbit(h,175)
local calls=task_calls;b:update(0);assert(task_calls==calls,"Quest duplicated on load")

-- Changing level allows offline storage, never moves or respawns the heli.
spawn_count=creates;descriptor.on_level_changing();assert(servers[r.id].offline==true)
level_name="some_other_mod_level";advance(60000);b:update(0);assert(creates==spawn_count and h.enemy==nil)
assert(not r.dead and task_status_functor.adb_helicopter_boss_status(manager.task_info[id],id)==nil)
level_name="mod_surface_custom";descriptor.on_game_load();b:update(0);assert(h.hp==damaged and h.enemy==a)

-- Only death earns reward, exactly once; cancelled/wrong-identity objects do
-- not release someone else's entity or pay on unload.
hit(c,10000000);b:update(0);assert(r.dead and h.m_dead)
assert(task_status_functor.adb_helicopter_boss_status(manager.task_info[id],id)=="complete")
xr_effects.adb_helicopter_boss_complete(nil,nil,{id});xr_effects.adb_helicopter_boss_complete(nil,nil,{id})
assert(a.paid==1000000 and r.stage=="complete")
reset();r,id=schedule({health_percent="10",arrival_seconds="0",reward_money="0"});advance(250)
c,b,h=client_for(r);b:update(0);assert(h.hp==0.1 and not h.m_use_rocket_on_attack)
c.p=vec(100,55,0);b:update(0);hit(c,100);b:update(0)
xr_effects.adb_helicopter_boss_complete(nil,nil,{id});assert(a.paid==0 and r.reward_granted)
reset();r,id=schedule({arrival_seconds="0"});advance(250);c,b,h=client_for(r);b:update(0)
servers[r.id].object_name="unrelated_recycled_id"
assert(task_status_functor.adb_helicopter_boss_status(manager.task_info[id],id)=="fail")
xr_effects.adb_helicopter_boss_cancel(nil,nil,{id});assert(servers[r.id] and a.paid==0)

-- Task failure is safe, retry bounded, weapons stay off; restart clocks reset
-- transient retries, but not saved timer/HP. Multiple independent slots work.
reset();task_fail=true;r,id=schedule({arrival_seconds="0"});advance(250);c,b,h=client_for(r);c.p=vec(100,55,0)
calls=task_calls;b:update(0);b:update(0);assert(task_calls==calls+1 and not r.task_created and not h.m_use_mgun_on_attack)
task_fail=false;advance(5000);b:update(0);assert(r.task_created and h.m_use_mgun_on_attack)
local second,second_id=schedule({arrival_seconds="0"});assert(second_id~=id);advance(250);assert(second.id~=r.id)
reset();spawn_fail=true;r,id=schedule({arrival_seconds="0"});advance(250);spawn_count=creates
advance(250);assert(creates==spawn_count and not r.id)
local retry_save=copy(saved);spawn_fail=false;now=10;saved=retry_save;descriptor.on_game_load();advance(250)
assert(data()[id].id and data()[id].next_spawn_try==nil)

-- A broken modded PDA cannot roll back or repeat an accepted donation.
local messenger=news_manager.send_tip;news_manager.send_tip=function() error("broken external UI") end
local accepted=schedule({arrival_seconds="45",donor_name="MessageDonor"})
assert(accepted.stage=="waiting" and accepted.remaining_ms==45000)
news_manager.send_tip=messenger

-- Own packet validation fails before any NaN can reach native SetfHealth.
r=data()[id];c,b,h=client_for(r);p=packet();p:w_u8(1);p:w_float(0/0)
assert(not pcall(function() b:load(p) end))
print("Helicopter boss smoke passed: native HP/attack/binder contract, save isolation, timer, levels, quest, queue, bounded retries.")
