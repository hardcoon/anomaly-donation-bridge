local root,optional=assert(arg[1]),assert(arg[2])
local sep=package.config:sub(1,1)
local prefix="anomaly_donation_bridge_"
local handler="hardcoon.adb.battle_royale"
local save_key="adb_battle_royale_v1"
local vip_spot="ui_pda2_quest_npc_location"
local old_spot="ui_secondary_task_blink"
local selected_visuals={["actors\\stalker_novice.ogf"]=true,["actors\\stalker_experienced.ogf"]=true}
local function load_module(directory,name)
    local chunk=assert(loadfile(directory..sep..prefix..name..".script"))
    local env=setmetatable({},{__index=_G})
    setfenv(chunk,env)()
    _G[prefix..name]=env
    return env
end
local function copy(value)
    if type(value)~="table" then return value end
    local result={}
    for key,item in pairs(value) do result[key]=copy(item) end
    return result
end

local function vec(x,y,z)
    return {x=x,y=y,z=z,set=function(self,a,b,c) self.x,self.y,self.z=a,b,c;return self end}
end
function vector() return vec(0,0,0) end
local width=24
local function vertex(id)
    assert(id>=0 and id<width*width,"invalid native vertex")
    return vec(id%width*10,0,math.floor(id/width)*10)
end
local here,storage,servers,clients,callbacks,pending,tags,spots,logs
local clock,frame,sequence,news,hud_title,move_count,kit_count,fail_partial_hero_ammo,stock_visual_api
local Server,Client={},{}
Server.__index,Client.__index=Server,Client
Client.__eq=function() error("engine objects must be compared by identity") end

