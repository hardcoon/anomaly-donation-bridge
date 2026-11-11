local core,root=assert(arg[1]),assert(arg[3])
local function load_at(path)
    local env=setmetatable({},{__index=_G});local f=assert(loadfile(path));setfenv(f,env);f();return env
end
local native_mt={__eq=function() error("No such operator [__eq] defined in class [game_object]") end}
local function proxy(t) return setmetatable(t,native_mt) end
local definitions={
    ball={class="AMMO",box_size="30"},ap={class="AMMO",box_size="20"},pistol_ammo={class="AMMO",box_size="25"},
    rechambered={class="AMMO",box_size="15"},grenade_ammo={class="AMMO",box_size="1"},
    invalid={class="AMMO",box_size="0"},overflow={class="AMMO",box_size="65536"},
    non_ammo={class="II_FOOD",box_size="20"},excluded={class="AMMO",box_size="50"}
}
local excluded,slots,active,created,announcements={}, {},nil,{},{}
local actor_living,fail_at,throw_at,release_fail=true,nil,nil,false
local spawn_calls=0
local owner=proxy({})
function owner:id() return 0 end
function owner:alive() return actor_living end
function owner:position() return {x=1,y=2,z=3} end
function owner:level_vertex_id() return 20 end
function owner:game_vertex_id() return 30 end
function owner:active_item() return active end
function owner:item_in_slot(slot) assert(slot==2 or slot==3,"Unconfirmed extra pistol slot was probed");return slots[slot] end
db={actor=owner}
function system_ini() return {
    section_exist=function(_,s) return definitions[s]~=nil end,
    line_exist=function(_,s,k) return definitions[s] and definitions[s][k]~=nil end,
    r_string=function(_,s,k) return definitions[s][k] end}
