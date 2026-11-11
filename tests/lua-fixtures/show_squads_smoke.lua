local core, effects, optional=assert(arg[1]),assert(arg[2]),assert(arg[3])
local sep=package.config:sub(1,1)
local function load(root,name)
    local env=setmetatable({},{__index=_G})
    local chunk=assert(loadfile(root..sep..name..'.script'));setfenv(chunk,env);chunk();_G[name]=env;return env
end
local memory,process={},1234
local kernel={}
function kernel.GetCurrentProcessId() return process end
function kernel.GetEnvironmentVariableA(name,buffer,size)
    local value=memory[name]
    if not value then return 0 end
    if not buffer then return #value+1 end
    assert(size>#value);buffer.text=value;return #value
end
function kernel.SetEnvironmentVariableA(name,text) memory[name]=text;return 1 end
package.loaded.ffi={cdef=function()end,load=function()return kernel end,
    new=function()return{}end,string=function(buffer,length)return buffer.text:sub(1,length)end}
anomaly_donation_bridge_common=load(core,'anomaly_donation_bridge_common')
local function registry() anomaly_donation_bridge_registry=load(core,'anomaly_donation_bridge_registry') end
registry()
DEV_DEBUG=false;dbg_map_hud=false
local world,xml_valid=1,true
local adds,removes,changes,serialized=0,{},0,0
local spots,objects={},{}
local FRIEND='alife_presentation_squad_friend_1'
local ENEMY='alife_presentation_squad_enemy_1'
local NEUTRAL='alife_presentation_squad_neutral_1'
local function key(id,spot)return id..'|'..spot end
local function foreign(id,spot,text)spots[key(id,spot)]={hint=text,foreign=true}end
level={name=function()return 'l'..world end}
function level.map_has_object_spot(id,spot)return spots[key(id,spot)] and 1 or 0 end
function level.map_add_object_spot(id,spot,text)
    assert(xml_valid,'Unknown map type would assert in native code')
    assert(spot==FRIEND or spot==ENEMY or spot==NEUTRAL)
    assert(not spots[key(id,spot)],'Duplicate marker')
    adds=adds+1;spots[key(id,spot)]={hint=text}
end
function level.map_add_object_spot_ser()serialized=serialized+1;error('Temporary markers must not enter saves')end
function level.map_change_spot_hint(id,spot,text)
    local entry=assert(spots[key(id,spot)]);assert(not entry.foreign,'Changed foreign hint')
    changes=changes+1;entry.hint=text
end
function level.map_remove_object_spot(id,spot)
    local entry=spots[key(id,spot)];assert(not entry or not entry.foreign,'Removed a foreign marker')
    removes[#removes+1]=key(id,spot);spots[key(id,spot)]=nil
end
function CScriptXmlInit()return{ParseFile=function(_,file)assert(file=='map_spots.xml')end,
    NodeExist=function(_,name,index)return xml_valid and index==0 and(name==FRIEND or name==ENEMY or name==NEUTRAL)end}end
local actor={id=function()return 0 end,alive=function()return true end,
    is_entity_alive=function()return true end,stalker=true,section=function()return 'human' end,
    character_community=function()return 'stalker' end,relation=function()return 0 end}
db={actor=actor,storage={}}
db.storage[0]={object=actor}
objects[0]={id=0,m_game_vertex_id=1}
function alife()return{object=function(_,id)return objects[id]end}end
-- Vanilla registers actor/smarts here, but NOT squads: their on_register
-- writes id -> true into SIMBOARD.squads. Keep these sources distinct.
simulation_objects={object_registry={objects[0]},is_on_the_same_level=function(a,b)return a.m_game_vertex_id==b.m_game_vertex_id end}
SIMBOARD={squads={}}
is_squad_monster={monster=true};game_object={friend=0,neutral=1,enemy=2}
game={translate_string=function(name)return name end}
function IsMonster(object)return object.monster==true end
function IsStalker(object)return object.stalker==true end
anomaly_donation_bridge_catalog={snapshot=function()return{entries={
    {section_id='dog',kind='mutant',display_name='Собака'},
    {section_id='boar',kind='mutant',display_name='Кабан'},
    {section_id='human',kind='npc',display_name='Сталкер'},
    {section_id='adb_faction_actor_stalker',kind='faction',faction='actor_stalker',display_name='Одиночки'},
    {section_id='adb_faction_actor_bandit',kind='faction',faction='actor_bandit',display_name='Бандиты'}
}}end}
local function creature(id,section,group,relation,online,community)
    local human=section=='human' or section=='mod_human'
    objects[id]={id=id,group_id=group or 65535,m_game_vertex_id=world,
        community=function()return community or 'stalker' end,
        name=function()return 'creature_'..id end,section_name=function()return section end}
    if online then db.storage[id]={object={monster=not human and section~='prop',stalker=human,
        is_entity_alive=function()return true end,alive=function()return true end,
        character_community=function()return community or 'stalker' end,
        relation=function()return relation end,section=function()return section end}}end
end
local function squad(id,faction,color,members,location)
    local obj={id=id,player_id=faction,m_game_vertex_id=location or world,members=members,color=color}
    function obj:name()return 'squad_'..self.id end
    function obj:section_name()return 'sim_squad' end
    function obj:npc_count()return #self.members end
    function obj:get_squad_relation()return self.color end
    function obj:squad_members()
        local index=0;return function()index=index+1;local member=self.members[index];return member and{id=member}end
    end
    function obj:show()error('Must not enable debug display')end
    objects[id]=obj;SIMBOARD.squads[id]=true
    return obj
end
creature(201,'human',101,0,true);creature(202,'human',102,1,true);creature(203,'human',103,2,true)
local friend=squad(101,'stalker','friends',{201})
local neutral=squad(102,'stalker','neutral',{202})
squad(103,'bandit','enemy',{203})
creature(301,'dog',104,2,false);creature(302,'dog',104,2,false);creature(303,'dog',104,2,false)
local dogs=squad(104,'monster','monster',{301,302,303})
creature(401,'boar',nil,2,true);creature(402,'dog',nil,0,true)
creature(410,'human',nil,0,true);creature(411,'human',nil,1,true)
creature(412,'mod_human',nil,2,true,'bandit') -- not in the catalog: native IsStalker is enough
creature(413,'human',nil,1,true);objects[413].m_game_vertex_id=2
creature(414,'human',nil,1,true);db.storage[414].object.alive=function()return false end
creature(415,'human',nil,1,true,'actor_stalker');db.storage[415].object.character_community=nil -- server fallback
creature(416,'prop',nil,1,true) -- living-object-shaped non-NPC must still be ignored
local elsewhere=squad(105,'stalker','enemy',{501},2)
creature(501,'human',105,2,false);objects[501].m_game_vertex_id=2
-- Existing contacts on commander ids and an existing owner on a squad id
-- must both be preserved, without hint changes or removal at expiry.
foreign(201,'warfare_stalker_spot','normal PDA contact')
foreign(103,ENEMY,'foreign custom squad marker')
local name='anomaly_donation_bridge_effect_hardcoon_adb_show_squads'
local handler='hardcoon.adb.show_squads'
local module=load(optional,name);module.on_game_start()
local function dispatch(duration,now)
    return anomaly_donation_bridge_registry.dispatch(handler,{actor=actor,now=now or 0},duration and{duration_seconds=tostring(duration)}or{})
end
local function update(now)anomaly_donation_bridge_registry.update(now)end
local function hint(id,spot)return assert(spots[key(id,spot)]).hint end
assert(select(1,dispatch(nil,0))=='executed');update(0)
assert(spots[key(410,FRIEND)] and spots[key(411,NEUTRAL)] and spots[key(412,ENEMY)],'Standalone NPCs are missing')
assert(not spots[key(0,FRIEND)] and not spots[key(413,NEUTRAL)] and not spots[key(414,NEUTRAL)] and not spots[key(416,NEUTRAL)],'Actor/dead NPC/other level/non-NPC exposed')
assert(not spots[key(201,FRIEND)] and not spots[key(202,NEUTRAL)],'Squad NPC was duplicated as a solo marker')
local cp=anomaly_donation_bridge_common.game_text_cp1251
local function expected(title,count)return cp(title)..'\\n'..cp('Количество: ')..count end
assert(hint(101,FRIEND)==expected('Одиночки',1))
assert(hint(102,NEUTRAL)==expected('Одиночки',1))
assert(hint(104,ENEMY)==expected('Собака',3))
assert(hint(410,FRIEND)==expected('Одиночки',1) and hint(412,ENEMY)==expected('Бандиты',1))
assert(hint(415,NEUTRAL)==expected('Одиночки',1),'Standalone NPC server faction fallback failed')
assert(spots[key(401,ENEMY)] and spots[key(402,FRIEND)],'Standalone monsters or friendly pet missing')
assert(not spots[key(105,ENEMY)],'Other location was exposed')
assert(spots[key(103,ENEMY)].hint=='foreign custom squad marker')
local before=adds
for _=1,10 do update(0)end
assert(adds==before,'Paused updates created duplicate markers')
dogs.members={301,302};neutral.color='enemy';db.storage[411].object.alive=function()return false end;update(1000)
assert(hint(104,ENEMY)==expected('Собака',2) and changes>0)
assert(not spots[key(411,NEUTRAL)],'Dead standalone NPC marker survived refresh')
assert(not spots[key(102,NEUTRAL)] and spots[key(102,ENEMY)],'Relation change did not update color')
assert(select(1,dispatch('bad',1000))=='rejected')
assert(select(1,dispatch(0,1000))=='rejected')
update(30000)
for _,entry in pairs(spots)do assert(entry.foreign,'Expired temporary marker survived')end
assert(not memory.ADB_SHOW_SQUADS_SESSION_V1 and serialized==0 and not DEV_DEBUG and not dbg_map_hud)

-- F5 leaves nonserializable marks in the current world. F9 clears them and
-- replaces Lua state; only remaining time comes back from process memory.
assert(select(1,dispatch(30,31000))=='executed');update(31000);update(41000)
local saved=assert(memory.ADB_SHOW_SQUADS_SESSION_V1)
assert(saved:match('|20000$'),'Countdown was not stored in process RAM')
for k,entry in pairs(spots)do if not entry.foreign then spots[k]=nil end end
_G.__adb_show_squads_session_v1=nil;registry();module=load(optional,name);module.on_game_start()
anomaly_donation_bridge_registry.on_game_load();update(500000)
assert(spots[key(104,ENEMY)],'F9 did not rebuild temporary markers')
assert(memory.ADB_SHOW_SQUADS_SESSION_V1:match('|20000$'),'Loading time consumed or restarted countdown')
update(520000)
for _,entry in pairs(spots)do assert(entry.foreign,'F9 restarted full duration')end

-- Travel cleans the old location before the server objects become invalid.
assert(select(1,dispatch(30,530000))=='executed');update(530000);update(535000)
anomaly_donation_bridge_registry.on_level_changing()
for _,entry in pairs(spots)do assert(entry.foreign)end
world=2;objects[0].m_game_vertex_id=2;update(600000)
assert(memory.ADB_SHOW_SQUADS_SESSION_V1:match('|25000$'))
anomaly_donation_bridge_registry.on_game_load();update(600000)
assert(spots[key(105,ENEMY)] and not spots[key(101,FRIEND)],'New map did not use the remaining current-location lease')
update(625000);assert(not spots[key(105,ENEMY)])

-- A marker whose object id has been reused is not ours to remove.
world=1;objects[0].m_game_vertex_id=1
assert(select(1,dispatch(30,630000))=='executed');update(630000)
spots[key(101,FRIEND)]=nil
friend.name=function()return 'replacement_squad' end
foreign(101,FRIEND,'replacement owner');update(631000);update(660000)
assert(hint(101,FRIEND)=='replacement owner')

-- Missing marker XML is rejected before native AddMapLocation can assert.
anomaly_donation_bridge_registry.on_game_load();xml_valid=false;before=adds
assert(select(1,dispatch(30,670000))=='rejected' and adds==before)
xml_valid=true
-- Stale environment from another game process cannot revive this effect.
memory.ADB_SHOW_SQUADS_SESSION_V1='1|99999|0|30000'
_G.__adb_show_squads_session_v1=nil;registry();module=load(optional,name);module.on_game_start();update(700000)
assert(not memory.ADB_SHOW_SQUADS_SESSION_V1)
for _,entry in pairs(spots)do assert(entry.foreign)end
-- A missing simulation board must not consume the lease, dereference stale
-- objects or create a replacement board during world loading.
assert(select(1,dispatch(30,705000))=='executed');update(705000)
local board=SIMBOARD;SIMBOARD=nil;update(706000);update(709000)
assert(memory.ADB_SHOW_SQUADS_SESSION_V1:match('|30000$'),'Missing board consumed the countdown')
SIMBOARD=board;update(709000);update(739000)
for _,entry in pairs(spots)do assert(entry.foreign)end
simulation_objects.object_registry=nil -- the template does not depend on it
-- Large registries are processed in bounded batches, without a one-frame
-- burst of native map calls. Repeated effects extend, never shorten a lease.
for index=1,150 do squad(1000+index,'stalker','neutral',{5000+index})end
before=adds
assert(select(1,dispatch(30,740000))=='executed');update(740000)
assert(adds>before and adds-before<=64,'Unbounded native marker creation in one update')
assert(adds-before<150,'Large registry was scanned in one update')
update(740100);update(740200);update(740300)
for index=1,150 do assert(spots[key(1000+index,NEUTRAL)],'Batched scan lost a squad')end
update(770000)
assert(select(1,dispatch(10,780000))=='executed');update(780000);update(784000)
assert(memory.ADB_SHOW_SQUADS_SESSION_V1:match('|6000$'))
assert(select(1,dispatch(2,784000))=='executed');update(784000)
assert(memory.ADB_SHOW_SQUADS_SESSION_V1:match('|6000$'),'Repeated effect shortened the active timer')
assert(select(1,dispatch(20,784000))=='executed');update(784000)
assert(memory.ADB_SHOW_SQUADS_SESSION_V1:match('|20000$'),'Repeated effect did not extend the timer')
update(804000)
for _,entry in pairs(spots)do assert(entry.foreign)end
print('show squads smoke passed: no debug, current level, live/offline groups, standalone NPCs/mutants, two-line CP1251 hints, colors/counts, foreign ownership, F5/F9/travel/process RAM expiry')