clsid={actor=1,stalker=2,inventory_box=3,level_changer=4,obj_bolt=5,item=6,mutant=7}
game_object={enemy=2}
function IsStalker(o,c) return (c or o:clsid())==clsid.stalker end
function IsMonster(o) return o:clsid()==clsid.mutant end
function IsInvbox(_,c) return c==clsid.inventory_box end
function IsItem(kind,section) return kind=="quest" and section=="quest_item" end
function get_object_story_id(id) return id==18 and "quest_pda" or nil end
function time_global() return clock end
function device() return {frame=frame,f_time_delta=0.025,is_paused=function() return false end} end
function RegisterScriptCallback(name,fn) callbacks[name]=fn end
function printf(_,msg) logs[#logs+1]=msg end

function Server:name() return self.unique_name end
function Server:section_name() return self.section end
function Server:clsid() return self.class end
function Server:alive() return self.living end
function Server:set_character_name(name) self.nickname=name end
function Server:visible_for_map(show)
    if show~=nil then self.map_visible=show end
    return self.map_visible==true
end
function Client:id() return self.object_id end
function Client:name() return servers[self.object_id].unique_name end
function Client:section() return servers[self.object_id].section end
function Client:clsid() return servers[self.object_id].class end
function Client:alive() return servers[self.object_id].living end
function Client:level_vertex_id() return servers[self.object_id].node end
function Client:game_vertex_id() return servers[self.object_id].m_game_vertex_id end
function Client:position() return vertex(self:level_vertex_id()) end
function Client:parent()
    return clients[servers[self.object_id].parent_id]
end
function Client:is_talking() return false end
function Client:get_current_holder() return nil end
function Client:has_info(info) return self[info]==true end
function Client:disable_info_portion(info) self[info]=nil end
function Client:inv_box_closed_status() return self.closed==true end
function Client:inv_box_can_take_status() return true end
function Client:is_level_changer_enabled() return self.exit_on==true end
function Client:enable_level_changer(value) self.exit_on=value end
function Client:character_community() return self.community end
function Client:set_character_community(value) self.community=value end
function Client:get_visual_name() return servers[self.object_id].visual or "actor_visual_stalker" end
function Client:set_visual_name(visual,...)
    assert(self:section()=="adb_battle_royale_participant","foreign NPC visual was changed")
    assert(select("#",...)==(stock_visual_api and 0 or 1),"incorrect native visual signature")
    assert(selected_visuals[visual],"unselected or incompatible visual was loaded")
    servers[self.object_id].visual=visual
end
function Client:force_set_goodwill(_,target) self.hostile=target:id() end
function Client:set_relation(_,target)
    self.relations=self.relations or {}
    self.relations[target:id()]=true
end
function Client:movement_enabled(value) self.running=value end
function Client:see() return false end
function Client:memory_remove_links() end
function Client:make_object_visible_somewhen(actor) self.sees=actor:id() end
function Client:enable_memory_object() end
function Client:is_on_belt(item) return item:id()==17 end
function Client:move_to_belt(item) self.restored_belt=item:id() end
function Client:iterate_inventory(fn)
    for id,s in pairs(servers) do
        if s.parent_id==self:id() then fn(self,clients[id]) end
    end
end
function Client:item_in_slot(slot)
    local item=self.slots and self.slots[slot]
    return item and servers[item] and servers[item].parent_id==self:id() and clients[item] or nil
end
local function later(frames,fn) pending[#pending+1]={frame=frame+frames,fn=fn} end
function Client:move_to_slot(item,slot)
    assert(self:id()==0,"stock move_to_slot is an actor event")
    assert(not self:item_in_slot(slot),"occupied actor slot")
    later(1,function() self.slots[slot]=item:id() end)
end
function Client:set_ammo_elapsed(count) self.rounds=count end
function Client:set_actor_position(p)
    move_count=move_count+1
    later(2,function() servers[0].node=math.floor(p.z/10)*width+math.floor(p.x/10) end)
end
function Client:transfer_item(item,target)
    local s=servers[item:id()]
    assert(s.parent_id==self:id(),"duplicate ownership request")
    later(1,function() s.parent_id=65535 end)
    later(2,function() s.parent_id=target:id() end)
end
function Client:take_item(item)
    local s=servers[item:id()]
    assert(s.parent_id==65535)
    later(2,function() s.parent_id=self:id() end)
end

function load_var(npc,key) return npc[key] end
function save_var(npc,key,value) npc[key]=value end
function se_save_var(id,name,key,value) tags[id..":"..name..":"..key]=value end
function se_load_var(id,name,key) return tags[id..":"..name..":"..key] end
axr_companions={set_companion_to_wait_state=function(npc)
    npc.npcx_beh_wait=true
    npc.fight_from_point=npc:level_vertex_id()
end}
alife_storage_manager={get_state=function() return storage end}
news_manager={send_tip=function(_,message) news[#news+1]=message end}
level={
    present=function() return true end,
    name=function() return here end,
    vertex_position=vertex,
    vertex_in_direction=function(v) return v<width*width and v or 4294967295 end,
    vertex_link=function(v,d)
        local x,y=v%width,math.floor(v/width)
        if d==0 and x>0 then return v-1 end
        if d==1 and x<width-1 then return v+1 end
        if d==2 and y>0 then return v-width end
        if d==3 and y<width-1 then return v+width end
        return 4294967295
    end,
    iterate_nearest=function() end,
    object_by_id=function(id) return clients[id] end,
    map_has_object_spot=function(id,spot) return spots[id..":"..spot] and 1 or 0 end,
    map_add_object_spot_ser=function(id,spot,hint) spots[id..":"..spot]=hint end,
    map_change_spot_hint=function(id,spot,hint)
        local key=id..":"..spot
        assert(spots[key],"changing a missing native mark")
        spots[key]=hint
    end,
    map_remove_object_spot=function(id,spot) spots[id..":"..spot]=nil end
}
function game_graph() return {vertex=function(_,gvid) return {level_id=function() return gvid end} end} end
local life={}
function alife() return life end
function life:level_name(id) return id==14 and "test_map" or "outside_map" end
function life:object(id) return servers[id] end
function life:get_children(owner)
    local ids={}
    for id,child in pairs(servers) do
        if child.parent_id==owner.id then ids[id]=true end
    end
    return next,ids,nil
end
function life:set_switch_online(id,value)
    assert(servers[id].class==clsid.stalker,"another object's ALife settings changed")
    servers[id].allow_online=value
end
function life:set_switch_offline(id,value)
    assert(servers[id].class==clsid.stalker,"another object's ALife settings changed")
    servers[id].allow_offline=value
end
local sections={
    adb_battle_royale_participant={},
    novice_outfit={kind="o_light",slot="6"},
    wpn_pm={kind="w_pistol",slot="1",ammo_mag_size="8",ammo_class="ammo_9x18_fmj"},
    ammo_9x18_fmj={kind="a_pistol",box_size="20"},
    knife={class="WP_KNIFE"},existing_pistol={},artifact={},device_pda={},backpack={},quest_item={}
}
local ini={}
function system_ini() return ini end
function ini:section_exist(section) return sections[section]~=nil end
function ini:line_exist(section,key) return sections[section] and sections[section][key]~=nil end
function ini:r_string(section,key) return sections[section][key] end
local function object(id,class,section,node,parent,map,online)
    local s=setmetatable({id=id,class=class,section=section,node=node,parent_id=parent or 65535,
        unique_name=section..id,m_game_vertex_id=map or 14,living=true},Server)
    servers[id]=s
    if online~=false then
        local c=setmetatable({object_id=id},Client)
        clients[id]=c
        db.storage[id]={object=c}
    end
    return s,clients[id]
end
function alife_create(section,_,node,gvid)
    sequence=sequence+1
    return object(sequence,clsid.stalker,section,node,nil,gvid)
end
function alife_create_item(section,owner,props)
    if props.ammo then
        -- The Anomaly item processor returns a table even for a single box.
        return {life:create_ammo(section,owner:position(),owner:level_vertex_id(),
            owner:game_vertex_id(),owner:id(),props.ammo)}
    end
    sequence,kit_count=sequence+1,kit_count+1
    return object(sequence,clsid.item,section,owner:level_vertex_id(),owner:id())
end
function life:create_ammo(section,_,lv,gv,owner_id,rounds)
    local box_size=tonumber(sections[section].box_size)
    assert(lv==clients[owner_id]:level_vertex_id() and gv==clients[owner_id]:game_vertex_id())
    assert(rounds>=1 and rounds<=box_size)
    if fail_partial_hero_ammo and owner_id==0 and rounds<box_size then return end
    sequence,kit_count=sequence+1,kit_count+1
    local s=object(sequence,clsid.item,section,lv,owner_id,gv)
    s.rounds=rounds
    return s
end
function alife_release_id(id)
    for child,s in pairs(servers) do
        if s.parent_id==id then servers[child],clients[child],db.storage[child]=nil,nil,nil end
    end
    servers[id],clients[id],db.storage[id]=nil,nil,nil
end

local rendered
local native_hud={
    AddDialogToRender=function(_,window) assert(not rendered);rendered=window end,
    RemoveDialogToRender=function(_,window) assert(rendered==window);rendered=nil end
}
function get_hud() return native_hud end
function GetFontLetterica16Russian() return {} end
function Frect() return {set=function(self) return self end} end
function vector2() return {set=function(self,x,y) self.x,self.y=x,y;return self end} end
CUIScriptWnd={
    SetAutoDelete=function() end,SetWndRect=function() end,Update=function() end,
    Enable=function(_,enabled) assert(not enabled,"HUD must not capture input") end,
    AttachChild=function() end,Show=function(self,visible) self.visible=visible end
}
function class(name)
    local env=getfenv(2)
    return function(base)
        env.super=function() end
        env[name]=setmetatable({},{__index=base,__call=function(cls)
            local window=setmetatable({},{__index=cls})
            window:__init()
            return window
        end})
    end
end
function CUITextWnd()
    return {
        SetAutoDelete=function() end,SetFont=function() end,SetTextComplexMode=function() end,
        SetTextAlignment=function(_,alignment) assert(alignment==2,"HUD text must be centered") end,
        SetEllipsis=function() end,SetTextColor=function() end,SetWndSize=function() end,
        SetWndPos=function(self,p) self.pos=p end,SetText=function(self,value) self.value=value end,
        Show=function(self,visible) self.visible=visible end
    }
end

local sha=load_module(root,"sha256")
local common=load_module(root,"common")
local normal_entries={
    sim_default_stalker_0={kind="npc",section_id="sim_default_stalker_0",available=true},
    sim_default_stalker_1={kind="npc",section_id="sim_default_stalker_1",available=true},
    sim_default_stalker_2={kind="npc",section_id="sim_default_stalker_2",available=true}
}
local models={
    sim_default_stalker_0={"actors\\stalker_novice.ogf","actors\\broken_model.ogf"},
    sim_default_stalker_1={"actors\\stalker_experienced.ogf"},
    sim_default_stalker_2={"actors\\unselected.ogf"}
}
anomaly_donation_bridge_catalog={
    snapshot=function() return {entries={normal_entries.sim_default_stalker_0,
        normal_entries.sim_default_stalker_1,normal_entries.sim_default_stalker_2}} end,
    is_available=function(section,kind) return kind=="npc" and normal_entries[section]~=nil end,
    validate_sections=function(params,key,kind)
        if params.catalog_revision~="catalog" or kind~="npc" then return nil,"catalog_revision_mismatch" end
        local sections,why=common.split_csv(params[key])
        if not sections then return nil,why end
        local result={}
        for _,section in ipairs(sections) do
            if not normal_entries[section] then return nil,"catalog_selection_stale_or_quarantined" end
            result[#result+1]=normal_entries[section]
        end
        return result
    end
}
anomaly_donation_bridge_npc_visuals={
    models_for_npc=function(section) return models[section] end,
    prepare_visual=function(path,section)
        assert(section=="adb_battle_royale_participant" and selected_visuals[path],"unsafe native visual")
        return path
    end
}
anomaly_donation_bridge_visual_safety={check=function(path,_,section)
    assert(section=="adb_battle_royale_participant")
    return selected_visuals[path]
end}
local battle,registry
local function load_scripts()
    callbacks={}
    registry=load_module(root,"registry")
    battle=load_module(optional,"effect_hardcoon_adb_battle_royale")
    battle.on_game_start()
end
local function reset()
    rendered=nil
    here="test_map"
    storage,servers,clients,pending,tags,spots,logs,news={},{},{},{},{},{},{},{}
    clock,frame,sequence,move_count,kit_count,fail_partial_hero_ammo=0,0,200,0,0,false
    stock_visual_api=false
    db={storage={}}
    local _,actor=object(0,clsid.actor,"actor",12+12*width)
    db.actor=actor
    actor.slots={[2]=15,[1]=16}
    object(15,clsid.item,"existing_pistol",actor:level_vertex_id(),0)
    object(16,clsid.item,"knife",actor:level_vertex_id(),0)
    object(17,clsid.item,"artifact",actor:level_vertex_id(),0)
    object(18,clsid.item,"device_pda",actor:level_vertex_id(),0)
    object(19,clsid.item,"backpack",actor:level_vertex_id(),0)
    object(20,clsid.item,"quest_item",actor:level_vertex_id(),19)
    local _,companion=object(25,clsid.stalker,"companion",30)
    companion.npcx_is_companion=true
    companion.fight_from_point=31
    local _,exit=object(26,clsid.level_changer,"level_changer",100)
    exit.exit_on=true
    object(27,clsid.level_changer,"level_changer",101)
    object(101,clsid.inventory_box,"inventory_box",100)
    object(102,clsid.inventory_box,"inventory_box",100,nil,14,false)
    object(103,clsid.inventory_box,"inventory_box",100,nil,15)
    object(104,clsid.inventory_box,"inventory_box",100)
    level.map_add_object_spot_ser(104,"treasure","another task")
    treasure_manager={caches={[101]=false,[102]=false,[103]=false,[104]=false}}
    load_scripts()
end
local function tick()
    frame,clock=frame+1,clock+25
    local rest={}
    for _,entry in ipairs(pending) do
        if entry.frame<=frame then entry.fn() else rest[#rest+1]=entry end
    end
    pending=rest
    callbacks.actor_on_update()
end
local function wait_until(predicate,message)
    for _=1,1000 do
        if predicate() then return end
        tick()
    end
    local current=storage[save_key] and storage[save_key].match
    local phase=current and (current.phase.."/"..tostring(current.waiting).."/"..tostring(current.step).."/"..#current.roster) or "idle"
    error(message.." ("..phase.."): "..table.concat(logs,", "))
end
local match_id=string.rep("a",32)
local function match() return storage[save_key].match end
local function open(count,policy)
    local result,reason=registry.dispatch(handler,{actor=db.actor,command={command_id=match_id,donation_id="opening"}},
        {participant_count=tostring(count),effect_policy=policy,catalog_revision="catalog",
            npc_sections="sim_default_stalker_0,sim_default_stalker_1"})
    assert(result=="executed",tostring(reason))
end
local function join(nick,name)
    battle.receive({"BATTLE_ROSTER",match_id,sha.digest(nick),name or nick})
end

reset()
open(3,"continue")
join("alice","Alice")
join("alice","ALICE")
tick()
assert(rendered.visible and rendered.labels[1][2].value==common.game_text_cp1251_lossy("Набор участников: 1/3"))
assert(battle.blocks(handler) and not battle.blocks("actor_boost"))
join("boris","Борис")
tick()
assert(match().phase=="recruiting" and #match().roster==2,"hero filled a place in recruitment")
join("carl","Карл")
wait_until(function() return match() and match().moved end,"could not reach submitted teleport")
assert(servers[15].parent_id==101 and servers[17].parent_id==101)
assert(servers[16].parent_id==0 and servers[18].parent_id==0 and servers[19].parent_id==0)
assert(not clients[26].exit_on and not clients[27].exit_on)
assert(clients[25].npcx_beh_wait and not spots["101:treasure"])
assert(battle.blocks("actor_boost"))
local spawn_count=sequence
-- Save after the teleport request, before the native movement event.
callbacks.on_before_load_input()
storage=copy(storage)
pending={}
load_scripts()
registry.on_game_load()
wait_until(function() return match().phase=="fighting" end,"teleport did not resume after reload")
tick()
local first,second,third=match().roster[1].object.id,match().roster[2].object.id,match().roster[3].object.id
assert(move_count==2 and sequence==spawn_count,"reload repeated a spawn or skipped teleport")
local fighters=0
for _,server in pairs(servers) do if server.section=="adb_battle_royale_participant" then fighters=fighters+1 end end
assert(fighters==3,"configured opponent count did not match the number of NPCs spawned")
for _,owner_id in ipairs({0,first,second,third}) do
    local boxes,rounds=0,0
    for _,s in pairs(servers) do
        if s.parent_id==owner_id and s.section=="ammo_9x18_fmj" then
            boxes,rounds=boxes+1,rounds+s.rounds
        end
    end
    assert(boxes==6 and rounds==112,"match kit lost or duplicated ammunition")
end
assert(clients[first].running and clients[first].sees==0 and clients[first].relations[second])
assert(rendered.labels[1][2].value==common.game_text_cp1251_lossy("Осталось в живых: 3"))
for _,entry in ipairs(match().roster) do
    local id=entry.object.id
    assert(clients[id].community=="arena_enemy" and selected_visuals[servers[id].visual])
    assert(servers[id].visual==entry.visual and servers[id]:visible_for_map())
    assert(spots[id..":"..vip_spot]==common.game_text_cp1251_lossy(entry.name))
end
-- On an older save the saved marker flag referred to a short-lived flash.
match().roster[1].marked=true
spots[first..":"..vip_spot],spots[first..":"..old_spot]=nil,"legacy marker"
servers[first]:visible_for_map(false)
callbacks.on_before_load_input()
storage=copy(storage)
load_scripts()
registry.on_game_load()
wait_until(function() return spots[first..":"..vip_spot] end,"saved opponent marker was not restored")
assert(not spots[first..":"..old_spot] and servers[first]:visible_for_map())
assert(not battle.blocks("actor_boost") and battle.blocks("teleport_random_level"))
local flags={}
callbacks.on_enemy_eval(clients[first],clients[25],flags)
assert(flags.override and flags.result==false,"private duel treated a companion as a fighter")
flags={}
callbacks.on_enemy_eval(clients[25],db.actor,flags)
assert(next(flags)==nil,"ordinary companion's enemy evaluation was modified")
callbacks.actor_on_before_death()
assert(not rendered,"HUD leaked after actor death")

callbacks.npc_on_death_callback(clients[first])
servers[first].living=false
callbacks.npc_on_death_callback(clients[first])
assert(#news==1 and news[1]==common.game_text_cp1251_lossy("%c[255,238,153,26]Alice%c[default]: выбыл"))
assert(not spots[first..":"..vip_spot])
local offline=clients[second]
clients[second]=nil
for _=1,205 do tick() end
assert(match() and not match().roster[2].out,"offline participant was counted as dead")
clients[second]=offline
callbacks.npc_on_death_callback(clients[second])
servers[second].living=false
tick()
assert(match() and not match().roster[3].out and not spots["101:treasure"],"victory fired before the third opponent fell")
assert(rendered.labels[1][2].value==common.game_text_cp1251_lossy("Осталось в живых: 1"))
callbacks.npc_on_death_callback(clients[third])
servers[third].living=false
wait_until(function() return not match() end,"the last opponent did not finish the match")
assert(#news==3 and not spots["101:treasure"],"victory notice preceded the equipment mark")
callbacks.on_before_load_input()
storage=copy(storage)
load_scripts()
registry.on_game_load()
local stash_hint=common.game_text_cp1251_lossy("Батл-Роял: Снаряжение")
wait_until(function() return spots["101:treasure"]==stash_hint end,"victory did not reveal the stash after reload")
assert(#news==4 and news[4]==common.game_text_cp1251_lossy("Вы смогли выжить. Ваше снаряжение отмечено на карте в КПК."))
assert(not spots[second..":"..vip_spot] and not spots[third..":"..vip_spot])
assert(clients[26].exit_on and not clients[27].exit_on)
storage[save_key].recovery[match_id].stash.marked=true
spots["101:treasure"]="legacy stash hint"
wait_until(function() return spots["101:treasure"]==stash_hint end,"old stash hint was not upgraded")
assert(#news==4,"victory message was repeated")
assert(not clients[25].npcx_beh_wait and clients[25].fight_from_point==31)
assert(servers[15].parent_id==101 and servers[17].parent_id==101)
servers[15].parent_id,servers[17].parent_id=0,0
wait_until(function() return not spots["101:treasure"] end,"stash reservation did not clear")
assert(treasure_manager.caches[101]==false and spots["104:treasure"]=="another task")

reset()
open(1,"defer")
join("alice")
stock_visual_api=true
fail_partial_hero_ammo=true
wait_until(function() return match().phase=="rollback" end,"failed kit did not start rollback")
assert(servers[15].parent_id==101 and battle.blocks("actor_boost"))
wait_until(function() return match()==nil end,"failed kit did not finish rollback")
tick()
assert(servers[15].parent_id==0 and servers[17].parent_id==0 and clients[0]:item_in_slot(2):id()==15)
assert(kit_count>0 and clients[26].exit_on and move_count==0)
for id in pairs(servers) do assert(id<=200,"rollback left match-owned objects") end

reset()
open(32,"defer")
for first_index=1,32,7 do
    local fields={"BATTLE_ROSTER",match_id}
    for index=first_index,math.min(first_index+6,32) do
        fields[#fields+1]=sha.digest("name"..index);fields[#fields+1]="Viewer "..index
    end
    battle.receive(fields)
end
assert(#match().roster==32 and battle.status()[6]=="32")
battle.receive({"BATTLE_CANCEL",match_id})
assert(not match() and battle.status()[2]=="idle")
print("PASS battle royale opponent counts, appearance selection, VIP marks, victory, F9 and rollback")
