local core,optional=assert(arg[1]),assert(arg[3])
local function load_module(root,name)
    local env=setmetatable({},{__index=_G});local f=assert(loadfile(root.."/"..name..".script"))
    setfenv(f,env);f();return env
end
local function copy(t)
    if type(t)~="table" then return t end
    local r={};for k,v in pairs(t) do r[k]=copy(v) end;return r
end
vector=setmetatable({},{__call=function() return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end} end})
local function v(x,y,z) return vector():set(x or 0,y or 0,z or 0) end
-- luabind game_object has no __eq operator. Plain tables previously hid this
-- native failure. Distinct object proxies now reject == and ~= like the game.
local native_object_mt={__eq=function() error("No such operator [__eq] defined in class [game_object]") end}
local function game_object_proxy(o) return setmetatable(o,native_object_mt) end
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_helicopter_companion"
local SECTION="adb_helicopter_companion"
local KEY="adb_helicopter_companion_v1"
local now,paused,precache,level_name,blocked,sky_blocked=1000,false,0,"custom_surface",false,false
local saved,servers,clients,callbacks,tips={},{},{},{},{}
local serial,creates,releases,bind_update,packet_fail,missing,spawn_fail=0,0,0,false,false,false,false
game_object={enemy=2,neutral=1,friend=0}
function time_global() return now end
function device() return {is_paused=function() return paused end,precache_frame=precache} end
function app_ready() return true end
local actor=game_object_proxy({id=function() return 0 end,alive=function() return true end,
    is_entity_alive=function() return true end,position=function(self) return self.p or v() end,
    level_vertex_id=function() return 11 end,game_vertex_id=function() return 22 end})
db={actor=actor,storage={}}
function db.add_obj(o) db.storage[o:id()].object=o end
function db.del_obj(o) db.storage[o:id()]=nil end
function db.add_heli() error("Support must not join stock hostile helicopter AI") end
level_weathers={bLevelUnderground=false}
alife_storage_manager={get_state=function() return saved end}
level={name=function() return level_name end,object_by_id=function(id) return clients[id] end,
    get_bounding_volume=function() return {min=v(-2000,-50,-2000),max=v(2000,15,2000)} end,
    ray_pick=function(pos,_,_,kind) assert(kind==1);return blocked or sky_blocked and pos.y==2 end}
rq_target={rqtStatic=1};rq_result=setmetatable({},{__call=function() return {} end})
local sections={mod_heli_a={class="C_HLCP_S",visual="mod/heli_a.ogf"},
    mod_heli_b={class="C_HLCP_S",visual="mod/heli_b.ogf",startup_animation="rotor"},
    [SECTION]={class="C_HLCP_S",adb_heli_profile="1",script_binding=MODULE..".bind",rocket_class="mod_rocket"},
    mod_rocket={blast_r="10",frags_r="20"}}
function system_ini() return {section_exist=function(_,s) return sections[s]~=nil end,
    line_exist=function(_,s,k) return sections[s] and sections[s][k]~=nil end,
    r_string=function(_,s,k) return sections[s][k] end} end
game={motion_exists=function(_,anim) return not missing and (anim=="idle" or anim=="rotor") end}
function getFS() return {exist=function() return not missing end} end
local life={}
function life:object(id) return servers[id] end
function life:set_switch_online(id,value)
    assert(servers[id]);if value then assert(servers[id].packet_data.startup_animation~="$editor") end
    servers[id].online=value
end
function life:set_switch_offline(id,value) assert(servers[id]);servers[id].offline=value end
function alife() return life end
function alife_create(section,pos,lv,gv)
    assert(section==SECTION and lv==11 and gv==22 and pos.y==55)
    if spawn_fail then error("Simulated native spawn failure") end
    serial=serial+1;creates=creates+1
    local s={id=serial,section=section,object_name=SECTION..serial,position=pos,
        packet_data={visual_name="$editor",startup_animation="$editor",engine_sound="",motion_name="",skeleton_name=""}}
    function s:section_name() return self.section end
    function s:name() return self.object_name end
    servers[s.id]=s;return s
