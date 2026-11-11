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
local wall,logs,damage_calls,camera_sets,removes=nil,{},0,0,0
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
local NAME="adb_chimera_form_visual_v1"
local SAVE="adb_chimera_form_recovery_v1"
local fail_jump,fail_jump_read,jump_writes=false,false,{}
local actor={jump=6,pos=v(),living=true,visual="mod/actor",velocity=v(),hp=0.77,health=0.77}
-- Luabind game_object does not implement __eq; different wrappers must not
-- be compared with ==, including during target iteration and actor guards.
local object_meta={__eq=function() error("No such operator [__eq] defined in class [game_object]") end}
setmetatable(actor,object_meta)
function actor:position() return self.pos end
function actor:id() return 0 end
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
local velocity_writes,fail_velocity={},false
function actor:set_movement_speed(value)
    velocity_writes[#velocity_writes+1]=copy(value);self.velocity=copy(value)
    if fail_velocity then fail_velocity=false;error("velocity_failed_after_write") end
end
local move_defaults={walk_accel=11.5,run_coef=2.41,runback_coef=1.33,walk_back_coef=0.5,
    sprint_koef=2.08,walk_strafe_coef=1,run_strafe_coef=1,sprint_strafe_coef=0.8}
local move_values,move_writes,fail_move,fail_move_before={}, {},nil,nil
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
    for key,value in pairs(move_defaults) do assert(math.abs(move_values[key]-value)<0.00001,"Leaked movement coefficient: "..key) end
end
local world_gravity,ceiling=19.62,nil
local wall_flags,barrier=0,nil

alife_create=forbidden;alife_create_item=forbidden;show_indicators=forbidden;disable_input=forbidden
db={actor=actor};_HUD_STATE=true;_GUIs={};actor_effects={item_not_in_use=true}
actor_menu={is_hud_free=function() return not busy end}
function Check_UI() return busy end
function time_global() return now end
function device() return {frame=frame,cam_dir=initial_dir,width=display_width,height=display_height,fov=camera_fov,is_paused=function() return paused end} end
local body_debug="0"
function get_console() return {get_string=function(_,key) assert(key=="show_actor_body");return body_debug end,execute=forbidden} end
alife_storage_manager={get_state=function() return memory end}
local sections={arbitrary_boss_x={class="SM_CHIMS",visual="custom\\independent_chimera"},
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
sound_object=setmetatable({looped=1},{__call=function(_,name)
    assert(not missing_sounds,"Missing sound file must never be constructed")
    local s={name=name}
    function s:play_at_pos(owner,pos,delay,flags)
        assert(owner:id()==actor:id() and delay==0 and (flags==0 or flags==1))
        self.pos=copy(pos);self.active=true;sound_events[#sound_events+1]=name
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
    local object={kind=kind,pos=v(0,0,z),living=true,hits={},health=1,object_id=enemy_id}
    setmetatable(object,object_meta)
    function object:id() return self.object_id end
    function object:section() return "test_"..kind end
    function object:center(hud) assert(hud==false);return v(self.pos.x,self.pos.y+1,self.pos.z) end
    function object:alive() return self.living end
    function object:list_bones(hud) assert(hud==false);return {[0]="zero_damage_root",[7]="modded_collision_bone"} end
    function object:hit(h)
        assert(h.bone=="modded_collision_bone" and h.draftsman:id()==actor:id() and h.type==hit.wound)
        damage_calls=damage_calls+1;self.hits[#self.hits+1]=h
        if not self.immune then self.health=math.max(0,self.health-h.power) end
    end
    function object:position() assert(not self.stale);return copy(self.pos) end
    function object:parent() return self.owner end
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
DIK_keys={DIK_ESCAPE=1,DIK_1=2,DIK_2=3,DIK_3=4,DIK_F=2,DIK_R=4,MOUSE_1=3,MOUSE_2=5,
    DIK_X=45,DIK_SPACE=57,DIK_G=6,DIK_H=7,DIK_J=8,DIK_K=9,DIK_W=17,DIK_F5=63,DIK_F9=67}
key_bindings={kWPN_1=20,kWPN_2=21,kWPN_3=22,kWPN_FIRE=30,kUSE=31,kCAM_1=32,kINVENTORY=33,
    kFWD=45,kBACK=46,kL_STRAFE=47,kR_STRAFE=48,kSPRINT_TOGGLE=49,kJUMP=50,kQUICK_SAVE=51,kQUICK_LOAD=52,kACTIVE_JOBS=34,kCUSTOM20=35,kNEXT_SLOT=36,kPREV_SLOT=37}
local jump_dik=DIK_keys.DIK_SPACE
function bind_to_dik(bind)
    return bind==key_bindings.kJUMP and jump_dik or bind==key_bindings.kQUICK_SAVE and DIK_keys.DIK_F5 or DIK_keys.DIK_F9
end
function dik_to_keyname(dik) return dik==DIK_keys.DIK_G and "G" or "Unknown" end
level_input={on_mouse_move=function() end}
level={
    physics_world=function() return {gravity=function() return world_gravity end} end,
    iterate_nearest=function(origin,radius,visit)
        assert(radius>0 and radius<=34)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) for _,object in ipairs(targets) do if object.id and object:id()==id then return object end end end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_chimera" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==false and speed==1)
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
        local nearest,object,flags=range+1,nil,0
        if wall and math.abs(dir.z)>0.00001 then
            local t=(wall-origin.z)/dir.z
            if t>=0 and t<=range then nearest=t;flags=wall_flags end
        end
        if barrier and math.abs(dir.z)>0.00001 then
            local t=(barrier.z-origin.z)/dir.z
            local y=origin.y+dir.y*t
            if t>=0 and t<=range and t<nearest and y<=barrier.height then nearest=t;flags=0 end
        end
        if math.abs(dir.y)>0.00001 then
            local floor_t=-origin.y/dir.y
            if floor_t>=0 and floor_t<=range and floor_t<nearest then nearest=floor_t;flags=0 end
            if ceiling then
                local t=(ceiling-origin.y)/dir.y
                if t>=0 and t<=range and t<nearest then nearest=t;flags=0 end
            end
        end
        if mode==rq_target.rqtBoth then
            for _,target in ipairs(targets) do
                local center=target:center(false)
                local delta=v(center.x-origin.x,center.y-origin.y,center.z-origin.z)
                local t=delta.x*dir.x+delta.y*dir.y+delta.z*dir.z
                local error2=(delta.x-dir.x*t)^2+(delta.y-dir.y*t)^2+(delta.z-dir.z*t)^2
                if target:id()~=ignored:id() and t>=0 and t<=range and t<nearest and error2<0.15^2 then nearest,object,flags=t,target,0 end
            end
        end
        if nearest<=range then result.range=nearest;result.object=object;result.material_flags=flags;result.element=object and (object.no_bone and 65535 or 7);return true end
        return false
    end
}
function RegisterScriptCallback(name,f) callbacks[name]=f end
function CBeforeHitAfterCalcs(h,target,bone) callbacks.on_before_hit_after_calcs(h,target,bone) end
function printf(fmt,...) logs[#logs+1]=string.format(fmt,...) end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.chimera_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_chimera_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.chimera_form"])
local function params(duration) return {mutant_sections="arbitrary_boss_x",duration_seconds=duration or "60",catalog_revision="revision"} end
local function active() return descriptor.is_blocking() end
local function advance(ms)
    now=now+ms;frame=frame+1
    local windows={};for wnd in pairs(hud.windows) do windows[#windows+1]=wnd end
    for _,wnd in ipairs(windows) do wnd:Update() end
    descriptor.update()
end
local function key(dik,bind,kind)
    local flags={ret_value=true};callbacks[kind or "on_before_key_press"](dik,bind,false,flags);return flags
end
local function start(duration,attack_power)
    local p=params(duration);local context={}
    p.attack_power_percent=attack_power
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
local function reject(p,why)
    local ok,status,reason=descriptor.can_execute({},p);assert(not ok and reason==why,tostring(reason))
    assert(not active() and not attachments[NAME])
end
local function form_hp()
    for wnd in pairs(hud.windows) do
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.chimera_ui.hp.main.text):match("(%d+)%% HP"))
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


-- Native class and key guards: two modes, no source sections/actor jump config edits.
local p=params();p.catalog_revision="old";reject(p,"catalog_revision_mismatch")
p=params();p.mutant_sections="a_dog";reject(p,"chimera_visual_class_unsupported")
p=params();p.attack_key="W";reject(p,"chimera_ability_key_invalid")
p=params();p.attack_key="SPACE";reject(p,"chimera_ability_key_reserved")
p=params();p.boost_key="SPACE";reject(p,"chimera_ability_key_reserved")
p=params();p.boost_key="F5";reject(p,"chimera_ability_key_invalid")
p=params();p.boost_key="MOUSE_1";reject(p,"chimera_ability_keys_duplicate")
for _,value in ipairs({"0","9","100000","300.5","NaN"}) do
    p=params();p.health_percent=value;reject(p,"chimera_health_percent_invalid")
end
for _,value in ipairs({"0","9","9001","200.5","NaN"}) do
    p=params();p.attack_power_percent=value;reject(p,"chimera_attack_power_invalid")
end
missing="jump_attack_2";reject(params(),"chimera_animation_missing_jump_attack_2");missing=nil
local setter=actor.set_movement_speed;actor.set_movement_speed=nil
assert(not descriptor.is_supported());reject(params(),"chimera_form_api_unavailable");actor.set_movement_speed=setter
local speed_setter=actor.set_actor_walk_accel;actor.set_actor_walk_accel=nil
assert(not descriptor.is_supported());reject(params(),"chimera_form_api_unavailable");actor.set_actor_walk_accel=speed_setter
local function ui() for wnd in pairs(hud.windows) do return wnd.chimera_ui end end
local function text(value) return anomaly_donation_bridge_common.game_text_utf8(value) end
local function stop()
    key(DIK_keys.DIK_ESCAPE);stopped()
    assert(actor.jump==6 and #jump_writes==0,"Native jump setting was modified");movement_original()
    actor.pos=v();actor.velocity=v();initial_dir=v(0,0,1);targets={};wall=nil;ceiling=nil;wall_flags=0;barrier=nil
end
local body=start();advance(16)
assert(form_hp()==100 and body.motion=="stand_idle_0")
assert(ui().keys.melee==nil and text(ui().keys.leap.main.text)=="ЛКМ")
assert(ui().labels.melee==nil and text(ui().labels.leap.main.text)=="Низкая дуга")
assert(text(ui().keys.high_leap.main.text)=="ПКМ" and text(ui().labels.high_leap.main.text)=="Высокая дуга")
assert(text(ui().keys.boost.main.text)=="X" and text(ui().labels.boost.main.text)=="Ускорение")
assert(ui().labels.leap.main.color==GetARGB(255,180,185,180) and math.abs(ui().left+ui().width/2-512)<0.01)
assert(ui().ring.texture=="adb_burer_aim_ring" and level.camera_pos.y>=1.6)
for _,bind in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20}) do assert(not key(100,bind).ret_value) end
actor.velocity=v(0,0,4);advance(16);assert(body.motion=="stand_run_fwd_0");stop()
-- Native movement formula: full sprint is the default with either walk/run,
-- and pressing the game's sprint key cannot multiply it a second time.
body=start();advance(16)
local sprint=move_defaults.walk_accel*move_defaults.run_coef*move_defaults.sprint_koef
assert(math.abs(move_values.walk_accel-sprint)<0.00001 and move_values.run_coef==1 and move_values.sprint_koef==1)
assert(math.abs(move_values.walk_accel*move_values.runback_coef-move_defaults.walk_accel*move_defaults.runback_coef*move_defaults.sprint_koef)<0.00001)
assert(move_values.walk_strafe_coef==move_defaults.sprint_strafe_coef and move_values.run_strafe_coef==move_defaults.sprint_strafe_coef)
assert(key(100,key_bindings.kFWD).ret_value,"Ordinary native movement was blocked")
actor.velocity=v(0,0,0.5);advance(16);assert(body.motion=="stand_run_fwd_0")
assert(ui().labels.melee==nil)
-- Toggle only on presses; default X must consume its native sprint-toggle.
local count_before=#move_writes
assert(not key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE).ret_value);advance(1)
assert(math.abs(move_values.walk_accel-sprint*1.5)<0.00001 and #move_writes==count_before+1)
assert(text(ui().labels.boost.main.text)=="Ускорение включено")
assert(not key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE,"on_before_key_hold").ret_value);advance(16)
assert(not key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE,"on_before_key_release").ret_value);advance(16)
assert(#move_writes==count_before+1,"Boost accumulated during a hold/release")
key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
assert(math.abs(move_values.walk_accel-sprint)<0.00001 and #move_writes==count_before+2)
key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
assert(math.abs(move_values.walk_accel-sprint*1.5)<0.00001)
local old_count=#velocity_writes;key(DIK_keys.MOUSE_2,99);advance(350)
assert(#velocity_writes==old_count and body.motion=="stand_run_fwd_0","RMB pounce launched without a target")
assert(ui().notice.main.shown==false,"Empty aim must not display an instruction")
stop()
local target
local function aim_initial() initial_dir=v(0,-0.1,1) end
local function launch(distance,power,high_arc)
    target=enemy("mutant",distance);targets={target};aim_initial();body=start(nil,power)
    local count=#velocity_writes
    key(high_arc and DIK_keys.MOUSE_2 or DIK_keys.MOUSE_1,99);advance(1);advance(119)
    assert(#velocity_writes==count and body.motion=="jump_attack_0")
    advance(1)
    assert(#velocity_writes==count+1 and body.motion=="jump_attack_1","Pounce failed to launch")
    assert(#target.hits==0 or distance<2,"Pounce did remote damage at launch")
    return copy(velocity_writes[#velocity_writes])
end
-- Ballistic speed/arc adapts to near/mid/far targets and current gravity.
local speeds={}
for _,distance in ipairs({3,8,14,30}) do
    local velocity=launch(distance);speeds[#speeds+1]=velocity.z
    assert(velocity.y>0 and velocity.z>0 and velocity.x^2+velocity.y^2+velocity.z^2<=45^2)
    local travel=distance-0.9;local t=travel/velocity.z
    assert(math.abs(velocity.y*t-world_gravity*t*t/2-0.03)<0.0001,"Arc misses destination height")
    assert(not key(100,key_bindings.kFWD).ret_value and not key(100,key_bindings.kJUMP).ret_value)
    assert(key(100,key_bindings.kFWD,"on_before_key_release").ret_value)
    stop();assert(actor.velocity.x==0 and actor.velocity.z==0)
end
assert(speeds[1]<speeds[2] and speeds[2]<=speeds[3]+0.001 and speeds[4]>speeds[3],"Fixed-distance dash instead of adaptive trajectory")
world_gravity=9.81;local low=launch(8);stop();world_gravity=19.62
local high=launch(8);assert(high.y>low.y);stop()

-- Integrate native-like ballistic motion without any position setter in effect.
local function fly(milliseconds)
    local remaining=milliseconds
    while remaining>0 do
        local ms=math.min(16,remaining);remaining=remaining-ms;local dt=ms/1000
        actor.pos.x=actor.pos.x+actor.velocity.x*dt
        actor.pos.y=actor.pos.y+actor.velocity.y*dt-world_gravity*dt*dt/2
        actor.pos.z=actor.pos.z+actor.velocity.z*dt
        actor.velocity.y=actor.velocity.y-world_gravity*dt
        if actor.pos.y<0 then actor.pos.y=0;actor.velocity.y=0 end
        if wall and actor.pos.z>wall-0.4 then actor.pos.z=wall-0.4;actor.velocity.z=0 end
        advance(ms)
    end
end
-- Both arcs reach a target at 30m by actual contact, with bounded apex/speed.
local long_arcs={}
for _,high_arc in ipairs({false,true}) do
    local velocity=launch(30,nil,high_arc);long_arcs[#long_arcs+1]=velocity
    assert(velocity.y^2/(2*world_gravity)<=(high_arc and 6 or 2)+0.01)
    assert(velocity.z>0 and velocity.x^2+velocity.y^2+velocity.z^2<=45^2)
    local moving_speed=move_values.walk_accel
    assert(not key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE).ret_value);advance(1)
    assert(move_values.walk_accel==moving_speed,"Boost altered a flight already in progress")
    fly(2600)
    assert(#target.hits==1,"30m pounce did not reach a real contact target")
    stop()
end
assert(long_arcs[1].z>long_arcs[2].z and long_arcs[1].y<long_arcs[2].y,"30m arc modes became identical")
-- Boost changes running speed, not the ballistic jump or attack strength.
local unboosted=launch(14);stop()
target=enemy("mutant",14);targets={target};aim_initial();start()
key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
local boosted=velocity_writes[#velocity_writes]
assert(math.abs(boosted.z-unboosted.z)<0.00001 and math.abs(boosted.y-unboosted.y)<0.00001)
fly(1800);assert(#target.hits==1 and target.hits[1].power==3.5);stop()
launch(8);fly(1500)
assert(#target.hits==1 and math.abs(target.hits[1].power-3.5)<0.0001,"No real contact hit")
fly(500);assert(#target.hits==1,"Pounce damage repeated each frame");stop()
for _,power in ipairs({10,100,350,9000}) do
    launch(8,tostring(power));fly(1500)
    assert(#target.hits==1 and math.abs(target.hits[1].power-1.75*power/100)<0.0001);stop()
end
launch(8);target.immune=true;fly(1500)
assert(#target.hits==1 and target.health==1,"Native protection was bypassed");stop()
-- Moving target is re-read after windup, no stale destination.
target=enemy("npc",6);targets={target};aim_initial();start()
key(DIK_keys.MOUSE_1,99);advance(1);target.pos.z=11;advance(300)
assert(#velocity_writes>0)
local vel=velocity_writes[#velocity_writes]
local time=(11-0.9)/vel.z
assert(math.abs(vel.y*time-world_gravity*time*time/2-0.03)<0.0001);stop()
-- A target that evades during flight is not hit remotely.
launch(8);target.pos.x=8;fly(1500)
assert(#target.hits==0 and actor.pos.z>6.5,"A look-ahead ground ray stopped descent early");stop()
local low_launch=launch(8);stop()
local high_launch=launch(8,nil,true)
assert(high_launch.y>low_launch.y and high_launch.z<low_launch.z,"Explicit high arc did not differ from low arc")
fly(1500);assert(#target.hits==1,"High pounce never made a real contact hit");stop()
-- Wall/ceiling, ground, invalid gravity and excessive distance block launch.
for _,mode in ipairs({"wall","ceiling","air","gravity","far"}) do
    target=enemy("mutant",mode=="far" and 35 or 8);targets={target};aim_initial()
    if mode=="wall" then wall=4 elseif mode=="ceiling" then ceiling=2
    elseif mode=="air" then actor.pos.y=2 elseif mode=="gravity" then world_gravity=0 end
    start();local count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);advance(350)
    assert(#velocity_writes==count,"Unsafe launch: "..mode)
    if ui().notice.main.shown then
        local expected=(mode=="wall" or mode=="ceiling") and "Путь перекрыт" or mode=="air" and "Не готово" or "Не получилось"
        assert(text(ui().notice.main.text)==expected,"Wrong compact jump feedback: "..mode)
    end
    if mode=="gravity" then assert(ui().notice.main.shown,"Jump failure feedback disappeared: "..mode) end
    if mode=="far" then assert(ui().notice.main.shown==false,"Out-of-aim target must remain silent") end
    stop();world_gravity=19.62
end
-- A door closing during preparation cancels; dead target cannot be attacked.
target=enemy("npc",8);targets={target};aim_initial();start()
local count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);wall=4;advance(300)
assert(#velocity_writes==count and ui().notice.main.shown and text(ui().notice.main.text)=="Путь перекрыт");stop()
target=enemy("npc",8);targets={target};aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);target.living=false;advance(300)
assert(#velocity_writes==count);stop()
-- Obstacle appearing midair blocks contact damage as well as native motion.
launch(8);wall=4;fly(1500);assert(#target.hits==0);stop()

-- Independent 300% health, exact native protection and rider's entry HP.
-- Passable foliage is ignored; explicit actor-obstacle material still blocks.
target=enemy("mutant",8);targets={target};wall=4;wall_flags=128;aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count+1,"Passable foliage blocked the arc/target acquisition");stop()
target=enemy("mutant",8);targets={target};wall=4;wall_flags=128+4096;aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count,"Actor-obstacle material was skipped");stop()
-- A low roof permits the fast low profile; a curb selects a higher profile.
local low_arc=launch(8);stop()
target=enemy("mutant",8);targets={target};barrier={z=1.5,height=0.9};aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count,"LMB silently switched to a higher arc")
key(DIK_keys.MOUSE_2,99);advance(1);advance(120)
assert(#velocity_writes==count+1 and velocity_writes[#velocity_writes].y>low_arc.y+0.1,"No higher arc around the curb");stop()
target=enemy("mutant",14);targets={target};ceiling=3.2;aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count+1,"Low profile failed under adequate headroom");stop()
-- Other creatures in the path are contact targets, not walls at planning time.
target=enemy("npc",8);local bystander=enemy("npc",2);bystander.pos.x=0.4;bystander.pos.y=0.6
targets={target,bystander};aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count+1,"A foreground NPC blocked preparation");stop()
-- A rejected windup does not consume the cooldown: retry immediately.
target=enemy("npc",8);targets={target};aim_initial();start()
count=#velocity_writes;key(DIK_keys.MOUSE_1,99);advance(1);wall=4;advance(120)
assert(#velocity_writes==count);wall=nil
key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count+1,"Cancelled preparation consumed cooldown");stop()
-- 1.5s from the actual launch, with a short input buffer before readiness.
launch(8);fly(1000)
target=enemy("npc",actor.pos.z+8);targets={target}
count=#velocity_writes;key(DIK_keys.MOUSE_2,99);advance(300)
assert(#velocity_writes==count,"Cooldown was not enforced")
assert(text(ui().notice.main.text)=="Не готово","Cooldown must use the short readiness label")
key(DIK_keys.MOUSE_1,99);advance(200)
assert(#velocity_writes==count,"Buffered click launched before 1.5s")
advance(120)
assert(#velocity_writes==count+1,"Buffered jump did not launch after 1.5s");stop()
-- A physics-blocked takeoff is refunded, rather than locking out another try.
launch(8);actor.velocity=v();advance(200);advance(300)
target=enemy("npc",8);targets={target};count=#velocity_writes
key(DIK_keys.MOUSE_1,99);advance(1);advance(120)
assert(#velocity_writes==count+1,"Failed takeoff kept a cooldown");stop()

actor.health=0.42;start();actor_hit(0.3,hit.strike);advance(1)
assert(form_hp()==90 and actor.health==0.42)
actor_hit(100,hit.strike);advance(1);stopped();assert(actor.health==0.42);actor.health=0.77
-- Every cancellation releases velocity/input/HUD; saving never replays flight.
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
    start();key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
    assert(math.abs(move_values.walk_accel-sprint*1.5)<0.00001)
    callbacks[event]();stopped();movement_original()
end
start();key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
descriptor.on_level_changing();stopped();movement_original()
start();key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
actor_hit(100);advance(1);stopped();movement_original()
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
    launch(8);callbacks[event]();stopped()
    assert(actor.velocity.z==0 and actor.velocity.y>0,"Cleanup removed gravity/kept dash")
    assert(key(100,key_bindings.kFWD).ret_value);actor.pos=v();actor.velocity=v()
end
launch(8);descriptor.on_level_changing();stopped();assert(actor.velocity.z==0);actor.pos=v();actor.velocity=v()
launch(8);local data={};callbacks.save_state(data);stopped();assert(actor.velocity.z==0)
assert(data[SAVE].version==3 and #data[SAVE].movement==7 and data[SAVE].form_health==nil and data[SAVE].jump_before==nil);movement_original()
memory={[SAVE]=data[SAVE]};actor.health=0.21;camera=0;custom=true
for _,entry in ipairs(data[SAVE].movement) do move_values[entry.key]=entry.applied end
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.77);movement_original();actor.pos=v();actor.velocity=v()
-- Service save keeps HP/time/parameters only; flight and impulse never resume.
launch(8);actor_hit(0.3);advance(1)
descriptor.on_effect_save("before","chimera-service");assert(active() and not attachments[NAME] and actor.velocity.z==0);movement_original()
callbacks.save_state({});descriptor.on_effect_save("after","chimera-service",true);service_files["chimera-service.scop"]=true
count=#velocity_writes;advance(16)
assert(active() and attachments[NAME] and form_hp()==90 and #velocity_writes==count,"Service save replayed pounce")
stop()
fail_setup=true;assert(descriptor.execute({},params())=="rejected");stopped();movement_original()
fail_move="run_coef";assert(descriptor.execute({},params())=="rejected");stopped();movement_original()
-- Roll back both old/new lease values if toggling fails before or after write.
for _,before_write in ipairs({false,true}) do
    for _,already_boosted in ipairs({false,true}) do
        start()
        if already_boosted then key(DIK_keys.DIK_X,99);advance(1) end
        if before_write then fail_move_before="walk_accel" else fail_move="walk_accel" end
        key(DIK_keys.DIK_X,99);advance(1);stopped();movement_original()
    end
end
target=enemy("npc",8);targets={target};aim_initial();start();fail_velocity=true
key(DIK_keys.MOUSE_1,99);advance(1);advance(300)
stopped();assert(actor.velocity.z==0,"Partial launch failure kept dash");actor.pos=v();actor.velocity=v()
movement_original()
start("1");paused=true;advance(5000);paused=false;advance(999);assert(active());advance(1);stopped()
assert(#jump_writes==0 and #fx_created==0)
-- A loaded unrelated world owns its HP/velocity; old flight must not alter it.
launch(8);actor.health=0.26;actor.velocity=v(3,0,1)
for key,value in pairs(move_defaults) do move_values[key]=value end
move_values.walk_accel=15
attachments={};hud.windows={};custom=false;camera=2;memory={}
descriptor.on_game_load();descriptor.update();stopped()
assert(actor.health==0.26 and actor.velocity.x==3 and actor.velocity.z==1,"Old flight mutated loaded actor")
assert(move_values.walk_accel==15,"Old lease overwrote loaded movement")
for key,value in pairs(move_defaults) do move_values[key]=value end
actor.health=0.77;actor.pos=v();actor.velocity=v()
-- A third-party change ends the lease, but its value survives cleanup.
start();move_values.run_coef=1.7;advance(16);stopped()
assert(move_values.run_coef==1.7)
move_values.run_coef=move_defaults.run_coef;movement_original()
-- Old queued schema remains accepted, no melee action, fresh default becomes LMB.
p=params();p.melee_key="MOUSE_1";p.leap_key="MOUSE_2"
assert(descriptor.execute({},p)=="executed");assert(text(ui().keys.leap.main.text)=="ЛКМ");stop()
p=params();p.attack_key="G";assert(descriptor.execute({},p)=="executed")
assert(text(ui().keys.leap.main.text)=="G");stop()
p=params();p.boost_key="H";assert(descriptor.execute({},p)=="executed")
key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
assert(math.abs(move_values.walk_accel-sprint)<0.00001,"Old boost binding remained active")
key(DIK_keys.DIK_H,99);advance(1)
assert(math.abs(move_values.walk_accel-sprint*1.5)<0.00001);stop()
-- Reapplying after many service saves must never compound sprint speed.
for i=1,3 do
    start();descriptor.on_effect_save("before","sprint-save-"..i);movement_original()
    callbacks.save_state({});descriptor.on_effect_save("after","sprint-save-"..i,true)
    service_files["sprint-save-"..i..".scop"]=true;advance(16)
    assert(math.abs(move_values.walk_accel-sprint)<0.00001);stop()
end
-- A boosted service save releases coefficients first, then reapplies once.
start();key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
for i=1,3 do
    descriptor.on_effect_save("before","boost-save-"..i);movement_original()
    callbacks.save_state({});descriptor.on_effect_save("after","boost-save-"..i,true)
    service_files["boost-save-"..i..".scop"]=true;advance(16)
    assert(active() and math.abs(move_values.walk_accel-sprint*1.5)<0.00001,"Service-save boost stacked/was lost")
end
stop()
-- A failed service save never restores boost/form.
start();key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
descriptor.on_effect_save("before","boost-save-failed");movement_original()
callbacks.save_state({});descriptor.on_effect_save("after","boost-save-failed",false);advance(16)
stopped();movement_original()
-- Late-serialization journal knows the current boosted lease, not just base.
start();key(DIK_keys.DIK_X,key_bindings.kSPRINT_TOGGLE);advance(1)
local boost_data={};callbacks.save_state(boost_data);stopped();movement_original()
local boost_entry=assert(boost_data[SAVE].movement[1]);assert(boost_entry.key=="walk_accel")
assert(math.abs(boost_entry.applied-sprint*1.5)<0.00001)
memory={[SAVE]=boost_data[SAVE]};actor.health=0.21;camera=0;custom=true
for _,entry in ipairs(boost_data[SAVE].movement) do move_values[entry.key]=entry.applied end
descriptor.on_game_load();descriptor.update();stopped();movement_original()
-- A same-world outfit/visual replacement still releases owned movement/dash.
launch(8);actor.visual="mod/another_actor";advance(16)
assert(not active() and not attachments[NAME] and actor.velocity.z==0)
movement_original();actor.visual="mod/actor";actor.pos=v();actor.velocity=v()
local saw_failure=false
for _,message in ipairs(logs) do
    assert(not message:find(" damage:",1,true) and not message:find("form ability:",1,true) and
        not message:find("form started:",1,true) and not message:find("form ended:",1,true) and
        not message:find("suspended form cancelled:",1,true),"Routine action log spam returned: "..message)
    if message:find("form error:",1,true) then saw_failure=true end
end
assert(saw_failure,"Real runtime/setup errors must remain logged")
print("Chimera form CF-004 smoke passed (mock API): both 30m contact arcs, X +50% toggle/rebinding/no stacking, transactional boost rollback and service-save recovery, shared 1.5s cooldown/refunds/input buffer, native materials/obstacles, sprint restoration, HP300/attack200 and isolated load recovery.")
