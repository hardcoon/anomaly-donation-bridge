local core,optional=assert(arg[1]),assert(arg[3])
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_stationary_mgun"
local SECTION,BLAST,KEY="adb_stationary_mgun","adb_stationary_mgun_blast","adb_stationary_mguns_v1"
local SPEEDS={60,90,120}
local function gun_section(s) return s==SECTION or s:match("^adb_stationary_mgun_r%d%d%d$")~=nil end
local function copy(t) if type(t)~="table" then return t end;local r={};for k,v in pairs(t) do r[k]=copy(v) end;return r end
local function load_module(root,name)
    local env=setmetatable({},{__index=_G});local chunk=assert(loadfile(root.."/"..name..".script"))
    setfenv(chunk,env);chunk();return env
end
vector=setmetatable({},{__call=function() return {
    set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end,
    getH=function(self) return math.atan2(-self.x,self.z) end} end})
local function vec(x,y,z) return vector():set(x,y,z) end
local memory,callbacks,servers,clients,descriptor,module={},{},{},{},nil,nil
local clock,serial,creates,explosions,detach_calls,warnings,logs=0,0,0,0,0,0,0
local paused,living,blocked_exit,no_floor,wall,model_missing,setup_fail=false,true,false,false,false,false,false
local current_map,held,delayed_release=1,nil,false
local queued_release={}
local missing_texture,missing_userdata,missing_physics,old_model,ammo_fills=false,false,false,false,0
CWeaponStatMgun={}
local a={}
function a:alive() return living end
function a:position() return vec(0,0,0) end
function a:direction() return vec(0,0,1) end
function a:level_vertex_id() return current_map*100 end
function a:game_vertex_id() return current_map*10 end
function a:get_attached_vehicle() return held end
function a:get_current_holder() return held and held:get_holder_class() end
function a:detach_vehicle(force)
    assert(force==false,"Do not force/teleport an actor out of an obstructed holder")
    detach_calls=detach_calls+1
    if not blocked_exit and held then held.holder.occupied=false;held=nil end
end
db={actor=a};USE_MARSHAL=true
function time_global() return clock end
function device() return {is_paused=function() return paused end} end
function printf() logs=logs+1 end
function RegisterScriptCallback(name,f) assert(not callbacks[name]);callbacks[name]=f end
alife_storage_manager={get_state=function() return memory end}
rq_target={rqtStatic=1,rqtBoth=2};rq_result=setmetatable({},{__call=function() return {} end})
local vertices={}
level={object_by_id=function(id) return clients[id] end,
    vertex_in_direction=function(_,direction,radius)
        local id=#vertices+1;vertices[id]=vec(direction.x*radius,0,direction.z*radius);return id
    end,
    vertex_position=function(id) return vertices[id] end,
    ray_pick=function(from,direction,distance,kind,result,ignored)
        assert(ignored==a and distance>0)
        if direction.y==-1 then
            assert(kind==rq_target.rqtStatic)
            if no_floor then return false end
            result.range=from.y;return true
        end
        assert(kind==rq_target.rqtBoth)
        if wall then result.range=0.1;return true end
        return false
    end}
local config={
    [SECTION]={class="W_STMGUN",script_binding=MODULE..".bind",visual="anomaly_donation_bridge\\stationary_mgun.ogf",adb_stationary_profile="4",unlimited_ammo="true",lock_exit="false"},
    [BLAST]={class="G_RGD5_S"}}
for _,speed in ipairs({20,40,60,90,120}) do
    local section=SECTION..string.format("_r%03d",speed)
    config[section]=copy(config[SECTION]);config[section].visual="anomaly_donation_bridge\\stationary_mgun"..string.format("_r%03d.ogf",speed)
    config[section].adb_rotation_speed_dps=tostring(speed)
end
function system_ini() return {
    section_exist=function(_,s) return config[s]~=nil end,
    line_exist=function(_,s,k) return config[s] and config[s][k]~=nil end,
    r_string=function(_,s,k) return config[s][k] end} end
