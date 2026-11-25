local core, optional = assert(arg[1]), assert(arg[2])
local prefix = "anomaly_donation_bridge_"
local handler = "hardcoon.adb.traitor"
local save_key = "adb_traitor_v1"
local round_id = string.rep("a", 32)
local pink = "%c[255,255,140,196]"

local function load_module(path, name)
    local env = setmetatable({}, {__index=_G})
    local chunk = assert(loadfile(path.."/"..prefix..name..".script"))
    setfenv(chunk, env)()
    _G[prefix..name] = env
    return env
end

local function copy(value)
    if type(value) ~= "table" then return value end
    local result = {}
    for key,item in pairs(value) do result[key] = copy(item) end
    return result
end

local common = load_module(core, "common")
local function cp(text) return common.game_text_cp1251_lossy(text) end
local function round() return alife_storage_manager.get_state()[save_key] end
local clock, frame, paused, ui, loading, objects, online, cb, news, deaths, logs, saves
local engine, traitor
local skip_notice, prevent_death, next_id, spawns
local reputation
local store
clsid = {stalker=1}
function IsStalker(_, cls) return cls == clsid.stalker end
function RegisterScriptCallback(name, fn) cb[name]=fn end
function get_object_story_id(id) return id==90 and "quest_npc" or nil end
function se_load_var(_, _, _) return nil end
function printf(_, value) logs[#logs+1]=value end
function exec_console_cmd() saves=saves+1 end
function device() return {frame=frame,precache_frame=loading and 1 or 0,is_paused=function() return paused end} end
function time_global() return clock end
function Check_UI() return ui end
local life={object=function(_,id) return objects[id] end}
function alife() return life end
level={
    present=function() return true end,
    object_by_id=function(id) return online[id] end
}
alife_storage_manager={get_state=function() return store end}
news_manager={send_tip=function(_,message)
    if skip_notice and message:find(cp("Погиб"),1,true) then return false end
    news[#news+1]=message
    return true
end}
anomaly_donation_bridge_catalog={
    validate_sections=function(params,key,kind)
        assert(key=="npc_sections" and kind=="npc")
        if params.catalog_revision~="catalog" then return nil,"catalog_revision_mismatch" end
        if params.npc_sections~="sim_a,sim_b" then return nil,"selection_invalid" end
        return {{section_id="sim_a"},{section_id="sim_b"}}
    end,
    is_available=function(section,kind) return kind=="npc" and (section=="sim_a" or section=="sim_b") end
}

local function native_death(id,killer)
    objects[id].live=false
    local statistic=game_statistics.actor_statistics.killed_companions
    statistic.count=statistic.count+1
    reputation=math.max(-4000,reputation+statistic.rept)
    cb.npc_on_death_callback(online[id],killer)
end

local function add_npc(id,name)
    local object={id=id,group_id=65535,live=true,shown=name,
        name=function(self) return "native_"..self.id end,
        section_name=function() return "sim_ordinary" end,
        character_name=function(self) return self.shown end,
        community=function() return "stalker" end,
        alive=function(self) return self.live end,
        clsid=function() return clsid.stalker end}
    local npc={
        id=function() return id end, name=function() return object:name() end,
        section=function() return object:section_name() end, character_name=function() return object.shown end,
        alive=function() return object.live end,
        kill=function(self,killer)
            assert(killer==self and object.live,"invalid actor credit or repeated death")
            deaths[#deaths+1]=id
            if not prevent_death then native_death(id,self) end
        end
    }
    objects[id],online[id]=object,npc
    axr_companions.non_task_companions[id]=true
    return object
end

anomaly_donation_bridge_named_companion={
    spawn_recruited=function(_,section,name,remember)
        assert(section=="sim_a" or section=="sim_b")
        next_id,spawns=next_id+1,spawns+1
        local member=add_npc(next_id,cp(name))
        remember(member)
        return "executed","spawned",member.id
    end
}
load_module(core,"relife_companions")

local function boot()
    cb={}
    engine=load_module(core,"registry")
    traitor=load_module(optional,"effect_hardcoon_adb_traitor")
    traitor.on_game_start()
    assert(traitor.enabled())
end

local function reset(names)
    clock,frame,paused,ui,loading=0,0,false,false,false
    store,objects,online,news,deaths,logs={},{},{},{},{},{}
    saves,skip_notice,prevent_death,next_id,spawns=0,false,false,100,0
    reputation=240
    game_statistics={actor_statistics={killed_companions={count=0,rept=-75}}}
    axr_companions={non_task_companions={},companion_squads={}}
    db={actor={
        id=function() return 0 end,
        alive=function() return true end,
        character_reputation=function() return reputation end,
        set_character_reputation=function(_,value) reputation=value end
    }}
    math.randomseed(71471)
    for index,name in ipairs(names) do add_npc(index,cp(name)) end
    add_npc(90,cp("Сюжетный NPC"))
    boot()
end
local function start(minimum,interval)
    local status,why=engine.dispatch(handler,
        {actor=db.actor,command={donation_id="paid-opener",command_id=round_id}},
        {npc_sections="sim_a,sim_b",catalog_revision="catalog",
            minimum_members=tostring(minimum),interval_seconds=tostring(interval)})
    assert(status=="executed",tostring(why))
end
local function tick(ms)
    clock,frame=clock+(ms or 100),frame+1
    engine.update(clock)
end
local function until_true(condition, message)
    for _=1,250 do
        if condition() then return end
        tick()
    end
    error(message.." "..table.concat(logs," | "))
end
local function reload()
    cb.on_before_load_input()
    store=copy(store)
    boot()
    engine.on_game_load()
    for _=1,16 do tick() end
end
local function clues()
    local count=0
    for _,line in ipairs(news) do
        if line:find(cp("Погиб"),1,true) then
            local begin=assert(line:find(pink,1,true),"missing pink clue")
            local condition=assert(line:sub(begin+#pink):match("^(.+)%%c%[default%]%.$"),"pink escaped the clue")
            assert(line:sub(1,#cp("Погиб "))==cp("Погиб ") and
                line:find("%c[255,238,153,26]",1,true)<begin and not condition:find(cp("предателя"),1,true),
                "PDA did not limit pink to the actual condition")
            count=count+1
        end
    end
    return count
end
local function incoming_hit(id,source,blocked)
    local damage,flags={draftsman=source,power=14,impulse=8}, {ret_value=not blocked}
    cb.npc_on_before_hit(online[id],damage,15,flags)
    assert(damage.power==14 and damage.impulse==8 and flags.ret_value==(not blocked),"participant damage was modified")
    return damage.draftsman
end
local function player_kills(id,source)
    local killer=incoming_hit(id,source or db.actor)
    assert(killer==online[id],"the engine still sees the actor as the attacker")
    native_death(id,killer)
end
local group={"Барабан","Караул","Полярис","Стрелец","Сокол","Ворон","Lemon","Boo"}

reset(group)
start(5,60)
until_true(function() return round().phase=="running" and not round().roster_notice end,"old party did not start")
assert(#round().people==#group and spawns==0 and traitor.status()[2]=="idle")
assert(news[1]:sub(1,#pink)==pink and news[1]:find(cp("Предатель в отряде").."%c[default]",1,true),
    "roster title color leaked into the participant list")
assert(incoming_hit(90,db.actor)==db.actor,"a story NPC outside the roster was protected")
local chosen=round().traitor
assert(incoming_hit(round().people[chosen].id,db.actor,true)==db.actor,"the template changed a blocked hit")
assert(incoming_hit(round().people[chosen].id,online[90])==online[90],"an unrelated attacker was rewritten")
assert(incoming_hit(round().people[chosen].id,db.actor)==online[round().people[chosen].id],
    "non-lethal hits still reach native player relation accounting")
local before=round().remaining
ui=true
for _=1,25 do tick() end
ui=false
tick()
assert(round().remaining==before and #deaths==0 and clues()==0)
loading=true
engine.on_level_changing()
for _=1,20 do tick() end
loading=false
engine.on_game_load()
for _=1,15 do tick() end
assert(round().traitor==chosen and round().remaining==before,"changing level reset progress")

local victim=round().people[round().proof[1].victim].id
tick(60000)
assert(#deaths==1 and deaths[1]==victim and clues()==0,"wrong first victim or early clue")
assert(reputation==240 and game_statistics.actor_statistics.killed_companions.count==1,
    "a timed death changed the actor's reputation or lost the death statistic")
skip_notice=true
tick()
assert(round().death.phase=="confirmed" and #deaths==1)
reload()
assert(#deaths==1 and clues()==0,"reload repeated a committed death")
skip_notice=false
until_true(function() return clues()==1 end,"confirmed death did not deliver its clue")
reload()
assert(#deaths==1 and clues()==1,"reload repeated a clue")
local wrong
for index,row in ipairs(round().people) do
    if index~=chosen and not row.out then wrong=row.id;break end
end
local weapon={id=function() return 190 end,parent=function() return db.actor end}
player_kills(wrong,weapon)
tick()
assert(round() and clues()==1 and reputation==240,"killing an innocent ended the round, exposed a role or changed reputation")
local traitor_id=round().people[chosen].id
reputation=-3975
local fatal_source=incoming_hit(traitor_id,db.actor)
assert(fatal_source==online[traitor_id],"the lethal hit kept actor attribution")
objects[traitor_id].live=false
tick()
assert(round(),"zero health closed the round before the death callback")
native_death(traitor_id,fatal_source)
tick()
assert(not round() and logs[#logs]:find(" won",1,true) and clues()==1 and saves==0 and reputation==-3975,
    "killing the traitor near the lower reputation limit did not preserve the exact value")
assert(incoming_hit(wrong,db.actor)==db.actor,"the roster protection stayed active after the round")

reset({"Барабан","Караул","Полярис"})
start(5,1)
assert(traitor.status()[6]=="2")
traitor.receive({"TRAITOR_ROSTER",round_id,"","more","paid-opener","барабан","paid-2","Lemon"})
assert(traitor.status()[6]=="1" and traitor.status()[7]=="paid-2" and #round().recruits==1)
traitor.receive({"TRAITOR_ROSTER",round_id,"","last","stale","Boo"})
assert(#round().recruits==1)
traitor.receive({"TRAITOR_ROSTER",round_id,"paid-2","last","paid-3","Ёжик"})
until_true(function() return round().phase=="running" and not round().roster_notice end,"recruited party did not start")
assert(#round().people==5 and spawns==2 and objects[101].shown=="Lemon" and objects[102].shown==cp("Ёжик"))
until_true(function() return not round() end,"last living traitor did not end the round")
assert(#deaths==4 and clues()==4 and logs[#logs]:find(" lost",1,true) and saves==0)
assert(reputation==240,"the timed series accumulated a companion-death penalty")

reset(group)
start(5,60)
until_true(function() return round().phase=="running" and not round().roster_notice end,"unrelated death setup")
local ambient_id=round().people[round().proof[1].victim].id
native_death(ambient_id,online[90])
assert(reputation==165,"the template refunded an unrelated combat death")

reset(group)
start(5,1)
until_true(function() return round().phase=="running" and not round().roster_notice end,"pending kill setup")
prevent_death=true
until_true(function() return round().death and round().death.phase=="requested" end,"native kill not queued")
local requested_id=deaths[1]
prevent_death=false
reload()
assert(#deaths==2 and deaths[2]==requested_id and clues()==1,
    "loading before the native death lost the recorded victim or hint")

reset({"Ар", "Ал", "Ан"})
start(3,60)
tick()
assert(round().phase=="recruiting" and traitor.status()[6]=="1","ambiguous nicknames started an unfair puzzle")
assert(#deaths==0 and spawns==0)
traitor.receive({"TRAITOR_ROSTER",round_id,"","last"})
until_true(function() return not round() end,"an exhausted history left the effect waiting for new donors")
assert(news[#news]:sub(1,#pink)==pink and
    news[#news]:find(cp("Предатель в отряде").."%c[default]",1,true) and
    news[#news]:find(cp("однозначные улики"),1,true))

reset({"Барабан","Караул","Полярис"})
start(5,60)
traitor.receive({"TRAITOR_ROSTER",round_id,"","last","old-donation","Lemon"})
reload()
until_true(function() return not round() end,"a saved incomplete roster stayed active")
assert(spawns==0 and #deaths==0,"an insufficient roster was partially spawned")
assert(news[#news]:find(cp("В истории не хватило"),1,true))
print("PASS traitor roster, Cyrillic and Latin names, clues, game-only saves, timed deaths and verdicts")
