local core,_,optional=assert(arg[1]),assert(arg[2]),assert(arg[3])
local function module(root,name)
    local e=setmetatable({},{__index=_G});local f=assert(loadfile(root..'/'..name..'.script'));setfenv(f,e);f();_G[name]=e;return e
end
local callbacks={}
function RegisterScriptCallback(name,fn)callbacks[name]=callbacks[name] or {};table.insert(callbacks[name],fn)end
DIK_keys={DIK_PRIOR=201,DIK_END=207}
local common=module(core,'anomaly_donation_bridge_common')
local registry=module(core,'anomaly_donation_bridge_registry')
local runtime=module(core,'anomaly_donation_bridge_named_companion')
local objects,storage,vars,clients={},{},{},{}
local next_id=40;local fail_refresh=false
local actor={id=function()return 0 end,level_vertex_id=function()return 1 end,game_vertex_id=function()return 1 end,
    position=function()return {x=0,y=0,z=0}end,character_community=function()return 'actor_stalker'end}
db={actor=actor};xr_motivator={motivator_binder={}}
stalker_ids={sound_script=27};snd_type={talk=1}
actor.sound_prefix=function()end;actor.set_sound_mask=function()end
actor.add_sound=function()end;actor.remove_sound=function()end
actor.get_visual_name=function()end;actor.set_visual_name=function()end
actor.set_character_rank=function()end;actor.set_character_icon=function()end
local rating='novice, 3499, trainee, 6499, experienced, 8999, professional, 11999, veteran, 14499, expert, 17499, master, 26999, legend'
function system_ini()return {line_exist=function(_,s,k)return s=='game_relations' and k=='rating'end,
    r_string=function()return rating end}end
local speech_calls=0
sound_theme={npc_sound={play=function()speech_calls=speech_calls+1;return 'original'end}}
xr_sound={play_sound=function()speech_calls=speech_calls+1;return 'legacy'end}
alife_storage_manager={get_se_obj_state=function(s,create)
    if not storage[s.id] and create then storage[s.id]={name=s:name()}end
    return storage[s.id]
end}
function se_save_var(id,name,key,value)vars[id..':'..name]=vars[id..':'..name] or {};vars[id..':'..name][key]=value end
function se_load_var(id,name,key)return vars[id..':'..name] and vars[id..':'..name][key]end
local life={}
function alife()return life end
function life:actor()return actor end
function life:object(id)return objects[id]end
function life:release(s)objects[s.id]=nil end
axr_companions={companion_squads={},non_task_companions={}}
SIMBOARD={assign_squad_to_smart=function()end,setup_squad_and_group=function()end}
-- LuaBind may hand out distinct userdata wrappers for the same game object.
-- Its game_object class has no equality operator. Plain Lua table mocks hid
-- the local.2 regression; use real Lua userdata which rejects __eq here.
local proxy_targets=setmetatable({},{__mode='k'})
local proxy_type=newproxy(true)
local proxy_mt=getmetatable(proxy_type)
proxy_mt.__eq=function()error('No such operator [__eq] defined in class [game_object]')end
proxy_mt.__index=function(proxy,key)
    local target=assert(proxy_targets[proxy]);local value=target[key]
    if type(value)=='function' then return function(_,...)return value(target,...)end end
    return value
end
proxy_mt.__newindex=function(proxy,key,value)proxy_targets[proxy][key]=value end
local function game_object(target)
    if not target then return nil end
    if type(target)=='userdata' then target=assert(proxy_targets[target])end
    local proxy=newproxy(proxy_type);proxy_targets[proxy]=target;return proxy