end
utils_stpk={get_heli_data=function(s) return copy(s.packet_data) end,set_heli_data=function(d,s)
    assert(s.online==false,"Must block native online switching before packet edit")
    if packet_fail then error("Simulated packet failure") end;s.packet_data=copy(d)
end}
anomaly_donation_bridge_catalog={capable=function() return true end,
    is_available=function(s,kind) return kind=="helicopter" and sections[s]~=nil and s~=SECTION end,
    validate_sections=function(p,key,kind)
        assert(key=="helicopter_sections" and kind=="helicopter")
        if p.catalog_revision~="rev" then return nil,"catalog_revision_mismatch" end
        local entries={}
        for name in tostring(p[key] or ""):gmatch("[^,]+") do
            if not sections[name] or name==SECTION then return nil,"catalog_selection_stale_or_quarantined" end
            entries[#entries+1]={section_id=name}
        end
        if #entries==0 then return nil,"selection_empty" end;return entries
    end}
function RegisterScriptCallback(name,f) assert(not callbacks[name]);callbacks[name]=f end
news_manager={send_tip=function(a,message,_,_,duration)
    assert(a==actor and duration==8000);tips[#tips+1]=message;return true
end}
function printf() end
callback={helicopter_on_hit=9};CHelicopter={}
object_binder={reinit=function() end,net_spawn=function(self)
    self:save({w_u8=function() end,w_float=function() end});return true
end,net_destroy=function() end,save=function() end,load=function() end,update=function() end}
local constructing
function super(obj) constructing.object=obj end
function class(name)
    local env=getfenv(2);return function(base)
        env[name]=setmetatable({},{__index=base,__call=function(c,obj)
            local self=setmetatable({},{__index=c});constructing=self;self:__init(obj);constructing=nil;return self
        end})
    end
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_common.game_text_cp1251_lossy=function(t) return t end
anomaly_donation_bridge_common.rollback_server_objects=function(items)
    assert(not bind_update,"Must not delete the native helicopter from its binder update")
    for _,s in ipairs(items) do
        releases=releases+1;local c=clients[s.id];if c then c.binder:net_destroy() end
        servers[s.id]=nil;clients[s.id]=nil
    end
end
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.helicopter_companion");descriptor=d end}
local module=load_module(optional,MODULE);module.on_game_start();module.on_game_start();assert(descriptor.is_supported())
local function data() return saved[KEY] or {} end
local function reset()
    saved={};servers={};clients={};db.storage={};tips={};paused=false;precache=0
    level_name="custom_surface";blocked=false;sky_blocked=false;level_weathers.bLevelUnderground=false;actor.p=nil
    packet_fail=false;missing=false;spawn_fail=false;descriptor.on_game_load()
end
local function advance(ms)
    now=now+ms;descriptor.update(now)
    for _,o in pairs(clients) do bind_update=true;o.binder:update(ms);bind_update=false end
end
local function options(p)
    local params=copy(p or {});if params.helicopter_sections==nil then params.helicopter_sections="mod_heli_a" end
    if params.catalog_revision==nil then params.catalog_revision="rev" end
    return params
end
local function schedule(p)
    local ctx={};local ok,state,reason=descriptor.can_execute(ctx,options(p));assert(ok,tostring(state)..":"..tostring(reason))
    assert(descriptor.execute(ctx)=="executed");return data()[ctx.options.slot],ctx.options.slot
end
local function client_for(r)
    local h={hp=1,sets=0,m_dead=false,m_max_mgun_dist=500,visible=true}
    function h:GetfHealth() return self.hp end
    function h:SetfHealth(hp) self.hp=hp;self.sets=self.sets+1 end
    function h:ClearEnemy() self.enemy=nil end
    function h:SetEnemy(o) assert(o:id()~=actor:id());self.enemy=o end
    function h:LookAtPoint(p,active) self.look=p;self.facing=active end
    function h:UseFireTrail(b) assert(b==false) end
    function h:isVisible(o) return self.visible and not o.invisible end
    function h:TurnEngineSound(b) assert(self.native_ready,"Sound handle before native spawn");self.sound=b end
    function h:TurnLighting() assert(self.native_ready,"Light handle before native spawn") end
    function h:SetMaxVelocity(n) self.speed=n end
    function h:SetSpeedInDestPoint() end
    function h:SetDestPosition(p) self.destination=p end
    function h:Die() self.m_dead=true;self.died=true end
    local o=game_object_proxy({object_id=r.id,object_name=r.identity,p=servers[r.id].position,h=h})
    function o:id() return self.object_id end
    function o:section() return SECTION end
    function o:name() return self.object_name end
    function o:position() return self.p end
    function o:get_helicopter() return self.h end
    function o:is_entity_alive() return false end
    function o:set_callback(_,f,b) self.on_hit=f;self.hit_binder=b end
    function o:bind_object(b) self.binder=b end
    clients[r.id]=o;module.bind(o);o.binder:reinit();assert(o.binder:net_spawn(servers[r.id]));h.native_ready=true
    return o,o.binder,h
end
local function active(p)
    local r,key=schedule(p);advance(250);assert(r.stage=="arriving")
    local o,b,h=client_for(r);advance(250);assert(r.stage=="active")
    return r,o,b,h,key
end
local function npc(id,rel,pos,companion)
    local o=game_object_proxy({object_id=id,rel=rel,p=pos,living=true,companion=companion})
    function o:id() return self.object_id end
    function o:position() return self.p end
    function o:is_entity_alive() return true end
    function o:alive() return self.living end
    function o:relation(a) assert(a==actor);return self.rel end
    function o:has_info(key) assert(key=="npcx_is_companion");return self.companion==true end
    db.storage[id]={object=o};return o
end
local function packet()
    local p={values={},cursor=0};function p:w_u8(n) self.values[#self.values+1]=n end
    p.w_float=p.w_u8;function p:r_u8() self.cursor=self.cursor+1;return self.values[self.cursor] end;p.r_float=p.r_u8
    return p
end
-- Invalid values, missing live catalog/native profile, underground and unknown
-- classification must defer or reject WITHOUT introducing a native object.
for key,values in pairs({duration_seconds={"9","100000000","10.5","nan"},follow_distance={"0","1001","bad"},
    use_rockets={"1","yes"},engine_sound={"yes","0"}}) do
    for _,value in ipairs(values) do
        local ok,state=descriptor.can_execute({},options({[key]=value}));assert(not ok and state=="rejected")
    end
end
for _,value in ipairs({true,"unknown"}) do
    level_weathers.bLevelUnderground=value;local ok,state=descriptor.can_execute({},options());assert(not ok and state=="deferred")
end
level_weathers.bLevelUnderground=false
sections[SECTION].adb_heli_profile="0";assert(not descriptor.can_execute({},options()));sections[SECTION].adb_heli_profile="1"
assert(not descriptor.can_execute({},options({catalog_revision="stale"})))
assert(not descriptor.can_execute({},options({helicopter_sections=SECTION})))
blocked=true;assert(not descriptor.can_execute({},options()));blocked=false
missing=true;descriptor.on_game_load();assert(not descriptor.can_execute({},options()));missing=false;descriptor.on_game_load()

reset();local r,key=schedule();assert(r.remaining_ms==60000 and r.follow_distance==100 and not r.rockets and r.sound and r.health==0.5)
packet_fail=true;advance(250);assert(r.stage=="waiting" and not next(servers));packet_fail=false
advance(5000);local o,b,h=client_for(r);advance(250);assert(h.sound==true and not h.enemy)
assert(h.hp==0.5,"New support helicopter must start with 50% health")
assert(servers[r.id].packet_data.visual_name=="mod/heli_a.ogf")
assert(math.abs(math.sqrt(h.destination.x^2+h.destination.z^2)-100)<1e-6)
actor.p=v(500,0,400);advance(2250)
assert(math.abs(math.sqrt((h.destination.x-500)^2+(h.destination.z-400)^2)-100)<1e-6,"Following did not track moving actor")

-- Actual hostility and scene overrides, not helicopter faction. Neutral NPCs,
-- friendly mutants, companions, dead targets and non-creatures are excluded.
reset();r,o,b,h=active()
local enemy=npc(101,2,v(250,0,0));local friend=npc(102,0,v(5,0,0));local neutral=npc(103,1,v(10,0,0))
local companion=npc(104,2,v(1,0,0),true);local dead=npc(105,2,v(2,0,0));dead.living=false
local actor_alias=npc(106,2,v(0,0,0));actor_alias.object_id=0
advance(250);assert(h.enemy==enemy and h.m_use_mgun_on_attack and not h.m_use_rocket_on_attack)
enemy.rel=1;advance(250);assert(not h.enemy and not h.m_use_mgun_on_attack)
enemy.rel=2;enemy.invisible=true;advance(250);assert(not h.enemy);enemy.invisible=false
xr_combat_ignore={ignore_enemy_by_overrides=function() return true end};advance(250);assert(not h.enemy)
xr_combat_ignore=nil;advance(250);assert(h.enemy==enemy)
for _,hit_type in ipairs({"fire_wound","explosion","wound","strike"}) do
    local hit={draftsman=o,type=hit_type,power=0.6,impulse=400}
    local flags={ret_value=true};callbacks.actor_on_before_hit(hit,0,flags);assert(flags.ret_value==false)
    for _,victim in ipairs({friend,neutral,companion}) do
        flags={ret_value=true};callbacks.npc_on_before_hit(victim,hit,0,flags);assert(flags.ret_value==false)
        flags={ret_value=true};callbacks.monster_on_before_hit(victim,hit,0,flags);assert(flags.ret_value==false)
    end
    flags={ret_value=true};callbacks.npc_on_before_hit(enemy,hit,0,flags);assert(flags.ret_value==true)
    flags={ret_value=false};callbacks.npc_on_before_hit(enemy,hit,0,flags);assert(flags.ret_value==false)
    flags={ret_value=true};callbacks.actor_on_before_hit({draftsman=enemy,type=hit_type},0,flags);assert(flags.ret_value)
    flags={ret_value=true};callbacks.npc_on_before_hit(friend,{draftsman=enemy},0,flags);assert(flags.ret_value)
end
local original_name=o.object_name;o.object_name="recycled_id";local flags={ret_value=true}
callbacks.actor_on_before_hit({draftsman=o},0,flags);assert(flags.ret_value,"Recycled ID granted invulnerability");o.object_name=original_name

reset();r,o,b,h=active({use_rockets="true",engine_sound="false"})
assert(h.sound==false);enemy=npc(101,2,v(250,0,0));neutral=npc(103,1,v(1000,0,1000))
advance(250);assert(h.m_use_rocket_on_attack,"Safe native rocket fire was blocked")
neutral.p=v(245,0,0);advance(250);assert(not h.m_use_rocket_on_attack and h.m_use_mgun_on_attack)
neutral.p=v(1000,0,1000);sections.mod_rocket.blast_r=nil;advance(250);assert(not h.m_use_rocket_on_attack)
sections.mod_rocket.blast_r="10";advance(250);assert(h.m_use_rocket_on_attack)

-- Save exact remaining milliseconds (Lua double, not a 32-bit packet field).
-- F9 restores both health and timer, with no time spent on loading screens.
reset();r,o,b,h=active({duration_seconds="120"});advance(9000)
h.hp=0.37;o.on_hit(o.hit_binder);local p=packet();b:save(p);callbacks.save_state(saved)
local snapshot=copy(saved);local saved_left=r.remaining_ms;local saved_id=r.id;assert(saved_left==111000)
advance(30000);assert(r.remaining_ms==81000)
callbacks.actor_on_net_destroy();now=now+600000;saved=copy(snapshot);r=data()["1"]
descriptor.on_game_load();h.hp=1;b:load(p);advance(250)
assert(r.id==saved_id and r.remaining_ms==saved_left and math.abs(h.hp-0.37)<1e-6)
advance(1000);assert(r.remaining_ms==saved_left-1000 and math.abs(h.hp-0.37)<1e-6)
paused=true;advance(10000);paused=false;advance(250);assert(r.remaining_ms==saved_left-1000)
precache=5;advance(10000);precache=0;advance(250);assert(r.remaining_ms==saved_left-1000)

-- Level changes retire old offline servers only after unloading, preserve
-- damage/model/options/timer and never spawn in underground or covered areas.
local left=r.remaining_ms;local count=creates;descriptor.on_level_changing()
assert(not h.enemy and servers[r.id].offline);level_name="another_surface";descriptor.on_game_load();advance(250)
assert(creates==count,"Duplicated old helicopter before its client unloaded")
clients[r.id]=nil;level_weathers.bLevelUnderground=true;advance(250)
assert(r.stage=="waiting" and r.remaining_ms==left and not next(servers))
advance(100000);assert(r.remaining_ms==left and creates==count)
level_weathers.bLevelUnderground=false;advance(5250);assert(r.id~=saved_id and creates==count+1)
o,b,h=client_for(r);advance(250);assert(math.abs(h.hp-0.37)<1e-6 and r.remaining_ms==left)

-- Expiry disables all weapons immediately and begins actual flight. Exactly
-- one completion tip, no Die/explosion, no releasing a live binder in update.
reset();r,o,b,h=active({duration_seconds="10"});enemy=npc(101,2,v(250,0,0));advance(9750)
assert(r.stage=="active" and h.enemy);sky_blocked=true;advance(250)
assert(r.stage=="leaving" and not h.enemy and not h.m_use_mgun_on_attack and not h.m_use_rocket_on_attack)
assert(#tips==1 and tips[1]=="Вертолёт закончил патрулирование" and h.speed==40 and h.destination and not h.died)
assert(h.destination.y==o.p.y+30,"Indoor actor prevented helicopter departure")
snapshot=copy(saved);p=packet();b:save(p);descriptor.on_game_load();saved=copy(snapshot);r=data()["1"];b:load(p);advance(250)
assert(#tips==1 and r.stage=="leaving");advance(20000);assert(not clients[r.id] and r.stage=="retired" and not h.died)
advance(60000);assert(not next(data()),"Retired slots never became reusable")

reset();r,o,b,h=active({duration_seconds="99999999",follow_distance="300",helicopter_sections="mod_heli_b"})
assert(r.remaining_ms==99999999000 and servers[r.id].packet_data.startup_animation=="rotor")
callbacks.save_state(saved);snapshot=copy(saved);descriptor.on_game_load();saved=copy(snapshot);r=data()["1"];advance(250);advance(1000)
assert(r.remaining_ms==99999998000,"Long timer overflowed across save/load")

reset();r,o,b,h=active({duration_seconds="10",follow_distance="1000"});advance(10000)
assert(r.stage=="leaving" and h.speed==40 and h.destination.y>o.p.y,
    "Long follow distance prevented outward departure")

reset();r,o,b,h=active();h.hp=0;o.on_hit(o.hit_binder);advance(250)
assert(r.dead and h.m_dead and #tips==0);advance(60000);advance(60000);assert(not next(data()))

-- Saved pre-1.3.2 full-health helicopters keep their existing HP. The new
-- default applies to newly accepted effects, never by healing/halving loads.
reset();r=schedule();r.health=1;advance(250);o,b,h=client_for(r);advance(250)
assert(h.hp==1,"Existing full-health save was changed by the new default")
local old_packet=packet();b:save(old_packet);local old_state=copy(saved)
descriptor.on_game_load();saved=copy(old_state);b:load(old_packet);advance(250)
assert(h.hp==1,"F9 changed old saved health to the new 50% default")
print("Helicopter companion passed: parameters/live models, native spawn transaction, moving follow point, hostility/ceasefire/LOS, selective actor/NPC/mutant hit veto, rocket exclusion, pause/precache, F9 health/timer rollback, cross-level/underground migration, 99,999,999s duration and once-only departure notice.")