function getFS() return {exist=function(_,alias,path)
    if alias=="$game_textures$" then assert(path=="wpn\\wpn_pkm_mounted.dds" or path=="wpn\\wpn_pkm_lenta.dds");return not missing_texture end
    if alias=="$game_config$" then assert(path=="models\\weapons\\anomaly_donation_bridge_stationary_mgun.ltx");return not missing_userdata end
    assert(alias=="$game_meshes$" and (path=="anomaly_donation_bridge\\stationary_mgun.ogf" or path:match("^anomaly_donation_bridge\\stationary_mgun_r%d%d%d%.ogf$")));return not model_missing
end} end
news_manager={send_tip=function(target,message,_,_,ms)
    assert(target==a and ms==5000 and message=="Пулемёт сейчас рванёт, нужно уходить");warnings=warnings+1
end}
object_binder={net_spawn=function() return true end,net_destroy=function() end}
local constructing
function super(obj) constructing.object=obj end
function class(name)
    local env=getfenv(2)
    return function(base)
        env[name]=setmetatable({},{__index=base,__call=function(c,obj)
            local self=setmetatable({},{__index=c});constructing=self;self:__init(obj);constructing=nil;return self
        end})
    end
end
local function make_client(s)
    local c={holder={occupied=false},pos=copy(s.pos)}
    function c:id() return s.id end
    function c:name() return s:name() end
    function c:section() return s:section_name() end
    function c:position() return self.pos end
    function c:get_visual_name() return old_model and "dynamics\\weapons\\wpn_mounted\\wpn_mounted_pkm_trenoga" or config[s:section_name()].visual:gsub("%.ogf$","") end
    c.stm={ammo=0,speed=tonumber(config[s:section_name()].adb_rotation_speed_dps) or 10}
    function c.stm:GetAmmoElapsed() return self.ammo end
    function c.stm:SetAmmoElapsed(n) assert(n==1);self.ammo=n;ammo_fills=ammo_fills+1 end
    function c:get_stmgun() return self.stm end
    function c:get_physics_shell() return {get_element_by_bone_name=function(_,bone)
        assert(bone=="bone_rotate_x" or bone=="bone_rotate_y");return not missing_physics and {} or nil
    end} end
    function c:parent() return nil end
    function c:get_holder_class() return self.holder end
    function c.holder:SetEnterLocked(value) self.enter_locked=value end
    function c.holder:SetExitLocked(value) self.exit_locked=value end
    function c.holder:engaged() return self.occupied end
    function c:bind_object(binder) self.binder=binder end
    function c:explode(argument)
        assert(self:section()==BLAST and argument==0)
        for _,gun in pairs(clients) do if gun_section(gun:section()) then
            assert(not gun.holder.occupied,"Exploded an occupied native holder")
        end end
        explosions=explosions+1
    end
    clients[s.id]=c;return c
end
local life={}
function life:object(id,all) assert(all==true);return servers[id] end
function life:set_switch_online(id,value)
    assert(servers[id]);if setup_fail then error("mock configuration failure") end
    servers[id].switch_online=value
end
function life:set_switch_offline(id,value) assert(servers[id]);servers[id].switch_offline=value end
local function destroy(id) servers[id]=nil;clients[id]=nil end
function life:release(s,force)
    assert(force==true and servers[s.id]==s)
    if clients[s.id] and gun_section(s:section_name()) then assert(not clients[s.id].holder.occupied) end
    if delayed_release then queued_release[s.id]=true else destroy(s.id) end
end
function alife() return life end
function alife_create(section,pos,lv,gv)
    assert(gun_section(section) or section==BLAST)
    assert(pos.y>0 and math.sqrt(pos.x^2+pos.z^2)>=2.5 and lv>=0 and gv>=0)
    serial=serial+1;creates=creates+1
    local s={id=serial,pos=copy(pos),map=current_map,online=true}
    function s:name() return section.."_"..self.id end
    function s:section_name() return section end
    servers[s.id]=s
    local c=make_client(s)
    if gun_section(section) then
        module.bind(c);assert(c.binder:net_spawn(s) and c.binder:net_save_relevant())
        -- Native net_Spawn overwrites ammo after the Lua binder; the hook must
        -- run AFTER that import, not initialize the soon-to-be-reset magazine.
        c.stm.ammo=0;module.net_spawn_after(c)
    end
    return s
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_common.game_text_cp1251_lossy=function(s) return s end
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.stationary_mgun");descriptor=d end}
local function reload()
    callbacks={};module=load_module(optional,MODULE);module.on_game_start();assert(descriptor.is_supported())
