local core,optional=assert(arg[1]),assert(arg[3])
local function load_module(root,name)
    local env=setmetatable({},{__index=_G});local chunk=assert(loadfile(root.."/"..name..".script"))
    setfenv(chunk,env);chunk();return env
end
local function v(x,y,z)
    return {x=x or 0,y=y or 0,z=z or 0,set=function(self,a,b,c) self.x,self.y,self.z=a,b,c;return self end,
        getH=function(self) return math.atan2(-self.x,self.z) end,
        getP=function(self) return math.atan2(self.y,math.sqrt(self.x*self.x+self.z*self.z)) end}
end
vector=function() return v() end
vector2=function() return {set=function(self,x,y) self.x,self.y=x,y;return self end} end
Frect=function() return {set=function(self,...) return self end} end
local function copy(p) return v(p.x,p.y,p.z) end
local now,frame,paused,busy,missing,fail_setup=1000,1,false,false,nil,false
local memory,callbacks,attachments,targets={},{},{},{}
local camera_side_wall
local wall,logs,damage_calls,camera_sets,removes=nil,{},0,0,0
local ray_layers={}
local camera,custom=2,false
local initial_dir=v(0,0,1)
local camera_fov=67.5
local display_width,display_height=1920,1080
local fx,sounds,fx_created,sound_events={},{},{},{}
local missing_sounds
local copied_files,copy_count,delete_count={},0,0
local service_files={}
local visual_center_y
local protection={artefact=1,outfit=0,immunity=1,health_part=1,bone=1,boost=0}
local NAME="adb_controller_form_visual_v1"
local SAVE="adb_controller_form_recovery_v1"
local fail_jump,fail_jump_read,jump_writes=false,false,{}
local actor={jump=6,pos=v(),living=true,visual="mod/actor",velocity=v(),hp=0.77,health=0.77}
-- Luabind game_object does not implement __eq; different wrappers must not
-- be compared with ==, including during target iteration and actor guards.
local object_meta={__eq=function() error("No such operator [__eq] defined in class [game_object]") end}
setmetatable(actor,object_meta)
function actor:position() return self.pos end
function actor:id() return 0 end
function actor:item_in_slot() return nil end
function actor:alive() return self.living and self.health>0 end
function actor:get_visual_name() return self.visual end
function actor:get_movement_speed() return self.velocity end
function actor:is_talking() return false end
function actor:get_current_holder() return self.holder end
function actor:active_item() return self.item end
function actor:set_actor_direction(yaw,pitch,roll) self.angles={yaw,pitch,roll} end
function actor:get_actor_jump_speed() if fail_jump_read then error("jump_read_failed") end;return self.jump end
function actor:set_actor_jump_speed(value)
    assert(type(value)=="number" and value>0)
    self.jump=value;jump_writes[#jump_writes+1]=value
    if fail_jump then fail_jump=false;error("jump_failed_after_change") end
end
local function forbidden() error("Forbidden persistent actor/world mutation") end
actor.set_visual_name=forbidden;actor.set_bone_visible=forbidden;actor.activate_slot=forbidden
actor.set_actor_position=forbidden;actor.set_movement_speed=forbidden;actor.invulnerable=forbidden
alife_create=forbidden;alife_create_item=forbidden;show_indicators=forbidden;disable_input=forbidden
local move_defaults={walk_accel=11.5,run_coef=2.41,runback_coef=1.33,walk_back_coef=0.5,
    sprint_koef=2.08,walk_strafe_coef=1,run_strafe_coef=1,sprint_strafe_coef=0.8}
local move_values,move_writes,fail_move,fail_move_before={},{},nil,nil
for key,value in pairs(move_defaults) do
    local field=key;move_values[field]=value
    actor["get_actor_"..field]=function() return move_values[field] end
    actor["set_actor_"..field]=function(_,value)
        if fail_move_before==field then fail_move_before=nil;error("movement_set_failed_before_change") end
        move_values[field]=value;move_writes[#move_writes+1]={key=field,value=value}
        if fail_move==field then fail_move=nil;error("movement_set_failed_after_change") end
    end
end
local function movement_original()
    for key,value in pairs(move_defaults) do assert(math.abs(move_values[key]-value)<0.00001,"Leaked movement field: "..key) end
end
db={actor=actor,storage={}};_HUD_STATE=true;_GUIs={};actor_effects={item_not_in_use=true}
actor_menu={is_hud_free=function() return not busy end}
function Check_UI() return busy end
function time_global() return now end
function device() return {frame=frame,cam_dir=initial_dir,width=display_width,height=display_height,fov=camera_fov,is_paused=function() return paused end} end
local body_debug="0"
local fail_zoom
function get_console() return {
    get_string=function(_,key) assert(key=="show_actor_body");return body_debug end,
    get_float=function(_,key) assert(key=="fov");return camera_fov end,
    execute=function(_,command)
        local value=tonumber(command:match("^fov ([%d%.]+)$"));assert(value and value>=5 and value<=180,"Unexpected console mutation")
        camera_fov=value
        if fail_zoom then fail_zoom=false;error("FOV setter failed after change") end
    end
} end
alife_storage_manager={get_state=function() return memory end}
local sections={arbitrary_boss_x={class="SM_CONTR",visual="custom\\independent_controller"},
    a_dog={class="SM_DOG_S",visual="custom\\dog"},loose_prop={class="O_PHYS_S"},test_mutant={class="SM_BOARW"},test_npc={class="AI_STL_S"}}
local stories={}
local servers={}
local weapons={}
local no_tear
function get_object_story_id(id) return stories[id] end
function IsItem(kind,section) return false end
function system_ini() return {section_exist=function(_,s) return sections[s]~=nil end,
    r_string=function(_,s,k) return assert(sections[s])[k] end,line_exist=function(_,s,k) return sections[s] and sections[s][k]~=nil end} end
function getFS() return {
    exist=function(_,alias,path)
        if alias=="$game_saves$" then return service_files[path] end
        if path==nil then return copied_files[alias] and {size_real=#copied_files[alias]} or nil end
        if alias=="$game_data$" then return false end
        if alias=="$game_particles$" then return not no_tear and path=="anomaly2\\body_tear_00.pe" end
        if alias=="$game_sounds$" then
            return not missing_sounds and (path:match("_0%.ogg$") or path:match("earth4%.ogg$") or path=="material\\human\\step\\earth1.ogg" or path=="anomaly\\anomaly_body_tear_1.ogg" or path=="weapons\\test_pistol.ogg")~=nil
        end
        return not missing or missing~=path
    end,
    file_copy=function() copy_count=copy_count+1;error("Bite must not inspect particle library") end,
    file_delete=function() delete_count=delete_count+1;error("No temporary files expected") end,
    r_open=function() error("Unsafe native IReader must not be acquired") end,
    r_close=function() error("Unsafe native IReader must not be acquired") end
} end
particles_object=function(name)
    assert(name=="anomaly2\\body_tear_00" and not no_tear,"Unverified particle constructed")
    fx_created[#fx_created+1]=name
    local p={active=false}
    function p:set_direction(dir) assert(dir.y==1) end
    function p:play_at_pos(pos,looped) assert(looped==false);self.pos=copy(pos);self.active=true end
    function p:stop() self.active=false end
    fx[#fx+1]=p;return p
end
sound_object=setmetatable({looped=1,s2d=2},{__call=function(_,name)
    assert(not missing_sounds,"Missing sound file must never be constructed")
    local s={name=name}
    function s:play_at_pos(owner,pos,delay,flags)
        assert((owner:id()==actor:id() or owner.kind=="npc") and delay==0 and (flags==0 or flags==1 or flags==2))
        if flags==sound_object.s2d then assert(pos.x==0 and pos.y==0 and pos.z==0,"2D footsteps used world coordinates / panned to one ear") end
        self.pos=copy(pos);self.flags=flags;self.active=true;sound_events[#sound_events+1]=name
    end
    function s:set_position(pos) self.pos=copy(pos) end
    function s:stop() self.active=false end
    sounds[#sounds+1]=s;return s
end})
game={motion_exists=function(model,name) return missing~=name and not (missing=="bites" and name:match("^stand_attack_")) end}
function IsStalker(object) return object.kind=="npc" end
function IsMonster(object) return object.kind=="mutant" end
function IsWeapon(object) return object.kind=="weapon" end
local enemy_id=10
local function enemy(kind,z)
    enemy_id=enemy_id+1
    local object={kind=kind,pos=v(0,1.3,z),living=true,hits={},health=1,object_id=enemy_id,relations={},scripted=false,orders={},serial=enemy_id}
    setmetatable(object,object_meta)
    function object:id() return self.object_id end
    function object:section() return "test_"..kind end
    function object:center(hud) assert(hud==false);return copy(self.pos) end
    function object:alive() return self.living end
    function object:bounding_box(hud) assert(not hud);return {max={y=self.kind=="npc" and 1.8 or (self.height or 1.2)}} end
    function object:list_bones(hud) assert(hud==false);return {[0]="zero_damage_root",[7]="modded_collision_bone"} end
    function object:hit(h)
        assert(h.bone=="modded_collision_bone")
        assert(h.type==hit.wound and h.draftsman:id()==actor:id() or h.type==hit.fire_wound and (h.draftsman:id()==actor:id() or h.draftsman:id()==self:id()))
        damage_calls=damage_calls+1;self.hits[#self.hits+1]=h
        if not self.immune then self.health=math.max(0,self.health-h.power) end
    end
    function object:position() assert(not self.stale);return copy(self.pos) end
    function object:parent() return self.owner end
    function object:kill(killer) assert(self.living);self.living=false;self.health=0;self.killer_id=killer:id() end
    function object:get_visual_name() return "test_human" end
    function object:item_in_slot(slot) assert(slot==1);return self.pistol end
    function object:animation_count() return self.animation_queue and #self.animation_queue or 0 end
    function object:add_animation(name,full,looped)
        assert(full and not looped)
        if self.reject_animation then return end
        self.animation_queue=self.animation_queue or {};self.animation_queue[#self.animation_queue+1]=name
    end
    function object:clear_animations() self.animation_queue={};self.cleared=(self.cleared or 0)+1 end
    function object:target_movement_type() return self.movement or 2 end
    function object:set_movement_type(value) self.movement=value end
    function object:set_item(action,weapon) assert(action==_G.object.idle and weapon:id()==self.pistol:id());self.weapon_drawn=true end
    function object:in_smart_cover() return self.smart_cover==true end
    function object:name() return "unit_"..self.serial end
    function object:character_name() return self:name() end
    function object:character_community() return self.kind end
    function object:level_vertex_id() return 1 end
    function object:accessible(vertex) return not self.blocked_path end
    function object:set_path_type(path) assert(path==game_object.level_path);self.path=path end
    function object:set_dest_level_vertex_id(vertex) assert(vertex>1 and vertex<4294967295);self.destination=vertex end
    function object:motivation_action_manager()
        return {action=function(_,id) assert(id==188115);return not self.panic_immune and {} or nil end}
    end
    function object:goodwill(other) return self.relations[other:id()] or 0 end
    function object:force_set_goodwill(value,other)
        self.relations[other:id()]=value-relation_registry.community_goodwill(self.kind,other:id())-
            relation_registry.community_relation(self.kind,other.kind or "actor")
        if self.fail_relation then self.fail_relation=false;error("native_setter_failed_after_change") end
        if self.fail_restore then self.fail_restore=false;error("restore_failure") end
    end
    function object:relation(other)
        if other:id()==0 and self.hostile~=nil and self.relations[0]==nil then return self.hostile and 2 or 0 end
        local value=self:goodwill(other)+relation_registry.community_goodwill(self.kind,other:id())+
            relation_registry.community_relation(self.kind,other.kind or "actor")
        return value< -500 and 2 or value>500 and 0 or 1
    end
    function object:has_info() return false end
    function object:get_script() assert(not self.stale);return self.scripted end
    function object:get_script_name() assert(not self.stale);return self.script_owner or "" end
    function object:script(enabled,owner)
        assert(not self.stale)
        assert(enabled~=self.scripted and (enabled or owner==self.script_owner),
            "Invalid sequence of taking an entity under script control")
        self.scripted=enabled;self.script_owner=enabled and owner or ""
        if not enabled then self.orders={} end
    end
    function object:enable_memory_object(target,enabled) assert(not self.stale);self.memory_enabled=enabled end
    function object:make_object_visible_somewhen(target) assert(not self.stale);self.remembered=target:id() end
    object.force_set_position=forbidden
    function object:reset_action_queue()
        assert(self.scripted and self.script_owner=="xr_logic")
        self.orders={};self.queue_resets=(self.queue_resets or 0)+1
    end
    local cse={id=object:id(),unit=object,kind=kind}
    function cse:name() return self.unit:name() end
    function cse:section_name() return self.unit:section() end
    function cse:community() assert(self.kind=="npc" or self.kind=="actor","Only traders have communities");return self.kind end
    function cse:force_set_goodwill(value,target_id)
        local victim=assert(servers[target_id])
        self.unit.relations[target_id]=value-relation_registry.community_goodwill(self:community(),target_id)-
            relation_registry.community_relation(self:community(),victim:community())
        if self.unit.fail_restore then self.unit.fail_restore=false;error("restore_failure") end
    end
    servers[cse.id]=cse
    db.storage[object:id()]=db.storage[object:id()] or {}
    object.get_physics_shell=function() error("Living creature physics must not be probed") end
    return object
end
hit=setmetatable({strike=1,fire_wound=2,wound=3,explosion=4,radiation=5,burn=6},{__call=function() return {} end})
rq_result=function() return {} end;rq_target={rqtStatic=1,rqtBoth=2}
local hud={windows={}}
function hud:AddDialogToRender(window)
    assert(not window.stale);self.windows[window]=true
end
function hud:RemoveDialogToRender(window)
    assert(not window.stale,"Old HUD userdata accessed after load")
    self.windows[window]=nil
end
function get_hud() return hud end
CUIScriptWnd={SetAutoDelete=function() end,SetWndRect=function() end,
    AttachChild=function(self,child) self.child=child;self.children=self.children or {};self.children[#self.children+1]=child end,
    Update=function(self) assert(not self.stale) end,
    ShowDialog=forbidden,HideDialog=forbidden,AllowMovement=forbidden}
local constructing
function super() end
function class(name)
    local env=getfenv(2)
    return function(base)
        env[name]=setmetatable({},{__index=base,__call=function(cls,...)
            local self=setmetatable({},{__index=cls});constructing=self;self:__init(...);constructing=nil;return self
        end})
    end
end
CUITextWnd=function() return {SetAutoDelete=function() end,SetWndPos=function(self,pos) self.pos=pos end,SetWndSize=function(self,size) self.size=size end,
    AdjustWidthToText=function(self) self.size.x=#(self.text or "")*7*math.min(1,(display_height/display_width)/(768/1024)) end,
    GetWidth=function(self) return self.size.x end,
    Show=function(self,shown) self.shown=shown end,
    SetFont=function() end,SetTextAlignment=function(self,alignment) self.alignment=alignment end,
    SetTextComplexMode=function(_,complex) assert(complex==false,"HUD rows must not rely on complex-text newline parsing") end,
    SetText=function(self,text) assert(not text:find("\n",1,true));self.text=text end,SetTextColor=function(self,color) self.color=color end} end
CUIStatic=function() return {SetAutoDelete=function() end,SetWndPos=function(self,pos) self.pos=pos end,
    Show=function(self,shown) self.shown=shown end,
    SetWndSize=function(self,size) self.size=size end,InitTexture=function(self,texture) assert(texture=="adb_burer_aim_ring","No black DDS backgrounds allowed");self.texture=texture end,
    SetStretchTexture=function(_,stretch) assert(stretch) end} end
function GetFontLetterica16Russian() return "font" end
function GetARGB(a,r,g,b) return a*16777216+r*65536+g*256+b end
DIK_keys={DIK_X=45,DIK_V=47,DIK_ESCAPE=1,DIK_1=2,DIK_2=3,DIK_3=4,DIK_F=2,DIK_R=4,MOUSE_1=3,MOUSE_2=5,
    DIK_SPACE=57,DIK_G=6,DIK_H=7,DIK_J=8,DIK_K=9,DIK_W=17,DIK_F5=63,DIK_F9=67}
key_bindings={kWPN_1=20,kWPN_2=21,kWPN_3=22,kWPN_FIRE=30,kUSE=31,kCAM_1=32,kINVENTORY=33,
    kSPRINT_TOGGLE=54,kFWD=55,kCROUCH=56,kJUMP=50,kQUICK_SAVE=51,kQUICK_LOAD=52,kACTIVE_JOBS=34,kCUSTOM20=35,kNEXT_SLOT=36,kPREV_SLOT=37}
local sprint_dik=DIK_keys.DIK_X
local jump_dik=DIK_keys.DIK_SPACE
function bind_to_dik(bind)
    return bind==key_bindings.kJUMP and jump_dik or bind==key_bindings.kSPRINT_TOGGLE and sprint_dik or bind==key_bindings.kQUICK_SAVE and DIK_keys.DIK_F5 or DIK_keys.DIK_F9
end
function dik_to_keyname(dik) return dik==DIK_keys.DIK_G and "G" or "Unknown" end
level_input={on_mouse_move=function() end}
level={
    iterate_nearest=function(origin,radius,visit)
        assert(radius>0 and radius<=61)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) if id==0 then return actor end;if weapons[id] then return weapons[id] end;for _,object in ipairs(targets) do if object.id and object:id()==id and not object.offline then return object end end end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_controller" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==true and speed==1,"Controller pose crossfade must not change playback speed")
            self.motion=motion;self.motions[#self.motions+1]=motion
            self.motion_end=motion~="stand_idle_0" and now+1000 or nil
            return motion=="stand_idle_0" and 0 or 1000
        end
        function body:set_position(pos) assert(not self.stale);self.pos=copy(pos) end
        function body:get_center() return visual_center_y and v(0,visual_center_y,0) end
        function body:set_rotation(h,p,b) assert(not self.stale and p==0 and b==0);self.rotation=h end
        attachments[name]=body;return body
    end,
    get_attachment=function(name)
        -- Do not bless pointer-wrapper equality in mocks. C++ API calls can
        -- produce fresh luabind userdata while the model's Lua userdata stays.
        local body=attachments[name]
        return body and setmetatable({},{__index=body}) or nil
    end,
    remove_attachment=function(name) assert(attachments[name] and not attachments[name].stale);attachments[name]=nil;removes=removes+1 end,
    get_active_cam=function() return camera end,
    set_active_cam=function(mode) camera=mode;if fail_setup then fail_setup=false;error("camera_failed_after_change") end end,
    set_cam_custom_position_direction=function(pos,angles,smooth,hudEnabled,hudAffect)
        assert(smooth==1 and hudEnabled==false and hudAffect==false)
        custom=true;camera_sets=camera_sets+1;level.camera_pos=copy(pos);level.camera_angles=copy(angles)
    end,
    remove_cam_custom_position_direction=function() custom=false end,
    ray_pick=function(origin,dir,range,mode,result,ignored)
        assert(ignored and type(ignored:id())=="number" and range>0 and range<=100000)
        local nearest,object,material_flags=range+1,nil,0
        if camera_side_wall and math.abs(dir.x)>0.00001 then
            local t=(camera_side_wall-origin.x)/dir.x
            if t>=0 and t<=range then nearest=t end
        end
        if wall and math.abs(dir.z)>0.00001 then
            local t=(wall-origin.z)/dir.z
            if t>=0 and t<=range then nearest=t end
        end
        if math.abs(dir.z)>0.00001 then
            for _,layer in ipairs(ray_layers) do
                local t=(layer.z-origin.z)/dir.z
                if t>=0 and t<=range and t<nearest then nearest,material_flags=t,layer.flags end
            end
        end
        if mode==rq_target.rqtBoth then
            for _,target in ipairs(targets) do
                local delta=v(target.pos.x-origin.x,target.pos.y-origin.y,target.pos.z-origin.z)
                local t=delta.x*dir.x+delta.y*dir.y+delta.z*dir.z
                local error2=(delta.x-dir.x*t)^2+(delta.y-dir.y*t)^2+(delta.z-dir.z*t)^2
                if not target.no_center_collision and target:id()~=ignored:id() and t>=0 and t<=range and t<nearest and error2<0.15^2 then nearest,object,material_flags=t,target,0 end
            end
        end
        if nearest<=range then result.range=nearest;result.object=object;result.material_flags=object and 128 or material_flags;result.element=object and (object.no_bone and 65535 or 7);return true end
        return false
    end
}
function RegisterScriptCallback(name,f) callbacks[name]=f end
function CBeforeHitAfterCalcs(h,target,bone) callbacks.on_before_hit_after_calcs(h,target,bone) end
function printf(fmt,...) logs[#logs+1]=string.format(fmt,...) end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.controller_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
function actor:name() return "actor_0" end
function actor:section() return "actor" end
function actor:character_community() return "actor" end
actor.kind="actor"
servers[0]={id=0,name=function() return actor:name() end,section_name=function() return actor:section() end,community=function() return "actor" end}
relation_registry={community_goodwill=function(_,id) return id==0 and 31 or 17 end,
    community_relation=function(a,b) return a==b and 400 or -230 end}
function alife() return {object=function(_,id) return servers[id] end,
    iterate_objects=function(_,visit)
        for _,server in pairs(servers) do if visit(server) then return end end
    end
} end
game_object={friend=0,neutral=1,enemy=2,level_path=3}
state_mgr={set_state=function(object,state) assert(state=="sprint");object.panic_state=state end}
action_base={execute=function(self) self.base_called=true end}
axr_stalker_panic={actid=188115,evaluator_stalker_panic={evaluate=function() return false end},
    action_stalker_panic={execute=function(self) self.original_called=true end}}
level.vertex_in_direction=function(vertex,dir,distance) assert(vertex==1 and distance==12);return 2+math.floor(math.abs(dir.x)*20) end
CAI_Stalker__BeforeHitCallback=function() end;CBaseMonster__BeforeHitCallback=function() end
act=setmetatable({attack=1,rest=2,panic=3},{__call=function(_,kind,target) return {kind=kind,target=target} end})
cond=setmetatable({time_end=1},{__call=function(_,kind,time) return {condition=kind,time=time} end})
function action(object,order,condition)
    assert(object.kind=="mutant" and object.scripted and object.script_owner=="xr_logic")
    assert(condition.time==(order.kind==act.panic and 8000 or 60000000))
    object.orders[#object.orders+1]=order;object.order=order;object.command_count=(object.command_count or 0)+1
end
local project_override
level.world2ui=function(pos,hud,offscreen)
    assert(not hud and not offscreen)
    if project_override=="nil" then return nil end
    if project_override=="offscreen" then return {x=2000,y=384} end
    return {x=512+pos.x*6,y=384-pos.y*10}
end
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_controller_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.controller_form"])
local function params(duration) return {camera_mode="center",mutant_sections="arbitrary_boss_x",duration_seconds=duration or "60",catalog_revision="revision"} end
local function active() return descriptor.is_blocking() end
local function advance(ms)
    now=now+ms;frame=frame+1
    for _,body in pairs(attachments) do
        if body.motion_end and now>=body.motion_end then body.motion="idle";body.motion_end=nil end
    end
    local windows={};for wnd in pairs(hud.windows) do windows[#windows+1]=wnd end
    for _,wnd in ipairs(windows) do wnd:Update() end
    descriptor.update()
end
local function key(dik,bind,kind)
    local flags={ret_value=true};callbacks[kind or "on_before_key_press"](dik,bind,false,flags);return flags
end
local function start(options)
    local p=params();for k,val in pairs(options or {}) do p[k]=val end
    local allowed,status,why=descriptor.can_execute({},p)
    assert(allowed,tostring(status)..":"..tostring(why))
    assert(descriptor.execute({},p)=="executed");assert(active() and custom and camera==0 and attachments[NAME])
    return attachments[NAME]
end
local function stopped()
    assert(not active() and not attachments[NAME] and not custom and camera==2)
    assert(actor.hp==0.77 and actor.visual=="mod/actor" and _HUD_STATE==true)
    movement_original()
    assert(math.abs(camera_fov-67.5)<0.00001,"Aim zoom leaked outside form")
    for _,p in ipairs(fx) do assert(not p.active,"VFX left behind after form cleanup") end
    for _,s in ipairs(sounds) do assert(not s.active,"Sound left behind after form cleanup") end
end
-- Legacy recovery tests intentionally end a suspended save before inspecting
-- the human cleanup journal. Continuation itself is tested by the shared suite.
local function cancel_saved_form()
    assert(active() and not custom,"saving must suspend native state before serialization")
    callbacks.on_before_load_input();stopped()
end
local function reject(p,why)
    local ok,status,reason=descriptor.can_execute({},p);assert(not ok and reason==why,tostring(reason))
    assert(not active() and not attachments[NAME])
end
local function form_hp()
    for wnd in pairs(hud.windows) do
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.controller_ui.hp.main.text):match("(%d+)%% HP"))
    end
end
local function actor_hit(power,kind,draftsman,flags)
    local h={power=power,type=kind or hit.fire_wound,draftsman=draftsman}
    flags=flags or {ret_value=true}
    if callbacks.actor_on_before_hit then callbacks.actor_on_before_hit(h,0,flags) end
    -- Native order: raw callback -> belt -> outfit -> immunity/boost ->
    -- health/bone coefficients -> calculated callback -> actor HP.
    if flags.ret_value~=false then
        h.power=math.max(0,(h.power or 0)*protection.artefact-protection.outfit)
        h.power=h.power*math.max(0,protection.immunity-protection.boost)
        if h.type~=hit.radiation then
            h.power=h.power*protection.health_part
            if h.type==hit.fire_wound or h.type==hit.wound or h.type==hit.burn then h.power=h.power*protection.bone end
        end
        CBeforeHitAfterCalcs(h,actor,0)
        if h.type~=hit.radiation then actor.health=actor.health-(h.power or 0) end
    end
    return h,flags
end

local function ui() for wnd in pairs(hud.windows) do return wnd.controller_ui end end
local function utf8(text) return anomaly_donation_bridge_common.game_text_utf8(text) end
local function stop()
    key(DIK_keys.DIK_ESCAPE);stopped()
    assert(memory.adb_controller_servants_recovery_v1==nil,"Restoration ledger leaked")
end
local function cast()
    key(DIK_keys.MOUSE_1)
    advance(16)
    for i=1,4 do advance(700) end
    advance(200)
end
local function capture(target)
    targets={target};cast()
    assert(utf8(ui().info.main.text):find("Слуги: 1/",1,true),"Capture failed: "..utf8(ui().info.main.text))
end

-- Metadata validation, safe APIs and three rebindable keys.
local p=params();p.catalog_revision="old";reject(p,"catalog_revision_mismatch")
p=params();p.mutant_sections="a_dog";reject(p,"controller_visual_class_unsupported")
for _,value in ipairs({"0","51","10.5","NaN"}) do
    p=params();p.max_servants=value;reject(p,"controller_max_servants_invalid")
end
for _,value in ipairs({"0","60001","1.5"}) do p=params(value);reject(p,"controller_duration_invalid") end
p=params();p.capture_key="F";reject(p,"controller_ability_keys_duplicate")
p=params();p.melee_key="W";reject(p,"controller_ability_key_invalid")
missing="psy_attack_2";reject(params(),"controller_animation_missing_psy_attack_2");missing=nil
local body=start();advance(16)
assert(form_hp()==100 and actor.health==0.77 and actor.jump==6)
assert(utf8(ui().keys.control.main.text)=="ЛКМ" and utf8(ui().keys.aim.main.text)=="ПКМ" and utf8(ui().keys.melee.main.text)=="F" and utf8(ui().keys.cycle.main.text)=="R")
assert(utf8(ui().labels.control.main.text)=="Контроль" and utf8(ui().labels.melee.main.text)=="Ближний удар" and not ui().keys.mark)
assert(ui().labels.melee.main.color==GetARGB(255,180,185,180) and math.abs(ui().left+ui().width/2-512)<0.01)
assert(ui().ring.texture=="adb_burer_aim_ring" and level.camera_pos.y>=2.4)
for _,bind in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20,key_bindings.kNEXT_SLOT}) do
    assert(not key(100,bind).ret_value)
end
assert(not key(DIK_keys.DIK_SPACE,key_bindings.kJUMP).ret_value)
assert(key(DIK_keys.DIK_SPACE,key_bindings.kJUMP,"on_before_key_release").ret_value)
stop()

-- Native human AI: restore original RAW goodwill, not a fabricated neutral/friend value.
local npc=enemy("npc",10);npc.hostile=true;npc.relations[0]=-9000
start();capture(npc)
assert(npc:relation(actor)==0 and npc.relations[0]~=-9000)
assert(npc.scripted==false,"NPC planner must remain native")
local attacks={};callbacks.on_enemy_eval(npc,actor,attacks)
assert(attacks.override and attacks.result==false)
local orders=memory.adb_controller_servants_recovery_v1.rows[1]
assert(orders.kind=="npc" and #orders.relations==1 and orders.relations[1].before==-9000)
local log_count=#logs
for i=1,100 do advance(16) end
assert(#logs==log_count,"Ordinary frame/order log spam")
stop();assert(npc.relations[0]==-9000)

-- Friend, neutral and hostile NPCs are all controllable; no spawns/removals or faction changes.
for _,relation in ipairs({-9000,0,9000}) do
    npc=enemy("npc",10);npc.relations[0]=relation
    start();capture(npc);stop();assert(npc.relations[0]==relation)
end

-- Native mutant command, no friendly relation requirement; orders are not restarted every frame.
local mutant=enemy("mutant",10);mutant.hostile=true
start();capture(mutant)
assert(mutant.scripted and mutant.script_owner=="xr_logic" and mutant.order.kind==act.rest)
local count=mutant.command_count
advance(1000);assert(mutant.command_count==count)
for i=1,8 do
    callbacks.monster_on_update(mutant)
    mutant:script(false,"xr_logic")
    if i%2==0 then mutant:script(true,"xr_logic") end
    advance(300)
    assert(mutant.scripted and mutant.script_owner=="xr_logic" and #mutant.orders==1)
end
count=mutant.command_count
for i=1,100 do advance(16) end
assert(mutant.command_count==count,"Native orders restarted on each frame")
local foe=enemy("npc",17);foe.hostile=true;foe.pos.x=1;targets={mutant,foe}
advance(300);assert(mutant.order.kind==act.attack and mutant.order.target:id()==foe:id())
local flags={};callbacks.on_enemy_eval(mutant,foe,flags);assert(flags.override and flags.result)
local friend=enemy("npc",17);friend.pos.x=-1;friend.hostile=false
flags={};callbacks.on_enemy_eval(mutant,friend,flags);assert(flags.override and not flags.result)
stop();assert(not mutant.scripted)

-- Original scripted owner is restored; another mod taking control is not overwritten.
mutant=enemy("mutant",10);mutant.scripted=true;mutant.script_owner="mob_walker"
db.storage={[mutant:id()]={active_scheme="mob_walker",mob_walker={actions={}}}}
xr_logic={issue_event=function(object,scheme,event)
    assert(object:id()==mutant:id() and scheme==db.storage[mutant:id()].mob_walker and event=="reset_scheme")
    object:script(true,"mob_walker");object.orders={{kind="restored_native_patrol"}}
end}
start();capture(mutant);stop();assert(mutant.scripted and mutant.script_owner=="mob_walker" and #mutant.orders==1)
db.storage={};xr_logic=nil
mutant.scripted=false;start();capture(mutant);mutant.script_owner="new_foreign_script";advance(300)
assert(utf8(ui().info.main.text):find("Слуги: 0/",1,true));stop()
assert(mutant.script_owner=="new_foreign_script")

-- All servants automatically choose actor enemies, never friends/neutral.
npc=enemy("npc",10);npc.relations[0]=50;start();capture(npc);npc.pos.x=4
mutant=enemy("mutant",10);targets={npc,mutant};cast();assert(mutant.scripted)
npc.pos.x=5;mutant.pos.x=-5
friend=enemy("npc",15);friend.hostile=false
local other=enemy("npc",18);other.hostile=true;other.pos.x=2
npc.relations[other:id()]=7621;other.relations[npc:id()]=-147
targets={npc,mutant,friend,other};advance(300)
assert(npc.remembered==other:id() and npc:relation(other)==2)
assert(mutant.order.kind==act.attack and mutant.order.target:id()==other:id())
flags={};callbacks.on_enemy_eval(npc,friend,flags);assert(flags.override and not flags.result)
flags={};callbacks.on_enemy_eval(npc,other,flags);assert(flags.override and flags.result)
local h={power=.4,draftsman=mutant};flags={ret_value=true};callbacks.npc_on_before_hit(other,h,0,flags);assert(flags.ret_value and h.power==.4)
h={power=.4,draftsman=mutant};flags={ret_value=true};callbacks.actor_on_before_hit(h,0,flags);assert(not flags.ret_value and h.power==0)
h={power=.4,draftsman=npc};flags={ret_value=true};callbacks.npc_on_before_hit(friend,h,0,flags);assert(not flags.ret_value and h.power==0)
other.living=false;advance(300)
assert(npc.relations[other:id()]==7621 and other.relations[npc:id()]==-147 and mutant.order.kind==act.rest)
stop();assert(npc.relations[0]==50)

-- Cap does not evict or convert anyone else. Failed casts do not steal cooldown.
npc=enemy("npc",10);start({max_servants="1"});capture(npc)
npc.pos.x=3;mutant=enemy("mutant",10);targets={npc,mutant};cast()
assert(not mutant.scripted and utf8(ui().info.main.text):find("Слуги: 1/1",1,true))
stop()
npc=enemy("npc",10);targets={npc};wall=5;start();cast()
assert(npc.relations[0]==nil);stop();wall=nil
ray_layers={{z=3,flags=128},{z=6,flags=128}};start();capture(npc);stop();ray_layers={}
-- More than 64 nearby loot/helper objects must not hide an eligible target.
do
    local victim=enemy("npc",10);start();targets={}
    for i=1,100 do local prop=enemy("prop",1);prop.pos.x=20;targets[#targets+1]=prop end
    targets[#targets+1]=victim;cast()
    assert(utf8(ui().info.main.text)=="Слуги: 1/10","Loose props exhausted capture candidate budget")
    stop()
    -- A servant standing between the camera/body and a new victim is not cover.
    local first=enemy("npc",10);local second=enemy("npc",20);start();capture(first)
    targets={first,second};cast()
    assert(utf8(ui().info.main.text)=="Слуги: 2/10","Servant skeleton blocked a mental capture")
    stop()
end
start();targets={npc};key(DIK_keys.MOUSE_1);advance(16)
assert(body~=attachments[NAME]);assert(attachments[NAME].motion=="psy_attack_0")
local first=attachments[NAME]
key(DIK_keys.MOUSE_1);advance(16);assert(first.motion=="psy_attack_0")
for i=1,4 do advance(700) end
assert(npc.relations[0]~=nil);stop()

-- Default ten slots is an actual runtime cap, not only a metadata default.
for _,distance in ipairs({31,100,1000,10000}) do
    npc=enemy("npc",distance);start();capture(npc)
    assert(ui().servants[1].main.shown,"Far captured servant has no world label")
    stop();local before=npc.relations[0]
    targets={npc};wall=distance/2;start();cast()
    assert(npc.relations[0]==before,"Unlimited capture went through a solid wall")
    stop();wall=nil
    npc.offline=true;start();cast()
    assert(utf8(ui().info.main.text)=="Слуги: 0/10","Offline creature was brought under control")
    stop()
end
local units={};targets={};start()
for i=1,10 do
    local unit=enemy("npc",10);unit.relations[0]=i*137;units[#units+1]=unit;targets[#targets+1]=unit
    cast();unit.pos.x=100
end
assert(utf8(ui().info.main.text):find("Слуги: 10/10",1,true))
local eleventh=enemy("npc",10);targets[#targets+1]=eleventh;cast()
assert(eleventh.relations[0]==nil);stop()
for i,unit in ipairs(units) do assert(unit.relations[0]==i*137) end

-- Incoming calculated damage only drains isolated form HP; native immunity remains outside Lua.
targets={};start()
actor_hit(.9);advance(16);assert(form_hp()==78 and actor.health==.77,"Default 400% HP must absorb .9 damage as 22.5%, not the old 30%")
stop();assert(actor.health==.77)
start({health_percent="300"})
actor_hit(.9);advance(16);assert(form_hp()==70 and actor.health==.77,"Explicit saved 300% HP must remain unchanged")
stop();assert(actor.health==.77)
-- F melee produces real native hit once; base .55, configurable 200% default.
local victim=enemy("mutant",2);targets={victim};body=start()
local before=damage_calls;key(DIK_keys.DIK_F);advance(16);advance(460)
assert(damage_calls==before+1 and math.abs(victim.hits[1].power-1.1)<.001,
    "Melee hits="..(damage_calls-before).." power="..tostring(victim.hits[1] and victim.hits[1].power).." logs="..table.concat(logs," | "))
advance(600);assert(damage_calls==before+1)
stop()

-- Expiry/F5/F9/GUI/transition restore identities and relations; servants are never deleted.
for _,exit in ipairs({"expiry","savekey","loadkey","gui","level"}) do
    npc=enemy("npc",10);npc.relations[0]=-2345
    start(exit=="expiry" and {duration_seconds="5"} or nil);capture(npc)
    if exit=="expiry" then advance(3000)
    elseif exit=="savekey" then callbacks.on_before_save_input();cancel_saved_form()
    elseif exit=="loadkey" then callbacks.on_before_load_input()
    elseif exit=="gui" then callbacks.GUI_on_show()
    else descriptor.on_level_changing() end
    stopped();assert(servers[npc:id()] and npc.relations[0]==-2345)
end
-- Offline trader rollback uses the CSE API, even when no game_object exists.
npc=enemy("npc",10);npc.relations[0]=-1234
start();capture(npc);npc.offline=true;key(DIK_keys.DIK_ESCAPE)
stopped();assert(npc.relations[0]==-1234);npc.offline=false
-- Partial native setter failure rolls back the pre-written lease.
npc=enemy("npc",10);npc.relations[0]=-3456;npc.fail_relation=true
start();targets={npc};cast();assert(npc.relations[0]==-3456);stop()
-- One restoration failure is retained/retried, without trapping player input.
npc=enemy("npc",10);npc.relations[0]=-4567;start();capture(npc);npc.fail_restore=true
key(DIK_keys.DIK_ESCAPE)
assert(not custom and not attachments[NAME] and active())
assert(key(100,key_bindings.kINVENTORY).ret_value)
local cleanup_save={};callbacks.save_state(cleanup_save)
assert(cleanup_save.adb_controller_servants_recovery_v1 and #cleanup_save.adb_controller_servants_recovery_v1.rows==1)
advance(1000);stopped();assert(npc.relations[0]==-4567)

-- ID reuse cannot inherit control or receive an old restoration write.
npc=enemy("npc",10);start();capture(npc)
local old_id=npc:id();local replacement=enemy("npc",10);replacement.object_id=old_id
servers[old_id]=servers[replacement.serial];servers[old_id].id=old_id
replacement.relations[0]=777;targets={replacement};advance(300);stop()
assert(replacement.relations[0]==777)

-- Native late save journal restores THIS loaded world's raw relations, not old userdata.
npc=enemy("npc",10);npc.relations[0]=-6789;start();capture(npc)
local saved={};callbacks.save_state(saved);cancel_saved_form()
assert(saved[SAVE] and saved.adb_controller_servants_recovery_v1 and npc.relations[0]==-6789)
npc.relations[0]=100000 -- simulate native relation registry serialized before Lua save callback
if saved[SAVE] then saved[SAVE].form_state=nil end;memory=saved;descriptor.on_game_load();advance(16)
assert(npc.relations[0]==-6789 and memory.adb_controller_servants_recovery_v1==nil and not active())
-- Service save frees leases BEFORE serialization, then reacquires only same-session identities.
npc=enemy("npc",10);npc.relations[0]=-7890;start();capture(npc)
descriptor.on_effect_save("before","service",nil)
assert(not custom and npc.relations[0]==-7890 and active())
local snapshot={};callbacks.save_state(snapshot);service_files["service.scop"]=true
descriptor.on_effect_save("after","service",true);advance(16)
assert(custom and npc:relation(actor)==0 and form_hp()==100)
stop();assert(npc.relations[0]==-7890)
-- A new world must never touch userdata or the ledger of an abandoned old world.
npc=enemy("npc",10);start();capture(npc)
npc.stale=true;attachments[NAME].stale=true
for window in pairs(hud.windows) do window.stale=true end
memory={};targets={};attachments={};hud.windows={};camera=2;custom=false
for key,value in pairs(move_defaults) do move_values[key]=value end -- freshly loaded actor
descriptor.on_game_load();advance(16);stopped()
-- 60,000-second boundary and pause do not lose the timer.
targets={};start({duration_seconds="60000"});paused=true;advance(60000);assert(active());paused=false
advance(59999000);assert(active());advance(1000);stopped()
assert(actor.jump==6 and #jump_writes==0,"Controller must not lease jump height")

-- Shared camera contract: legacy centre, default shoulder, live/remapped V,
-- collision, no hold-repeat, service-save persistence and per-activation default.
do
    targets={};wall=nil;ray_layers={};initial_dir=v(0,0,1);actor.velocity=v();visual_center_y=nil
    local p=params();assert(descriptor.execute({},p)=="executed")
    local centre=copy(level.camera_pos)
    assert(math.abs(centre.x)<0.001)
    assert(math.abs(centre.z-(actor.pos.z-3.6))<0.001 and math.abs(centre.y-(actor.pos.y+2.6))<0.001,"Centre camera framing changed")
    key(DIK_keys.DIK_ESCAPE);stopped()
    p.camera_mode=nil
    assert(descriptor.execute({},p)=="executed")
    local side=copy(level.camera_pos)
    local visual=attachments[NAME];local body_before=copy(visual.pos)
    assert(side.x>0.1 and side.y<centre.y and side.z>centre.z,"Default camera is not closer/lower/right shoulder")
    assert(math.abs(side.z-(actor.pos.z-1.4))<0.001 and math.abs(side.x-(actor.pos.x+0.44))<0.001 and math.abs(side.y-(actor.pos.y+1.82))<0.001,"Controller reference framing must place the body lower/left at the same 1.4m scale")
    key(DIK_keys.MOUSE_2);advance(16)
    local zoom_tan=math.tan(camera_fov*math.pi/360);local base_tan=math.tan(67.5*math.pi/360)
    local zoom_distance=1.4
    assert(math.abs(level.camera_pos.z-side.z)<0.001 and math.abs(level.camera_pos.x-side.x)<0.001 and math.abs(level.camera_pos.y-side.y)<0.001 and math.abs(camera_fov-67.5*0.65)<0.001,"RMB moved the camera instead of changing only FOV")
    assert(math.abs(visual.pos.x-body_before.x)<0.00001 and math.abs(visual.pos.y-body_before.y)<0.00001 and math.abs(visual.pos.z-body_before.z)<0.00001,"RMB teleported the controller visual")
    local function project_body(camera,distance,tan_fov)
        return (actor.pos.x-camera.x)/(distance*tan_fov),(actor.pos.y+1.9-camera.y)/(distance*tan_fov),0.6/(distance*tan_fov)
    end
    local bx,by,bsize=project_body(side,1.4,base_tan)
    local zx,zy,zsize=project_body(level.camera_pos,zoom_distance,zoom_tan)
    local gain=base_tan/zoom_tan
    assert(gain>1 and math.abs(zx-bx*gain)<0.00001 and math.abs(zy-by*gain)<0.00001 and math.abs(zsize-bsize*gain)<0.00001,"RMB must magnify body and world together, not freeze body size")
    for i=1,5 do
        key(DIK_keys.MOUSE_2,nil,"on_before_key_hold");advance(16)
        assert(math.abs(level.camera_pos.z-side.z)<0.00001 and math.abs(level.camera_pos.x-side.x)<0.00001 and math.abs(level.camera_pos.y-side.y)<0.00001,"Held RMB accumulates camera offsets")
        assert(math.abs(visual.pos.x-body_before.x)<0.00001 and math.abs(visual.pos.y-body_before.y)<0.00001 and math.abs(visual.pos.z-body_before.z)<0.00001,"Held RMB moves the visual independently of the actor")
    end
    key(DIK_keys.MOUSE_2,nil,"on_before_key_release");advance(16)
    assert(math.abs(level.camera_pos.x-side.x)<0.001 and math.abs(level.camera_pos.z-side.z)<0.001,"RMB release did not restore close shoulder")
    local view
    for wnd in pairs(hud.windows) do view=wnd.controller_ui end
    assert(view and view.keys.camera.main.text=="V")
    local old_motion=attachments[NAME].motion;key(DIK_keys.DIK_F)
    assert(not key(DIK_keys.DIK_V).ret_value);advance(16)
    assert(attachments[NAME].motion~=old_motion,"V discarded queued attack")
    assert(math.abs(level.camera_pos.x-centre.x)<0.001 and math.abs(level.camera_pos.y-centre.y)<0.001)
    assert(not key(DIK_keys.DIK_V,nil,"on_before_key_hold").ret_value);advance(16)
    assert(math.abs(level.camera_pos.x)<0.001,"Holding V repeatedly toggled the camera")
    descriptor.on_effect_save("before","camera_switch",nil)
    local saved={};callbacks.save_state(saved);service_files["camera_switch.scop"]=true
    descriptor.on_effect_save("after","camera_switch",true);advance(16)
    assert(custom and math.abs(level.camera_pos.x)<0.001,"Service save lost live camera choice")
    key(DIK_keys.DIK_V);advance(16);assert(math.abs(level.camera_pos.x-side.x)<0.001)
    assert(p.camera_mode==nil,"Live toggle overwrote template default")
    key(DIK_keys.DIK_ESCAPE);stopped()
    assert(descriptor.execute({},p)=="executed" and level.camera_pos.x>0.1)
    key(DIK_keys.DIK_ESCAPE);stopped()
    camera_side_wall=0.3;assert(descriptor.execute({},p)=="executed")
    assert(level.camera_pos.x<=0.101,"Shoulder camera crossed a side wall")
    key(DIK_keys.DIK_ESCAPE);stopped();camera_side_wall=nil
    wall=-0.5;assert(descriptor.execute({},p)=="executed")
    assert(level.camera_pos.z>side.z+0.5,"Shoulder camera crossed a rear wall")
    key(DIK_keys.DIK_ESCAPE);stopped();wall=nil
    p.camera_key="G";assert(descriptor.execute({},p)=="executed")
    assert(key(DIK_keys.DIK_V).ret_value);key(DIK_keys.DIK_G);advance(16)
    assert(math.abs(level.camera_pos.x)<0.001,"Rebound camera key did not switch")
    key(DIK_keys.DIK_ESCAPE);stopped()
    p=params();p.camera_mode="invalid";reject(p,"controller_camera_mode_invalid")
    p=params();p.camera_key="MOUSE_1";reject(p,"controller_ability_keys_duplicate")
end
-- StopAtEnd locomotion must replay; real loops must not restart per frame.
do
    targets={};actor.velocity=v(0,0,1);local body=start()
    for i=1,100 do advance(50);assert(body.motion=="stand_walk_fwd_0","Finite walk clip froze") end
    assert(#body.motions>=5 and #body.motions<12,"Walk animation is restarted every frame")
    actor.velocity=v();advance(16);local count=#body.motions
    for i=1,100 do advance(50) end
    assert(body.motion=="stand_idle_0" and #body.motions==count,"True looping idle restarted")
    stop()
end
-- Hollow bounding-sphere centres are valid: visible NPCs need not collide at centre.
do
    local npc=enemy("npc",10);npc.no_center_collision=true;npc.relations[0]=-9012
    -- Online native API is sufficient even without an offline CSE setter.
    servers[npc:id()].force_set_goodwill=nil
    start();capture(npc);stop();assert(npc.relations[0]==-9012)
    npc.no_center_collision=false
    targets={npc};start();key(DIK_keys.MOUSE_1);advance(16)
    local body=attachments[NAME];local motion_count=#body.motions
    key(DIK_keys.DIK_V);advance(16)
    assert(body.motion=="psy_attack_0" and #body.motions==motion_count,"V interrupted psy attack")
    key(DIK_keys.DIK_V);advance(16)
    for i=1,4 do advance(700) end
    assert(utf8(ui().info.main.text):find("Слуги: 1/",1,true));stop()
end
-- Walk only: native/remapped press + hold blocked, releases/WASD/crouch/V pass.
do
    targets={};start()
    assert(move_values.run_coef==1 and move_values.sprint_koef==1 and math.abs(move_values.walk_accel-move_defaults.walk_accel*1.21)<0.000001)
    assert(move_values.runback_coef==move_defaults.walk_back_coef and move_values.run_strafe_coef==move_defaults.walk_strafe_coef)
    local writes=#move_writes
    for _,binding in ipairs({key_bindings.kJUMP,key_bindings.kSPRINT_TOGGLE}) do
        assert(not key(123,binding).ret_value)
        assert(not key(123,binding,"on_before_key_hold").ret_value)
        assert(key(123,binding,"on_before_key_release").ret_value)
    end
    assert(key(DIK_keys.DIK_W,key_bindings.kFWD).ret_value and key(123,key_bindings.kCROUCH).ret_value)
    key(DIK_keys.DIK_V);advance(16);assert(active())
    for i=1,100 do advance(16) end
    assert(#move_writes==writes,"Controller rewrote movement on every frame")
    stop()
    local p=params();p.camera_key="X";reject(p,"controller_ability_key_reserved")
    fail_move="sprint_koef";assert(descriptor.execute({},params())=="rejected");stopped()
    start();local save={};callbacks.save_state(save);cancel_saved_form()
    assert(save[SAVE].version==5 and #save[SAVE].movement==6)
    for _,entry in ipairs(save[SAVE].movement) do move_values[entry.key]=entry.applied end
    if save[SAVE] then save[SAVE].form_state=nil end;memory=save;descriptor.on_game_load();advance(16);movement_original()
    start();move_values.run_coef=7;advance(16);assert(active() and move_values.run_coef==7,"External speed edit cancelled the form")
    key(DIK_keys.DIK_ESCAPE);assert(not active() and move_values.run_coef==7)
    move_values.run_coef=move_defaults.run_coef;stopped();movement_original()
end
-- Green world labels for both kinds, compact count with no target-name row.
do
    local npc=enemy("npc",10);npc.relations[0]=15;start();capture(npc)
    local view=ui();local tag=view.servants[1]
    assert(utf8(tag.main.text)=="Слуга" and tag.main.color==GetARGB(255,100,220,110) and tag.main.shown)
    assert(#tag.controls==9 and tag.pos.y<384-npc.pos.y*10,"Servant label is not above the head")
    assert(view.info.pos.y>384 and view.target==nil)
    assert(utf8(view.info.main.text)=="Слуги: 1/10" and not utf8(view.info.main.text):find("Цель",1,true))
    assert(view.notice.pos.y>view.info.pos.y+24)
    local old_x=tag.pos.x;npc.pos.x=1;advance(200);assert(tag.pos.x>old_x)
    npc.pos.x=0;wall=5;advance(200);assert(tag.main.shown,"Identification tag was hidden by partial cover")
    wall=nil;ray_layers={{z=5,flags=128}};advance(200);assert(tag.main.shown,"Passable foliage hid a servant label")
    ray_layers={};project_override="offscreen";advance(200);assert(tag.main.shown,"Stale native projection hid custom-camera tag")
    project_override="nil";advance(200);assert(tag.main.shown,"Missing native projector hid custom-camera tag")
    npc.pos.x=100;advance(200);assert(not tag.main.shown,"Offscreen world label shown");npc.pos.x=0
    project_override=nil;npc.pos.z=-10;advance(200);assert(not tag.main.shown,"Behind-camera servant label visible")
    npc.pos.z=10;advance(200);assert(tag.main.shown)
    npc.living=false;advance(300);assert(not tag.main.shown and utf8(view.info.main.text)=="Слуги: 0/10")
    stop();assert(npc.relations[0]==15)
    local mutant=enemy("mutant",10);mutant.height=4;start();capture(mutant)
    assert(ui().servants[1].main.shown and utf8(ui().servants[1].main.text)=="Слуга")
    mutant.pos.x=4;local foe=enemy("npc",16);foe.hostile=true;targets={mutant,foe};advance(300)
    assert(ui().target==nil and mutant.order.target:id()==foe:id(),"Automatic enemy targeting was removed with the HUD row")
    stop()
end
-- Held RMB zoom is independent of capture/F/V; every exit restores exact FOV.
do
    targets={};start();local base=copy(level.camera_pos)
    key(DIK_keys.MOUSE_2);advance(16)
    assert(math.abs(camera_fov-67.5*0.65)<0.00001 and math.abs(level.camera_pos.z-base.z)<0.00001 and math.abs(level.camera_pos.x-base.x)<0.00001 and math.abs(level.camera_pos.y-base.y)<0.00001,"Centre mode RMB must change only FOV, not move the camera")
    local before=level.camera_angles.x
    callbacks.on_before_mouse_move(100,0,false,{ret_value=false});advance(16)
    local turn=(level.camera_angles.x-before)%(math.pi*2);turn=math.min(turn,math.pi*2-turn)
    assert(turn>0.13 and turn<0.14)
    key(DIK_keys.DIK_V);advance(16);assert(camera_fov<67.5 and active())
    key(DIK_keys.MOUSE_2,nil,"on_before_key_release");assert(math.abs(camera_fov-67.5)<0.00001)
    stop()
    local npc=enemy("npc",10);npc.pos.x=0.44;targets={npc};start({camera_mode="side"})
    key(DIK_keys.MOUSE_2);advance(16);cast()
    assert(utf8(ui().info.main.text):find("Слуги: 1/",1,true) and camera_fov<67.5)
    stop()
    -- Legacy saved RMB control binding cannot collide with the new aim key.
    start({mark_key="MOUSE_1",control_key="MOUSE_2"});stop()
    for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
        start();key(DIK_keys.MOUSE_2);advance(16);callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end
    end
    start();key(DIK_keys.MOUSE_2);advance(16)
    local save={};callbacks.save_state(save);cancel_saved_form()
    assert(save[SAVE].zoom and save[SAVE].zoom.before==67.5)
    camera_fov=save[SAVE].zoom.applied;if save[SAVE] then save[SAVE].form_state=nil end;memory=save;descriptor.on_game_load();advance(16);stopped()
    start();key(DIK_keys.MOUSE_2);fail_zoom=true;advance(16);stopped()
    start();key(DIK_keys.MOUSE_2);advance(16);camera_fov=80;advance(16)
    assert(active() and camera_fov==80,"Another addon's FOV was overwritten")
    key(DIK_keys.DIK_ESCAPE);assert(camera_fov==80);camera_fov=67.5;stopped()
end
-- R selects exactly two colored modes; hold never repeats or changes a queued cast.
do
    targets={};start()
    assert(ui().labels.control.main.color==GetARGB(255,100,220,110))
    assert(ui().keys.control.main.color==GetARGB(255,238,205,125),"Only the Control description should turn green")
    key(DIK_keys.DIK_R);advance(16)
    assert(utf8(ui().labels.control.main.text)=="Разорвать внутренности")
    assert(ui().labels.control.main.color==GetARGB(255,255,65,65))
    key(DIK_keys.DIK_R,nil,"on_before_key_hold");advance(16)
    assert(utf8(ui().labels.control.main.text)=="Разорвать внутренности")
    key(DIK_keys.DIK_R);advance(16)
    assert(utf8(ui().labels.control.main.text)=="Контроль" and ui().labels.control.main.color==GetARGB(255,100,220,110) and not ui().keys.panic)
    stop()
    local npc=enemy("npc",10);targets={npc};start()
    key(DIK_keys.MOUSE_1);advance(16);key(DIK_keys.DIK_R)
    for i=1,4 do advance(700) end
    assert(npc:alive() and utf8(ui().info.main.text)=="Слуги: 1/10","R changed an already queued capture")
    stop()
end
-- Exact native gravity tearing particle + sound, single-target fatal damage.
for _,kind in ipairs({"npc","mutant"}) do
    local victim=enemy(kind,10);local bystander=enemy("npc",11);bystander.pos.x=4
    targets={victim,bystander};start();key(DIK_keys.DIK_R)
    local before=#fx_created;cast()
    assert(not victim:alive() and bystander:alive() and #bystander.hits==0)
    assert(fx_created[before+1]=="anomaly2\\body_tear_00" and #fx_created==before+1)
    assert(victim.killer_id==actor:id() and utf8(ui().info.main.text)=="Слуги: 0/10")
    assert(fx[#fx].active and actor.health==0.77)
    local heard=false;for _,s in ipairs(sounds) do if s.name=="anomaly\\anomaly_body_tear_1" then heard=true end end
    assert(heard,"Native gravity tearing sound not played")
    stop()
end
-- Killing a former servant first releases its exact temporary relations/AI.
do
    local npc=enemy("npc",10);npc.relations[0]=-9100;targets={npc};start();capture(npc)
    key(DIK_keys.DIK_R);cast()
    assert(not npc:alive() and npc.relations[0]==-9100 and utf8(ui().info.main.text)=="Слуги: 0/10")
    stop()
end
-- Cycling twice wraps to Control; no NPC animation, weapon or kill timers.
do
    local npc=enemy("npc",10);targets={npc};start()
    npc.add_animation=forbidden;npc.set_item=forbidden
    key(DIK_keys.DIK_R);key(DIK_keys.DIK_R);cast()
    assert(npc:alive() and #npc.hits==0 and npc:animation_count()==0)
    assert(utf8(ui().info.main.text)=="Слуги: 1/10")
    advance(21000);assert(npc:alive() and #npc.hits==0)
    stop()
end
-- A service snapshot from the removed third mode resumes as Control.
do
    local npc=enemy("npc",10);targets={npc};start()
    descriptor.on_effect_save("before","legacy-mode",nil)
    local suspended
    for i=1,100 do
        local name,value=debug.getupvalue(descriptor.update,i)
        if not name then break end
        if name=="suspended" then suspended=value;break end
    end
    assert(suspended,"Service snapshot not found");suspended.ability_mode=3
    local saved={};callbacks.save_state(saved);service_files["legacy-mode.scop"]=true
    descriptor.on_effect_save("after","legacy-mode",true);advance(16)
    assert(utf8(ui().labels.control.main.text)=="Контроль")
    cast();assert(npc:alive() and #npc.hits==0 and utf8(ui().info.main.text)=="Слуги: 1/10")
    stop()
end
-- F9 cancels a legacy execution lease, never resumes its shot or pending kill.
-- Offline entities wait; a different token/identity is never modified.
local old_move=move;move={stand=0}
for _,case in ipairs({"online","offline","foreign_token","reused_id"}) do
    local npc=enemy("npc",10);targets={npc}
    local row={id=npc:id(),name=npc:name(),section=npc:section(),kind="npc",relations={},
        execution=true,token="legacy-execution",move_before=2,deadline=now-1,started=now-30000}
    npc.animation_queue={"idle_0_to_psy_1_idle_0","psy_1_shot_0","psy_1_death_0"};npc.movement=move.stand
    db.storage[npc:id()].adb_controller_execution_v1=case=="foreign_token" and "another-owner" or row.token
    if case=="offline" then npc.offline=true end
    if case=="reused_id" then row.name="abandoned_entity" end
    memory={adb_controller_servants_recovery_v1={version=1,rows={row}}}
    descriptor.on_game_load();advance(16)
    if case=="offline" then
        assert(active() and npc:animation_count()==3 and npc:alive())
        npc.offline=false;advance(1000)
    end
    assert(npc:alive() and #npc.hits==0)
    if case=="online" or case=="offline" then
        assert(npc:animation_count()==0 and npc.movement==2 and not db.storage[npc:id()].adb_controller_execution_v1)
    else
        assert(npc:animation_count()==3 and npc.movement==move.stand,"Another owner's state was changed")
    end
    advance(21000);assert(npc:alive() and #npc.hits==0);stopped()
    assert(memory.adb_controller_servants_recovery_v1==nil)
end
move=old_move
assert(copy_count==0 and delete_count==0,"Unneeded particle extraction in normal control/loose-effect tests")
-- New session-gated F5/F9 behavior, through the actual module callbacks.
assert(loadfile(core.."/../../../tests/lua-fixtures/form_save_roundtrip.lua"))()({
    name="controller",key=SAVE,actor=actor,callbacks=callbacks,
    start=function() start({duration_seconds="20"}) end,stopped=stopped,advance=advance,active=active,
    live=function() return custom end,descriptor=function() return descriptor end,
    cancel=function() callbacks.on_before_load_input() end,
    set_memory=function(value) memory=value end,set_paused=function(value) paused=value end,
    elapse_silent=function(ms) now=now+ms end,
    file_exists=function(name) service_files[name..".scop"]=true end,
    reload_common=function() anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common") end
})

print("Controller COF-016 smoke passed (mock API): exactly two modes, no suicide startup/timer, legacy execution cancellation and mode reset; 400% HP, FOV-only aim, camera/V, 1.21 walking, servants and movement/FOV/HP/goodwill recovery.")

