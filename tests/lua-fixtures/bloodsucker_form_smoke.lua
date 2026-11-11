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
local NAME="adb_bloodsucker_form_visual_v1"
local SAVE="adb_bloodsucker_form_recovery_v1"
local fail_jump,fail_jump_read,jump_writes=false,false,{}
local fail_spatial,fail_model=false,false
local spatial_writes,velocity_writes,forgotten={},{},{}
local actor={spatial=61,jump=6,pos=v(),living=true,visual="mod/actor",velocity=v(),hp=0.77,health=0.77}
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
actor.set_actor_position=forbidden;actor.invulnerable=forbidden
alife_create=forbidden;alife_create_item=forbidden;show_indicators=forbidden;disable_input=forbidden
function actor:get_spatial_type() return self.spatial end
function actor:set_spatial_type(value)
    self.spatial=value;spatial_writes[#spatial_writes+1]=value
    if fail_spatial then fail_spatial=false;error("spatial_failed_after_change") end
end
function actor:set_movement_speed(value) self.velocity=copy(value);velocity_writes[#velocity_writes+1]=copy(value) end
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
local function expected_movement()
    local old_sprint=move_defaults.walk_accel*move_defaults.run_coef*move_defaults.sprint_koef
    assert(math.abs(move_values.walk_accel-old_sprint)<0.00001,"Walk does not match entry sprint")
    assert(move_values.run_coef==1 and move_values.sprint_koef==1.5,"Run/sprint compounded")
    assert(math.abs(move_values.walk_accel*move_values.run_coef*move_values.sprint_koef-old_sprint*1.5)<0.00001)
    assert(move_values.walk_strafe_coef==move_defaults.sprint_strafe_coef and move_values.run_strafe_coef==move_defaults.sprint_strafe_coef)
    assert(math.abs(move_values.runback_coef-move_defaults.runback_coef/move_defaults.run_coef)<0.00001)
end
db={actor=actor};_HUD_STATE=true;_GUIs={};actor_effects={item_not_in_use=true}
actor_menu={is_hud_free=function() return not busy end}
function Check_UI() return busy end
function time_global() return now end
function device() return {frame=frame,cam_dir=initial_dir,width=display_width,height=display_height,fov=camera_fov,is_paused=function() return paused end} end
local body_debug="0"
function get_console() return {get_string=function(_,key) assert(key=="show_actor_body");return body_debug end,execute=forbidden} end
alife_storage_manager={get_state=function() return memory end}
local sections={arbitrary_boss_x={class="SM_BLOOD",visual="custom\\independent_bloodsucker",Predator_Visual="custom\\cloak_bloodsucker"},
    a_dog={class="SM_DOG_S",visual="custom\\dog"},loose_prop={class="O_PHYS_S"}}
local stories={}
function get_object_story_id(id) return stories[id] end
function IsItem(kind,section) return false end
function system_ini() return {section_exist=function(_,s) return sections[s]~=nil end,
    r_string=function(_,s,k) return assert(sections[s])[k] end,line_exist=function(_,s,k) return sections[s] and sections[s][k]~=nil end} end
function getFS() return {
    exist=function(_,alias,path)
        if alias=="$game_saves$" then return service_files[path] end
        if path==nil then return copied_files[alias] and {size_real=#copied_files[alias]} or nil end
        if alias=="$game_data$" or alias=="$game_particles$" then return false end
        if alias=="$game_sounds$" then
            return not missing_sounds and (path:match("_0%.ogg$") or path:match("earth4%.ogg$"))~=nil
        end
        return not missing or missing~=path
    end,
    file_copy=function() copy_count=copy_count+1;error("Bite must not inspect particle library") end,
    file_delete=function() delete_count=delete_count+1;error("No temporary files expected") end,
    r_open=function() error("Unsafe native IReader must not be acquired") end,
    r_close=function() error("Unsafe native IReader must not be acquired") end
} end
particles_object=function(name)
    fx_created[#fx_created+1]=name;error("Bite has no native attack particles")
end
sound_object=setmetatable({looped=1,s2d=2},{__call=function(_,name)
    assert(not missing_sounds,"Missing sound file must never be constructed")
    local s={name=name}
    function s:play_at_pos(owner,pos,delay,flags)
        assert(owner:id()==actor:id() and delay==0 and (flags==0 or flags==1 or flags==2))
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
    local object={kind=kind,pos=v(0,1.1,z),living=true,hits={},health=1,object_id=enemy_id}
    setmetatable(object,object_meta)
    function object:id() return self.object_id end
    function object:section() return "test_"..kind end
    function object:center(hud) assert(hud==false);return copy(self.pos) end
    function object:alive() return self.living end
    function object:list_bones(hud) assert(hud==false);return {[0]="zero_damage_root",[7]="modded_collision_bone"} end
    function object:hit(h)
        assert(h.bone=="modded_collision_bone" and h.draftsman:id()==actor:id() and h.type==hit.wound)
        damage_calls=damage_calls+1;self.hits[#self.hits+1]=h
        local calculated={power=self.immune and 0 or h.power,type=h.type,draftsman=h.draftsman,impulse=h.impulse,direction=h.direction}
        if self.defer then self.pending_calculated=calculated;return end
        CBeforeHitAfterCalcs(calculated,self,7)
        self.health=math.max(0,self.health-calculated.power)
    end
    function object:position() assert(not self.stale);return copy(self.pos) end
    function object:parent() return self.owner end
    function object:memory_remove_links(who) assert(who:id()==0);forgotten[#forgotten+1]=self:id() end
    object.script=forbidden;object.force_set_position=forbidden;object.reset_action_queue=forbidden
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
DIK_keys={DIK_V=47,DIK_ESCAPE=1,DIK_1=2,DIK_2=3,DIK_3=4,DIK_F=2,DIK_R=4,MOUSE_1=3,MOUSE_2=5,
    DIK_SPACE=57,DIK_G=6,DIK_H=7,DIK_J=8,DIK_K=9,DIK_W=17,DIK_F5=63,DIK_F9=67}
key_bindings={kWPN_1=20,kWPN_2=21,kWPN_3=22,kWPN_FIRE=30,kUSE=31,kCAM_1=32,kINVENTORY=33,
    kJUMP=50,kQUICK_SAVE=51,kQUICK_LOAD=52,kACTIVE_JOBS=34,kCUSTOM20=35,kNEXT_SLOT=36,kPREV_SLOT=37,
    kFWD=55,kBACK=56,kL_STRAFE=58,kR_STRAFE=59,kSPRINT_TOGGLE=60,kCROUCH=61}
local jump_dik=DIK_keys.DIK_SPACE
function bind_to_dik(bind)
    return bind==key_bindings.kJUMP and jump_dik or bind==key_bindings.kQUICK_SAVE and DIK_keys.DIK_F5 or DIK_keys.DIK_F9
end
function dik_to_keyname(dik) return dik==DIK_keys.DIK_G and "G" or "Unknown" end
level_input={on_mouse_move=function() end}
level={
    iterate_nearest=function(origin,radius,visit)
        assert(radius>0 and radius<=151)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) for _,object in ipairs(targets) do if object.id and object:id()==id then return object end end end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_bloodsucker" and not attachments[name])
        local body={model=model,motions={}}
        function body:set_model(value)
            assert(value=="custom\\independent_bloodsucker" or value=="custom\\cloak_bloodsucker")
            self.model=value
            if fail_model then fail_model=false;error("model_failed_after_change") end
        end
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==true and speed==1,"Bloodsucker transitions must crossfade without slowing playback")
            self.motion=motion;self.motions[#self.motions+1]=motion
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
        assert(ignored and type(ignored:id())=="number" and range>0 and range<=80)
        local nearest,object,material_flags=range+1,nil,0
        if dir.y< -0.5 and math.abs(dir.z)<0.01 and origin.y>=0 and origin.y<=range then nearest=origin.y/-dir.y end
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
                if target:id()~=ignored:id() and t>=0 and t<=range and t<nearest and error2<0.15^2 then nearest,object,material_flags=t,target,0 end
            end
        end
        if nearest<=range then result.range=nearest;result.object=object;result.material_flags=material_flags;result.element=object and (object.no_bone and 65535 or 7);return true end
        return false
    end
}
function RegisterScriptCallback(name,f) callbacks[name]=f end
function CBeforeHitAfterCalcs(h,target,bone) callbacks.on_before_hit_after_calcs(h,target,bone) end
function printf(fmt,...) logs[#logs+1]=string.format(fmt,...) end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.bloodsucker_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_bloodsucker_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.bloodsucker_form"])
local function params(duration) return {camera_mode="center",mutant_sections="arbitrary_boss_x",duration_seconds=duration or "60",catalog_revision="revision"} end
local function active() return descriptor.is_blocking() end
local function advance(ms)
    if not paused then
        local next_z=actor.pos.z+actor.velocity.z*ms/1000
        if wall and actor.velocity.z>0 then next_z=math.min(next_z,wall-0.35) end
        actor.pos=v(actor.pos.x+actor.velocity.x*ms/1000,actor.pos.y,next_z)
    end
    now=now+ms;frame=frame+1
    local windows={};for wnd in pairs(hud.windows) do windows[#windows+1]=wnd end
    for _,wnd in ipairs(windows) do wnd:Update() end
    descriptor.update()
end
local function key(dik,bind,kind)
    local flags={ret_value=true};callbacks[kind or "on_before_key_press"](dik,bind,false,flags);return flags
end
local function start(duration,attack_power,health)
    local p=params(duration);local context={}
    p.attack_power_percent=attack_power;p.health_percent=health
    local allowed,status,why=descriptor.can_execute(context,p)
    assert(allowed,tostring(status)..":"..tostring(why))
    assert(descriptor.execute(context,p)=="executed");assert(active() and custom and camera==0 and attachments[NAME])
    assert(next(copied_files)==nil and copy_count==0 and #fx_created==0,"Bite must not borrow unrelated particles")
    return attachments[NAME]
end
local function stopped()
    assert(not active() and not attachments[NAME] and not custom and camera==2)
    assert(actor.hp==0.77 and actor.visual=="mod/actor" and _HUD_STATE==true)
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
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.bloodsucker_ui.hp.main.text):match("(%d+)%% HP"))
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

local function get_ui() for wnd in pairs(hud.windows) do return wnd.bloodsucker_ui end end
local function utf8(text) return anomaly_donation_bridge_common.game_text_utf8(text) end
local function stop()
    key(DIK_keys.DIK_ESCAPE);stopped()
    assert(actor.spatial%16>=8,"AI visibility leaked")
    movement_original()
    actor.velocity=v();actor.pos=v();initial_dir=v(0,0,1)
end
local function strike()
    key(DIK_keys.MOUSE_1,99);advance(1);advance(299)
    advance(1);advance(251);advance(500)
end
local function toggle() key(DIK_keys.MOUSE_2,99);advance(1) end
local function cloak_indicator(active)
    local ui=get_ui()
    assert(utf8(ui.labels.cloak.main.text)=="Невидимость:","Cloak prefix was replaced/coloured with the state")
    assert(utf8(ui.cloak_state.main.text)==(active and "ВКЛ" or "ВЫКЛ"),"Wrong cloak indicator text")
    assert(ui.cloak_state.main.color==(active and GetARGB(255,120,220,140) or GetARGB(255,180,185,180)),"Wrong cloak indicator colour")
    assert(ui.labels.cloak.main.color==GetARGB(255,180,185,180),"Cloak description must remain grey")
    assert(ui.keys.cloak.main.color==GetARGB(255,238,205,125),"Cloak key lost its accent colour")
    assert(ui.cloak_state.pos.x>ui.labels.cloak.pos.x and ui.cloak_state.pos.y==ui.labels.cloak.pos.y,"Cloak state is not inline")
    assert(ui.cloak_state.pos.x+ui.cloak_state:TextWidth()<=ui.left+ui.width+0.01,"Cloak state escaped the centred column")
    for i=1,8 do assert(ui.cloak_state.controls[i].color==GetARGB(245,0,0,0),"Cloak state lost its outline") end
    assert(math.abs(ui.left+ui.width/2-512)<0.01,"Cloak state broke column centring")
end
-- Dynamic catalog/model safety, no editor/engine changes.
local p=params();p.catalog_revision="old";reject(p,"catalog_revision_mismatch")
p=params();p.mutant_sections="a_dog";reject(p,"bloodsucker_visual_class_unsupported")
p=params();p.cloak_key="MOUSE_1";reject(p,"bloodsucker_ability_keys_duplicate")
for _,name in ipairs({"W","F5","F9","ESCAPE"}) do
    p=params();p.melee_key=name;reject(p,"bloodsucker_ability_key_invalid")
end
for _,value in ipairs({"0","9","100000","300.5","NaN"}) do
    p=params();p.health_percent=value;reject(p,"bloodsucker_health_percent_invalid")
end
missing="custom\\cloak_bloodsucker.ogf";reject(params(),"bloodsucker_predator_visual_missing");missing=nil
local spatial_api=actor.set_spatial_type;actor.set_spatial_type=nil
assert(not descriptor.is_supported());reject(params(),"bloodsucker_form_api_unavailable");actor.set_spatial_type=spatial_api
local body=start();advance(16);local ui=get_ui()
assert(form_hp()==100 and ui.labels.cloak and not ui.labels.dash and not ui.labels.jump)
assert(utf8(ui.keys.melee.main.text)=="ЛКМ" and utf8(ui.keys.cloak.main.text)=="ПКМ")
cloak_indicator(false)
expected_movement()
assert(ui.labels.melee.main.color==GetARGB(255,180,185,180) and ui.ring.texture=="adb_burer_aim_ring")
assert(math.abs(ui.left+ui.width/2-512)<0.01 and level.camera_pos.y>=2.3)
for _,binding in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20,key_bindings.kNEXT_SLOT}) do
    assert(not key(100,binding).ret_value,"Inventory/PDA escaped input guard")
end
local mouse={ret_value=false};callbacks.on_before_mouse_move(100,0,false,mouse);advance(16)
assert(mouse.ret_value==true and math.abs(math.sin(actor.angles[1])-math.sin(0.25))<0.001)
stop()
display_width,display_height=3440,1440;start();advance(1);ui=get_ui()
assert(math.abs(ui.left+ui.width/2-512)<0.01)
assert(math.abs(ui.ring.size.x/ui.ring.size.y-(1440/3440)/(768/1024))<0.001)
cloak_indicator(false);toggle();cloak_indicator(true);advance(1000);toggle();cloak_indicator(false)
stop();display_width,display_height=1920,1080
-- Blending is visual only: no restart every frame, extra transition wait,
-- movement/sprint/jump lock, camera lag or forced velocity during a strike.
targets={};body=start();local motion_count=#body.motions
advance(16);assert(#body.motions==motion_count,"Idle blending restarted every frame")
key(DIK_keys.MOUSE_1,99);advance(1);assert(body.motion=="stand_attack_0","Strike did not start on the next frame")
motion_count=#body.motions
for _,binding in ipairs({key_bindings.kFWD,key_bindings.kBACK,key_bindings.kL_STRAFE,key_bindings.kR_STRAFE,
    key_bindings.kSPRINT_TOGGLE,key_bindings.kCROUCH,key_bindings.kJUMP}) do
    assert(key(DIK_keys.DIK_W,binding).ret_value,"Blending blocked native movement input")
end
local old_velocity_writes=#velocity_writes;local old_camera_sets=camera_sets
actor.velocity=v(0,0,4)
local turn={ret_value=false};callbacks.on_before_mouse_move(24,-12,false,turn)
advance(16)
assert(turn.ret_value and camera_sets==old_camera_sets+1 and actor.pos.z>0,"Attack blending delayed movement/camera update")
assert(math.abs(math.sin(actor.angles[1])-math.sin(0.06))<0.001,"Attack blending ignored mouse turn")
assert(#velocity_writes==old_velocity_writes and #body.motions==motion_count,"Attack blending forced movement/restarted the cycle")
advance(984);assert(body.motion=="stand_run_0","Crossfade added an end-of-attack wait")
motion_count=#body.motions;advance(16);assert(#body.motions==motion_count,"Run blending restarted every frame")
stop()
body=start();key(DIK_keys.MOUSE_1,99);advance(1);stop()
local timed_target=enemy("npc",1.5);timed_target.health=100;targets={timed_target};body=start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(299)
assert(#timed_target.hits==0,"Crossfade advanced the native 30% hit point")
advance(1);assert(#timed_target.hits==1,"Crossfade delayed the native 30% hit point")
advance(700);assert(body.motion=="stand_idle_0" and #timed_target.hits==1,"Crossfade prolonged/repeated the attack")
stop()
-- Three ordered strikes, native wound/immunity, heal +10 percentage points
-- of the form maximum once per REAL own damaging hit (not actor entry HP).
actor.health=0.42
local target=enemy("npc",1.5);target.health=100;targets={target};body=start()
actor_hit(1,hit.strike);advance(1);assert(form_hp()==50 and actor.health==0.42,"Default 200% form must hold two base HP units")
for i,power in ipairs({1.9,2.2,1.9,1.9}) do
    strike()
    assert(#target.hits==i,"Ordered strike missed or fired twice")
    assert(target.hits[i].impulse==50 and math.abs(target.hits[i].power-power)<0.00001)
    assert(form_hp()==50+i*10,"Missing/repeated 10% form heal")
    assert(actor.health==0.42,"Lifesteal changed rider HP")
end
local order={}
for _,name in ipairs(body.motions) do if name:match("^stand_attack_%d$") then order[#order+1]=name end end
assert(table.concat(order,",")=="stand_attack_0,stand_attack_1,stand_attack_2,stand_attack_0")
strike();strike();assert(form_hp()==100,"Form overheal/HP cap failed")
stop();assert(actor.health==0.42)
for _,capacity in ipairs({"100","200","300","9000","99999"}) do
    target=enemy("mutant",1.5);target.health=100;targets={target};start(nil,nil,capacity)
    actor_hit(tonumber(capacity)/100/2,hit.strike);advance(1);assert(form_hp()==50)
    strike();assert(form_hp()==60 and actor.health==0.42);stop()
end
target=enemy("npc",1.5);target.immune=true;targets={target};start();actor_hit(1);advance(1)
strike();assert(form_hp()==50 and target.health==1,"Immune target healed form");stop()
target=enemy("npc",1.5);targets={target};wall=0.8;start();actor_hit(1);advance(1)
strike();assert(#target.hits==0 and form_hp()==50,"Wall bypass/heal on miss");stop();wall=nil
targets={};start();actor_hit(1);advance(1);strike();assert(form_hp()==50);stop()
target=enemy("npc",1.5);targets={target};ray_layers={{z=0.3,flags=128},{z=0.6,flags=128}}
start();strike();assert(#target.hits==1,"Foliage blocked melee");stop();ray_layers={}
target=enemy("npc",1.5);targets={target};ray_layers={{z=0.6,flags=128+4096}}
start();strike();assert(#target.hits==0,"ActorObstacle did not stop melee");stop();ray_layers={}
target=enemy("npc",1.5);target.no_bone=true;targets={target};start();actor_hit(1);advance(1)
strike();assert(#target.hits==0 and form_hp()==50,"Invalid hit bone healed form");stop()
target=enemy("npc",1.5);target.living=false;targets={target};start();actor_hit(1);advance(1)
strike();assert(#target.hits==0 and form_hp()==50,"Corpse healed form");stop()
-- Cloak is visual + native AI visibility, with no collision/relations changes.
local mutant=enemy("mutant",3);target=enemy("npc",2);targets={target,mutant};body=start()
local normal_flags=actor.spatial;toggle()
cloak_indicator(true)
key(DIK_keys.DIK_V);advance(0)
assert(body.model=="custom\\cloak_bloodsucker" and actor.spatial==normal_flags-8,"V cancelled invisibility")
key(DIK_keys.DIK_V);advance(0);cloak_indicator(true)
assert(body.model=="custom\\cloak_bloodsucker" and actor.spatial==normal_flags-8 and #forgotten>=2)
local flags={override=false,result=true};callbacks.on_enemy_eval(target,actor,flags)
assert(flags.override and flags.result==false,"Enemy can select cloaked actor")
local foreign={override=false,result=true};callbacks.on_enemy_eval(target,mutant,foreign)
assert(not foreign.override and foreign.result,"Cloak changed NPC-vs-mutant combat")
local hits=damage_calls;local moves=#velocity_writes
key(DIK_keys.MOUSE_1,99);key(DIK_keys.DIK_F,99);advance(1200)
assert(damage_calls==hits and #velocity_writes==moves,"Attack allowed while cloaked")
local newcomer=enemy("mutant",1);targets[#targets+1]=newcomer;local forgotten_before=#forgotten
advance(250);assert(#forgotten==forgotten_before+1,"New online enemy was not cleared")
actor.spatial=actor.spatial+64;toggle()
cloak_indicator(false)
assert(body.model=="custom\\independent_bloodsucker" and actor.spatial==normal_flags+64,"Unrelated spatial flags overwritten")
flags={override=false,result=true};callbacks.on_enemy_eval(target,actor,flags)
assert(not flags.override and flags.result,"Enemy override remained after revealing")
advance(1000);assert(damage_calls==hits,"Blocked attack buffered until reveal")
stop();actor.spatial=61
-- A strike interrupted by cloak cannot fire its delayed hit.
target=enemy("npc",1.5);target.health=100;targets={target};body=start()
key(DIK_keys.MOUSE_1,99);advance(1);toggle();advance(800)
assert(#target.hits==0 and body.model=="custom\\cloak_bloodsucker");stop()
-- A native hit event queued before cloaking is also cancelled at its actual
-- post-calculation boundary. No late damage or healing while hidden.
target=enemy("npc",1.5);target.health=100;target.defer=true;targets={target};start()
actor_hit(1);advance(1);key(DIK_keys.MOUSE_1,99);advance(1);advance(300)
assert(target.pending_calculated and target.pending_calculated.power>0)
toggle();CBeforeHitAfterCalcs(target.pending_calculated,target,7)
assert(target.pending_calculated.power==0 and target.health==100)
advance(300);assert(form_hp()==50,"Cancelled late hit healed hidden form");stop()
-- Preserve pre-existing non-visibility rather than force enabling it.
actor.spatial=53;start();toggle();advance(400);toggle()
assert(actor.spatial==53);key(DIK_keys.DIK_ESCAPE);stopped();assert(actor.spatial==53);actor.spatial=61
-- F is no longer an ability, including when an old saved parameter remains.
targets={};p=params();p.dash_key="F"
assert(descriptor.execute({},p)=="executed");body=attachments[NAME]
local old_moves=#velocity_writes;local old_hits=damage_calls
assert(key(DIK_keys.DIK_F,99).ret_value)
assert(not key(DIK_keys.DIK_F,key_bindings.kUSE).ret_value)
advance(500);assert(#velocity_writes==old_moves and damage_calls==old_hits and not get_ui().labels.dash)
expected_movement();stop()
-- Snapshot the build's live coefficients, never hard-code a fixed absolute speed.
local normal_run,normal_sprint=move_defaults.run_coef,move_defaults.sprint_koef
move_defaults.run_coef=1.7;move_values.run_coef=1.7;move_defaults.sprint_koef=1.9;move_values.sprint_koef=1.9
start();expected_movement();stop()
move_defaults.run_coef=normal_run;move_values.run_coef=normal_run
move_defaults.sprint_koef=normal_sprint;move_values.sprint_koef=normal_sprint
-- No multiplication or setter on each frame, sprint or cloak toggle.
local written=#move_writes;start();assert(#move_writes==written+7)
for i=1,4 do advance(16);expected_movement() end
toggle();advance(400);toggle();assert(#move_writes==written+7)
assert(key(101,99).ret_value,"Unrelated native input was consumed");stop()
assert(#move_writes==written+14)
-- Capability/preflight failures perform no partial mutation.
local setter=actor.set_actor_run_coef;actor.set_actor_run_coef=nil
assert(not descriptor.is_supported());reject(params(),"bloodsucker_form_api_unavailable")
actor.set_actor_run_coef=setter
move_values.run_coef=0;reject(params(),"bloodsucker_movement_values_unavailable");move_values.run_coef=move_defaults.run_coef
-- Failure before/after every setter rolls back fields which were really changed.
for _,field in ipairs({"walk_accel","run_coef","runback_coef","walk_back_coef","sprint_koef","walk_strafe_coef","run_strafe_coef"}) do
    fail_move=field;assert(descriptor.execute({},params())=="rejected");stopped();movement_original()
    fail_move_before=field;assert(descriptor.execute({},params())=="rejected");stopped();movement_original()
end
-- A foreign addon's field is never overwritten on exit; other leased fields restore.
start();move_values.walk_accel=73;advance(1);assert(active() and move_values.walk_accel==73,"External speed edit cancelled the form")
key(DIK_keys.DIK_ESCAPE);assert(not active() and move_values.walk_accel==73)
move_values.walk_accel=move_defaults.walk_accel;stopped();movement_original()
-- Every exit restores only owned actor visibility and removes the attachment.
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
    start();toggle();callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end;assert(actor.spatial==61);movement_original()
end
start("1");toggle();paused=true;advance(5000);assert(active() and actor.spatial==53)
paused=false;advance(999);assert(not active() and actor.spatial==61);stopped()
start();toggle();descriptor.on_level_changing();stopped();assert(actor.spatial==61)
fail_model=true;start();toggle();stopped();assert(actor.spatial==61)
fail_spatial=true;start();toggle();stopped();assert(actor.spatial==61)
-- Actor protections/capacity remain separate and damage is charged once.
targets={};start();protection.immunity=0.5;actor_hit(0.6);advance(1)
assert(form_hp()==85 and actor.health==0.42);callbacks.actor_on_update();advance(1);assert(form_hp()==85)
actor_hit(100);advance(1);stopped();assert(actor.health==0.42 and actor:alive());protection.immunity=1
-- Late native save journal repairs visibility/HP of THAT save, not an old world.
start();toggle();local data={};callbacks.save_state(data);cancel_saved_form();assert(actor.spatial==61)
assert(data[SAVE].version==2 and data[SAVE].spatial_owned and data[SAVE].form_health==nil and #data[SAVE].movement==7)
movement_original()
data[SAVE].form_state=nil;memory={[SAVE]=data[SAVE]};actor.spatial=53+64;actor.health=0.21;camera=0;custom=true
for _,entry in ipairs(data[SAVE].movement) do move_values[entry.key]=entry.applied end
descriptor.on_game_load();descriptor.update();stopped();assert(actor.spatial==61+64 and actor.health==0.42)
movement_original()
actor.spatial=61
start();toggle();attachments={};hud.windows={};actor.spatial=61;camera=2;custom=false;memory={};actor.health=0.31
-- Native load initializes a different actor/world: old runtime leases must
-- not overwrite its coefficients, even though the numeric actor ID is reused.
for key,value in pairs(move_defaults) do move_values[key]=value end
move_values.walk_accel=73
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.31 and actor.spatial==61)
assert(move_values.walk_accel==73,"Stale form overwrote unrelated loaded movement")
move_values.walk_accel=move_defaults.walk_accel;movement_original()
-- BSF-001 journals remain loadable and have no movement lease to restore.
memory={[SAVE]={version=1,actor_health=0.42,actor_visual=actor.visual,spatial_owned=false,camera_owned=false}}
descriptor.on_game_load();descriptor.update();movement_original()
actor.health=0.42
-- Service saves suspend cloak BEFORE serialization and resume same HP/time,
-- skin, toggle state and combo position only in the same live session.
target=enemy("npc",1.5);target.health=100;targets={target};start("10");actor_hit(1);advance(1);strike();toggle()
descriptor.on_effect_save("before","blood-service")
assert(active() and not attachments[NAME] and actor.spatial==61);movement_original()
data={};callbacks.save_state(data);assert(not data[SAVE].spatial_owned)
assert(#data[SAVE].movement==0,"Suspended service save serialized active movement lease")
descriptor.on_effect_save("after","blood-service",true);service_files["blood-service.scop"]=true
advance(16);assert(active() and actor.spatial==53 and form_hp()==60 and attachments[NAME].model=="custom\\cloak_bloodsucker")
cloak_indicator(true)
expected_movement()
advance(400);toggle();cloak_indicator(false);strike();assert(target.hits[#target.hits].power==2.2 and form_hp()==70);stop()
for _,mode in ipairs({"load","failed","timeout"}) do
    start();toggle();descriptor.on_effect_save("before",mode);assert(actor.spatial==61)
    if mode=="load" then descriptor.on_game_load();descriptor.update()
    elseif mode=="failed" then descriptor.on_effect_save("after",mode,false)
    else descriptor.on_effect_save("after",mode,true);advance(10001) end
    stopped();assert(actor.spatial==61);movement_original()
end
assert(copy_count==0 and delete_count==0 and #fx_created==0,"Unexpected particle/native resource handling")
for _,message in ipairs(logs) do
    assert(not message:find(" damage:",1,true) and not message:find("form ability:",1,true) and
        not message:find("form started:",1,true) and not message:find("form ended:",1,true),"Routine log spam")
end
-- Largest form duration is accepted and expires without timer overflow.
targets={};start("60000");advance(59999000);assert(active(),"60000-second form expired early")
advance(1000);stopped()
reject(params("60001"),"bloodsucker_duration_invalid")

-- Shared camera contract: legacy centre, default shoulder, live/remapped V,
-- collision, no hold-repeat, service-save persistence and per-activation default.
do
    targets={};wall=nil;ray_layers={};initial_dir=v(0,0,1);actor.velocity=v();visual_center_y=nil
    local p=params();assert(descriptor.execute({},p)=="executed")
    local centre=copy(level.camera_pos)
    assert(math.abs(centre.x)<0.001)
    key(DIK_keys.DIK_ESCAPE);stopped()
    p.camera_mode=nil
    assert(descriptor.execute({},p)=="executed")
    local side=copy(level.camera_pos)
    assert(side.x>0.1 and side.y<centre.y and side.z>centre.z,"Default camera is not closer/lower/right shoulder")
    local view
    for wnd in pairs(hud.windows) do view=wnd.bloodsucker_ui end
    assert(view and view.keys.camera.main.text=="V")
    local old_motion=attachments[NAME].motion;key(DIK_keys.MOUSE_1)
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
    p=params();p.camera_mode="invalid";reject(p,"bloodsucker_camera_mode_invalid")
    p=params();p.camera_key="MOUSE_1";reject(p,"bloodsucker_ability_keys_duplicate")
end
-- New session-gated F5/F9 behavior, through the actual module callbacks.
assert(loadfile(core.."/../../../tests/lua-fixtures/form_save_roundtrip.lua"))()({
    name="bloodsucker",key=SAVE,actor=actor,callbacks=callbacks,
    start=function() start("20") end,stopped=stopped,advance=advance,active=active,
    live=function() return custom end,descriptor=function() return descriptor end,
    cancel=function() callbacks.on_before_load_input() end,
    set_memory=function(value) memory=value end,set_paused=function(value) paused=value end,
    elapse_silent=function(ms) now=now+ms end,
    file_exists=function(name) service_files[name..".scop"]=true end,
    reload_common=function() anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common") end
})

print("Bloodsucker BSF-007 smoke passed (mock API): default 200% separate HP/preserved explicit 300%, native crossfade/no frame restarts or added action waits, live movement/sprint/jump/mouse/Esc during attacks, unchanged hit timings, green uppercase cloak status/grey inline description/outline/centred widescreen HUD, nonstacking movement/rollback/save recovery, ordered wounds/+10% heal, spatial cloak/no debug, no attacks while invisible, foliage, input and save/load/service resume.")