end
local function advance(ms) clock=clock+ms;callbacks.actor_on_update() end
local function fresh()
    memory={};servers={};clients={};held=nil;queued_release={};vertices={};clock=0;serial=0
    creates=0;explosions=0;detach_calls=0;warnings=0;logs=0
    paused=false;living=true;blocked_exit=false;no_floor=false;wall=false;model_missing=false;setup_fail=false
    current_map=1;delayed_release=false;reload();advance(0);advance(1000)
    missing_texture=false;missing_userdata=false;missing_physics=false;old_model=false;ammo_fills=0;CWeaponStatMgun={}
end
local function start(seconds,speed)
    local params={};if seconds~=nil then params.lifetime_seconds=tostring(seconds) end
    if speed~=nil then params.rotation_speed_dps=tostring(speed) end
    local context={};local ok,status,why=descriptor.can_execute(context,params)
    assert(ok,tostring(status)..":"..tostring(why))
    assert(descriptor.execute(context)=="executed")
    local r=memory[KEY].turrets[serial];assert(r and servers[r.id].switch_offline==true)
    return r.id
end
local function state(id) return memory[KEY].turrets[id] end
local function mount(id) held=assert(clients[id]);held.holder.occupied=true end
local function leave_map(map)
    descriptor.on_level_changing();assert(not held)
    for id,s in pairs(servers) do s.online=false;clients[id]=nil end
    current_map=map
    -- The native save/storage is the same ALife world; no spawned replacements.
    descriptor.on_game_load();advance(0);advance(1000)
end
local function enter_map(map)
    current_map=map
    for _,s in pairs(servers) do if s.map==map then s.online=true;make_client(s) end end
    descriptor.on_game_load();advance(0);advance(1000)
end

fresh();local id=start();assert(state(id).lifetime_seconds==60 and state(id).remaining_ms==60000)
assert(state(id).rotation_speed_dps==60 and clients[id].stm.speed==60 and servers[id]:section_name()==SECTION.."_r060")
assert(clients[id].stm.ammo==1 and ammo_fills==1 and not clients[id].holder.enter_locked,"Fresh native empty magazine was not initialized after spawn")
advance(1000);advance(1000);assert(ammo_fills==1,"Infinite ammo must not be refilled every frame")
assert(servers[id].angle and math.abs(servers[id].angle.x)<0.001 and math.abs(servers[id].angle.y)<0.001)
for _,seconds in ipairs({0,1,59,60,6000}) do fresh();id=start(seconds);assert(state(id).lifetime_seconds==seconds) end
for _,value in ipairs({"-1","6001","1.5","oops"}) do fresh()
    local ok,status=descriptor.can_execute({},{lifetime_seconds=value});assert(not ok and status=="rejected" and creates==0)
end
-- Speed belongs to each spawned native model, not the global camera/mouse or
-- another turret. Reject unknown profiles before creating anything.
fresh();local by_speed={}
for _,speed in ipairs(SPEEDS) do
    local n=start(0,speed);by_speed[speed]=n
    assert(state(n).rotation_speed_dps==speed and clients[n].stm.speed==speed)