end
level={object_by_id=function(id)return game_object(clients[id])end}
local latest
function life:create(section)
    assert(section=='online_offline_group');next_id=next_id+1
    local squad={id=next_id};objects[squad.id]=squad
    function squad:add_squad_member(sec)
        assert(sec=='sim_default_stalker_0','story profile/section must NEVER be spawned')
        next_id=next_id+1
        local s={id=next_id,online=false,section=sec};objects[s.id]=s;self.member=s;latest=s
        function s:name()return 'fresh_companion_'..self.id end
        function s:set_character_name(name)self.display=name end
        function s:community()return 'stalker'end
        function s:force_set_goodwill()end
        function s:set_rank(value)self.rank_value=value end
        return s.id
    end
    function squad:set_squad_relation(v)assert(v=='friend')end
    function squad:refresh()
        if fail_refresh then error('injected_setup_failure')end
        local s=self.member
        local data=alife_storage_manager.get_se_obj_state(s)
        assert(not data or not data.visual,'stock net_spawn must not attempt a visual change before NET exists')
        local n={visual=data and data.visual or 'actors/ordinary',faction='stalker',prefix='male/',sounds={},removed=0}
        n.rank_value=s.rank_value or 100;n.icon=data and data.icon_name or 'base_icon'
        for i=0,25 do n.sounds[i]='male' end -- missing native slot must not cause remove_sound to assert
        function n:id()return s.id end;function n:name()return s:name()end;function n:section()return s.section end
        function n:character_community()return self.faction end
        function n:set_character_community(v)self.faction=v end
        function n:alive()return true end
        function n:set_character_rank(value)self.rank_value=value end
        function n:set_character_icon(value)self.icon=value end
        function n:get_visual_name()return self.visual end
        function n:set_visual_name(v,force)
            assert(self.native_ready,'regression: changing visual inside net_spawn triggers emergency save with empty NET')
            assert(force==false,'use the wandering trader call signature')
            self.visual=v;self.visual_changes=(self.visual_changes or 0)+1
        end
        function n:sound_prefix(v)if v then self.prefix=v end;return self.prefix end
        function n:set_sound_mask(v)self.mask=v end
        function n:add_sound(path,count,kind,priority,mask,id,bone)
            if not self.sounds[id] then self.sounds[id]=self.prefix..path end
            return 0
        end
        function n:remove_sound(id)assert(self.sounds[id],'native remove would assert');self.sounds[id]=nil;self.removed=self.removed+1 end
        clients[s.id]=n
        for _,fn in ipairs(callbacks.npc_on_net_spawn or {})do fn(game_object(n),s)end
        assert(not n.visual_changes,'visual must remain unchanged until native spawn returns')
        n.native_ready=true
    end
    return squad
end
common.spawn_points=function()return {{position=actor:position(),vertex_id=1}},'requested_ring'end
common.rollback_server_objects=function(list)
    for _,s in ipairs(list)do if s.member then objects[s.member.id]=nil end;objects[s.id]=nil end
    return true
end
local visual_path='actors\\pack\\unique.ogf';local visual_available=true;local visual_safe=true;local safety_checks=0
anomaly_donation_bridge_npc_visuals={spawn_section=function()return 'sim_default_stalker_0'end,
    prepare_visual=function(path,section)
        assert(section=='sim_default_stalker_0');safety_checks=safety_checks+1
        if not visual_safe then return nil,'special_visual_missing_bone_bip01_spine2'end
        return path:gsub('%.ogf$','')..'.ogf'
    end,resolve=function(id)return id=='visual_1' and visual_available and visual_path or nil end,
    resolve_icon=function(id)return id=='visual_1' and 'ui_source_profile' or nil end,
    resolve_visual_icon=function(path)return path:gsub('%.ogf$','')=='actors\\pack\\unique' and 'ui_source_profile' or nil end}
anomaly_donation_bridge_catalog={capable=function()return true end,
    validate_revision=function(p)return p.catalog_revision=='rev','stale'end,
    is_available=function(s,k)return s=='sim_default_stalker_0' and k=='npc'end,
    is_spawn_safe=function(s,k)return s=='sim_default_stalker_0' and k=='npc'end,
    random_available=function(kind)return {section_id=kind=='npc_visual' and 'visual_1' or 'sim_default_stalker_0'}end,
    validate_section=function(p,key,kind)
        if p.catalog_revision~='rev' then return nil,'stale' end
        if kind=='npc_visual' and (p[key]~='visual_1' or not visual_available) then return nil,'invalid_visual'end
        return {section_id=p[key]}
    end}