end
anomaly_donation_bridge_catalog={is_available=function(s,kind)
    assert(kind=="item");return definitions[s]~=nil and not excluded[s]
end}
local life={}
function life:create_ammo(section,pos,lv,gv,id,size)
    assert(pos.x==1 and lv==20 and gv==30 and id==0)
    assert(definitions[section].class=="AMMO" and size==tonumber(definitions[section].box_size),"Native ammo class/box size assertion risk")
    spawn_calls=spawn_calls+1
    if spawn_calls==throw_at then error("native spawn exception") end
    if spawn_calls==fail_at then return nil end
    local s={id=100+spawn_calls,section=section,count=size};created[#created+1]=s;return s
end
function alife() return life end
anomaly_donation_bridge_common=load_at(core.."/anomaly_donation_bridge_common.script")
anomaly_donation_bridge_common.game_text_cp1251_lossy=function(s) return s end
anomaly_donation_bridge_common.rollback_server_objects=function(objects)
    if release_fail then return false end
    for _,s in ipairs(objects) do
        for index=#created,1,-1 do if created[index].id==s.id then table.remove(created,index) end end
    end
    return true
end
news_manager={send_tip=function(a,text,_,_,duration)
    assert(a:id()==0 and duration==6000);announcements[#announcements+1]=text
end}
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.equipment_ammo");descriptor=d end}
local module=load_at(root.."/anomaly_donation_bridge_effect_hardcoon_adb_equipment_ammo.script")
module.on_game_start();assert(descriptor.is_supported())
local function weapon(id,types,selected)
    local w={types=types,selected=selected}
    function w:AmmoTypeForEach(fn)
        for index,section in ipairs(self.types) do if fn(index-1,section)==true then break end end
    end
    function w:GetAmmoType() return self.selected end
    local item=proxy({weapon=w,id=function() return id end})
    function item:cast_Weapon() return self.weapon end
    function item:get_ammo_type() return self.weapon.selected end
    return item,w
end
local knife=weapon(9,{},0)
local function reset()
    excluded={};slots={};active=nil;created={};announcements={};actor_living=true
    fail_at=nil;throw_at=nil;release_fail=false;spawn_calls=0
end
local function prepare(params)
    local ctx={actor=owner};local ok,status,why=descriptor.can_execute(ctx,params or {})
    return ctx,ok,status,why
end
local function run(params)
    local ctx,ok,status,why=prepare(params);assert(ok,tostring(status)..":"..tostring(why))
    local state,reason=descriptor.execute(ctx);assert(state=="executed",tostring(state)..":"..tostring(reason));return ctx
end
local function deferred(params,reason)
    local _,ok,state,why=prepare(params)
    assert(not ok and state=="deferred" and why==reason,tostring(state)..":"..tostring(why))
    assert(#created==0)
end

-- Slot checkboxes default on; packs are PER WEAPON, not per cartridge type.
reset();slots[3]=weapon(1,{"ball","ap"},1);slots[2]=weapon(2,{"pistol_ammo"},0)
run({ammo_packs="2"})
assert(#created==4 and created[1].section=="ap" and created[1].count==20 and created[2].section=="ap")
assert(created[3].section=="pistol_ammo" and created[3].count==25 and #announcements==1)

reset();slots[3]=weapon(1,{"ball"},0);slots[2]=weapon(2,{"ball"},0);run()
assert(#created==2,"Two same-caliber weapons must each receive one box")
reset();slots[3]=weapon(1,{"ball"},0);slots[2]=slots[3];run()
assert(#created==1,"Same physical weapon through two slot aliases was counted twice")
reset();slots[3]=weapon(1,{"ball"},0);slots[2]=weapon(2,{"ap"},0);run({primary="false",ammo_packs="3"})
assert(#created==3 and created[1].section=="ap")
reset();slots[3]=knife;slots[2]=weapon(2,{"ball"},0);run();assert(#created==1)
reset();deferred({},"equipment_ammo_waiting_for_equipped_weapon")

-- Hands-only overrides slot selection, supports ANY extra slot without
-- guessing its index, and waits while hands are empty/holding a non-gun.
reset();slots[3]=weapon(1,{"ball"},0)
deferred({hands_only="true"},"equipment_ammo_waiting_for_weapon_in_hands")
active=knife;deferred({hands_only="true"},"equipment_ammo_waiting_for_weapon_in_hands")
active=proxy({id=function() return 90 end,cast_Weapon=function() return nil end})
deferred({hands_only="true"},"equipment_ammo_waiting_for_weapon_in_hands")
active=weapon(91,{"pistol_ammo"},0)
run({hands_only="true",primary="false",secondary="false",ammo_packs="4"})
assert(#created==4 and created[1].section=="pistol_ammo")

-- Use actual upgraded/caliber-swapped weapon vector and zero-based selected
-- index, including the currently active grenade-launcher mode. No hardcoded
-- base section, caliber, ammo quantity or weapon mutation.
reset();local w;slots[3],w=weapon(1,{"rechambered","ap"},0);run();assert(created[1].section=="rechambered")
reset();slots[3]=weapon(1,{"grenade_ammo"},0);run();assert(created[1].count==1)
reset();slots[3]=weapon(1,{"ball","ap"},255);run();assert(created[1].section=="ball")
reset();slots[3]=weapon(1,{"ball","ap"},nil);run();assert(created[1].section=="ball")
reset();excluded.excluded=true;slots[3]=weapon(1,{"invalid","overflow","non_ammo","excluded","ap"},3)
run();assert(#created==1 and created[1].section=="ap")
reset();excluded.ball=true;slots[3]=weapon(1,{"ball"},0);deferred({},"equipment_ammo_waiting_for_equipped_weapon")

-- Re-evaluate after the before-effect save. A holstered/replaced/rechambered
-- weapon cannot receive ammunition selected using stale preflight data.
reset();active=weapon(1,{"ball"},0);local ctx,ok=prepare({hands_only="true"});assert(ok)
active=knife;assert(descriptor.execute(ctx)=="deferred" and #created==0)
active=weapon(2,{"ap"},0);assert(descriptor.execute(ctx)=="executed" and created[1].section=="ap")

-- Complete packs and transactional rollback; an untracked native exception
-- is uncertain and cannot be automatically retried as a fresh donation.
reset();slots[3]=weapon(1,{"ball"},0);ctx,ok=prepare({ammo_packs="3"});assert(ok);fail_at=2
local state,why=descriptor.execute(ctx);assert(state=="rejected" and #created==0 and #announcements==0)
reset();slots[3]=weapon(1,{"ball"},0);ctx,ok=prepare({ammo_packs="3"});assert(ok);throw_at=2
state,why=descriptor.execute(ctx);assert(state=="uncertain" and #created==0)
reset();slots[3]=weapon(1,{"ball"},0);ctx,ok=prepare({ammo_packs="3"});assert(ok);fail_at=2;release_fail=true
state,why=descriptor.execute(ctx);assert(state=="uncertain" and #created==1)
reset();slots[3]=weapon(1,{"ball"},0);run({ammo_packs="50"});assert(#created==50)
for _,params in ipairs({{ammo_packs="0"},{ammo_packs="51"},{ammo_packs="1.5"},{primary="yes"},
    {hands_only="1"},{pistol="true"},{primary="false",secondary="false"}}) do
    reset();local _,allowed,status=prepare(params);assert(not allowed and status=="rejected" and #created==0)
end
reset();actor_living=false;deferred({hands_only="true"},"actor_unavailable")
print("Equipment ammunition passed: live upgraded/current ammo type, full pack sizes, two slots/dedup, arbitrary-slot active gun waiting, fresh execution scan, exclusions/class safety, invalid params, transactional rollback/uncertain exceptions and 50 packs.")