end
callbacks.save_state(memory);local speed_saved=copy(memory)
reload();descriptor.on_game_load();advance(0);advance(1000)
for speed,n in pairs(by_speed) do assert(state(n).rotation_speed_dps==speed and clients[n].stm.speed==speed) end
assert(creates==#SPEEDS and detach_calls==0,"Speed restore recreated or detached native holders")
leave_map(2);enter_map(1)
for speed,n in pairs(by_speed) do assert(state(n).rotation_speed_dps==speed and clients[n].stm.speed==speed) end
assert(creates==#SPEEDS)
for _,value in ipairs({"0","-1","5","10","15","30","45","180","61","20.5","oops","1e999"}) do fresh()
    local ok,status=descriptor.can_execute({},{rotation_speed_dps=value})
    assert(not ok and status=="rejected" and creates==0,"Invalid native speed accepted: "..value)
end
for _,speed in ipairs({20,40}) do fresh();id=start(0,speed)
    assert(state(id).rotation_speed_dps==60 and clients[id].stm.speed==60,"Legacy queued speed must migrate to 60")
    local original=servers[id];local legacy_section=SECTION..string.format("_r%03d",speed)
    function original:section_name() return legacy_section end
    clients[id]=make_client(original);callbacks.save_state(memory)
    local record=state(id);record.section=legacy_section;record.rotation_speed_dps=speed
    reload();descriptor.on_game_load();advance(0);advance(1000)
    assert(state(id).rotation_speed_dps==speed and clients[id].stm.speed==speed and creates==1,"Previously saved native profile was lost")
end
fresh();id=start(0,60);callbacks.save_state(memory);local wrong=copy(memory)
wrong[KEY].turrets[id].rotation_speed_dps=90;memory=wrong;reload();advance(0);advance(1000)
assert(not state(id) and servers[id],"Mismatched saved profile was accepted or deleted an unknown gun")
-- sg2/sg3 saves have no speed metadata. Keep their original base section and
-- native 10 degrees/sec instead of replacing the live PH shell.
fresh();id=start(0);local old=state(id);old.section=nil;old.rotation_speed_dps=nil;memory[KEY].version=1
local old_server=servers[id];function old_server:section_name() return SECTION end
clients[id]=make_client(old_server);reload();advance(0);advance(1000)
assert(state(id).rotation_speed_dps==10 and state(id).section==SECTION and clients[id].stm.speed==10 and creates==1)
fresh();id=start(10);advance(3000);callbacks.save_state(memory);local saved=copy(memory)
assert(state(id).remaining_ms==7000);mount(id)
advance(2000);assert(state(id).remaining_ms==5000)
memory=copy(saved);reload();descriptor.on_game_load();advance(0);advance(1000)
assert(state(id).remaining_ms==7000 and held==clients[id] and creates==1 and detach_calls==0,
    "F9 detached, respawned or restarted the native saved holder")
advance(7000);assert(not held and detach_calls==1 and state(id).phase=="arming" and warnings==1)
assert(servers[id] and clients[id].holder.enter_locked and not clients[id].holder.exit_locked)
advance(4999);assert(explosions==0);advance(1);assert(state(id).phase=="blast" and creates==2)
local blast_id=state(id).blast.id;local flags={ret_value=true}
callbacks.actor_on_item_before_pickup(clients[blast_id],flags);assert(flags.ret_value==false)
advance(0);assert(flags.ret_value==true and explosions==1 and not servers[id] and servers[blast_id])
advance(1000);assert(servers[blast_id] and state(id),"Destroying the gun prematurely deleted the native explosion")
saved=copy(memory);memory=copy(saved);reload();advance(0);advance(1000);advance(14000)
assert(explosions==1 and not servers[blast_id] and not state(id),"Saved explosion was replayed or leaked")

-- The PDA warning is one-shot and its five-second countdown is saved too.
fresh();id=start(1);advance(1000);advance(2000);callbacks.save_state(memory);saved=copy(memory)
assert(state(id).phase=="arming" and state(id).stage_ms==3000 and warnings==1)
advance(3000);advance(0);assert(explosions==1)
-- Restore native world objects along with this earlier save, not future ones.
memory=copy(saved);local saved_server={id=id,pos=vec(0,0.02,3),map=1,online=true}
function saved_server:name() return memory[KEY].turrets[self.id].name end
function saved_server:section_name() return memory[KEY].turrets[self.id].section end
servers={[id]=saved_server};clients={};make_client(saved_server);reload();advance(0);advance(1000)
assert(state(id).stage_ms==3000 and warnings==1)
advance(2999);assert(state(id).phase=="arming");advance(1);advance(0)
assert(explosions==2 and warnings==1,"F9 repeated the warning or failed to restore the shutdown countdown")

-- A blocked native dismount must never destroy the actor's holder. Retries
-- are bounded and a fresh five-second grace starts only after successful exit.
fresh();id=start(1);mount(id);blocked_exit=true;advance(1000);advance(0);advance(500)
assert(detach_calls==1 and held and creates==1 and explosions==0)
advance(500);assert(detach_calls==2 and state(id).phase=="exit")
callbacks.save_state(memory);assert(held and servers[id] and detach_calls==2,"F5 mutated an occupied holder")
blocked_exit=false;advance(1000);assert(not held and state(id).phase=="arming")
mount(id);blocked_exit=true;advance(2000);assert(explosions==0 and state(id).phase=="exit")
blocked_exit=false;advance(1000);advance(5000);advance(0);assert(explosions==1 and warnings==1)

-- Permanent means across maps, Lua reloads and saves on another map too.
fresh();id=start(0);advance(6000000);assert(state(id).phase=="active")
local original=servers[id];leave_map(2);advance(10000000);callbacks.save_state(memory);saved=copy(memory)
assert(servers[id]==original and not clients[id] and creates==1 and state(id).lifetime_seconds==0)
memory=copy(saved);reload();advance(0);advance(1000);leave_map(3);enter_map(1)
assert(servers[id]==original and clients[id] and state(id).phase=="active" and creates==1 and explosions==0 and warnings==0,
    "A permanent turret vanished, followed actor or was duplicated on map return")
mount(id);advance(6000000);assert(held and detach_calls==0)

-- Timed turret expiring on a different map is removed THERE. No grenade or
-- forced online remote object may materialize next to the actor.
fresh();id=start(5);leave_map(2);advance(5000)
assert(not servers[id] and not state(id) and creates==1 and explosions==0)
enter_map(1);assert(not servers[id] and creates==1)
fresh();id=start(10);paused=true;advance(10000);assert(state(id).remaining_ms==10000)
paused=false;living=false;advance(10000);assert(state(id).remaining_ms==10000)
living=true;advance(1000);assert(state(id).remaining_ms==9000)

-- Identity guard: recycled IDs, unrelated native holders, unknown occupants.
fresh();id=start(1);clients[id].holder.occupied=true;advance(5000)
assert(state(id).phase=="exit" and explosions==0 and detach_calls==0 and servers[id])
clients[id].holder.occupied=false;advance(0);advance(5000);advance(0);assert(explosions==1)
fresh();id=start(1);local foreign={name=function() return "unrelated" end,section_name=function() return "stationary_mgun" end}
servers[id]=foreign;clients[id]=nil;advance(5000)
assert(servers[id]==foreign and not state(id) and explosions==0)
fresh();id=start(0);memory={};destroy(id);descriptor.on_game_load();advance(0);advance(1000)
assert(not state(id) and creates==1,"Loading a save before the effect resurrected future guns")

for _,problem in ipairs({"floor","wall","model","holder","texture","userdata"}) do fresh()
    no_floor=problem=="floor";wall=problem=="wall";model_missing=problem=="model"
    missing_texture=problem=="texture";missing_userdata=problem=="userdata"
    if problem=="holder" then held={get_holder_class=function() return {} end} end
    local ok,status=descriptor.can_execute({},{lifetime_seconds="60"})
    assert(not ok and status=="deferred" and creates==0,"Unsafe placement/API was not deferred: "..problem)
end
fresh();local context={};assert(descriptor.can_execute(context,{lifetime_seconds="1"}));wall=true
assert(descriptor.execute(context)=="deferred" and creates==0,"Execute used a stale safe-place snapshot")
fresh();setup_fail=true;context={};assert(descriptor.can_execute(context,{}))
assert(descriptor.execute(context)=="rejected" and creates==1 and not next(servers) and not next(memory[KEY].turrets))
assert(logs==1)
-- Runtime incompatibilities block entry without forcing a broken native
-- detach, changing a live visual or altering any unrelated gun/input handler.
fresh();missing_physics=true;id=start(1);advance(5000)
assert(clients[id].holder.enter_locked and ammo_fills==0 and detach_calls==0 and explosions==0)
fresh();old_model=true;id=start(1);advance(5000)
assert(clients[id].holder.enter_locked and ammo_fills==0 and detach_calls==0 and explosions==0)
fresh();CWeaponStatMgun=nil;local ok,status,why=descriptor.can_execute({},{})
assert(not ok and status=="deferred" and why=="stationary_mgun_rotation_speed_api_unavailable" and creates==0,
    "Do not silently pretend legacy engines support configurable native speed")
-- GE_DESTROY can remain queued for several frames; the event is at most once.
fresh();id=start(1);delayed_release=true;advance(1000);advance(5000);advance(0)
assert(explosions==1 and state(id).phase=="cleanup" and queued_release[id])
for _=1,10 do advance(0) end;assert(explosions==1)
destroy(id);advance(1000);assert(state(id) and servers[state(id).blast.id])
print("Stationary mgun smoke passed: 60/90/120 profiles, default 60, legacy speeds/saves, timer 0-6000, native holder, guarded exit/explosion, F5/F9, permanent map return, offline expiry and identities.")
