local root,_,optional=assert(arg[1]),arg[2],assert(arg[3])
local checks=0
local function check(value,message) checks=checks+1;assert(value,message) end
local function clone(t)
    if type(t)~="table" then return t end
    local c={};for k,v in pairs(t)do c[k]=clone(v)end;return c
end
local module_name="anomaly_donation_bridge_effect_hardcoon_adb_new_life"
local core_name="anomaly_donation_bridge_reincarnation"
local az_handler
local archived_clients,death_calls
local handler,module,callbacks,storage,objects,clients,events,clock,frame,logs,scenario
local actor,npc,squad,manager,companions,created,transfers,moves,tasks
local opts,staging_blocked,create_fail,transfer_fail,move_fail,loading,paused,level_index
local native_pending=0
local ghost_inventory,hidden_servers,native_callback_errors
local native_slot_violations,name_writes,travel_requests,travel_fail
local knife_touches
local visuals,visual_calls,missing_visual
local frame_delta,input_disabled,held_walk
local suppress_sleep_callbacks,inside_camera_callback
local packet_writes,packet_failure,packet_ignored,offline_failure,live_npc_visual_calls
local state_read_calls,inventory_open
local definitions={
    player_outfit={class="E_STLK",kind="o_light",slot="6"},npc_outfit={class="E_STLK",kind="o_medium",slot="6"},
    npc_helmet={class="E_HLMET",kind="o_helmet",slot="11"},player_gun={class="WP_PM",slot="1"},
    npc_gun={class="WP_AK74",slot="2"},ammo={class="AMMO"},quest_item={quest=true},device_pda_3={},food={},anim_prop={anim=true},
    wpn_knife3={class="WP_KNIFE",slot="0",knife=true},
    custom_blade={class="WP_KNIFE",slot="0,1",knife=true},
    pack_melee={class="CUSTOM",kind="w_melee",knife=true}
}
local function load_module(name,dir)
    local env=setmetatable({},{__index=_G});local chunk=assert(loadfile(dir.."/"..name..".script"))
    setfenv(chunk,env);chunk();_G[name]=env;return env