module(optional,'anomaly_donation_bridge_effect_hardcoon_adb_special_companion').on_game_start()
assert(#registry.ids()==1 and registry.get('hardcoon.adb.special_companion'),'special template must work without ordinary effect installed')
module(optional,'anomaly_donation_bridge_effect_spawn_named_companion').on_game_start()
assert(#callbacks.npc_on_before_hit==1,'shared runtime must not apply protection twice')
local handler=registry.get('hardcoon.adb.special_companion')
local function params()return {section_id='visual_1',catalog_revision='rev',donor_name='Donor',incoming_damage_percent='50',nickname_key='END'}end
local context={actor=actor};assert(handler.can_execute(context,params()));assert(context.options.visual==visual_path)
assert(handler.execute(context)=='executed');local s=latest
assert(s.display=='Donor' and storage[s.id].name==s:name() and storage[s.id].adb_special_companion_visual=='actors\\pack\\unique')
assert(not storage[s.id].visual,'never hand a model to stock xr_motivator net_spawn')
assert(s.rank_value==17500 and storage[s.id].adb_special_companion_rank==17500,'master rank comes from this build, not a fixed vanilla number')
assert(storage[s.id].icon_name=='ui_source_profile' and storage[s.id].adb_special_companion_icon=='ui_source_profile')
assert(axr_companions.non_task_companions[s.id] and se_load_var(s.id,s:name(),'companion'))
local n=game_object(clients[s.id])
assert(n.removed==27 and n.prefix:find('adb_special_companion_silent',1,true))
for i=0,26 do assert(n.sounds[i]==n.prefix..'unused','no native male reaction may survive')end
assert(sound_theme.npc_sound.play({},s.id)==false and xr_sound.play_sound(n)==nil and speech_calls==0)
callbacks.npc_on_net_spawn[1](n,s);assert(n.removed==27,'duplicate callback must not rebuild speech')
assert(#callbacks.npc_on_net_spawn==1,'both templates must share one silence callback')
handler.update(50);assert(not n.visual_changes,'defer visual change past the spawn frame')
handler.update(150);assert(n.visual_changes==1 and n.visual==visual_path)
assert(n.rank_value==17500 and n.icon=='ui_source_profile')
handler.update(250);assert(n.visual_changes==1,'never reload the model on every update')
local hit={power=.8};callbacks.npc_on_before_hit[1](clients[s.id],hit,0,{ret_value=true});assert(hit.power==.4)
handler.on_game_load();handler.update(1000)
assert(storage[s.id].adb_special_companion_visual=='actors\\pack\\unique','save/load must preserve selected appearance')
hit={power=.8};callbacks.npc_on_before_hit[1](clients[s.id],hit,0,{ret_value=true});assert(hit.power==.4,'damage must recover from saved object markers')
local p=params();p.random_npc='true';p.section_id='';context={actor=actor};assert(handler.can_execute(context,p) and context.options.visual==visual_path)
p=params();p.catalog_revision='old';assert(not handler.can_execute({actor=actor},p))
visual_available=false;assert(not handler.can_execute({actor=actor},params()));visual_available=true
local saved_storage=alife_storage_manager;alife_storage_manager=nil;assert(not handler.is_supported({actor=actor}));alife_storage_manager=saved_storage
fail_refresh=true;context={actor=actor};assert(handler.can_execute(context,params()));assert(handler.execute(context)=='rejected')
assert(not axr_companions.non_task_companions[latest.id] and not storage[latest.id].visual,'failed setup must remove only its own registration/override')
assert(not storage[latest.id].adb_special_companion_visual)
fail_refresh=false
local normal=registry.get('spawn_named_companion');p=params();p.section_id='sim_default_stalker_0';context={actor=actor}
assert(normal.can_execute(context,p) and not context.options.visual);assert(normal.execute(context)=='executed')
assert(not storage[latest.id],'ordinary companions must not acquire a special appearance')
assert(clients[latest.id].prefix=='male/' and clients[latest.id].removed==0)
assert(clients[latest.id].rank_value==100 and clients[latest.id].icon=='base_icon','ordinary NPC rank and portrait remain unchanged')
assert(sound_theme.npc_sound.play({},latest.id)=='original' and xr_sound.play_sound(clients[latest.id])=='legacy')
callbacks.npc_on_net_destroy[1](n)
assert(sound_theme.npc_sound.play({},s.id)=='original','destroy must clear the online mute registration')
callbacks.npc_on_net_spawn[1](n,s)
assert(n.removed==54 and sound_theme.npc_sound.play({},s.id)==false,'persisted marker must restore silence after going online again')
storage[s.id].adb_special_companion_rank=nil;storage[s.id].adb_special_companion_icon=nil
n.rank_value=100;n.icon='base_icon'
handler.update(1200);assert(n.visual_changes==1,'already correct restored model needs no native setter')
assert(n.rank_value==17500 and n.icon=='ui_source_profile' and storage[s.id].icon_name=='ui_source_profile','upgrade old saved special companions with master rank and matching portrait')
-- A local.1 save must be migrated before the stock binder sees its visual key.
storage[s.id].visual='actors/pack/unique.ogf'
assert(alife_storage_manager.get_se_obj_state(s).visual==nil and storage[s.id].adb_special_companion_visual)
storage[s.id].visual='actors/another-mod'
assert(alife_storage_manager.get_se_obj_state(s).visual=='actors/another-mod','do not erase another mod override')
storage[s.id].visual=nil
storage[s.id].name='another object'
callbacks.npc_on_net_destroy[1](n);callbacks.npc_on_net_spawn[1](n,s)
assert(n.removed==54,'stale server identity must not mute an unrelated NPC')
storage[s.id].visual='actors\\pack\\unique'
assert(alife_storage_manager.get_se_obj_state(s).visual,'stale saved identity must not be migrated')
-- Pending visual work is tied to an exact online object, not only a reused ID.
storage[s.id].name=s:name();storage[s.id].visual=nil
callbacks.npc_on_net_spawn[1](n,s)
clients[s.id]=clients[latest.id]
handler.update(1400);assert(clients[latest.id].visual_changes==nil,'never apply old model to another client')
local saved_add=actor.add_sound;actor.add_sound=nil
assert(not handler.can_execute({actor=actor},params()),'missing required speech API must reject special spawn')
assert(normal.can_execute({actor=actor},p),'ordinary companion must not require silence API');actor.add_sound=saved_add
rating='novice, 299, experienced, 599, veteran, 899, master'
local other={actor=actor};assert(handler.can_execute(other,params()) and other.options.rank==900,'support modpack rank thresholds')
rating='novice, 299, veteran';assert(not handler.can_execute({actor=actor},params()),'missing master rank must be rejected before spawn')
print('PASS special companion: LuaBind userdata rejects equality; aliases, deferred visual, mute/destroy, persisted master/profile icon, custom ranks, save migration and ordinary isolation')

rating='novice, 299, experienced, 599, veteran, 899, master'
local safe_context={actor=actor};assert(handler.can_execute(safe_context,params()))
local before_id=next_id;visual_safe=false
local allowed,_,reason=handler.can_execute({actor=actor},params())
assert(not allowed and reason=='special_visual_missing_bone_bip01_spine2')
local status,why=handler.execute(safe_context)
assert(status=='rejected' and why=='special_visual_missing_bone_bip01_spine2' and next_id==before_id,'unsafe model must be rejected BEFORE creating a squad/member')
clients[s.id]=n;storage[s.id].name=s:name();storage[s.id].visual=nil
callbacks.npc_on_net_destroy[1](n);callbacks.npc_on_net_spawn[1](n,s)
local changes=n.visual_changes;handler.update(2000)
assert(n.visual_changes==changes and safety_checks>0,'unsafe saved appearance must never reach native visual setter')
print('PASS special companion safety: validation before spawn, recheck before execution, saved-appearance guard')
