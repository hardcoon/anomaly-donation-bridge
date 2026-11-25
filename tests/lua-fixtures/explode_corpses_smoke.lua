local core_root,_,effect_root=assert(arg[1]),assert(arg[2]),assert(arg[3])
local ID="hardcoon.adb.explode_corpses"
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_explode_corpses"
local SAVE="adb_explode_corpses_v1"
local SEP=package.config:sub(1,1)
local function load_module(root,name)
    local chunk=assert(loadfile(root..SEP..name..".script"))
    local environment=setmetatable({},{__index=_G});setfenv(chunk,environment);chunk();_G[name]=environment
    return environment
end
local function clone(value)
    if type(value)~="table" then return value end
    local result={};for k,v in pairs(value) do result[k]=clone(v) end;return result
end
local Vec={};Vec.__index=Vec
function Vec:set(x,y,z) self.x,self.y,self.z=x,y,z;return self end
function Vec:distance_to_sqr(p) return (self.x-p.x)^2+(self.y-p.y)^2+(self.z-p.z)^2 end
local function v(x,y,z) return setmetatable({x=x or 0,y=y or 0,z=z or 0},Vec) end
function vector() return v() end

local world,clients,events,callbacks,data,blasts,hits,tips,logs,release_calls
local now,frame,paused,menu,precache,here,next_id,command_id,frame_releases,maximum_releases
local old_config,helper_class,fail_spawn,refuse_release,refuse_offline,offline_ids,fail_explode
local handler,effect,loot_calls,pickup_flags
local native_queue,native_requests,inside_stock_cleanup,physics_ready
local native_death_errors,invalid_alive_calls
local missing_particle,missing_sound,fx,sounds,stock_bodies,only_native,archive_only
local particle_data,virtual_files,copied,deleted
local real_io_open=io.open
local function u32(n)
    return string.char(n%256,math.floor(n/256)%256,math.floor(n/65536)%256,math.floor(n/16777216)%256)