end
anomaly_donation_bridge_common=load_module("anomaly_donation_bridge_common",root)
local function enqueue(fn,delay)events[#events+1]={at=frame+(delay or 2),fn=fn};native_pending=native_pending+1 end
local function item(id,section,parent,slot)
    local se={id=id,sec=section,parent_id=parent,internal="item_"..id,slot=slot,condition=.43,ammo=12,upgrades="test-upgrade"}
    function se:name()return self.internal end
    function se:section_name()return self.sec end
    function se:clsid()return definitions[section].class or "item" end
    objects[id]=se
    local obj={}
    function obj:id()return id end
    function obj:section()return section end
    function obj:clsid()return definitions[section].class end
    function obj:condition()return se.condition end
    clients[id]=obj
    return se
end
local function human(id,name,gvid,position,group)
    local se={id=id,parent_id=65535,m_game_vertex_id=gvid,m_level_vertex_id=50+id,position=clone(position),
        group_id=group,internal=id==0 and "actor" or "sim_default_"..id,sec=id==0 and "actor" or "sim_default_stalker",
        display=name,icon="icon_"..id,comm=id==0 and "actor_stalker"or "bandit",visual="actors\\stalker\\npc_model",smart=65535,living=true,online=true,
        native_health=.73,native_flags=4917}
    function se:STATE_Read()state_read_calls=state_read_calls+1;error("SIMULATED_NATIVE_BUSY_HANDS: full STATE_Read is prohibited")end
    function se:name()return self.internal end
    function se:section_name()return self.sec end
    function se:character_name()return self.display end
    function se:set_character_name(v)
        name_writes[#name_writes+1]={id=id,name=v};self.display=v
    end
    function se:character_icon()return self.icon end
    function se:community()return self.comm end
    function se:alive()return self.living end
    function se:kill()self.living=false;death_calls[#death_calls+1]={id=id,offline=true}end
    function se:clsid()return "stalker" end
    if id~=0 then
        function se:smart_terrain_id()return self.smart end
        function se:brain()return {can_choose_alife_tasks=function()end}end
    end
    objects[id]=se
    local obj={slots={},vulnerable=false,script_owner=nil,active_slot=id==0 and 2 or 3}
    function obj:id()return id end
    function obj:alive()return se.living end
    function obj:set_health_ex(value)self.health=value end
    function obj:kill(_,bypass)
        death_calls[#death_calls+1]={id=id,bypass=bypass};se.living=false
    end
    function obj:name()return se:name()end
    function obj:position()return clone(se.position)end
    function obj:level_vertex_id()return se.m_level_vertex_id end
    function obj:game_vertex_id()return se.m_game_vertex_id end
    function obj:get_visual_name()return self.client_visual or se.visual end
    function obj:set_visual_name(v)
        if id~=0 then live_npc_visual_calls=live_npc_visual_calls+1;error("SIMULATED_NATIVE_BUSY_HANDS: live NPC ChangeVisual is prohibited")end
        se.visual=v;self.client_visual=v
    end
    function obj:character_name()return se.display end
    -- Deliberately NO game_object:set_character_name: only the CSE server
    -- binding exports it, including actor and already-online NPCs.
    function obj:set_character_icon(v)se.icon=v end
    function obj:character_community()return se.comm end
    function obj:set_character_community(v)se.comm=v end
    function obj:goodwill(who)assert(who==actor);return self.gw or 0 end
    function obj:force_set_goodwill(value,who)assert(who==actor,"game_object goodwill binding requires object, not numeric id");self.gw=value end
    function obj:script(capture,owner)
        if capture then if not self.script_owner then self.script_owner=owner end
        elseif self.script_owner==owner then self.script_owner=nil end
    end
    function obj:get_script()return self.script_owner~=nil end
    function obj:get_script_name()return self.script_owner end
    -- Deliberately absent on actor: the real binding only accepts CCustomMonster.
    if id~=0 then function obj:invulnerable(value)if value~=nil then self.vulnerable=value end;return self.vulnerable end end
    function obj:iterate_inventory(fn,ctx)
        local ids={};for iid,it in pairs(objects)do if it.parent_id==id then ids[#ids+1]=iid end end;table.sort(ids)
        local function invoke(it)
            local ok,stop=pcall(fn,ctx,it)
            if not ok then native_callback_errors=native_callback_errors+1;error("SIMULATED_NATIVE_LUABIND_FATAL: "..tostring(stop))end
            return stop==true
        end
        for _,iid in ipairs(ids)do if invoke(assert(clients[iid]))then return end end
        for _,e in ipairs(ghost_inventory)do if e.owner==id and invoke(e.item)then return end end
    end
    function obj:item_in_slot(slot)
        local iid=self.slots[slot];local it=iid and objects[iid]
        return it and it.parent_id==id and clients[iid]or nil
    end
    function obj:active_item()return self:item_in_slot(self.active_slot or 2)end
    function obj:move_to_slot(it,slot,no_activate)
        if definitions[it:section()].knife then knife_touches=knife_touches+1 end
        check(no_activate==true,"equip submits a native nonactivating slot event")
        local def=assert(definitions[it:section()]);local base=assert(tonumber(def.slot))+1
        local valid=slot==base or (base==2 or base==3)and(slot==2 or slot==3)
        if not valid then native_slot_violations=native_slot_violations+1 end
        assert(valid,"LTX slot must be converted to native slot+1; no helmet/outfit corruption")
        enqueue(function()
            if objects[it:id()]and objects[it:id()].parent_id==id then
                for s,iid in pairs(self.slots)do if iid==it:id()then self.slots[s]=nil end end
                self.slots[slot]=it:id()
            end
        end)
    end
    function obj:move_to_ruck(it)
        if definitions[it:section()].knife then knife_touches=knife_touches+1 end
        enqueue(function()for s,iid in pairs(self.slots)do if iid==it:id()then self.slots[s]=nil end end end)
    end
    function obj:activate_slot(slot)self.active_slot=slot end
    function obj:transfer_item(it,dest)
        if definitions[it:section()].knife then knife_touches=knife_touches+1 end
        transfers[#transfers+1]={id=it:id(),source=id,target=dest:id()}
        if transfer_fail and #transfers==transfer_fail then error("injected transfer failure")end
        enqueue(function()
            local e=assert(objects[it:id()]);assert(e.parent_id==id,"native transfer starts at actual owner")
            e.parent_id=dest:id()
            for s,iid in pairs(self.slots)do if iid==it:id()then self.slots[s]=nil end end
        end)
    end
    function obj:is_talking()return false end
    function obj:get_current_holder()return nil end
    function obj:set_actor_position(pos)
        moves[#moves+1]=clone(pos)
        if not move_fail then
            if pos.x==100 then
                check(storage.adb_new_life_v1.job.skip_cinema or visuals.pp[162802]and visuals.pp[162802].factor==1,
                    "final move occurs under black screen unless optional visuals failed")
                local keep=storage.adb_new_life_v1.job.options.keep_inventory
                check(self:item_in_slot(7)and self:item_in_slot(7):section()==(keep and "player_outfit"or"npc_outfit"),
                    "required outfit is ALREADY worn before final position setter")
                check(self:active_item()and self:active_item():id()==(keep and 1002 or 1100),
                    "required original gun is ALREADY active before final position setter")
            end
            se.position=clone(pos)
        elseif pos.x==100 then error("injected position failure")
        else se.position=clone(pos)end
    end
    function obj:get_task(task_id)return tasks[task_id]end
    function obj:give_task(t,dt,repeated,timer)
        assert(dt==0 and repeated==false and timer==0,"real give_task signature")
        tasks[t.id]=t
    end
    clients[id]=obj;archived_clients[id]=obj;return obj,se
end
local function reset(options)
    suppress_sleep_callbacks=false;inside_camera_callback=nil
    callbacks,storage,objects,clients,events,logs={},{},{},{},{},{}
    archived_clients,death_calls={},{}
    ghost_inventory,hidden_servers,native_callback_errors={},{},0
    native_slot_violations,name_writes=0,{}
    knife_touches=0
    visuals,visual_calls,missing_visual={pp={[11]={foreign=true}},cam={[10]={foreign=true}}},{},nil
    frame_delta,input_disabled,held_walk=.016,false,true
    packet_writes,packet_failure,packet_ignored,offline_failure,live_npc_visual_calls={},nil,false,false,0
    state_read_calls,inventory_open=0,false
    actor_effects=nil
    travel_requests,travel_fail=0,false
    clock,frame,level_index,native_pending=1000,100,1,0
    created,transfers,moves,tasks={},{},{},{}
    opts=options or {};staging_blocked,create_fail,transfer_fail,move_fail,loading,paused=false,nil,nil,false,false,false
    actor=human(0,"Old Player",1,{x=0,y=0,z=0},nil)
    objects[0].visual="actors\\stalker\\player_model"
    npc=human(10,"Target",1,{x=100,y=0,z=0},100)
    squad={id=100,members={[10]=true,[11]=true}}
    function squad:clsid()return "squad"end
    function squad:npc_count()local n=0;for _ in pairs(self.members)do n=n+1 end;return n end
    function squad:unregister_member(id)assert(self.members[id]);self.members[id]=nil;objects[id].group_id=65535 end
    function squad:register_member(id)assert(not self.members[id]);self.members[id]=true;objects[id].group_id=100 end
    objects[100]=squad
    manager={task_info={keep={current_target=20,task_giver_id=21,title="Existing task"}}}
    companions={companion_squads={[200]=true},non_task_companions={[21]=true}}
    item(1000,"quest_item",0);item(1001,"player_outfit",0,7);item(1002,"player_gun",0,2);item(1003,"device_pda_3",0)
    item(1100,"npc_gun",10,3);item(1101,"ammo",10)
    actor.slots[7]=1001;actor.slots[2]=1002;npc.slots[3]=1100
    db={actor=actor,actor_binder={character_icon="icon_0"},storage={[0]={}}}
    function set_inactivate_input_time(seconds)
        check(seconds==3601,"private native input lease, not stock sleep/theft duration")
        db.storage[0].disable_input_time={at=clock};db.storage[0].disable_input_idle=seconds
        input_disabled=true;held_walk=false
    end
    function vector()return {set=function(self,x,y,z)self.x,self.y,self.z=x,y,z;return self end}end
    function time_global()return clock end
    function device()return {frame=frame,precache_frame=loading and 10 or 0,f_time_delta=frame_delta,is_paused=function()return paused end}end
    function app_ready()return not loading end
    level={present=function()return true end,object_by_id=function(id)return clients[id]end,
        vertex_position=function(id)
            check(not loading,"level.ai is never probed while loading")
            if objects[10].m_game_vertex_id~=objects[0].m_game_vertex_id then
                check(id~=objects[10].m_level_vertex_id,"remote level vertex is never probed in current map")
            end
            return {x=0,y=0,z=0}
        end}
    function level.add_pp_effector(path,id,loop)
        check(path=="black_infinite_2.ppe"and id==162802 and loop==true or
            path=="surge_fade.ppe"and id==162804 and loop==false,"sleep effects use private IDs")
        visuals.pp[id]={factor=1};visual_calls[#visual_calls+1]={kind="pp",path=path}
    end
    function level.set_pp_effector_factor(id,value)
        check(id==162802 and value>=0.01 and value<=1,"PP factor never reaches the native self-delete threshold or touches foreign ids")
        if visuals.pp[id]then visuals.pp[id].factor=value end
    end
    function level.remove_pp_effector(id)check(id==162802 or id==162804,"only own PP removed");visuals.pp[id]=nil end
    function level.add_cam_effector(path,id,loop,callback)
        check(loop==false and (path=="camera_effects\\surge_02.anm"and id==162801 and callback==core_name..".sleep_midpoint" or
            path=="camera_effects\\surge_01.anm"and id==162803 and callback==core_name..".sleep_finish"),
            "alternative native sleep callbacks, without stock sleep manager/time changes")
        visuals.cam[id]={at=clock+700,callback=callback:match('%.([^%.]+)$')}
        visual_calls[#visual_calls+1]={kind="cam",path=path};return .7
    end
    function level.remove_cam_effector(id)
        check(id~=inside_camera_callback,"never remove a camera inside its own destructor callback")
        check(id==162801 or id==162803,"only own camera removed");visuals.cam[id]=nil
    end
    function level.disable_input()input_disabled=true;held_walk=false end
    function level.enable_input()input_disabled=false end
    function level.show_weapon(value)visuals.weapon_hidden=not value end
    function level.hide_indicators_safe()visuals.hud_hidden=true end
    function level.show_indicators()visuals.hud_hidden=false end
    function getFS()return {exist=function(_,alias,path)
        check(alias=="$game_anims$","resource preflight uses native animation alias")
        return path~=missing_visual and true or nil
    end}end
    actor_menu={is_hud_free=function()return not inventory_open end}
    function Check_UI()return inventory_open end
    _GUIs={}
    function game_graph()return {vertex=function(_,id)
        check(not loading,"never query navigation graph in loading/precache")
        return {level_id=function()return id end}
    end}end
    local sim={object=function(_,id)return not hidden_servers[id]and objects[id]or nil end,actor=function()return objects[0]end,
        level_name=function(_,id)return "level_"..id end}
    function sim:teleport_object(id,gvid,lvid,pos)
        local se=assert(objects[id]);se.m_game_vertex_id,se.m_level_vertex_id,se.position=gvid,lvid,clone(pos)
        se.online=false
        clients[id]=nil
        if not staging_blocked then enqueue(function()
            clients[id]=archived_clients[id];se.online=not offline_failure;clients[id].client_visual=se.visual
        end)end
    end
    function alife()return sim end
    alife_storage_manager={get_state=function()return storage end}
    task_manager={get_task_manager=function()return manager end}
    axr_companions=companions
    utils_stpk={get_stalker_data=function(se)
        return {visual_name=se.visual,preserved_health=.73,preserved_flags=4917,preserved_id=se.id}
    end,set_stalker_data=function(_,se)
        se:STATE_Read() -- catches the exact unsafe .23 regression, not an ordinary Lua validation error
    end}
    function net_packet()
        local p={fields={}}
        for _,kind in ipairs({"begin","u32","u16","stringZ"})do
            p["w_"..kind]=function(self,value)self.fields[#self.fields+1]={kind=kind,value=value}end
        end
        return p
    end
    function level.send(p,reliable,sequential,priority,immediate)
        check(reliable==true and sequential==true and priority==true and immediate==true,"native server event uses exact level.send signature")
        local f=p.fields
        check(#f==5 and f[1].kind=="begin"and f[1].value==8 and f[2].kind=="u32"and
            f[2].value==math.floor(clock)%4294967296 and f[3].kind=="u16"and f[3].value==35 and
            f[4].kind=="u16"and f[5].kind=="stringZ"and #f[5].value<256,"exact M_EVENT/GE_CHANGE_VISUAL wire only, no STATE body")
        local id=f[4].value;local se=assert(objects[id])
        check(id==storage.adb_new_life_v1.job.npc.id and se.online==true and clients[id]~=nil,"server visual event is NEVER sent to an offline/missing/foreign receiver")
        check(not inventory_open,"no native event while inventory is open")
        local client_before=clients[id]:get_visual_name()
        packet_writes[#packet_writes+1]={id=id,visual=f[5].value}
        if packet_ignored then return end
        -- Native single-player direct-connect sends to Process_event inline.
        -- The event changes only CSE_Visual, never the client's live model.
        se.visual=f[5].value
        check(clients[id]:get_visual_name()==client_before and se.native_health==.73 and se.native_flags==4917,
            "only server model field changes; live client, health and native flags remain untouched")
        if packet_failure==#packet_writes then error("injected partial server event failure")end
    end
    ini_sys={section_exist=function(_,sec)return definitions[sec]~=nil end,
        r_string_ex=function(_,sec,key)
            if definitions[sec]and definitions[sec].knife and key=="slot"then knife_touches=knife_touches+1 end
            return definitions[sec]and definitions[sec][key]
        end}
    function IsStalker(_,cls)return cls=="stalker"end
    function IsWeapon(obj)return obj and (obj:section()=="npc_gun"or obj:section()=="player_gun"or definitions[obj:section()].knife)end
    function IsItem(kind,sec)
        assert(kind~="weapon","stock _ITM has no generic weapon table; use IsWeapon")
        local d=definitions[sec]or {}
        return kind=="quest"and d.quest or kind=="outfit"and d.class=="E_STLK"or kind=="helmet"and d.class=="E_HLMET"or kind=="anim"and d.anim
    end
    function get_object_story_id(id)return objects[id]and objects[id].story end
    function se_load_var(id,_,key)
        if key=="companion" then return companions.non_task_companions[id] end
        return objects[id] and objects[id].vars and objects[id].vars[key]
    end
    local true_faction="stalker"
    gameplay_disguise={get_default_comm=function()return true_faction end,
        update_default=function(comm,instant)assert(instant==true);true_faction=comm;objects[0].comm="actor_"..comm end}
    death_manager={get_outfit_by_npc_visual=function()return {"npc_outfit","npc_helmet"}end}
    function alife_create_item(sec,owner)
        if create_fail==sec then return end
        local iid=1200+#created;created[#created+1]=sec
        return item(iid,sec,owner:id())
    end
    function alife_release_id(id)objects[id]=nil;clients[id]=nil;return true end
    function RegisterScriptCallback(name,fn)callbacks[name]=fn end
    function printf(fmt,...)logs[#logs+1]=string.format(fmt,...)end
    key_bindings={kQUICK_SAVE=54,kQUICK_LOAD=96}
    function CGameTask()
        local t={}
        for _,k in ipairs({"id","type","title","description","priority","icon_name","map_location","map_object_id"})do
            t["set_"..k]=function(self,v)self[k]=v end
        end
        function t:add_complete_func(v)self.complete=v end
        function t:add_fail_func(v)self.fail=v end
        return t
    end
    task={additional=1}
    function ChangeLevel()error("direct synchronous ChangeLevel from the effect is forbidden")end
    function JumpToLevel(target)
        check(native_pending==0,"stock transition requested only after native equipment event drain")
        check(actor:active_item():id()==(storage.adb_new_life_v1.job.options.keep_inventory and 1002 or 1100),
            "cross-map request only after chosen equipment is ready")
        travel_requests=travel_requests+1
        if travel_fail then return false end
        handler.on_level_changing()
        check(not visuals.cam[162801]and (storage.adb_new_life_v1.job.skip_cinema or visuals.pp[162802].factor==1),
            "transition autosave cannot capture our camera; optional black holds until load")
        local gvid=assert(tonumber(target:match('^level_(%d+)$')))
        -- Model the stock deferred travel: unwind the requesting callback,
        -- then load at the ordinary entry point, NOT the NPC's coordinates.
        enqueue(function()
            level_index=gvid;objects[0].m_game_vertex_id=gvid;objects[0].m_level_vertex_id=20
            objects[0].position={x=10,y=0,z=0};handler.on_game_load();loading=true
            callbacks.actor_on_first_update()
        end,3)
        return true
    end
    -- Anything destructive in stock Azazel must remain unreachable.
    gamemode_azazel=setmetatable({},{__index=function()error("native Azazel is prohibited")end})
    function set_actor_true_community()error("quest-cancelling faction wrapper is prohibited")end
    anomaly_donation_bridge_registry={register=function(id,value)
        if id=="hardcoon.adb.new_life" then handler=value
        elseif id=="hardcoon.adb.new_life_azazel" then az_handler=value else error(id) end;return true
    end}
    load_module("anomaly_donation_bridge_azazel_hud",root)
    load_module("anomaly_donation_bridge_relife_companions",root)
    load_module(core_name,root)
    module=load_module(module_name,optional);module.on_game_start()
end
local function tick(count)
    for _=1,count do
        frame=frame+1;clock=clock+100
        if not paused and not suppress_sleep_callbacks then
            for id,cam in pairs(visuals.cam)do
                if cam.at and clock>=cam.at then
                    inside_camera_callback=id;module[cam.callback]();inside_camera_callback=nil;visuals.cam[id]=nil
                end
            end
        end
        for i=#events,1,-1 do if frame>=events[i].at then local e=table.remove(events,i);native_pending=native_pending-1;e.fn()end end
        handler.update(clock)
    end
end
local function start(options)
    tick(17)
    local ctx={actor=actor,now=clock};local values={}
    for k,v in pairs(options or {})do values[k]=tostring(v)end
    check(handler.is_supported()==true,"stock native capability set accepted")
    check(handler.can_execute(ctx,values)==true,"new life validates defaults/options")
    check(handler.execute(ctx)=="executed"and handler.is_blocking(),"request starts exclusive async operation")
end
local function state()return assert(storage.adb_new_life_v1)end
local function completed()
    tick(350)
    check(not handler.is_blocking(),"transaction completes without leaving queue blocked: "..table.concat(logs,"; "))
    check(native_slot_violations==0,"no incompatible native slot calls, including compensation")
    check(not visuals.pp[162802]and not visuals.pp[162804]and not visuals.cam[162801]and not visuals.cam[162803]
        and not visuals.weapon_hidden and not visuals.hud_hidden and visuals.pp[11].foreign and visuals.cam[10].foreign,
        "own visual leases cleaned, genuine sleep/foreign effects untouched")
    check(not input_disabled and db.storage[0].disable_input_time==nil,"native held-key input lease released on completion/rollback")
    check(live_npc_visual_calls==0,"no native live model reload, not even during rollback")
    check(state_read_calls==0,"no full STATE_Read, not even during compensation or legacy recovery")
end
local function intact()
    check(objects[1000].parent_id==0 and objects[1003].parent_id==0,"quest item and actual PDA never leave actor")
    check(task_manager.get_task_manager()==manager and manager.task_info.keep.title=="Existing task"and
        manager.task_info.keep.forced_status==nil,"same task table and ongoing task preserved")
    check(axr_companions==companions and companions.companion_squads[200]and companions.non_task_companions[21],"companion relations untouched")
end
local function until_waiting()
    for _=1,250 do tick(1);if state().job and state().job.prepare_reason then return end end
    error("expected a pending inventory snapshot, got: "..table.concat(logs,"; "))
end
local function ghost(owner,id,section)
    local obj={id=function()return id end,section=function()return section end}
    ghost_inventory[#ghost_inventory+1]={owner=owner,item=obj}
end
local function window(kind,opened)
    if kind=="inventory" then inventory_open=opened
    elseif kind=="pda" then actor_menu.is_hud_free=function()return not opened end
    elseif kind=="dialog" then actor.is_talking=function()return opened end
    elseif kind=="trade"or kind=="custom"then _GUIs.Modal=opened and {}or nil
    elseif kind=="pause"then paused=opened
    elseif kind=="precache"then loading=opened
    elseif kind=="item_use"then actor_effects={item_not_in_use=not opened}
    else error("unknown window fixture")end
    if kind~="pause"and kind~="precache"and kind~="item_use"then
        callbacks[opened and "GUI_on_show"or "GUI_on_hide"]()
        callbacks.ActorMenu_on_mode_changed()
    end
end
for _,kind in ipairs({"inventory","pda","dialog","trade","custom","pause","precache","item_use"})do
    reset();tick(17);window(kind,true)
    local ctx={};local allowed,status=handler.can_execute(ctx,{})
    check(not allowed and status=="deferred"and handler.execute(ctx)=="deferred",kind.." keeps command deferred at BOTH entry points")
    tick(500)
    check(not handler.is_blocking()and not state().selection and not state().job and #transfers==0 and #packet_writes==0 and
        #visual_calls==0 and not input_disabled,"waiting never starts scan/camera/input/NPC work or native events: "..kind)
    window(kind,false)
    check(handler.can_execute(ctx,{})==false,"closing a window is not itself permission to start: "..kind)
    tick(15)
    check(handler.can_execute(ctx,{})==false,"all 15 rendered ready frames are respected after "..kind)
    tick(1)
    check(handler.can_execute(ctx,{})==true and handler.execute(ctx)=="executed","same queued request can start once after "..kind)
    completed();intact()
    check(#moves==1 and #transfers==4,"deferred request is not lost or doubled after "..kind)
end

reset();tick(17)
local race_context={}
check(handler.can_execute(race_context,{})==true,"reach native before-save race after valid preflight")
window("inventory",true)
check(handler.execute(race_context)=="deferred"and not handler.is_blocking()and not state().selection and #visual_calls==0,
    "inventory opened by another callback after preflight still cannot start the effect")
window("inventory",false);tick(16)
check(handler.can_execute(race_context,{})==true and handler.execute(race_context)=="executed","race remains retryable")
completed();intact()

reset();start();window("inventory",true);tick(500)
check(state().selection and not state().job and #transfers==0 and #packet_writes==0 and #visual_calls==0,
    "menu opened after durable request pauses selection before camera, movement or model events")
window("inventory",false);tick(15)
check(state().selection and not state().job,"durable request waits through post-menu frame barrier")
completed();intact()
check(#moves==1,"pending request resumes once, not a second effect")

reset();start()
for _=1,100 do tick(1);if state().job and state().job.phase=="prepare"then break end end
window("inventory",true)
local frozen_events=#packet_writes
tick(500)
check(state().job.phase=="prepare"and #packet_writes==frozen_events and #transfers==0,"mid-scene mod UI suspends ALL native writes")
window("inventory",false);completed();intact()
check(objects[0].position.x==100 and #transfers==4,"UI wait does not consume transaction deadline or lose the request")

reset();utils_stpk.set_stalker_data=nil;start();completed();intact()
check(#packet_writes==1 and state_read_calls==0,"native one-field event needs NO full-state writer at all")
-- CRASH-004: the engine's native live ChangeVisual can invoke BusyHandsDebug
-- before Lua pcall sees anything. The mock forbids EVERY live NPC model setter.
reset();level.send=nil;tick(17)
local no_packet,_,packet_reason=handler.can_execute({}, {})
check(not handler.is_supported()and not no_packet and packet_reason=="server_visual_api_missing"and
    #transfers==0 and #packet_writes==0 and squad.members[10],"missing native event API is rejected before selection or mutation")

reset();packet_failure=1;start();completed();intact()
check(#packet_writes==2 and packet_writes[1].visual=="actors\\stalker\\player_model"and
    packet_writes[2].visual=="actors\\stalker\\npc_model"and objects[10].visual=="actors\\stalker\\npc_model"and
    objects[10].position.x==100 and squad.members[10]and #transfers==0 and #created==0,
    "partial server event failure rolls original model/position/squad back before inventory exchange, without STATE_Read/ChangeVisual")

reset();packet_ignored=true;start();completed();intact()
check(#transfers==0 and #created==0 and objects[10].visual=="actors\\stalker\\npc_model"and squad.members[10]and
    table.concat(logs,"; "):find("npc_visual_event_not_applied",1,true),"ignored model event is detected instead of continuing a mismatched exchange")

reset();offline_failure=true;start()
for _=1,100 do tick(1);if state().job and state().job.phase=="rollback"then break end end
check(state().job and state().job.phase=="rollback"and #packet_writes==0 and #transfers==0,
    "missing online receiver cannot submit a server event or continue inventory transfer")
offline_failure=false;completed();intact()
check(objects[10].visual=="actors\\stalker\\npc_model"and squad.members[10],"online receiver refusal compensates the staged NPC")

reset();objects[0].visual="ACTORS/stalker/player_model.OGF";start();completed();intact()
check(#packet_writes==1 and npc:get_visual_name()=="ACTORS/stalker/player_model.OGF"and #transfers==4,
    "real actor visual with extension/case/slash differences is persisted before the real NPC returns online")

reset();start()
for _=1,100 do tick(1);if state().job and state().job.visual_recycled then break end end
npc.get_visual_name=function()return "foreign_model_override"end
completed();intact()
check(#transfers==0 and #created==0 and objects[10].visual=="actors\\stalker\\npc_model"and squad.members[10]and
    table.concat(logs,"; "):find("previous_body_visual_not_confirmed",1,true),
    "foreign spawn-time model override is detected BEFORE any ownership exchange, with safe offline compensation")

reset();start()
for _=1,70 do tick(1);if state().job and state().job.phase=="fade_out"then break end end
check(state().job.phase=="fade_out"and visuals.cam[162801]and objects[10].position.x==100 and #transfers==0,
    "camera starts before NPC staging or any inventory mutation")
check(input_disabled and not held_walk,"held running is natively released, not merely blocked on future key presses")
tick(2)
check(visuals.pp[162802].factor>0.01 and visuals.pp[162802].factor<1 and #transfers==0 and objects[10].position.x==100,
    "sleep motion and smooth fade precede actual exchange")
local factor=visuals.pp[162802].factor
paused=true;tick(40)
check(visuals.pp[162802].factor==factor and #transfers==0,"paused game cannot consume the fade or start mutation")
paused=false;completed();intact()

reset();start()
for _=1,70 do tick(1);if state().job and state().job.phase=="fade_out"then break end end
local fade_flags={ret=false};callbacks.on_before_save_input(fade_flags)
check(fade_flags.ret and not handler.on_effect_save("before"),"F5/program save fenced while custom camera runs")
handler.on_game_load();completed();intact()
check(#transfers==0 and #moves==0 and objects[10].position.x==100 and actor:item_in_slot(7):id()==1001,
    "external load during fade restores a plain playable actor without exchange or a stuck black screen")

reset();missing_visual="camera_effects\\surge_02.anm";start();completed();intact()
check(#visual_calls==0 and #transfers==4 and #moves==1,
    "missing optional motion skips native Start and still completes the exchange: visuals="..#visual_calls..
    " transfers="..#transfers.." moves="..#moves.." logs="..table.concat(logs,"; "))
reset();level.add_cam_effector=function()error('modpack rejected camera')end
start();completed();intact();check(#transfers==4 and #moves==1,"camera refusal cannot abort the transaction or leave black PP")
reset();level.add_pp_effector=nil;start();completed();intact()
check(#transfers==4,"absent visual API cannot block new life")
reset();suppress_sleep_callbacks=true;start();completed();intact()
check(#transfers==4,"missing animation callbacks use bounded watchdogs")
reset();frame_delta=0;start();completed();intact()
check(#transfers==4,"zero render delta does not stall a running registry/frame clock")

reset();start()
for _=1,70 do tick(1);if state().job and state().job.phase=="fade_out"then break end end
callbacks.actor_on_net_destroy()
check(not visuals.cam[162801]and not visuals.pp[162802]and visuals.cam[10].foreign and visuals.pp[11].foreign,
    "actor destruction retires only this cinematic's leases")
handler.on_game_load();completed()

for _,fps in ipairs({30,240,1000})do
    reset();frame_delta=1/fps;start()
    for _=1,70 do tick(1);if state().job and state().job.phase=="fade_out"then break end end
    local begun=clock
    for _=1,12 do tick(1);if state().job.phase~="fade_out"then break end end
    check(state().job.phase~="fade_out"and clock-begun<=1000,"100ms registry tick fades in real time at FPS="..fps)
    completed();intact()
end

reset();tick(17);db.storage[0].disable_input_time={};db.storage[0].disable_input_idle=30
local lock_allowed,lock_status=handler.can_execute({}, {})
check(not lock_allowed and lock_status=="deferred"and #visual_calls==0,"foreign stock/robbery input lease is not hijacked")

reset();start()
for _=1,70 do tick(1);if state().job and state().job.phase=="fade_out"then break end end
local foreign_stamp={}
db.storage[0].disable_input_time=foreign_stamp;db.storage[0].disable_input_idle=30
tick(350)
check(not handler.is_blocking()and input_disabled and db.storage[0].disable_input_time==foreign_stamp and
    db.storage[0].disable_input_idle==30,"completion cannot enable/erase a replacement lock owned by another cutscene")

reset();local good_input=level.disable_input
level.disable_input=function()good_input();error("partial native input capture failure")end
start();completed()
check(#transfers==0 and objects[10].position.x==100,"partial input failure cleans the exact lease before any NPC/gear mutation")

reset()
local initial_flags={ret=false};callbacks.on_before_save_input(initial_flags)
check(not initial_flags.ret and not handler.is_blocking(),"merely installing the template cannot block ordinary saves or other effects before restoration")
start()
local flags={ret=false};callbacks.on_before_save_input(flags);check(flags.ret,"F5/menu save fence during transaction")
flags={ret=false};callbacks.on_before_load_input(1,2,flags);check(flags.ret,"F9 fence during transaction")
flags={ret_value=true};callbacks.on_before_key_press(999,key_bindings.kQUICK_LOAD,false,flags);check(not flags.ret_value,"rebound quickload follows action")
check(handler.on_effect_save("before")==true and state().selection~=nil,
    "read-only selection autosave is safe and its request is durable, not interaction_suspend_failed")
completed();intact()
check(objects[1100].parent_id==0 and objects[1101].parent_id==0 and objects[1002].parent_id==10,"actual items exchanged, not copies: "..table.concat(logs,"; "))
check(objects[1100].condition==.43 and objects[1100].ammo==12 and objects[1100].upgrades=="test-upgrade","weapon wear/ammo/upgrades exactly retained")
check(#created==2 and created[1]=="npc_outfit"and created[2]=="npc_helmet","only model-matching outfit/helmet materialised, no starter kit")
check(actor:character_name()=="Target"and npc:character_name()=="Old Player","real NPC becomes prior body, actor takes identity")
check(actor.set_character_name==nil and npc.set_character_name==nil and #name_writes==2 and
    name_writes[1].id==0 and name_writes[2].id==10,"both names use actual CSE setters, never invented online methods")
check(objects[0].position.x==100 and objects[10].position.x==0 and #moves==1,
    "successful native contract reaches actual teleport to NPC original position; old body stays at actor source")
check(actor:item_in_slot(7):section()=="npc_outfit"and actor:item_in_slot(12):section()=="npc_helmet"and
    actor:item_in_slot(6)==nil and actor:active_item():id()==1100 and actor.active_slot==3,
    "outfit=7, helmet=12, gun raw LTX 2 -> native 3, bolt slot 6 untouched")
check(npc:get_visual_name()=="actors\\stalker\\player_model","previous body receives actual old actor appearance")
check(#packet_writes==1 and packet_writes[1].id==10 and objects[10].online,
    "one native server event supplies the model at next spawn; body_identity performs no live model reload")
check(gameplay_disguise.get_default_comm()=="stalker"and npc:character_community()=="arena_enemy"and npc.gw==-5000,"default faction retained and old body uses penalty-free enemy community")
check(not npc:get_script()and npc:invulnerable()==false,"all short NPC leases released")
local task_id=next(tasks);check(task_id and tasks[task_id].title=="ReLIFE"and tasks[task_id].map_object_id==10,"independent native ReLIFE task targets exact real previous body")
check(tasks[task_id].description:find('KILL Old Player".',1,true)~=nil,"task description contains actual previous name")
check(not module.relife_complete(task_id)and not module.relife_fail(task_id),"quest pending while matching body lives")
local old_transfer_count=#transfers
handler.on_game_load();tick(30)
check(#transfers==old_transfer_count and #moves==1,"completed F9 does not repeat exchange or movement")
objects[10].living=false;callbacks.npc_on_death_callback(npc);objects[10]=nil
check(module.relife_complete(task_id)and not module.relife_fail(task_id),"death receipt survives corpse removal")
flags={ret=false};callbacks.on_before_save_input(flags);check(not flags.ret and handler.on_effect_save("before"),"save/load return to normal after completion")

reset({keep_inventory=true});start({keep_inventory=true,keep_faction=false,hostile_previous_body=false,quest_previous_body=false});completed();intact()
check(objects[1001].parent_id==0 and objects[1002].parent_id==0 and objects[1100].parent_id==10 and #created==0 and #transfers==0 and
    actor:active_item():id()==1002 and actor:item_in_slot(7):section()=="player_outfit"and actor:item_in_slot(12)==nil,
    "keep-inventory leaves BOTH inventories and the actor's original equipment untouched, no NPC items or generated kit")
check(gameplay_disguise.get_default_comm()=="bandit"and npc:character_community()=="stalker"and next(tasks)==nil,"optional faction switch preserves storylines; peaceful body and quest off supported")

-- A pack can equip knives outside their base LTX slot. Keep actual objects,
-- including spare knives in the backpack, entirely outside the exchange.
local function add_knives(section,actor_slot)
    section=section or "wpn_knife3"
    actor_slot=actor_slot or 5
    item(1010,section,0,actor_slot);actor.slots[actor_slot]=1010
    item(1110,section,10,2);npc.slots[2]=1110;npc.active_slot=2
    item(1011,section,0);item(1111,section,10)
end
local function knives_intact()
    check(knife_touches==0,"knife slots are never validated or written, and knives never enter ownership events")
    for _,entry in ipairs({{1010,0},{1011,0},{1110,10},{1111,10}})do
        local it=objects[entry[1]]
        check(it.parent_id==entry[2]and it.condition==.43 and it.upgrades=="test-upgrade",
            "both owners retain equipped and spare knives with original wear/upgrades")
    end
    check(actor:item_in_slot(objects[1010].slot):id()==1010 and clients[10]:item_in_slot(2):id()==1110,"nonstandard knife slots remain untouched")
end
for _,section in ipairs({"wpn_knife3","custom_blade","pack_melee"})do
    reset();add_knives(section);start();completed();intact();knives_intact()
    check(objects[0].display=="Target"and #transfers==4 and objects[1002].parent_id==10 and objects[1100].parent_id==0,
        "knives cannot block reincarnation; ordinary gear still changes owners exactly once")
    check(actor:active_item():id()==1100,"an active NPC knife is not selected as inherited firearm")
end
reset();add_knives("custom_blade",3);start();completed();intact();knives_intact()
check(objects[0].display=="Target"and actor:active_item():id()==1100 and actor.active_slot==2,
    "inherited rifle uses the other weapon slot instead of displacing a retained knife")
reset();add_knives();transfer_fail=2;start();completed();intact();knives_intact()
check(objects[0].display=="Old Player"and objects[1002].parent_id==0 and objects[1100].parent_id==10,
    "partial exchange rollback restores other gear without touching knives")
reset({keep_inventory=true});add_knives();start({keep_inventory=true});completed();intact();knives_intact()
check(objects[0].display=="Target"and #transfers==0,"keep-inventory behavior remains unchanged with knives present")

reset();death_manager.get_outfit_by_npc_visual=function()return nil end;start();completed();intact()
check(#transfers==0 and #created==0 and #moves==0 and squad.members[10]and objects[10].position.x==100,"missing matching clothes aborts before ownership mutation and restores actual NPC")

reset();transfer_fail=2;start();completed();intact()
check(objects[1001].parent_id==0 and objects[1002].parent_id==0 and objects[1100].parent_id==10 and actor:item_in_slot(7):id()==1001,
    "injected partial async transfer is compensated, including equipped original slots")
tick(10);check(native_pending==0 and objects[1001].parent_id==0,"no late forward event steals gear after rollback")
check(actor:character_name()=="Old Player"and objects[10].position.x==100 and squad.members[10],"rollback restores identity/location/squad")

reset();create_fail="npc_helmet";start();completed();intact()
check(objects[1100].parent_id==10 and objects[1002].parent_id==0 and actor:item_in_slot(7):id()==1001,"clothing failure compensates already transferred real gear")
check(objects[1200]==nil,"rollback deletes only matching clothing it created, never real inventory")

reset();definitions.npc_outfit.helmet_avaliable="false";start();completed()
check(#created==1 and created[1]=="npc_outfit"and actor:item_in_slot(12)==nil,"integrated helmet suit never receives an incompatible separate helmet event")
definitions.npc_outfit.helmet_avaliable=nil

reset();item(1130,"npc_outfit",10,7);item(1131,"npc_helmet",10,12);npc.slots[7]=1130;npc.slots[12]=1131
start();completed()
check(#created==0 and actor:item_in_slot(7):id()==1130 and actor:item_in_slot(12):id()==1131 and objects[1130].condition==.43,
    "existing real worn outfit/helmet are transferred and equipped with original condition, not generated again")

reset();item(1130,"player_outfit",10);start();completed()
check(#created==2 and actor:item_in_slot(7):section()=="npc_outfit","an unrelated carried outfit is not confused with clothing visible on NPC")

reset();move_fail=true;start();completed();intact()
check(actor:character_name()=="Old Player"and npc:character_name()=="Target"and npc:character_community()=="bandit"and npc.gw==0,
    "final setter failure restores both identities/factions/personal goodwill")
check(objects[1100].parent_id==10 and objects[1002].parent_id==0 and #created==2 and objects[1200]==nil,"setter failure returns items, removes only owned generated cosmetics")
check(#packet_writes==2 and objects[10].visual=="actors\\stalker\\npc_model"and live_npc_visual_calls==0,
    "late movement failure restores original NPC appearance through one server field event, never full STATE_Read or a live rollback setter")

reset();start()
for _=1,250 do tick(1);if state().job and state().job.phase=="commit"then break end end
local legacy=assert(state().job);check(legacy.phase=="commit","reach equipped state for voting.18 recovery fixture")
-- Exact state from the user screenshot: raw slots were submitted unchanged,
-- online name setter failed before either name changed, rollback then stalled.
objects[0].display="Old Player";objects[10].display="Target";objects[10].comm="bandit";npc.gw=0
actor.slots[6]=legacy.outfit;actor.slots[7]=legacy.helmet;actor.slots[12]=nil
actor.slots[2]=1100;actor.slots[3]=nil
legacy.phase="rollback";legacy.identity_changed=true;legacy.reason="voting18_nil_online_name_setter"
handler.on_game_load();completed();intact()
check(not handler.is_blocking()and actor:item_in_slot(7):id()==1001 and actor:item_in_slot(12)==nil and
    actor:item_in_slot(6)==nil and actor:item_in_slot(2):id()==1002 and npc:item_in_slot(3):id()==1100,
    "legacy wrong-slot state is first de-slotted, original gear restored, native-correct slots confirmed")
check(actor:character_name()=="Old Player"and npc:character_name()=="Target"and objects[0].position.x==0 and objects[10].position.x==100,
    "old journal no longer repeats nil name setter; names/positions restored and queue unblocked")

reset();local original_name=objects[10].set_character_name
objects[10].set_character_name=function(self,value)self.set_character_name=original_name;error("injected NPC server name failure")end
start();completed();intact()
check(actor:character_name()=="Old Player"and npc:character_name()=="Target"and objects[1002].parent_id==0 and objects[1100].parent_id==10,
    "failure after the actor name changed compensates both identities and inventory through server setters")

reset();start()
check(handler.on_effect_save("before"),"auto after-effect save accepts durable read-only selection")
local selection_save=clone(storage);handler.on_game_load();storage=selection_save;completed();intact()
check(objects[0].position.x==100 and #moves==1 and #transfers==4 and state().selection==nil,
    "restoring immediate autosave resumes pending selection and completes a single real teleport")

reset();tick(17);objects[0].set_character_name=nil
local accepted,_,reason=handler.can_execute({}, {})
check(not accepted and reason=="actor_server_name_api_missing"and #transfers==0 and not handler.is_blocking(),
    "missing true actor setter is rejected BEFORE selection, detachment or gear changes")

reset();objects[10].set_character_name=nil;start();completed()
check(#transfers==0 and squad.members[10]and #created==0 and objects[10].position.x==100,
    "missing NPC server setter fails preflight before any mutation")

reset();npc.set_character_icon=nil;start();completed()
check(#transfers==0 and squad.members[10]and #created==0,"online NPC capability mismatch is detected before mutation")

reset();definitions.npc_helmet.slot="6";start();completed();intact()
check(#transfers==0 and #created==0,"misconfigured helmet cannot be placed in armor slot even when LTX requests it")
definitions.npc_helmet.slot="11"

reset();add_knives();start()
for _=1,250 do tick(1);if state().job and state().job.phase=="transfer"then break end end
check(state().job and state().job.phase=="transfer","reach an unresolved transaction save point")
check(handler.on_effect_save("before")==false,"unresolved actual ownership transaction remains fenced from saves")
handler.on_game_load();events={};native_pending=0;tick(60)
check(not handler.is_blocking()and #moves==0 and objects[1100].parent_id==10 and objects[1002].parent_id==0,"external F9 pending save rolls back, never resumes half exchange")
knives_intact()

reset();start()
for _=1,250 do tick(1);if state().job and state().job.phase=="transfer"then break end end
local late_storage=clone(storage)
handler.on_game_load();storage={};events={};native_pending=0
check(not module.relife_complete("late_task")and not module.relife_fail("late_task"),"early native quest callbacks cannot fail a task before marshal restoration")
tick(25);storage=late_storage;tick(65)
check(not handler.is_blocking()and #moves==0 and objects[1002].parent_id==0 and objects[1100].parent_id==10,
    "late marshal journal is discovered even after normal first playable frames and compensated")

reset();start()
for _=1,250 do tick(1);if state().job and state().job.phase=="equip"then break end end
check(objects[1100].parent_id==0,"reach confirmed ownership before simulated external map change")
objects[0].m_game_vertex_id=3;objects[0].m_level_vertex_id=350;objects[0].position={x=600,y=0,z=0};clients[10]=nil
handler.on_game_load();tick(100)
check(not handler.is_blocking()and objects[1002].parent_id==0 and objects[1100].parent_id==10 and
    objects[10].m_game_vertex_id==1 and objects[0].m_game_vertex_id==3 and objects[0].position.x==600,
    "an external level changer can be compensated without an old-map actor setter or lost gear: "..table.concat(logs,"; "))

-- Regression for the real retail-engine crash: an ALife assertion inside a
-- native inventory functor is NOT caught by an outer pcall. Simulate that
-- boundary and require the callback to remain non-throwing in every case.
reset();ghost(0,1900,"bolt");ghost(0,1901,"anim_prop");ghost(10,1902,"device_pda_3")
start();completed();intact()
check(native_callback_errors==0 and #transfers==4 and objects[1100].parent_id==0,
    "client-only service/animation props never need an ALife record, never transfer, and cannot trigger the fatal callback")

reset();hidden_servers[1002]=true;start();until_waiting()
check(native_callback_errors==0 and #transfers==0 and #created==0 and #moves==0,
    "temporary missing actor ALife entry waits outside native callback before ALL ownership mutations")
check(state().job.prepare_reason:find("owner=0 item=1002 section=player_gun",1,true)~=nil,
    "waiting diagnostic identifies owner, item id and actual section, not a blind fatal assertion")
hidden_servers[1002]=nil;completed();intact()
check(objects[1100].parent_id==0 and objects[1002].parent_id==10 and native_callback_errors==0,
    "settled server record resumes exactly one full exchange")

reset();hidden_servers[1101]=true;start();until_waiting()
check(#transfers==0 and state().job.prepare_reason:find("owner=10 item=1101 section=ammo",1,true),
    "missing target item cannot silently produce an incomplete NPC snapshot or move actor gear first")
tick(230);intact()
check(not handler.is_blocking()and native_callback_errors==0 and #transfers==0 and #created==0 and #moves==0 and
    objects[1002].parent_id==0 and objects[1100].parent_id==10 and squad.members[10]and objects[10].position.x==100,
    "permanently absent normal server record times out and restores NPC without touching either inventory or crashing")
check(table.concat(logs,"; "):find("timeout_prepare: inventory_server_missing owner=10 item=1101 section=ammo",1,true)~=nil,
    "bounded wait produces a useful terminal rollback reason")

reset();objects[1002].parent_id=65535;ghost_inventory[#ghost_inventory+1]={owner=0,item=clients[1002]}
start();until_waiting()
check(native_callback_errors==0 and #transfers==0 and state().job.prepare_reason:find("inventory_owner_unsettled",1,true),
    "client inventory with a not-yet-settled parent never throws across native iterator")
objects[1002].parent_id=0;ghost_inventory={};completed()
check(objects[1100].parent_id==0 and objects[1002].parent_id==10,"ownership settles before the snapshot and transfer")

reset();local real_id=clients[1002].id
clients[1002].id=function()error("injected item getter error")end
start();completed();intact();clients[1002].id=real_id
check(native_callback_errors==0 and #transfers==0 and #moves==0 and objects[1002].parent_id==0 and squad.members[10],
    "even an item getter error happens AFTER native callback has returned, allowing ordinary rollback instead of retail-engine fatal")

reset();staging_blocked=true;start();tick(400)
check(not handler.is_blocking()and objects[10].position.x==100 and squad.members[10],"offline staging timeout can rollback without a client NPC")

reset();objects[10].m_game_vertex_id=2;start();completed()
check(#transfers==0 and #moves==0 and #created==0,"current-location default never steals NPC on another map")

reset();objects[10].m_game_vertex_id=2;start({any_location=true})
for _=1,250 do tick(1);if loading then break end end
check(loading and state().job.phase=="arrival"and travel_requests==1,"cross-level journal committed before deferred stock travel")
local move_count=#moves;tick(20)
check(#moves==move_count and handler.is_blocking()and objects[0].position.x==10 and visuals.pp[162802].factor==1 and
    not visuals.cam[162801],"load/precache stays black without a camera track, navigation or second travel")
loading=false;tick(15)
check(objects[0].position.x==10 and visuals.pp[162802].factor==1 and #moves==move_count,
    "ordinary entry point remains covered for all readiness frames")
tick(110);intact()
check(not handler.is_blocking()and objects[0].position.x==100 and objects[0].m_game_vertex_id==2 and objects[10].m_game_vertex_id==1,
    "any-location arrival verified after real playable frames; previous body remains on source map")
check(next(tasks)~=nil and #transfers==4,"cross-map load does not repeat item events; quest issued once")

reset();objects[10].m_game_vertex_id=2;travel_fail=true;start({any_location=true});completed();intact()
check(travel_requests==1 and not loading and objects[0].position.x==0 and objects[1002].parent_id==0 and
    objects[1100].parent_id==10 and squad.members[10],"stock travel refusal compensates exchange instead of leaving arrival blocked forever")

reset({keep_inventory=true});objects[10].m_game_vertex_id=2
death_manager.get_outfit_by_npc_visual=function()error("clothing lookup must not be used when keeping inventory")end
objects[1100]=nil;clients[1100]=nil;npc.slots[3]=nil
start({keep_inventory=true,any_location=true})
for _=1,250 do tick(1);if loading then break end end
check(loading and travel_requests==1 and #transfers==0 and #created==0,"preserved equipment supports even unmapped/unarmed NPC without taking its items")
loading=false;completed();intact()
check(objects[0].position.x==100 and actor:active_item():id()==1002 and actor:item_in_slot(7):id()==1001,
    "cross-map exact-position arrival also preserves original worn outfit and active gun")

reset();objects[10].visual="ACTORS/custom/path/STALKER_MONOLITH_2.OGF"
local lookup_key
death_manager.get_outfit_by_visual=function(key)lookup_key=key;if key=="stalker_monolith_2"then return {"npc_outfit","npc_helmet"}end end
death_manager.get_outfit_by_npc_visual=function()error("fixed third path component is not a model key")end
start();completed();intact()
check(lookup_key=="stalker_monolith_2"and objects[1100].parent_id==0 and objects[0].position.x==100,
    "real death-table model key is canonical basename without extension, independent of path depth/case/slashes")

reset();death_manager.get_outfit_by_visual=function()return nil end;death_manager.get_outfit_by_npc_visual=function()return nil end
start();completed();intact()
check(#transfers==0 and #created==0 and #moves==0 and objects[10].position.x==100 and squad.members[10]and
    not state().job,"unsupported model is skipped before squad/smart/NPC movement, not staged and rolled back after selection")

for _,kind in ipairs({"story","task","companion","solo","foreign_control","quest_inventory"})do
    reset()
    if kind=="story"then objects[10].story="story_npc"
    elseif kind=="task"then manager.task_info.keep.current_target=10
    elseif kind=="companion"then companions.companion_squads[100]=true
    elseif kind=="solo"then squad.members[11]=nil
    elseif kind=="foreign_control"then npc.script_owner="other_mod"
    elseif kind=="quest_inventory"then item(1102,"quest_item",10)end
    start();completed();intact()
    check(#transfers==0 and #moves==0,"protected target rejected without gear mutations: "..kind)
end
reset();tick(17)
local ctx={};check(handler.can_execute(ctx,{unknown="true"})==false,"unknown parameter rejected")
check(handler.can_execute(ctx,{keep_inventory="maybe"})==false,"malformed checkbox rejected")
local original=IsWeapon;IsWeapon=nil;check(handler.is_supported()==false,"missing native classifier fails support safely");IsWeapon=original
storage.adb_new_life_v1.bodies="corrupt"
local ok,complete=pcall(module.relife_complete,"bad_task")
check(ok and complete==false,"invalid quest storage cannot escape native CGameTask completion functor")
local ok,failed=pcall(module.relife_fail,"bad_task")
check(ok and failed==false,"invalid quest storage cannot escape native CGameTask fail functor")
local function group_fixture(id,members)
    local group={id=id,members=members or {},scripted_target=nil}
    function group:name()return "group_"..self.id end
    function group:clsid()return "squad"end
    function group:npc_count()local n=0;for _ in pairs(self.members)do n=n+1 end;return n end
    function group:squad_members()
        local key
        return function()key=next(self.members,key);if key then return {id=key,object=objects[key]}end end
    end
    function group:unregister_member(mid)assert(self.members[mid]);self.members[mid]=nil;objects[mid].group_id=65535 end
    function group:register_member(mid)assert(not self.members[mid]);self.members[mid]=true;objects[mid].group_id=self.id end
    function group:set_squad_relation(relation)self.relation=relation end
    function group:refresh()self.refreshed=true end
    objects[id]=group
    for mid in pairs(group.members)do if objects[mid]then objects[mid].group_id=id end end
    return group
end
local function expanded_runtime()
    squad=group_fixture(100,{[10]=true,[11]=true})
    local next_group=300
    local sim=alife()
    function sim:create(section,pos,lvid,gvid)
        check(section=="online_offline_group","only an empty squad shell is created, never replacement companions")
        next_group=next_group+1;return group_fixture(next_group,{})
    end
    SIMBOARD={assign_squad_to_smart=function(_,g,smart)g.smart=smart end,
        setup_squad_and_group=function()end,
        remove_squad=function(_,g)
            check(g:npc_count()==0,"never delete a live member while cleaning an empty squad")
            objects[g.id]=nil
        end}
    function se_save_var(id,_,key,value)
        local se=assert(objects[id]);se.vars=se.vars or {};se.vars[key]=value
        if key=="companion"then companions.non_task_companions[id]=value end
    end
    function companions.remove_from_actor_squad(n)
        local se=objects[n:id()];companions.companion_squads[se.group_id]=nil
        companions.non_task_companions[se.id]=nil
    end
    function companions.add_to_actor_squad(n)
        local se=objects[n:id()];companions.non_task_companions[se.id]=true
        companions.companion_squads[se.group_id]=objects[se.group_id]
    end
    function alife_storage_manager.get_se_obj_state(se,create)
        storage.se_object=storage.se_object or {}
        if not storage.se_object[se.id]and create then storage.se_object[se.id]={name=se:name()}end
        return storage.se_object[se.id]
    end
    rawset(_G,"__anomaly_donation_bridge_named_companion_v1",nil)
    load_module("anomaly_donation_bridge_named_companion",root)
    callback={actor_before_death=123}
    load_module("anomaly_donation_bridge_effect_hardcoon_adb_new_life_azazel",optional).on_game_start()
end
local function follower(id,group,pos)
    local n=human(id,"Companion "..id,1,{x=pos or 20,y=0,z=0},group.id)
    group.members[id]=true;group.scripted_target="actor"
    companions.companion_squads[group.id]=group;companions.non_task_companions[id]=true
    se_save_var(id,objects[id]:name(),"adb_companion_community_v1","stalker")
    return n
end
local function activate(options)
    tick(17)
    local ctx,params={},{}
    for key,value in pairs(options or {})do params[key]=tostring(value)end
    check(az_handler.is_supported()==true,"native predeath capability supported")
    local can,_,why=az_handler.can_execute(ctx,params)
    check(can==true,"Azazel activation validation: "..tostring(why))
    check(az_handler.execute(ctx)=="executed","timed mode arms without forcing an immediate reincarnation")
    check(not handler.is_blocking(),"armed mode leaves the effect queue running")
end
local function lethal()
    local flags={ret_value=true}
    callbacks.actor_on_before_death(65535,flags)
    return flags
end

reset();expanded_runtime()
local old_group=group_fixture(200,{})
follower(22,old_group);follower(21,old_group);follower(23,old_group)
objects[23].story="quest_character" -- alongside actual active task 21
item(1300,"ammo",22)
start();completed()
local body=next(state().bodies);body=state().bodies[body]
check(body.group and objects[22].group_id==body.group.id and objects[10].group_id==body.group.id,
    "normal New Life leaves companions with former body in one actual squad")
check(not companions.non_task_companions[22] and objects[22].comm=="arena_enemy" and clients[22].gw==-5000,
    "former group's hostility follows old-body setting and companion maintenance is released")
check(objects[1300].parent_id==22 and objects[22].display=="Companion 22" and objects[22].native_health==.73,
    "former companion identity, health and real inventory are not recreated")
check(companions.non_task_companions[21]and companions.non_task_companions[23]and old_group.scripted_target=="actor",
    "quest/story companions remain followers even when sharing an ordinary companion's squad")
handler.on_game_load();tick(60)
check(objects[22].group_id==body.group.id and objects[1300].parent_id==22,"former group survives reload without duplicate members")

reset();expanded_runtime();old_group=group_fixture(200,{});follower(22,old_group)
start({leave_companions=false});completed()
check(companions.non_task_companions[22]and objects[22].group_id==200,"unchecked option preserves current companions")
reset();expanded_runtime();old_group=group_fixture(200,{});follower(22,old_group)
move_fail=true;start();completed()
check(companions.non_task_companions[22]and objects[22].group_id==200,"failed identity swap never takes the group from the player")
reset();expanded_runtime();old_group=group_fixture(200,{});follower(22,old_group)
start({hostile_previous_body=false});completed()
check(objects[22].comm=="stalker"and not clients[22].gw and objects[200]==nil,"friendly former group; only truly empty old squad is removed")

reset();expanded_runtime();add_knives();activate();tick(34)
local left=state().azazel.remaining_ms
check(left<180000 and objects[0].display=="Old Player","arming only starts a countdown")
local flags=lethal()
check(flags.ret_value==false and state().azazel.pending and #transfers==0 and #death_calls==0,
    "native predeath callback cancels death without mutating inventories or native objects")
check(not handler.on_effect_save("before"),"automatic effect saves cannot record a pending death before transfer")
check(lethal().ret_value==false,"duplicate lethal callback cannot queue a second body")
completed()
check(objects[0].display=="Target"and objects[0].comm=="actor_stalker"and not objects[10].living,
    "Azazel reincarnates, preserves faction and leaves actual old body dead")
check(objects[1002].parent_id==10 and objects[1100].parent_id==0 and not next(state().bodies) and not next(tasks),
    "real equipment changes once; no old-body quest is created")
check(state().azazel.rebirths==1 and not state().azazel.pending and actor.health==1,"mode remains armed after first death")
check(manager.task_info.keep.title=="Existing task","Azazel never fails player tasks")
knives_intact()

reset();expanded_runtime();old_group=group_fixture(200,{})
follower(22,old_group,200);follower(23,old_group,300)
activate({keep_inventory=true});tick(34);lethal();completed()
check(objects[0].display=="Companion 22" and not objects[22].living and companions.non_task_companions[23],
    "prefers nearest eligible human companion over nearer stranger; other companion stays")
check(old_group.scripted_target=="actor"and companions.companion_squads[200]and objects[1002].parent_id==0,
    "consuming one member keeps the rest of that squad following and respects keep inventory")
lethal();completed()
check(objects[0].display=="Companion 23"and state().azazel.rebirths==2 and not objects[23].living,
    "each subsequent death reincarnates again, including a singleton companion")
check(objects[200]==nil,"consumed singleton's empty squad is cleaned safely")

reset();expanded_runtime();old_group=group_fixture(200,{});follower(22,old_group,200)
activate({keep_inventory=true,prefer_companions=false,keep_faction=false});tick(34);lethal();completed()
check(objects[0].display=="Target"and objects[0].comm=="actor_bandit"and companions.non_task_companions[22],
    "preference off chooses nearest ordinary NPC and optional faction transfer does not detach followers")

reset();expanded_runtime();objects[10].m_game_vertex_id=2
activate({any_location=true,keep_inventory=true});tick(34);lethal()
tick(100);loading=false;clients[10]=nil;objects[10].online=false;tick(80)
check(objects[0].m_game_vertex_id==2 and not objects[10].living and state().azazel.rebirths==1,
    "cross-map reincarnation finishes with former body offline on the previous map")

reset();expanded_runtime();activate({duration_seconds=10,keep_inventory=true})
tick(20);local saved=clone(storage);local saved_left=state().azazel.remaining_ms
paused=true;tick(200);check(state().azazel.remaining_ms==saved_left,"pause does not consume Azazel")
paused=false;tick(5);storage=clone(saved);az_handler.on_game_load();tick(18)
check(state().azazel.remaining_ms>=saved_left-200 and state().azazel.options.keep_inventory,"F9 restores remaining time and options from loaded timeline")
-- Simulate a fresh Lua VM/process with only the serialized save retained.
load_module("anomaly_donation_bridge_azazel_hud",root);load_module(core_name,root)
module=load_module(module_name,optional);module.on_game_start()
load_module("anomaly_donation_bridge_effect_hardcoon_adb_new_life_azazel",optional).on_game_start()
clock=900000;frame=20;storage=clone(saved);az_handler.on_game_load();tick(18)
check(state().azazel.remaining_ms>=saved_left-200,"full process restart restores timer without spending offline time")
activate({duration_seconds=10});check(state().azazel.remaining_ms>10000,"repeat activation extends remaining duration")
tick(210);check(not state().azazel and lethal().ret_value==true,"expired mode returns ordinary death")

reset();expanded_runtime();objects[10].story="protected"
activate();tick(34);lethal();tick(5)
check(not objects[0].living and death_calls[#death_calls].bypass==true and not state().azazel,
    "no valid body falls back to native final death, without recursion or immortality")
reset();expanded_runtime();tick(17);storage.enable_azazel_mode=true
local can,_,why=az_handler.can_execute({},{});check(can==false and why=="stock_azazel_already_enabled","does not fight stock Azazel game mode")
storage.enable_azazel_mode=nil;callback.actor_before_death=nil
check(az_handler.is_supported()==false,"unsupported death API is rejected before arming")

-- A special companion must not reapply its old visual/faction after the
-- client's recycle; a failed transaction must put those overrides back.
for _,fail in ipairs({false,true})do
    reset();expanded_runtime();companions.non_task_companions[10]=true;companions.companion_squads[100]=squad
    local sd=alife_storage_manager.get_se_obj_state(objects[10],true)
    sd.adb_special_companion_visual="actors\\special\\hip";sd.adb_special_companion_icon="hip_icon";sd.icon_name="hip_icon"
    se_save_var(10,objects[10]:name(),"adb_companion_community_v1","bandit")
    if fail then move_fail=true end
    activate({keep_inventory=true});tick(34);lethal();completed()
    if fail then
        check(sd.adb_special_companion_visual=="actors\\special\\hip" and companions.non_task_companions[10]and
            objects[10].living and not objects[0].living,"failed reincarnation restores special visual and companion, then permits ordinary actor death")
    else
        check(not sd.adb_special_companion_visual and not sd.icon_name and not se_load_var(10,objects[10]:name(),"adb_companion_community_v1"),
            "successful reincarnation removes consumed companion's appearance and faction overrides")
    end
end

reset();expanded_runtime();activate({duration_seconds=60});tick(34)
local before=state().azazel.remaining_ms
handler.update(clock);az_handler.update(clock)
check(before==state().azazel.remaining_ms,"both installed presets share one tick, timer never advances twice")
reset();expanded_runtime();activate();tick(34);lethal();manager.task_info=nil;tick(2)
check(not objects[0].living and not state().azazel,"a selection API error cannot leave an immortal pending actor")
reset();expanded_runtime();activate({duration_seconds=60});tick(34)

-- Stock-style HUD: text is updated once per displayed second, not recreated
-- each frame, and the parent-owned native pointer is discarded on teardown.
local hud_created,hud_deleted,hud_writes,hud_text,hud_position=0,0,0
local hud_root={children={}}
function vector2()return {set=function(self,x,y)self.x,self.y=x,y;return self end}end
function get_hud()return {GetWindow=function()return hud_root end}end
function CScriptXmlInit()return {
    ParseFile=function(_,path)check(path=="ui_custom_msgs.xml","Azazel uses stock message layout")end,
    NodeExist=function()return true end,
    InitStatic=function(_,node,parent)
        check(node=="gameplay_notification","same native label as god mode")
        local window={live=true};parent.children[window]=true;hud_created=hud_created+1
        function window:Enable(value)check(self.live and not value,"timer cannot capture input")end
        function window:GetWidth()return 630 end;function window:GetHeight()return 30 end
        function window:SetWndPos(pos)hud_position=pos end
        function window:Show(value)check(self.live,"no stale native HUD pointer");self.visible=value end
        function window:TextControl()return {SetText=function(_,text)
            check(window.live,"text control alive");hud_text=text;hud_writes=hud_writes+1
        end}end
        function parent:DetachChild(child)
            check(child.live and self.children[child],"only the owned live window can be detached")
            child.live=false;self.children[child]=nil;hud_deleted=hud_deleted+1
        end
        return window
    end}end
local az_ui=anomaly_donation_bridge_azazel_hud
az_ui.update(180000,false);az_ui.update(180000,false)
check(hud_created==1 and hud_writes==1 and hud_position.x==197 and hud_position.y==718,
    "bottom-centre native timer, stable window across frames")
check(hud_text==anomaly_donation_bridge_common.game_text_cp1251("Азазель: 180 сек."),"exact Russian label")
az_ui.update(179000,false);check(hud_writes==2,"countdown updates text")
inventory_open=true;az_ui.update(179000,false)
local window=next(hud_root.children);check(not window.visible and hud_deleted==0,"menus hide without destroying the timer")
inventory_open=false;az_ui.update(179000,false);check(window.visible,"timer returns after menu")
callbacks.actor_on_net_destroy();check(hud_deleted==1,"timer detaches before actor HUD teardown")
hud_root={children={}};az_ui.loaded();frame=frame+8;az_ui.update(178000,false)
check(hud_created==2,"HUD is rebuilt on loaded game's new parent")
az_ui.update(nil);check(hud_deleted==2,"timer removed when Azazel ends")
get_hud=nil;CScriptXmlInit=nil;vector2=nil

print("New life / Azazel smoke passed: "..checks.." assertions (mock native APIs; no live game test).")
