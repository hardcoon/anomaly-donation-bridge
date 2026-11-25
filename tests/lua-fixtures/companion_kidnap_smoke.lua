local core=assert(arg[1])
local config_file=assert(io.open(core.."/../configs/scripts/anomaly_donation_bridge_kidnapping_standalone.ltx","rb"))
local standalone_config=config_file:read("*a")
config_file:close()
local checks=0
local function check(v,s) checks=checks+1;assert(v,s) end
local function module(name)
    local e=setmetatable({},{__index=_G});local chunk=assert(loadfile(core.."/"..name..".script"))
    setfenv(chunk,e);chunk();return e
end
local function clone(v)
    if type(v)~="table" then return v end
    local r={};for k,x in pairs(v) do r[k]=clone(x) end;return r
end
local clock,ms,frame,state,objects,clients,vars,callbacks,descriptor,manager,messages,logs,ui,native_tasks,latest_task
local next_id,creates,teleports,timejumps,kills,rescues,foreign_lock,resources,fail_guard,fail_camera
local input,hud,cameras,pp,level_here,sleep_callbacks
function vector() return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end} end
function time_global() return ms end
function device() return {frame=frame,precache_frame=0,is_paused=function() return false end} end
function app_ready() return true end
function Check_UI() return ui end
actor_menu={is_hud_free=function() return not ui end}
function RegisterScriptCallback(name,f) callbacks[name]=f end
function printf(fmt,...) logs[#logs+1]=string.format(fmt,...) end
function get_object_story_id() return nil end
function IsStalker(_,class) return class=="stalker" end
function se_load_var(id,name,k) return vars[id] and vars[id][k] end
function se_save_var(id,name,k,v) vars[id]=vars[id] or {};vars[id][k]=v end
game={get_game_time=function() return {seconds=clock,diffSec=function(self,other) return self.seconds-other.seconds end} end,
    translate_string=function(id)
        if id=="hostage_companion_task_1_actor_reply" then return "Stock: come with me" end
        if id=="hostage_companion_task_1_start_dialog_1" then return "Stock: please help" end
        error("unexpected stock dialogue string: "..id)
    end}
modules={stype_stalker=1}
function ini_file(path) return {path=path} end
task={additional=1,in_progress=1,completed=2,fail=0}
function CGameTask()
    local t={state=task.in_progress}
    for _,name in ipairs({"id","type","title","description","icon_name","priority","map_location","map_object_id"}) do
        t["set_"..name]=function(self,v) self[name]=v;if name=="icon_name" then self.icon=v end end
    end
    function t:add_complete_func(fn) self.complete=fn end
    function t:add_fail_func(fn) self.fail=fn end
    function t:add_on_fail_func(fn) self.on_fail=fn end
    function t:get_map_location() return self.map_location end
    function t:change_map_location(spot,id) self.map_location=spot;self.map_object_id=id;self.map_changes=(self.map_changes or 0)+1 end
    return t
end
utils_data={CTime_to_table=function(t) return {seconds=t.seconds} end,CTime_from_table=function(t) return t end}
game_graph=function() return {vertex=function(_,id) return {level_id=function() return id end} end} end
function getFS() return {exist=function() return resources end} end
function SendScriptCallback(name)
    check(name=="actor_on_sleep","unexpected script callback")
    sleep_callbacks=sleep_callbacks+1
end
local function client(s)
    local n={sid=s.id,valid=true}
    local function live(self) assert(self.valid,"stale client userdata") end
    function n:id() live(self);return self.sid end
    function n:character_name() live(self);return s.nickname end
    function n:invulnerable(v) live(self);if v~=nil then s.invul=v end;return s.invul end
    function n:character_community() live(self);return s.faction end
    function n:set_character_community(v) live(self);s.faction=v end
    function n:force_set_goodwill(v,target) live(self);s.goodwill=v end
    function n:has_info(id) live(self);return s[id]==true end
    function n:give_info_portion(id) live(self);s[id]=true end
    function n:disable_info_portion(id) live(self);s[id]=nil end
    function n:inactualize_patrol_path() live(self);s.path_reset=true end
    function n:kill(who) live(self);check(not ui,"must not kill under UI");s.living=false;kills=kills+1 end
    if db and db.storage then
        db.storage[s.id]=db.storage[s.id] or {}
    end
    if vars[s.id] and vars[s.id].companion_cannot_teleport then
        check(not vars[s.id].companion,"custom hostage scheme would crash stock companion loading")
        -- xr_motivator disables this infoportion before ordinary initialize_obj.
        s.npcx_is_companion=nil
    end
    return n
end
local function object(id,section,x,gvid,group)
    local s={id=id,internal=section..id,section=section,position={x=x or 0,y=0,z=0},m_game_vertex_id=gvid or 1,
        m_level_vertex_id=id+20,m_smart_terrain_id=65535,group_id=group or 65535,faction="stalker",living=true,invul=false}
    function s:name() return self.internal end
    function s:section_name() return self.section end
    function s:clsid() return self.section=="online_offline_group" and "group" or "stalker" end
    function s:alive() return self.living end
    function s:community() return self.faction end
    function s:force_set_goodwill(g) self.goodwill=g end
    function s:kill() check(not ui,"offline kill under UI");self.living=false;kills=kills+1 end
    if section=="online_offline_group" then
        s.members={}
        function s:squad_members()
            local key
            return function() key=next(self.members,key);if key then return {id=key,object=objects[key]} end end
        end
        function s:register_member(id) self.members[id]=true;objects[id].group_id=self.id end
        function s:unregister_member(id) self.members[id]=nil;objects[id].group_id=65535 end
        function s:npc_count() local count=0;for _ in pairs(self.members) do count=count+1 end;return count end
    end
    objects[id]=s;return s
end
local life={}
function alife() return life end
function life:object(id,no_assert)
    check(no_assert==true or objects[id]~=nil,"unsafe missing ALife lookup")
    return objects[id]
end
function life:level_name(id) return id==1 and "l01_escape" or "zaton" end
function life:create(section,p,lvid,gvid)
    if section~="online_offline_group" and (fail_guard==true or fail_guard==creates) then error("guard create refused") end
    creates=creates+1;next_id=next_id+1
    local s=object(next_id,section,p.x,gvid);s.position=clone(p);s.m_level_vertex_id=lvid
    if section~="online_offline_group" and gvid==level_here then clients[s.id]=client(s) end
    return s
end
function life:release(s) objects[s.id]=nil;clients[s.id]=nil end
function life:teleport_object(id,gvid,lvid,p)
    teleports=teleports+1
    local s=assert(objects[id]);s.position=clone(p);s.m_game_vertex_id=gvid;s.m_level_vertex_id=lvid
    if clients[id] then clients[id].valid=false;clients[id]=nil end
    -- The old pointer is unusable, just as native switch_offline destroys it.
end
anomaly_donation_bridge_common=module("anomaly_donation_bridge_common")
anomaly_donation_bridge_registry={register=function(id,d) descriptor=d;return true end}
anomaly_donation_bridge_catalog={
    validate_sections=function(p) if p.npc_sections=="bad" then return nil,"unsafe" end
        return {{section_id="guard_a"},{section_id="guard_b"}} end,
    is_spawn_safe=function(s,kind) return kind=="npc" and (s=="guard_a" or s=="guard_b") end}
anomaly_donation_bridge_named_companion={
    snapshot_registration=function(s) return {vars={special="unchanged"},fields={visual=s.visual}} end,
    release_following=function(s,consumed) check(consumed==false,"must retain donor appearance");s.following=false end,
    restore_registration=function(s,r) s.following=true;check(s.visual==r.fields.visual,"appearance changed") end}
alife_storage_manager={get_state=function() return state end}
game_relations={set_npcs_relation=function(n,actor,rel) objects[n:id()].relation=rel end}
news_manager={send_tip=function(_,message) messages[#messages+1]=message end}
local W,M
local fail_task,gamma_squad_lookup
local function reset()
    clock,ms,frame=100000,0,100;state={};objects={};clients={};vars={};callbacks={};messages={};logs={};ui=false
    native_tasks={};latest_task=nil
    fail_task,gamma_squad_lookup=false,false
    task_functor={};task_status_functor={};xr_effects={}
    task_manager={get_task_manager=function() return manager end}
    sleep_callbacks=0
    next_id,creates,teleports,timejumps,kills,rescues=1000,0,0,0,0,0
    foreign_lock,resources,fail_guard,fail_camera=false,true,false,false
    input,hud,cameras,pp,level_here=true,true,{},nil,1
    local g=object(5,"online_offline_group")
    local victim=object(10,"special_companion",0,1,5)
    victim.nickname="\205\232\234";victim.visual="custom.ogf";victim.items={knife=902,wpn=903};victim.following=true
    g:register_member(10);clients[10]=client(victim)
    object(11,"another_companion",2,1,5);objects[11].nickname="Second";objects[11].visual="second.ogf"
    objects[11].items={knife=904,wpn=905};g:register_member(11);clients[11]=client(objects[11])
    vars[10]={companion=true};vars[11]={companion=true}
    local actor={id=function() return 0 end,alive=function() return true end,is_talking=function() return ui end,
        character_name=function() return "\195\229\240\238\233" end,
        game_vertex_id=function() return level_here end,position=function() return {x=0,y=0,z=0} end,
        get_task=function(_,id,in_progress)
            local t=native_tasks[id]
            if t then return (not in_progress or t.state==task.in_progress) and t or nil end
            return manager.task_info[id]
        end,
        give_task=function(_,t,seconds,check_existing,ttl)
            if fail_task then error("native task refused") end
            check(not native_tasks[t.id],"duplicate native task")
            local r=state.adb_companion_kidnapping_v1.records[t.id]
            check(seconds==r.hours*3600 and ttl==seconds,"native deadline and display timer must match game hours")
            t.deadline=clock+seconds;native_tasks[t.id]=t
        end}
    db={actor=actor,storage={[0]={},[10]={},[11]={}}}
    level={present=function() return true end,object_by_id=function(id) return clients[id] end,
        change_game_time=function(_,h,minutes) check(h==1 and minutes==0,"one hour sleep");timejumps=timejumps+1;clock=clock+3600 end,
        add_pp_effector=function() pp=true end,remove_pp_effector=function() pp=nil end,
        set_pp_effector_factor=function() end,
        add_cam_effector=function(path,id,loop,callback) if fail_camera then error("camera refused") end;cameras[id]=callback end,
        remove_cam_effector=function(id) cameras[id]=nil end,
        disable_input=function() input=false end,enable_input=function() input=true end,
        show_weapon=function(v) hud=v end,show_indicators=function() hud=true end,hide_indicators_safe=function() hud=false end}
    simulation_objects={base_smarts={base=true}}
    local camp=object(30,"smart",300,1);camp.internal="camp"
    local base=object(31,"smart",400,1);base.internal="base"
    local remote=object(32,"smart",300,2);remote.internal="remote"
    SIMBOARD={smarts_by_names={camp=camp,base=base,remote=remote},smarts={[30]={},[31]={},[32]={}},
        assign_squad_to_smart=function(self,s,id)
            if gamma_squad_lookup and type(s.scripted_target)=="number" then
                error("attempt to index global 'obj' (a nil value)")
            end
            if type(s.scripted_target)=="string" then
                local target=self.smarts_by_names[s.scripted_target]
                check(target and target.id==id,"squad used a name that the engine could not resolve")
            end
            s.smart_id=id
        end,
        setup_squad_and_group=function() end,
        remove_squad=function(_,s) check(s:npc_count()==0,"removed nonempty group");objects[s.id]=nil end}
    axr_task_manager={hostages_by_id={[333]=999}}
    dialogs={rescue_hostage=function(_,n)
        check(not ui,"native rescue must wait for dialogue close")
        axr_task_manager.hostages_by_id[n:id()]=nil
    end}
    sim_offline_combat={task_squads={}}
    axr_companions={non_task_companions={[10]=true,[11]=true},companion_squads={[5]=g},
        remove_from_actor_squad=function(n)
            local s=objects[n:id()];axr_companions.companion_squads[s.group_id]=nil
            axr_companions.non_task_companions[s.id]=nil
        end,
        add_to_actor_squad=function(n)
            check(not ui,"rescue must wait until dialogue closes")
            rescues=rescues+1
            axr_companions.non_task_companions[n:id()]=true;vars[n:id()].companion=true
            n:give_info_portion("npcx_is_companion")
            db.storage[n:id()].ini_filename="scripts\\beh_companion.ltx"
            db.storage[n:id()].section_logic="logic";db.storage[n:id()].active_section="beh@base"
            objects[n:id()].target="actor"
        end,
        setup_companion_logic=function(n)
            check(clients[n:id()].valid,"scheme on stale NPC")
            -- Native helper always makes this NPC a companion again.
            n:give_info_portion("npcx_is_companion")
        end}
    xr_logic={
        set_new_scheme_and_logic=function() error("captive must not switch through companion setup") end,
        configure_schemes=function(n,ini,path,stype,logic)
            check(not n:has_info("npcx_is_companion"),"configured captive with companion marker still enabled")
            check(logic=="adb_kidnapping_logic","captive reload would activate inherited actor-following logic")
            local st=db.storage[n:id()];st.ini_filename=path;st.section_logic=logic;st.active_section=nil
        end,
        activate_by_section=function(n,ini,section,gulag,loaded)
            check(section=="beh@adb_kidnapped" and loaded==false,"must activate native hostage scheme directly")
            db.storage[n:id()].active_section=section
            local standalone=ini.path:find("_standalone.ltx",1,true)
            objects[n:id()].target=standalone and standalone_config:match("target%s*=%s*([%w_]+)") or "task_target_hostage"
            objects[n:id()].invul=true
        end}
    manager={task_info={}}
    function manager:give_task(id)
        local t={};task_functor.adb_companion_kidnap_text(id,"title",nil,t)
        function t:set_icon_name(value) self.icon=value end
        check(t.wait_time==24*3600,"native quest timer must use game seconds")
        self.task_info[id]=t
    end
    anomaly_donation_bridge_relife_companions=module("anomaly_donation_bridge_relife_companions")
    W=module("anomaly_donation_bridge_kidnapping_world");anomaly_donation_bridge_kidnapping_world=W
    M=module("anomaly_donation_bridge_kidnapping");anomaly_donation_bridge_kidnapping=M;M.register()
end
local function job()
    local d=state.adb_companion_kidnapping_v1
    return d and d.records[latest_task or d.scene or "adb_companion_kidnap_live_1"]
end
local function tick(count,driver)
    for i=1,count or 1 do
        ms=ms+1000;frame=frame+1
        for id in pairs(cameras) do
            cameras[id]=nil
            if id==192902 then M.fall_finished() else M.wake_finished() end
        end
        if driver~="actor" and descriptor.update then descriptor.update(ms) end
        if driver~="registry" and callbacks.actor_on_update then callbacks.actor_on_update() end
        for id,t in pairs(native_tasks) do
            if t.state==task.in_progress then
                if clock>=t.deadline or t.fail and M.native_fail(id) then
                    t.state=task.fail;M.native_failed(id)
                elseif t.complete and M.native_complete(id) then t.state=task.completed end
            end
        end
    end
end
local function start(overrides)
    local p={npc_sections="guard_a,guard_b",enemy_count="12",deadline_hours="24",any_location="false"}
    for k,v in pairs(overrides or {}) do p[k]=v end
    local c={actor=db.actor,command={id="test"}}
    local ok,status,why=descriptor.can_execute(c,p);check(ok,tostring(status)..":"..tostring(why))
    check(descriptor.execute(c)=="executed","kidnap not accepted");latest_task=c.options.task;return c
end
local function captive()
    tick(23);check(job().phase=="captive",table.concat(logs,"\n"))
    tick();check(job().task_given,"native task not issued")
end
reset();math.randomseed(4);start();local victim=clone(job().victim);captive()
local r=job();local original=objects[victim.id]
local original_items=clone(original.items)
check(timejumps==1 and clock==103600,"sleep replayed or wrong duration")
check(sleep_callbacks==1,"default startup stopped notifying the stock sleep handlers")
check(teleports==1 and #r.guards==12 and creates==14,"one captive move, two squads and twelve guards")
check(original.visual=="custom.ogf" or original.section=="another_companion","victim model altered")
check(original.nickname==victim.nickname,"victim nickname altered")
check(objects[5]:npc_count()==1,"other companion detached")
check(objects[r.hostage_group.id].scripted_target==nil and objects[r.guard_group.id].scripted_target==r.camp.id,
    "the checkbox must select the alternate target route")
check(not axr_companions.companion_squads[r.hostage_group.id] and not vars[r.victim.id].companion,
    "captive still belongs to actor party and cannot reload custom scheme")
check(axr_task_manager.hostages_by_id[333]==999 and axr_task_manager.hostages_by_id[victim.id]==r.camp.id,
    "standard kidnapping changed a foreign hostage or failed to register its own")
check(input and hud and not pp,"cinema/input leaked")
check(messages[1]:find(anomaly_donation_bridge_common.game_text_cp1251("Приходи, обкашляем вопросики"),1,true),"wrong PDA wording")
local gold="%c[255,238,153,26]"..victim.nickname.."%c[default]"
check(messages[1]:sub(1,#db.actor:character_name())==db.actor:character_name() and
    messages[1]:find(gold,1,true) and not messages[1]:find(anomaly_donation_bridge_common.game_text_cp1251("Э баклан"),1,true),
    "PDA greeting must begin with the current hero's engine name and highlight the captive in gold")
check(native_tasks[r.task].description:find(gold,1,true),"PDA quest description lost the golden NPC name")
check(native_tasks[r.task].map_location=="secondary_task_location","kidnapping must use the original donor task marker")
for _,g in pairs(r.guards) do check(objects[g.id].faction=="arena_enemy" and objects[g.id].goodwill==-5000,"guard not hostile participant") end
local flags={};callbacks.on_enemy_eval(clients[r.guards[1].id],{id=function() return 0 end},flags)
check(flags.override and flags.result,"guard must attack actor")
flags={};callbacks.on_enemy_eval(clients[r.guards[1].id],{id=function() return r.victim.id end},flags)
check(flags.override and not flags.result,"guard must ignore captive")
clients[r.victim.id]=client(original);callbacks.npc_on_net_spawn(clients[r.victim.id]);tick()
check(not clients[r.victim.id]:has_info("npcx_is_companion"),"captive regained native companion marker and follows actor")
check(original.target=="task_target_hostage","standard mode did not retain the stock captive scheme")
check(native_tasks[r.task].icon=="ui_inGame2_V_poiskah_Soroki","active task did not receive stock texture")
check(not descriptor.is_blocking(),"whole rescue quest must not block unrelated effects")
local retry={};local accepted,status,reason=descriptor.can_execute(retry,{npc_sections="guard_a"})
check(accepted,"another available companion must allow another kidnapping: "..tostring(reason))
-- Reproduce the old helper's companion mark / following scheme, including
-- external companion mode callbacks. Tick must repair actual native storage.
clients[r.victim.id]:give_info_portion("npcx_is_companion")
db.storage[r.victim.id].active_section="beh@base"
vars[r.victim.id].companion=true;axr_companions.non_task_companions[r.victim.id]=true
tick()
check(not clients[r.victim.id]:has_info("npcx_is_companion") and not vars[r.victim.id].companion and
    not axr_companions.non_task_companions[r.victim.id],"native party membership was not fully detached")
check(db.storage[r.victim.id].active_section=="beh@adb_kidnapped" and original.target=="task_target_hostage",
    "captive remained in the actor-following scheme")
local function localized(s) return anomaly_donation_bridge_common.game_text_cp1251_lossy(s,"?") end
local rescue_greeting=game.translate_string("hostage_companion_task_1_start_dialog_1")
check(M.answer_text()==game.translate_string("hostage_companion_task_1_actor_reply"),"standard rescue reply changed")
ui=true;M.request_rescue(db.actor,clients[r.victim.id]);check(r.phase=="captive","rescued with guards alive")
ui=false;for _,g in pairs(r.guards) do objects[g.id].living=false end
check(M.dialog_text()==rescue_greeting,"captive dialogue is missing")
ui=true;M.request_rescue(db.actor,clients[r.victim.id]);tick();check(r.phase=="release" and rescues==0,"native rescue mutated active dialog")
ui=false;tick();check(r.phase=="rescued" and rescues==1,"not restored to squad")
check(original.group_id==5 and original.invul==victim.invulnerable,"original group/invulnerability not restored")
check(original.items.knife==original_items.knife and original.items.wpn==original_items.wpn,"real inventory identity changed")
check(not axr_task_manager.hostages_by_id[victim.id] and not vars[victim.id].companion_cannot_teleport,"hostage flags leaked")
check(M.native_complete(r.task),"native task not completed")

reset();start();tick(7);local before=creates;local jumped=timejumps
callbacks.on_before_save_input();state=clone(state);callbacks.on_game_load();tick(25)
check(job().phase=="captive" and creates==14 and timejumps==jumped,"mid-scene load duplicated NPCs or sleep")
check(input and hud,"load leaked input")
before=creates;local started=job().started.seconds;state=clone(state);callbacks.on_game_load();tick(10)
check(creates==before and job().started.seconds==started,"captivity load respawned/reset deadline")
clock=clock+24*3600;ui=true;tick(4);check(kills==0,"timeout killed while UI open")
ui=false;tick();check(job().phase=="failed" and kills==1,"deadline did not kill actual captive")
tick(10);check(kills==1 and #messages==2,"timeout kill/message repeated")

reset();SIMBOARD.smarts_by_names.camp=nil
local c={};check(not descriptor.can_execute(c,{npc_sections="guard_a"}),"current map selected remote camp")
start({any_location="true"});captive();check(job().camp.id==32,"any location did not select remote smart")
check(objects[job().victim.id].m_game_vertex_id==2,"same victim was not transferred cross-map")

reset();resources=false;start();captive();check(input and hud and timejumps==1,"missing cameras blocked quest")
reset();fail_camera=true;start();captive();check(input and hud and not pp,"refused camera leaked controls")
reset();start();fail_guard=true;tick(25)
check(job().phase=="cancelled" and input and hud,"staging failure did not roll back")
check(objects[job().victim.id].group_id==5,"rollback lost companion")

reset();start();captive();local cancelled=job()
M.native_failed(cancelled.task);tick(8)
check(cancelled.phase=="cancelled" and objects[cancelled.victim.id].group_id==5,"quest cancellation stranded captive")
reset();start();captive();local saved_group=job().victim.group
objects[saved_group]=nil -- empty original squads can be removed by the simulation
for _,g in pairs(job().guards) do objects[g.id].living=false end
local s=objects[job().victim.id];clients[s.id]=client(s)
M.request_rescue(db.actor,clients[s.id]);tick(8)
check(job().phase=="rescued" and s.group_id~=saved_group and s:alive(),"missing original squad recreated the NPC or prevented rescue")

reset();db.storage[0].disable_input_time=123
check(not descriptor.can_execute({}, {npc_sections="guard_a"}) and input,"must not steal another mod's input lease")
reset();start();captive();local timeout=job();clients[timeout.victim.id]=client(objects[timeout.victim.id])
callbacks.npc_on_net_spawn(clients[timeout.victim.id]);tick();clock=clock+24*3600;tick(4)
check(timeout.phase=="failed" and kills==1,"online captive was not killed safely")
check(not vars[timeout.victim.id].companion,"corpse would reload the private captive section through companion logic")

reset();ui=true
check(not descriptor.can_execute({}, {npc_sections="guard_a"}),"started under inventory/dialog UI")
ui=false;axr_companions.non_task_companions={};axr_companions.companion_squads={}
check(not descriptor.can_execute({}, {npc_sections="guard_a"}) and timejumps==0,"no companions must defer without sleeping")
reset();start();captive();local id=job().victim.id;objects[id].internal="replacement"
tick();check(job().phase=="failed" and kills==0,"reused ID was modified")

-- Existing local.1 quest: keep its original native slot, victim and deadline;
-- repair the follower that already wandered away from the camp.
reset();start();captive()
local old=job();local previous_id=old.task;local old_stamp=old.started.seconds
local native=native_tasks[previous_id];native.icon="ui_inGame2_Odin_vystrel"
native.map_location="secondary_task_location_complex_timer"
native_tasks={};old.task="adb_companion_kidnap_01";old.direct_task=nil;old.captive_fixed_v2=nil;old.camp.target=nil
local legacy=state.adb_companion_kidnapping_v1
legacy.records[previous_id]=nil;legacy.records[old.task]=old;legacy.ongoing=nil;legacy.scene=nil;legacy.active=old.task
latest_task=old.task;manager.task_info[old.task]=native
objects[old.victim.id].position={x=0,y=0,z=0}
objects[old.hostage_group.id].scripted_target=nil
objects[old.guard_group.id].scripted_target=old.camp.id
axr_task_manager.hostages_by_id[old.victim.id]=old.camp.id
local c_before,j_before,t_before=creates,timejumps,teleports
callbacks.on_game_load();tick(8)
check(old.phase=="captive" and old.started.seconds==old_stamp and creates==c_before and timejumps==j_before,
    "old active quest was restarted while fixing captive membership")
check(teleports==t_before+1 and objects[old.victim.id].position.x==old.camp.point.x and native.icon=="ui_inGame2_V_poiskah_Soroki",
    "old follower was not returned once to camp with the stock task image")
check(native.map_location=="secondary_task_location" and native.map_changes==1,
    "existing quest must change its map marker once without restarting the deadline")
check(axr_task_manager.hostages_by_id[old.victim.id]==old.camp.id and axr_task_manager.hostages_by_id[333]==999,
    "legacy migration damaged the original or a foreign hostage record")
tick(8);check(teleports==t_before+1,"old captive repair keeps teleporting")

-- No single-quest or 32-slot cap. Each eligible human is an independent
-- captive; the engine persists uniquely named CGameTask instances.
reset()
for i=200,234 do
    local s=object(i,"companion_"..i,0,1,5);s.nickname="Companion "..i;s.visual="model_"..i
    s.items={knife=i+5000,wpn=i+6000};objects[5]:register_member(i)
    clients[i]=client(s);axr_companions.non_task_companions[i]=true
end
local captured={}
for i=1,37 do
    start({enemy_count="1",deadline_hours="168"});captive()
    local r=job();captured[#captured+1]=r
    check(r.phase=="captive" and native_tasks[r.task] and not descriptor.is_blocking(),"parallel captive blocked next kidnapping")
end
local d=state.adb_companion_kidnapping_v1
local count=0;for _ in pairs(d.ongoing) do count=count+1 end
check(count==37 and kills==0,"parallel kidnapping hit an artificial slot limit")
local ok,status,reason=descriptor.can_execute({}, {npc_sections="guard_a"})
check(not ok and reason=="kidnap_no_eligible_companion","must wait only when no eligible companions remain")
local saved_creates,saved_jumps=creates,timejumps
state=clone(state);callbacks.on_game_load();tick(8)
check(creates==saved_creates and timejumps==saved_jumps,"loading parallel quests duplicated guards/sleep")
local first=job()
local second=state.adb_companion_kidnapping_v1.records[captured[1].task]
local n1=client(objects[first.victim.id]);clients[first.victim.id]=n1;callbacks.npc_on_net_spawn(n1)
local n2=client(objects[second.victim.id]);clients[second.victim.id]=n2;callbacks.npc_on_net_spawn(n2)
for _,g in pairs(first.guards) do objects[g.id].living=false end
check(M.dialog_text(db.actor,n1)==rescue_greeting and M.dialog_text(db.actor,n2)~=rescue_greeting,
    "dialogue used another captive's guards")
M.request_rescue(db.actor,n1);tick(3)
check(first.phase=="rescued" and second.phase=="captive" and M.native_complete(first.task) and not M.native_complete(second.task),
    "rescuing one captive completed another quest")

-- The checkbox selects a private actor-update driver and stages one guard per tick.
reset();start({alternative_start="true"})
local alternate=job();local original=objects[alternate.victim.id]
local visual,items,nickname=original.visual,clone(original.items),original.nickname
tick(4,"registry")
check(alternate.phase=="out" and timejumps==0 and creates==0,"registry advanced an alternative record")
tick(25,"actor")
check(alternate.phase=="captive" and alternate.task_given and #messages==1,
    "actor-update driver could not issue quest/PDA message without registry updates")
check(timejumps==1 and clock==103600 and sleep_callbacks==0 and input and hud and not pp,
    "alternative startup changed sleep duration, called foreign sleep handlers or leaked controls")
check(creates==14 and teleports==1 and #alternate.guards==12 and
    original.visual==visual and original.nickname==nickname and original.items.knife==items.knife and original.items.wpn==items.wpn,
    "alternative startup replaced the NPC/inventory or duplicated its guards")
check(native_tasks[alternate.task].map_object_id==alternate.victim.id and
    native_tasks[alternate.task].map_location=="secondary_task_location","alternative native task points at another object")
check(objects[alternate.hostage_group.id].scripted_target=="camp" and objects[alternate.guard_group.id].scripted_target=="camp" and
    not axr_task_manager.hostages_by_id[alternate.victim.id],"alternate mode used the standard target or a stock quest")
local saved_creates,saved_jumps,started=creates,timejumps,alternate.started.seconds
callbacks.actor_on_update();callbacks.actor_on_update()
check(creates==saved_creates and timejumps==saved_jumps and #messages==1,"same-frame callbacks repeated work")
state=clone(state);callbacks.on_game_load();tick(10,"actor");alternate=job()
check(creates==saved_creates and timejumps==saved_jumps and alternate.started.seconds==started,
    "independent load callback reset the quest/deadline or replayed sleep/spawn")
for _,g in pairs(alternate.guards) do objects[g.id].living=false end
clients[alternate.victim.id]=client(original)
tick(1,"actor")
check(original.target=="cover_wait" and M.answer_text(db.actor,clients[alternate.victim.id])==localized("Хочешь жить — идём со мной!"),
    "alternate mode lost the private scheme or rescue dialogue")
ui=true;M.request_rescue(db.actor,clients[alternate.victim.id]);tick(3,"actor")
check(alternate.phase=="release" and rescues==0,"alternative rescue changed NPCs inside an active dialogue")
ui=false;tick(3,"actor")
check(alternate.phase=="rescued" and rescues==1 and original.group_id==5,"alternative captive was not restored to its squad")

-- Save halfway through guard creation, then stop the main bridge update pump.
reset();start({alternative_start="true"})
for _=1,20 do
    if job().spawn_cursor>=4 then break end
    tick(1,"actor")
end
check(job().spawn_cursor==4,"alternate mode did not reach the checkpoint")
local halfway=job();local spawned,jumped,moved=creates,timejumps,teleports
local cursor=halfway.spawn_cursor
callbacks.actor_on_update();callbacks.actor_on_update()
check(creates==spawned and halfway.spawn_cursor==cursor,"duplicate frame callbacks advanced guard creation")
callbacks.on_before_save_input();state=clone(state)
callbacks.on_level_changing();tick(15,"actor")
check(creates==spawned and timejumps==jumped and teleports==moved,"alternative driver ran during a level transition")
callbacks.on_game_load();tick(30,"actor")
check(job().phase=="captive" and creates==14 and timejumps==jumped and teleports==moved,
    "saved alternative stages replayed transfer/sleep or lost the spawn cursor")

-- A modded motivator may publish the native client before db.storage exists.
reset();start()
for _=1,20 do
    if job().victim_staged then break end
    tick()
end
check(job().victim_staged,"standard mode did not reach captive transfer")
local waiting=job();clients[waiting.victim.id]=client(objects[waiting.victim.id]);db.storage[waiting.victim.id]=nil
local spawn_ok,spawn_error=pcall(callbacks.npc_on_net_spawn,clients[waiting.victim.id])
check(spawn_ok,"captive AI ran inside native net_spawn before motivator storage: "..tostring(spawn_error))
tick(8)
check(waiting.phase=="stage" and waiting.step=="waiting_for_captive_storage" and not waiting.error,
    "default mode configured an uninitialized motivator or rolled back immediately")
db.storage[waiting.victim.id]={};tick(15)
check(waiting.phase=="captive" and waiting.task_given and creates==14 and teleports==1,
    "motivator initialization did not resume the saved stage")

reset();start({alternative_start="true"});fail_guard=3;tick(25,"actor")
check(job().phase=="cancelled" and input and hud and objects[job().victim.id].group_id==5,
    "alternative staging failure stranded the companion or camera")
check(not objects[job().guards[1].id] and not objects[job().guard_group.id],
    "partial staging failure left its guard or squad")
check(table.concat(logs,"\n"):find("phase=stage step=spawn_guard_2",1,true),"staging failure lost its task/phase/operation diagnosis")
reset();local allowed,status,reason=descriptor.can_execute({}, {npc_sections="guard_a",alternative_start="invalid"})
check(not allowed and status=="rejected" and reason=="kidnap_alternative_start_invalid","invalid alternative mode was accepted")

reset();start();captive();local normal=job();start({alternative_start="true"});tick(28)
local alternate=job();local quest_count=0;for _ in pairs(native_tasks) do quest_count=quest_count+1 end
check(quest_count==2 and normal.phase=="captive" and alternate.phase=="captive" and
    normal.victim.id~=alternate.victim.id and timejumps==2 and sleep_callbacks==1,
    "normal and alternative quests interfered or changed default sleep handlers")

-- The old path must still expose the numeric incompatibility with the
-- checkbox disabled, and recover the player after the refused assignment.
reset();gamma_squad_lookup=true;start();tick(25)
local old_route=job()
check(old_route.phase=="cancelled" and old_route.error:find("global 'obj'",1,true) and
    objects[old_route.victim.id].group_id==5 and input and hud,
    "default path was not isolated from the GAMMA workaround")

-- The alternate path can start, be loaded and end without any stock quest Lua.
reset()
gamma_squad_lookup=true
axr_task_manager,task_manager,task_functor,task_status_functor,xr_effects,sim_offline_combat,dialogs=nil,nil,nil,nil,nil,nil,nil
start({enemy_count="1",alternative_start="true"});captive()
local independent=job()
state=clone(state);callbacks.on_game_load();tick(8)
independent=job()
for _,g in pairs(independent.guards) do objects[g.id].living=false end
local same_npc=objects[independent.victim.id]
clients[same_npc.id]=client(same_npc)
M.request_rescue(db.actor,clients[same_npc.id]);tick(4)
check(independent.phase=="rescued" and same_npc.group_id==5 and vars[same_npc.id].companion==true and
    native_tasks[independent.task].state==task.completed and timejumps==1 and #messages==1,
    "fresh quest depended on stock quest Lua or repeated after load")

reset();start({alternative_start="true",enemy_count="1"});captive()
local old_alternate=job()
old_alternate.world_migrated=nil
axr_task_manager.hostages_by_id[old_alternate.victim.id]=old_alternate.camp.id
objects[old_alternate.hostage_group.id].scripted_target=nil
objects[old_alternate.guard_group.id].scripted_target=old_alternate.camp.id
local old_captive=client(objects[old_alternate.victim.id]);clients[old_alternate.victim.id]=old_captive
db.storage[old_alternate.victim.id].ini_filename="scripts\\anomaly_donation_bridge_kidnapping.ltx"
db.storage[old_alternate.victim.id].section_logic="adb_kidnapping_logic"
db.storage[old_alternate.victim.id].active_section="beh@adb_kidnapped"
callbacks.on_game_load();tick(8)
check(objects[old_alternate.hostage_group.id].scripted_target=="camp" and objects[old_alternate.guard_group.id].scripted_target=="camp" and
    not axr_task_manager.hostages_by_id[old_alternate.victim.id] and axr_task_manager.hostages_by_id[333]==999 and
    objects[old_alternate.victim.id].target=="cover_wait" and timejumps==1 and #messages==1,
    "saved alternate route did not migrate its own target and scheme")

reset();start();fail_task=true;tick(30)
local refused=job()
check(refused.phase=="cancelled" and objects[refused.victim.id].group_id==5 and not objects[refused.guard_group.id] and
    not objects[refused.hostage_group.id] and input and hud and #messages==0,
    "quest creation failure stranded the captive or its squads")
for _,g in pairs(refused.guards) do check(not objects[g.id],"rollback leaked an owned guard") end
check(table.concat(logs,"\n"):find("phase=captive step=issue_quest",1,true),"quest creation failure was not diagnosed")
io.write("PASS companion kidnapping: "..checks.." checks\n")