end
local function chunk(id,data) return u32(id)..u32(#data)..data end
local function particle_library(name) return chunk(3,chunk(0,chunk(2,name.."\0"))) end
io.open=function(path,mode)
    if archive_only and path:sub(1,8)=="fixture/" then
        local data=virtual_files[path];if not data then return end
        return {read=function(_,limit) return data:sub(1,limit) end,close=function() end}
    end
    return real_io_open(path,mode)
end
local Server={};Server.__index=Server
local Client={};Client.__index=Client
-- Native game_object deliberately has no luabind __eq overload. Tables used
-- to silently accept object==actor and hid the production selection failure.
Client.__eq=function() error("! No such operator [__eq] defined in class [game_object]") end
function Server:name() return self.name_value end
function Server:section_name() return self.section_value end
function Server:alive() return self.server_alive~=nil and self.server_alive or self.alive_value end
function Server:can_switch_online() return self.allow_online~=false end
function Server:can_switch_offline() return self.allow_offline~=false end
function Server:use_ai_locations(value) self.use_ai=value end
function Server:switch_offline()
    assert(self.allow_online==false and self.allow_offline==true)
    local id,name=self.id,self.name_value
    if refuse_offline==id then return end
    events[#events+1]=function()
        local s=world[id]
        if s and s.name_value==name then
            s.server_alive=s.alive_value -- stock net_Export/offline snapshot
            s.online=false;clients[id]=nil;db.storage[id]=nil
            for child_id,item in pairs(world) do
                if item.parent_id==id and item.client_only then world[child_id],clients[child_id]=nil,nil end
            end
        end
    end
end
function Client:id() return self.id_value end
function Client:name() return world[self.id_value].name_value end
function Client:section() return world[self.id_value].section_value end
function Client:alive()
    if self.kind~="actor" and self.kind~="npc" and self.kind~="mutant" then
        invalid_alive_calls=invalid_alive_calls+1 -- native Alive() logs and returns false
        return false
    end
    return world[self.id_value].alive_value
end
function Client:position()
    if world[self.id_value].position_error then error("Native position query failed") end
    local p=world[self.id_value].position
    return v(p.x,p.y,p.z)
end
function Client:level_vertex_id() return 5 end
function Client:game_vertex_id() return 9 end
function Client:is_talking() return false end
function Client:parent() local s=world[self.id_value];return s.parent_id and clients[s.parent_id] end
function Client:clsid() return world[self.id_value].client_only and clsid.obj_bolt or clsid.item end
function Client:force_set_position(p,force)
    assert(force==true and self:section()=="adb_corpse_blast")
    world[self.id_value].position={x=p.x,y=p.y,z=p.z}
end
function Client:get_physics_shell()
    if not physics_ready then return end
    return {get_elements_number=function() return 1 end}
end
function Client:list_bones() return {[0]="root",[1]="bip01_spine",[2]="bip01_head"} end
local function emit(name,...)
    for _,callback in ipairs(callbacks[name] or {}) do callback(...) end
end
function Client:explode(delay)
    error("New ruptures must use meatgrinder particles, never a grenade")
end
hit=setmetatable({explosion=5},{__call=function() return {} end})
function Client:hit(h)
    assert(h.type==hit.explosion and h.weapon_id==65535 and h.bone=="bip01_spine")
    local id,name,source_id=self:id(),self:name(),h.draftsman:id()
    assert(self.kind=="actor" or source_id~=db.actor:id(),"NPC death was attributed to the hero")
    events[#events+1]=function() -- GE_HIT is a queued engine event, not synchronous
        local target=world[id]
        if not target or target.name_value~=name then return end
        local flags={ret_value=true}
        emit(target.kind=="actor" and "actor_on_before_hit" or target.kind=="npc" and "npc_on_before_hit" or "monster_on_before_hit",
            unpack(target.kind=="actor" and {h,0,flags} or {clients[id],h,0,flags}))
        hits[#hits+1]={target=id,source=source_id,allowed=flags.ret_value,power=h.power,impulse=h.impulse}
        if not flags.ret_value then return end
        target.health_value=(target.health_value or 1)-h.power*(target.damage_factor or 1)
        if target.health_value>0 then return end
        target.alive_value=false;target.death_pose_pending=true
        local due=frame+3
        local function death_event() -- GE_DIE can arrive after source-body cleanup
            local victim=world[id]
            if not victim or victim.name_value~=name then return end
            if frame<due then events[#events+1]=death_event;return end
            if not world[source_id] then
                native_death_errors[#native_death_errors+1]="SV: src killer not exist"
                return -- zero HP, but no Die()/ragdoll: the production screenshot
            end
            victim.death_pose_pending=false;victim.death_registered=true
        end
        events[#events+1]=death_event
    end
end
local function install_client(s)
    local o=setmetatable({id_value=s.id,kind=s.kind},Client);clients[s.id]=o
    if s.kind=="npc" or s.kind=="mutant" then db.storage[s.id]={object=o} end
    return o
end
local function add_object(id,kind,x,attributes)
    local s=setmetatable({id=id,kind=kind,name_value="object_"..id,
        section_value=kind=="helper" and "adb_corpse_blast" or kind=="item" and "item_test" or kind=="npc" and "test_npc" or "test_mutant",
        alive_value=kind=="actor",online=true,level=here,position={x=x or 0,y=0,z=0}},Server)
    for k,value in pairs(attributes or {}) do s[k]=value end
    world[id]=s
    return install_client(s),s
end
local function corpse(id,x,kind,attributes) return add_object(id,kind or "npc",x,attributes) end
local function item(id,parent,attributes)
    local o,s=add_object(id,"item",0,attributes);s.parent_id=parent;return o,s
end
local sim={}
function sim:object(id) local s=world[id];return s and not s.client_only and s end
function sim:get_children(s)
    local ids={};for id,item in pairs(world) do if item.parent_id==s.id then ids[#ids+1]=id end end
    table.sort(ids);local index=0
    return function() index=index+1;return ids[index] end
end
function sim:set_switch_online(id,allow) world[id].allow_online=allow end
function sim:set_switch_offline(id,allow) world[id].allow_offline=allow end
function sim:release(s,recursive)
    assert(recursive==true and world[s.id]==s,"Released a stale server object")
    if s.kind=="npc" or s.kind=="mutant" then
        assert(inside_stock_cleanup,"Effect bypassed the stock corpse cleaner")
        assert(s.online==false and not clients[s.id] and not db.storage[s.id],"Body released before binder destruction")
    elseif s.kind=="item" then
        error("Effect attempted individual inventory deletion instead of stock whole-body cleanup")
    end
    release_calls[#release_calls+1]={id=s.id,at=now}
    if refuse_release==s.id then return end
    local id,name=s.id,s.name_value
    events[#events+1]=function()
        local function destroy_tree(parent)
            local ids={}
            for child_id,child in pairs(world) do if child.parent_id==parent then ids[#ids+1]=child_id end end
            for _,child_id in ipairs(ids) do destroy_tree(child_id) end
            world[parent],clients[parent],db.storage[parent]=nil,nil,nil
        end
        if world[id] and world[id].name_value==name then destroy_tree(id) end
    end
end
safe_release_manager={release=function(s)
    assert((s.kind=="npc" or s.kind=="mutant") and not s.alive_value,"Stock cleaner received a living/unselected entity")
    native_requests[#native_requests+1]=s.id;native_queue[s.id]=true
end,execute_destroy_callback=function()
    for id in pairs(native_queue) do
        local s=world[id]
        if s then
            if s.online then
                sim:set_switch_online(id,false);sim:set_switch_offline(id,true);s:switch_offline()
            elseif not db.storage[id] then
                inside_stock_cleanup=true
                local ok,why=pcall(sim.release,sim,s,true)
                inside_stock_cleanup=false
                if not ok then error(why) end
                native_queue[id]=nil
            end
        end
    end
end}
function alife() return sim end
function alife_record(s,keep) assert(s.online==false and keep==false) end
function alife_on_limit() return false end
function alife_create(section,p,lvid,gvid)
    error("Corpse rupture must not spawn ALife helpers")
end
function device()
    return {frame=frame,precache_frame=precache,f_time_delta=1/60,is_paused=function() return paused end}
end
function time_global() return now end
function app_ready() return true end
function RegisterScriptCallback(name,callback)
    callbacks[name]=callbacks[name] or {};callbacks[name][#callbacks[name]+1]=callback
end
function get_object_story_id(id) return world[id] and world[id].story end
function se_load_var(id,_,key) return world[id] and world[id][key] end
function get_object_squad(object) return world[object:id()].squad end
function IsStalker(o) return o and (o.kind=="npc" or o.kind=="actor") end
function IsMonster(o) return o and o.kind=="mutant" end
function IsItem(kind,section) assert(kind=="quest");return section=="item_quest" or section=="quest_bolt" end
function printf(format,...) logs[#logs+1]=string.format(format,...) end
clsid={obj_bolt=51,item=52}
function system_ini()
    return {section_exist=function(_,section) return not old_config and section=="adb_corpse_blast" end,
        line_exist=function(_,section,key) return section=="adb_corpse_blast" and key=="class" end,
        r_string=function() return helper_class end}
end
function getFS()
    return {exist=function(_,alias,path)
        if archive_only and alias=="$game_data$" and path=="particles.xr" then return {size_real=#particle_data} end
        if path==nil then return virtual_files[alias] and {size_real=#virtual_files[alias]} end
        if alias=="$game_particles$" then return not archive_only and not missing_particle and path=="anomaly2\\body_tear_00.pe" end
        if alias=="$game_sounds$" then return not missing_sound and path=="anomaly\\anomaly_body_tear_1.ogg" end
        return false
    end,update_path=function(_,alias,path) return "fixture/"..path end,
    file_copy=function(_,source,target)
        assert(source=="fixture/particles.xr" and not virtual_files[target]);copied=copied+1
        virtual_files[target]=particle_data
    end,file_delete=function(_,path) assert(virtual_files[path]);virtual_files[path]=nil;deleted=deleted+1 end,
    r_open=function() error("Unsafe native particle reader") end,r_close=function() error("Unsafe native particle reader close") end}
end
particles_object=function(name)
    assert(not missing_particle and name=="anomaly2\\body_tear_00","Unverified/non-controller rupture particle")
    if fail_spawn then error("Particle construction failed") end
    local result={}
    function result:set_direction(p) assert(p.x==0 and p.y==1 and p.z==0) end
    function result:play_at_pos(p,looped)
        assert(not looped)
        self.active=true;fx[#fx+1]=self
        blasts[#blasts+1]={at=now,position={x=p.x,y=p.y,z=p.z}}
        if fail_explode then error("Particle submitted then failed") end
    end
    function result:stop() self.active=false end
    return result
end
sound_object=function(name)
    assert(name=="anomaly\\anomaly_body_tear_1","Non-controller tearing sound")
    local result={}
    function result:play_at_pos(actor,p,delay,flags)
        assert(actor==db.actor and delay==0 and flags==0)
        self.active=true;sounds[#sounds+1]={name=name,x=p.x,y=p.y,z=p.z}
    end
    function result:stop() self.active=false end
    return result
end
level={object_by_id=function(id)
    local s=world[id]
    return s and s.level==here and not offline_ids[id] and clients[id] or nil
end,vertex_position=function(id) assert(id==5);return v() end,
name=function() return here end,present=function() return true end,
iterate_nearest=function(p,radius,callback)
    for id,object in pairs(clients) do
        if world[id].level==here and not offline_ids[id] and p:distance_to_sqr(object:position())<=radius*radius then
            if callback(object) then break end
        end
    end
end}
alife_storage_manager={get_state=function() return data end}
release_body_manager={get_release_body_manager=function()
    return {release_objects_table=stock_bodies,can_release=function(_,o) return not world[o:id()].known_info end}
end}
utils_item={has_quest_item=function(o) return world[o:id()].quest==true end}
death_manager={create_release_item=function(o)
    local s=world[o:id()];assert(s.kind=="npc","Butchered mutant")
    if s.death_dropped then return end;s.death_dropped=true;loot_calls=loot_calls+1
    if s.lazy_quest then s.quest=true end
    if s.lazy_loot then
        events[#events+1]=function() if world[s.id]==s then item(s.id+10000,s.id) end end
    end
end}
ActorMenu={get_actor_menu=function() return {IsShown=function() return menu end} end}
actor_menu={is_hud_free=function() return not menu end}
news_manager={send_tip=function(_,message) tips[#tips+1]=message end}

local function boot()
    anomaly_donation_bridge_common=load_module(core_root,"anomaly_donation_bridge_common")
    anomaly_donation_bridge_registry=load_module(core_root,"anomaly_donation_bridge_registry")
    effect=load_module(effect_root,MODULE);effect.on_game_start()
    handler=assert(anomaly_donation_bridge_registry.get(ID))
end
local function reset()
    world,clients,events,callbacks,data,blasts,hits,tips,logs,release_calls={},{},{},{},{},{},{},{},{},{}
    now,frame,paused,menu,precache,here,next_id,command_id=1000,0,false,false,0,"l01_escape",50000,0
    frame_releases,maximum_releases,loot_calls=0,0,0
    native_queue,native_requests,inside_stock_cleanup,physics_ready={},{},false,true
    native_death_errors,invalid_alive_calls={},0
    missing_particle,missing_sound,fx,sounds,stock_bodies,only_native,archive_only=false,false,{},{},{},false,false
    particle_data,virtual_files,copied,deleted=particle_library("anomaly2\\body_tear_00"),{},0,0
    old_config,helper_class,fail_spawn,refuse_release,refuse_offline,offline_ids,fail_explode=false,"G_RGD5_S",false,nil,nil,{},false
    db={storage={}};db.actor=add_object(0,"actor",0)
    axr_companions={non_task_companions={},companion_squads={}}
    boot();assert(handler.is_supported())
end
local function dispatch(params,key)
    command_id=command_id+1
    return anomaly_donation_bridge_registry.dispatch(ID,{actor=db.actor,now=now,command={command_id=key or "blast_"..command_id}},params or {})
end
local function tick(dt,apply)
    now=now+(dt or 16);frame=frame+1
    if apply~=false then local pending=events;events={};for _,event in ipairs(pending) do event() end end
    frame_releases=0;emit("actor_on_update")
end
local function run(ms)
    local until_time=now+ms
    while now<until_time do tick() end
end
local function save()
    assert(next(native_queue)==nil,"An owned ID-only stock cleanup request escaped across F9")
    local saved={};emit("save_state",saved)
    return {data=clone(saved),world=clone(world),actor=0}
end
local function load(saved,fresh_vm)
    emit("on_before_load_input");emit("actor_on_net_destroy")
    data=clone(saved.data);world=clone(saved.world);clients={};events={};db={storage={}};now=100;frame=0
    -- Deliberately retain the stock module's private queue like unpatched
    -- Anomaly. The effect must never leave its own pending IDs there at F5.
    for _,s in pairs(world) do setmetatable(s,Server);if s.online then install_client(s) end end
    db.actor=clients[0]
    if fresh_vm then callbacks={};boot() else anomaly_donation_bridge_registry.on_game_load() end
end
local function snapshot() return data[SAVE] end
local function rate_check()
    for i=2,#blasts do assert(blasts[i].at-blasts[i-1].at>=334,"Shared blast rate exceeded") end
    for i=4,#blasts do assert(blasts[i].at-blasts[i-3].at>=1000,"More than three blasts per second") end
end

-- Bounds, selectors, source defaults and prompt non-blocking ACK.
reset();corpse(10,1);corpse(11,2,"mutant");corpse(12,100.01);corpse(13,2,"npc",{alive_value=true})
local status,detail=dispatch()
assert(status=="executed" and detail=="corpse_blast_scheduled_2",detail)
assert(#blasts==0 and #snapshot().jobs==1,"Synchronous blast or missing save plan")
assert(anomaly_donation_bridge_registry.busy_reason()==nil,"Background blast blocked other commands")
local ok=anomaly_donation_bridge_registry.register("test.fixture.other",{execute=function() return "executed","other_ok" end})
assert(ok and anomaly_donation_bridge_registry.dispatch("test.fixture.other",{}, {})=="executed")
local first_count=#snapshot().jobs
assert(dispatch({},"blast_1")=="executed" and #snapshot().jobs==first_count,"Duplicate command scheduled another series")
run(4000);assert(not world[10] and not world[11] and world[12] and world[13],"Wrong corpse selection")
assert(#blasts==2 and #snapshot().jobs==0);rate_check()
assert(dispatch({},"blast_1")=="executed" and #snapshot().jobs==0,"Finished command repeated")
for _,bad in ipairs({{radius_meters="0"},{radius_meters="1000"},{radius_meters="2.5"},{corpse_type="other"},
    {damage_actor="yes"},{damage_npcs="yes"},{unknown="x"}}) do assert(dispatch(bad)=="rejected") end
for _,kind in ipairs({"npc","mutant","both"}) do
    reset();corpse(10,1);corpse(11,2,"mutant")
    assert(dispatch({corpse_type=kind})=="executed");run(2500)
    assert((world[10]==nil)==(kind~="mutant") and (world[11]==nil)==(kind~="npc"),"Corpse type filter")
end
reset();corpse(10,999);corpse(11,999.1);assert(dispatch({radius_meters="999"})=="executed");run(2000)
assert(not world[10] and world[11],"Inclusive radius limit")
reset();corpse(10,1);offline_ids[10]=true
local status,detail=dispatch();assert(status=="executed" and detail=="corpse_blast_no_eligible_bodies")
offline_ids[10]=nil;run(2000);assert(world[10] and #blasts==0,"Unloaded bodies entered the activation snapshot")
reset();corpse(10,1,"npc",{position_error=true})
local status,detail=dispatch()
assert(status=="rejected" and detail=="corpse_blast_target_scan_failed","Failed native scan was reported as successful/empty")
assert(#snapshot().jobs==0 and #blasts==0,"Failed scan scheduled native work")
world[10].position_error=nil;assert(dispatch()=="executed");run(2000)
assert(#blasts==1 and not world[10],"Corrected scan failed to select a corpse")

-- Existing stock protections, lazy quest loot, mutation budget and real bolt exception.
reset()
for i,flags in ipairs({{story="quest"},{quest=true},{dont_release=true},{known_info=true}}) do corpse(i+10,i, "npc",flags) end
corpse(20,8);axr_companions.non_task_companions[20]={}
corpse(21,9);item(201,21,{section_value="item_quest"})
corpse(22,10,"mutant");item(202,22,{story="quest_item"})
assert(dispatch()=="executed");run(5000)
assert(#blasts==0 and world[201] and world[202] and #snapshot().jobs==0,"Protected body/item was destroyed")
reset();corpse(10,1,"npc",{lazy_loot=true});corpse(11,2,"mutant")
for id=100,299 do item(id,id%2==0 and 10 or 11) end
item(300,10,{client_only=true,section_value="bolt_bullet"})
assert(dispatch()=="executed");run(6000)
assert(not world[10] and not world[11] and not world[300] and #blasts==2 and #native_requests==2,"Stock whole-body inventory cleanup")
assert(loot_calls==0,"Effect materialized random loot before destroying the body")
reset();corpse(10,1);item(100,10);item(101,100);item(102,100);item(103,10)
assert(dispatch()=="executed");run(3000)
assert(not world[10] and not world[100] and not world[101] and #blasts==1,"Nested ordinary inventory was not removed")
reset();corpse(10,1);item(100,10);item(101,100,{section_value="item_quest"});item(102,10)
assert(dispatch()=="executed");run(3000)
assert(world[10] and world[100] and world[101] and world[102] and #blasts==0,"Nested quest item was destroyed")
reset();corpse(10,1);item(100,10);item(101,100)
assert(dispatch()=="executed")
for i=1,200 do
    local r=snapshot().jobs[1] and snapshot().jobs[1].targets[1]
    if r and r.phase=="blast" then break end;tick()
end
world[100].parent_id=0
run(2500);assert(not world[10] and world[100] and world[101],"Destroyed contents of a container moved to the player")
reset();corpse(10,1);item(100,10);for i=101,140 do item(i,100) end
assert(dispatch()=="executed")
for i=1,300 do
    local r=snapshot().jobs[1] and snapshot().jobs[1].targets[1]
    if r and r.phase=="release_wait" and r.release_sent and #events>0 then break end;tick()
end
local nested_saved=save();load(nested_saved,true);run(4000)
assert(not world[10] and not world[100] and #blasts==1,"F9 failed nested pending destruction")

-- Four independent damage combinations; identity-bound source filtering.
reset();corpse(10,1);corpse(20,2,"npc",{alive_value=true});item(100,10)
assert(dispatch()=="executed");run(2500)
assert(not world[10] and not world[20].alive_value,"Fixture did not destroy source/kill the neighbour")
assert(#native_death_errors==0 and world[20].death_registered and not world[20].death_pose_pending,
    "NPC stayed standing at zero HP: corpse cleanup removed the killer before GE_DIE")
assert(invalid_alive_calls==0,"Alive() was called for an item/non-creature")
assert(#tips==0,"Corpse completion statistics leaked into PDA notifications")
reset();corpse(10,1);corpse(20,2,"npc",{alive_value=true,damage_factor=0})
assert(dispatch()=="executed");run(2500)
assert(world[20].alive_value and not world[20].death_registered,"Damage bypassed native protections")
for _,actor_damage in ipairs({false,true}) do for _,npc_damage in ipairs({false,true}) do
    reset();world[0].position={x=0,y=0,z=0};corpse(10,1)
    corpse(20,2,"npc",{alive_value=true});corpse(21,2,"mutant",{alive_value=true})
    assert(dispatch({damage_actor=tostring(actor_damage),damage_npcs=tostring(npc_damage)})=="executed")
    run(2000)
    local seen={};for _,hit in ipairs(hits) do seen[hit.target]=hit.allowed end
    assert((seen[0]==true)==actor_damage and (seen[20]==true)==npc_damage and (seen[21]==true)==npc_damage,"Damage checkboxes collided")
    assert(#blasts==1,"Newly killed creatures entered the same series")
    local foreign=clients[20] or clients[21];local h={draftsman=foreign,power=.5,impulse=4};local flags={ret_value=true}
    emit("actor_on_before_hit",h,0,flags);assert(flags.ret_value and h.power==.5,"Foreign hit was suppressed")
end end

-- Concurrent jobs share one pace; stalls cannot cause a catch-up burst.
reset();for i=10,17 do corpse(i,i) end
assert(dispatch({radius_meters="12"})=="executed")
assert(dispatch({radius_meters="999"})=="executed" and #snapshot().jobs==2)
run(1000);local before=#blasts;tick(3000)
assert(#blasts-before<=1,"Frame stall produced a burst")
run(7000);assert(#blasts==8 and #snapshot().jobs==0);rate_check()
reset()
for i=1,32 do corpse(100+i,1);assert(dispatch({},"limit_"..i)=="executed") end
corpse(200,1);assert(dispatch({},"over_limit")=="deferred" and #snapshot().jobs==32,"Unbounded background job queue")
assert(dispatch({},"limit_1")=="executed" and #snapshot().jobs==32,"Duplicate was blocked by queue limit")

-- Save at every asynchronous stage. Load drops unsaved engine requests.
for _,phase in ipairs({"queued","checking","blast","release_wait"}) do
    for _,fresh_vm in ipairs({false,true}) do
        reset();corpse(10,1,"npc",{lazy_loot=true});corpse(11,2,"mutant")
        for i=100,130 do item(i,10) end
        assert(dispatch()=="executed")
        local found=false
        for i=1,400 do
            for _,r in ipairs(snapshot().jobs[1] and snapshot().jobs[1].targets or {}) do
                if r.id==10 and r.phase==phase then found=true end
            end
            if found then break end;tick()
        end
        assert(found,"Test never reached phase "..phase)
        local saved=save();local prefix=#blasts;local dropped=#events
        load(saved,fresh_vm);run(8000)
        assert(not world[10] and not world[11] and #snapshot().jobs==0,"F9 lost progress in "..phase)
        assert(#blasts==2,"F9 repeated/skipped a blast in "..phase.." prefix="..prefix.." events="..dropped)
    end
end

-- Preserve a partially consumed cooldown and actual completed prefix.
reset();for i=10,16 do corpse(i,i) end;assert(dispatch()=="executed")
while #blasts<2 do tick() end
tick(100);local remaining=snapshot().cooldown_ms;assert(remaining>0 and remaining<334)
local saved=save();local prefix=#blasts;load(saved);local restored_at=now
tick();assert(#blasts==prefix,"Loaded cooldown fired in first ready frame")
while #blasts==prefix do tick() end
assert(now-restored_at>=remaining,"F9 erased remaining cooldown")
run(6000);assert(#blasts==7,"F9 reset the series instead of continuing it")

-- Pause menus and loading time, temporary offline targets, other levels and old saves.
reset();corpse(10,1);assert(dispatch()=="executed");run(32)
menu=true;run(5000);assert(#blasts==0);menu=false
paused=true;run(5000);assert(#blasts==0);paused=false
precache=12;run(5000);assert(#blasts==0);precache=0;run(2000);assert(#blasts==1)
reset();corpse(10,1);physics_ready=false;assert(dispatch()=="executed");run(2000)
assert(#blasts==1 and not world[10],"Meatgrinder rupture incorrectly waited for grenade physics")
reset();corpse(10,1);assert(dispatch()=="executed");offline_ids[10]=true;run(6000)
assert(#blasts==0 and #snapshot().jobs==1);offline_ids[10]=nil;run(2500);assert(#blasts==1)
reset();corpse(10,1);assert(dispatch()=="executed");run(32)
anomaly_donation_bridge_registry.on_level_changing();here="l02_garbage";anomaly_donation_bridge_registry.on_game_load()
run(3000);assert(#blasts==0 and #snapshot().jobs==1,"Different level consumed old targets")
here="l01_escape";anomaly_donation_bridge_registry.on_game_load();run(2500);assert(#blasts==1)
reset();local empty=save();corpse(10,1);assert(dispatch()=="executed");load(empty);run(2500)
assert(#blasts==0 and #snapshot().jobs==0,"An old save inherited the previous world's queue")

-- Reused IDs, resurrected bodies, spawn failures and uncertain native explosions.
reset();corpse(10,1);assert(dispatch()=="executed")
world[10].name_value="different_npc";world[10].alive_value=true
run(2000);assert(world[10] and #blasts==0)
reset();corpse(10,1);assert(dispatch()=="executed");world[10].alive_value=true;run(2000)
assert(world[10] and #blasts==0)
reset();corpse(10,1);item(100,10);fail_spawn=true;assert(dispatch()=="executed");run(2000)
assert(world[10] and world[100] and #blasts==0,"Spawn failure destroyed inventory")
reset();corpse(10,1);refuse_release=10;assert(dispatch()=="executed");run(3000)
local failed_saved=save();local prefix=#blasts;load(failed_saved);refuse_release=nil;run(4000)
assert(#blasts==prefix and not world[10],"Pending body deletion replayed the explosion")
reset();corpse(10,1);fail_explode=true;assert(dispatch()=="executed");run(2000)
local uncertain=save();load(uncertain);run(2000);assert(#blasts==1,"Uncertain explosion was retried")
reset();corpse(10,1);refuse_offline=10;assert(dispatch()=="executed");run(7000)
assert(world[10] and world[10].allow_online==true and #snapshot().jobs==0,"Failed cleanup left forced offline flags")

-- Invalid saved data is retained; missing native resources reject safely.
reset();corpse(10,1);assert(dispatch()=="executed");local broken=save()
broken.data[SAVE].jobs[1].targets[1].phase="bad";load(broken);run(1000)
assert(#blasts==0 and data[SAVE].jobs[1].targets[1].phase=="bad" and not handler.is_supported(),"Invalid save was silently reset")
reset();old_config=true;corpse(10,1);assert(dispatch()=="executed");run(2000)
assert(#blasts==1,"Rupture depends on a private grenade section")
reset();missing_particle=true;boot();assert(dispatch()=="rejected");assert(#blasts==0)

-- A client-side dead body must not be hidden by stale online CSE health.
reset();corpse(10,1,"npc",{server_alive=true});assert(dispatch()=="executed");run(2000)
assert(not world[10] and #blasts==1,"Stale server health suppressed a loaded dead client")
-- Same stock body registry and native query used by the cleaner; no offline bodies.
reset();corpse(10,1);db.storage[10]=nil;stock_bodies[10]=true
local native_scan=level.iterate_nearest;level.iterate_nearest=nil
assert(dispatch()=="executed");run(2000);assert(not world[10] and #blasts==1,"Stock corpse list ignored")
level.iterate_nearest=native_scan
reset();corpse(10,1,"mutant");db.storage[10]=nil
assert(dispatch()=="executed");run(2000);assert(not world[10] and #blasts==1,"Native loaded corpse ignored")
reset();corpse(10,1,"mutant");offline_ids[10]=true;stock_bodies[10]=true;db.storage[10]=nil
local status,detail=dispatch();assert(status=="executed" and detail=="corpse_blast_no_eligible_bodies")
-- Missing sound is cosmetic, and silent startup must never suppress the visual.
reset();missing_sound=true;corpse(10,1);assert(dispatch()=="executed");run(2000)
assert(#blasts==1 and #sounds==0 and not world[10])
-- Packed particles.xr is verified through VFS copy; never construct an
-- unverified particle or call the unsafe IReader close binding.
reset();archive_only=true;boot();corpse(10,1);corpse(11,2)
assert(dispatch()=="executed");run(3000)
assert(#blasts==2 and copied==1 and deleted==1 and next(virtual_files)==nil,"Archived particle probe leaked/repeated")
reset();archive_only=true;particle_data=u32(3)..u32(10000).."bad";boot()
assert(dispatch()=="rejected" and #blasts==0 and copied==1 and deleted==1,"Malformed library reached native constructor")
-- Retire an unfired local.2 grenade while continuing the original saved series.
for _,phase in ipairs({"helper_wait","blast"}) do
    reset();corpse(10,1);assert(dispatch()=="executed")
    local saved=save();local _,old=add_object(50001,"helper",1)
    saved.world=clone(world)
    local r=saved.data[SAVE].jobs[1].targets[1]
    r.phase=phase;r.wait_ms=5000;r.helper_id=old.id;r.helper_name=old:name()
    saved.data[SAVE].helpers[tostring(old.id)]={id=old.id,name=old:name(),section="adb_corpse_blast",level=here,
        damage_actor=false,damage_npcs=true,fired=false,remaining_ms=10000}
    load(saved,true);run(3000)
    assert(#blasts==1 and not world[10] and not world[50001],"local.2 helper migration lost/repeated a body")
end

print("PASS corpse ruptures: controller meatgrinder VFX/sound, stock/native corpse selection, 3/s, stock whole-body cleanup, damage options, F5/F9, local.2 migration")
