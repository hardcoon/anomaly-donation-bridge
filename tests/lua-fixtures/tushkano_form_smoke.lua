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
local NAME="adb_tushkano_form_visual_v1"
local SAVE="adb_tushkano_form_recovery_v1"
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
db={actor=actor};_HUD_STATE=true;_GUIs={};actor_effects={item_not_in_use=true}
actor_menu={is_hud_free=function() return not busy end}
function Check_UI() return busy end
function time_global() return now end
function device() return {frame=frame,cam_dir=initial_dir,width=display_width,height=display_height,fov=camera_fov,is_paused=function() return paused end} end
local body_debug="0"
function get_console() return {get_string=function(_,key) assert(key=="show_actor_body");return body_debug end,execute=forbidden} end
alife_storage_manager={get_state=function() return memory end}
local sections={arbitrary_boss_x={class="SM_TUSHK",visual="custom\\independent_tushkano"},
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
    local object={kind=kind,pos=v(0,0.3,z),living=true,hits={},health=1,object_id=enemy_id}
    setmetatable(object,object_meta)
    function object:id() return self.object_id end
    function object:section() return "test_"..kind end
    function object:center(hud) assert(hud==false);return copy(self.pos) end
    function object:alive() return self.living end
    function object:list_bones(hud) assert(hud==false);return {[0]="zero_damage_root",[7]="modded_collision_bone"} end
    function object:hit(h)
        assert(h.bone=="modded_collision_bone" and h.draftsman:id()==actor:id() and h.type==hit.strike)
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
DIK_keys={DIK_V=47,DIK_ESCAPE=1,DIK_1=2,DIK_2=3,DIK_3=4,DIK_F=2,DIK_R=4,MOUSE_1=3,MOUSE_2=5,
    DIK_SPACE=57,DIK_G=6,DIK_H=7,DIK_J=8,DIK_K=9,DIK_W=17,DIK_F5=63,DIK_F9=67}
key_bindings={kWPN_1=20,kWPN_2=21,kWPN_3=22,kWPN_FIRE=30,kUSE=31,kCAM_1=32,kINVENTORY=33,
    kJUMP=50,kQUICK_SAVE=51,kQUICK_LOAD=52,kACTIVE_JOBS=34,kCUSTOM20=35,kNEXT_SLOT=36,kPREV_SLOT=37}
local jump_dik=DIK_keys.DIK_SPACE
function bind_to_dik(bind)
    return bind==key_bindings.kJUMP and jump_dik or bind==key_bindings.kQUICK_SAVE and DIK_keys.DIK_F5 or DIK_keys.DIK_F9
end
function dik_to_keyname(dik) return dik==DIK_keys.DIK_G and "G" or "Unknown" end
level_input={on_mouse_move=function() end}
level={
    iterate_nearest=function(origin,radius,visit)
        assert(radius>0 and radius<=26)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) for _,object in ipairs(targets) do if object.id and object:id()==id then return object end end end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_tushkano" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==true and speed==1,"Tushkano pose crossfade must not change playback speed")
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
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.tushkano_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_tushkano_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.tushkano_form"])
local function params(duration) return {camera_mode="center",mutant_sections="arbitrary_boss_x",duration_seconds=duration or "60",catalog_revision="revision"} end
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
local function start(duration,attack_power,jump_height)
    local p=params(duration);local context={}
    p.attack_power_percent=attack_power;p.jump_height_percent=jump_height
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
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.tushkano_ui.hp.main.text):match("(%d+)%% HP"))
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

-- Guards: class filtered, temporary native APIs mandatory, bindings preserved.
local p=params();p.catalog_revision="old";reject(p,"catalog_revision_mismatch")
p=params();p.mutant_sections="a_dog";reject(p,"tushkano_visual_class_unsupported")
for _,keyname in ipairs({"W","F5","F9","ESCAPE"}) do
    p=params();p.melee_key=keyname;reject(p,"tushkano_ability_key_invalid")
end
p=params();p.melee_key="SPACE";reject(p,"tushkano_ability_key_reserved")
for _,value in ipairs({"0","9","100000","300.5","NaN"}) do
    p=params();p.health_percent=value;reject(p,"tushkano_health_percent_invalid")
end
for _,value in ipairs({"0","9","9001","150.5","NaN"}) do
    p=params();p.attack_power_percent=value;reject(p,"tushkano_attack_power_invalid")
end
for _,value in ipairs({"0","9","1001","300.5","NaN"}) do
    p=params();p.jump_height_percent=value;reject(p,"tushkano_jump_height_invalid")
end
missing="stand_run_0";reject(params(),"tushkano_animation_missing_stand_run_0");missing=nil
missing="bites";reject(params(),"tushkano_bite_animation_missing");missing=nil
local producer=CBeforeHitAfterCalcs;CBeforeHitAfterCalcs=nil
assert(not descriptor.is_supported());reject(params(),"tushkano_calculated_damage_api_unavailable")
CBeforeHitAfterCalcs=producer
local setter=actor.set_actor_jump_speed;actor.set_actor_jump_speed=nil
assert(not descriptor.is_supported());reject(params(),"tushkano_form_api_unavailable");actor.set_actor_jump_speed=setter
fail_jump_read=true;reject(params(),"tushkano_jump_speed_unavailable");fail_jump_read=false
local function close_jump(expected)
    assert(math.abs(actor.jump-(expected or 6))<0.00001,"Leaked/overwritten jump speed: "..actor.jump)
end
local function get_ui() for wnd in pairs(hud.windows) do return wnd.tushkano_ui end end
local function utf8(text) return anomaly_donation_bridge_common.game_text_utf8(text) end
local function stop() key(DIK_keys.DIK_ESCAPE);stopped();close_jump() end
local body=start();advance(16);close_jump(6*math.sqrt(2.5))
assert(body.motion=="stand_idle_0" and form_hp()==100)
local ui=get_ui()
assert(utf8(ui.keys.melee.main.text)=="ЛКМ" and utf8(ui.keys.jump.main.text)=="Пробел")
assert(utf8(ui.labels.melee.main.text)=="Укус" and utf8(ui.labels.jump.main.text)=="Прыжок")
assert(ui.labels.melee.main.color==GetARGB(255,180,185,180) and ui.ring.texture=="adb_burer_aim_ring")
assert(math.abs(ui.left+ui.width/2-512)<0.01 and ui.labels.stomp==nil and ui.labels.tele==nil)
assert(level.camera_pos.y>=0.6 and level.camera_pos.y<=1.6 and -level.camera_pos.z>=2)
local ring=ui.ring;assert(math.abs(ring.size.x/ring.size.y-0.75)<0.001,"Ring stretched on widescreen")
for _,bind in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20,key_bindings.kNEXT_SLOT}) do
    assert(not key(100,bind).ret_value)
end
assert(key(jump_dik,key_bindings.kJUMP).ret_value,"Native jump was blocked")
assert(key(jump_dik,key_bindings.kJUMP,"on_before_key_hold").ret_value)
jump_dik=DIK_keys.DIK_G;advance(16)
assert(utf8(get_ui().keys.jump.main.text)=="G","Jump hint ignored player's actual binding")
assert(key(jump_dik,key_bindings.kJUMP).ret_value);jump_dik=DIK_keys.DIK_SPACE
actor.velocity=v(0,0,1);advance(16);assert(body.motion=="stand_walk_fwd_0")
actor.velocity=v(0,0,4);advance(16);assert(body.motion=="stand_run_0")
local mouse={ret_value=false};callbacks.on_before_mouse_move(100,0,false,mouse);advance(16)
assert(mouse.ret_value==true and math.abs(math.sin(actor.angles[1])-math.sin(0.25))<0.001)
stop();actor.velocity=v();initial_dir=v(0,0,1)
assert(key(100,key_bindings.kACTIVE_JOBS).ret_value)
for _,height in ipairs({10,100,250,300,1000}) do
    start(nil,nil,tostring(height));close_jump(6*math.sqrt(height/100))
    advance(100);close_jump(6*math.sqrt(height/100));stop()
end
visual_center_y=0.45;start();advance(16);assert(level.camera_pos.y>=1.19 and level.camera_pos.y<=1.21)
stop();visual_center_y=nil
display_width,display_height=3440,1440;start();advance(16)
ui=get_ui();assert(math.abs(ui.left+ui.width/2-512)<0.01)
assert(math.abs(ui.ring.size.x/ui.ring.size.y-(1440/3440)/(768/1024))<0.001)
stop();display_width,display_height=1920,1080

-- One real bite event per animation, three native variants and baseline damage.
local target=enemy("mutant",1);targets={target};body=start()
for variant,impulse in ipairs({10,15,18}) do
    key(DIK_keys.MOUSE_1,99);advance(1)
    assert(body.motion=="stand_attack_"..(variant-1))
    local bite_cycles,bite_camera=#body.motions,camera_sets
    local bite_turn={ret_value=false};callbacks.on_before_mouse_move(8,0,false,bite_turn)
    assert(key(DIK_keys.DIK_W,99).ret_value and key(DIK_keys.DIK_SPACE,key_bindings.kJUMP).ret_value,
        "Tushkano crossfade blocked native movement/jump")
    advance(449);assert(#target.hits==variant-1)
    assert(bite_turn.ret_value and camera_sets==bite_camera+1 and #body.motions==bite_cycles,
        "Tushkano crossfade delayed the camera/restarted the bite")
    advance(1);assert(#target.hits==variant)
    assert(math.abs(target.hits[variant].power-0.3)<0.00001 and target.hits[variant].impulse==impulse)
    advance(1000);assert(#target.hits==variant,"Repeated bite on same action")
end
assert(sound_events[#sound_events]:find("tushkano_attack_",1,true))
stop()
for _,strength in ipairs({10,100,350,9000}) do
    target=enemy("npc",1);targets={target};start(nil,tostring(strength))
    key(DIK_keys.MOUSE_1,99);advance(1);advance(450)
    assert(#target.hits==1 and math.abs(target.hits[1].power-0.1*strength/100)<0.00001);stop()
end
target=enemy("npc",1);target.immune=true;targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(450)
assert(#target.hits==1 and target.health==1,"Native immunity bypassed");stop()
target=enemy("mutant",1);targets={target};wall=0.5;start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(450);assert(#target.hits==0,"Bite passed a wall");stop();wall=nil
-- The bite and its aim rays skip native Passable foliage, not actual cover.
for _,case in ipairs({
    {flags=128,layers=6,hit=true},{flags=128+4,layers=6,hit=true},
    {flags=128+4096,layers=1,hit=false},{flags=0,layers=1,hit=false},
    {layers=1,hit=false},{flags=128,layers=6,wall=0.8,hit=false},
    {flags=128,layers=17,hit=true},{flags=128,layers=6,beyond=true,hit=false}
}) do
    target=enemy("mutant",1);targets={target};wall=case.wall
    if case.beyond then target.pos.z=2 end
    for i=1,case.layers do ray_layers[i]={z=i*0.04,flags=case.flags} end
    start();key(DIK_keys.MOUSE_1,99);advance(1);advance(450)
    assert((#target.hits==1)==case.hit,"Foliage/solid boundary failed: bite/"..tostring(case.flags).."/"..case.layers)
    stop();wall=nil;ray_layers={}
end
targets={};start();local clear_camera_z=level.camera_pos.z;stop()
ray_layers={{z=-1,flags=128}};start()
assert(math.abs(level.camera_pos.z-clear_camera_z)<0.001,"Passable foliage pushed the camera in");stop()
ray_layers={{z=-1,flags=128+4096}};start()
assert(level.camera_pos.z>clear_camera_z+0.5,"Camera crossed ActorObstacle");stop();ray_layers={}

target=enemy("npc",2);targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(450);assert(#target.hits==0,"Bite reached beyond 1.5m");stop()
target=enemy("mutant",1);target.no_bone=true;targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(450);assert(#target.hits==0,"Bite guessed target bone")
for wnd in pairs(hud.windows) do assert(anomaly_donation_bridge_common.game_text_utf8(wnd.tushkano_ui.notice.main.text)=="Не получилось","Verbose failure notice returned") end
stop()
target=enemy("mutant",1);targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);paused=true;advance(5000);assert(#target.hits==0)
paused=false;advance(450);assert(#target.hits==1);stop()
-- A visual lacking one variant still uses verified remaining variants only.
missing="stand_attack_0";target=enemy("mutant",1);targets={target};body=start()
key(DIK_keys.MOUSE_1,99);advance(1);assert(body.motion=="stand_attack_1");advance(450)
assert(#target.hits==1 and target.hits[1].impulse==15);stop();missing=nil
missing_sounds=true;start();key(DIK_keys.MOUSE_1,99);advance(1);advance(450);stop();missing_sounds=nil
targets={}

-- Fictional HP: native protections applied once, rider's HP never changes.
actor.health=0.42;protection.immunity=1/3
start();actor_hit(0.9,hit.strike);advance(1)
assert(form_hp()==93 and actor.health==0.42,"400% capacity/normalized incoming damage is incorrect")
callbacks.actor_on_update();advance(1);assert(form_hp()==93,"Damage charged twice")
actor_hit(100,hit.strike);actor_hit(100,hit.strike);advance(1);stopped();close_jump()
assert(actor.health==0.42 and actor:alive())
protection.immunity=1
for _,health in ipairs({"100","400","9000","99999"}) do
    p=params();p.health_percent=health;assert(descriptor.execute({},p)=="executed")
    actor_hit(tonumber(health)/100/4);advance(1)
    assert(form_hp()==75 and actor.health==0.42);stop()
end
start();actor.health=0.12;callbacks.actor_on_update();advance(1)
assert(form_hp()==93 and actor.health==0.42);stop()
start();actor.health=-0.1
local death={ret_value=true};callbacks.actor_on_before_death(actor,death);advance(1)
assert(death.ret_value==false and actor.health==0.42);stop()
actor.health=0.77

-- Runtime jump lease: all exits, setup rollback, native/foreign ownership.
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
    start();callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end;close_jump()
end
start("1");paused=true;advance(5000);close_jump(6*math.sqrt(2.5))
paused=false;advance(999);assert(active());advance(1);stopped();close_jump()
start();descriptor.on_level_changing();stopped();close_jump()
start();actor.living=false;advance(16);stopped();close_jump();actor.living=true
fail_setup=true;assert(descriptor.execute({},params())=="rejected");stopped();close_jump()
fail_jump=true;assert(descriptor.execute({},params())=="rejected");stopped();close_jump()
start();actor.jump=9;advance(16);stopped();close_jump(9)
actor.jump=6
start();actor.jump=8;key(DIK_keys.DIK_ESCAPE);stopped();close_jump(8)
actor.jump=6
busy=false;start();busy=true;advance(16);stopped();close_jump();busy=false
-- No setter per-frame: a lease is applied once and restored once.
local count=#jump_writes;start();advance(16);advance(16);advance(16)
assert(#jump_writes==count+1);stop();assert(#jump_writes==count+2)

-- Save boundaries/journal belong to the loaded save, never an old world.
start();local save={};callbacks.save_state(save);cancel_saved_form();close_jump()
assert(save[SAVE].version==3 and save[SAVE].jump_before==6 and save[SAVE].form_health==nil)
assert(save.adb_burer_form_recovery_v1==nil and save.adb_pseudogiant_form_recovery_v1==nil)
save[SAVE].form_state=nil;memory={[SAVE]=save[SAVE]};actor.health=0.21;actor.jump=save[SAVE].jump_applied;camera=0;custom=true
descriptor.on_game_load();descriptor.update();stopped();close_jump();assert(actor.health==0.77)
-- A foreign/native value is not overwritten even when loading our journal.
save[SAVE].form_state=nil;memory={[SAVE]=save[SAVE]};actor.jump=7;descriptor.on_game_load();descriptor.update();close_jump(7);actor.jump=6
start();actor.health=0.29;actor.jump=5;attachments={};hud.windows={};custom=false;camera=2;memory={}
descriptor.on_game_load();descriptor.update();stopped();close_jump(5)
assert(actor.health==0.29,"Unrelated loaded save HP overwritten");actor.health=0.77;actor.jump=6

-- Service saves remove the jump lease BEFORE serialization, resume exactly once.
start(nil,"350","300");actor_hit(0.4);advance(1000)
descriptor.on_effect_save("before","tushkan-service");assert(active() and not attachments[NAME] and not custom);close_jump()
local data={};callbacks.save_state(data)
assert(data[SAVE].form_health==nil and data[SAVE].params==nil)
descriptor.on_effect_save("after","tushkan-service",true);service_files["tushkan-service.scop"]=true
advance(16);assert(active() and form_hp()==90);close_jump(6*math.sqrt(3))
target=enemy("npc",1);targets={target}
key(DIK_keys.MOUSE_1,99);advance(1);advance(450)
assert(#target.hits==1 and math.abs(target.hits[1].power-0.35)<0.00001);stop();targets={}
start();descriptor.on_effect_save("before","foreign-service");close_jump()
actor.jump=7;callbacks.save_state({});descriptor.on_effect_save("after","foreign-service",true)
service_files["foreign-service.scop"]=true;advance(16);close_jump(7*math.sqrt(2.5))
key(DIK_keys.DIK_ESCAPE);stopped();close_jump(7);actor.jump=6
for _,mode in ipairs({"load","failed","timeout"}) do
    start();descriptor.on_effect_save("before",mode);close_jump()
    if mode=="load" then descriptor.on_game_load();descriptor.update()
    elseif mode=="failed" then descriptor.on_effect_save("after",mode,false)
    else descriptor.on_effect_save("after",mode,true);advance(10001) end
    stopped();close_jump()
end
assert(copy_count==0 and delete_count==0 and #fx_created==0)
-- Two asynchronous service saves: no early resume, no jump compounding.
start("3");advance(500)
descriptor.on_effect_save("before","pair-a");descriptor.on_effect_save("before","pair-b");close_jump()
descriptor.on_effect_save("after","pair-a",true);service_files["pair-a.scop"]=true
callbacks.save_state({});advance(500);assert(active() and not attachments[NAME]);close_jump()
descriptor.on_effect_save("after","pair-b",true);service_files["pair-b.scop"]=true
advance(16);assert(active() and not attachments[NAME]);close_jump()
callbacks.save_state({});advance(16);assert(attachments[NAME]);close_jump(6*math.sqrt(2.5))
advance(2499);assert(active());advance(1);stopped();close_jump()
local saw_failure=false
for _,message in ipairs(logs) do
    assert(not message:find(" damage:",1,true) and not message:find("form ability:",1,true) and
        not message:find("form started:",1,true) and not message:find("form ended:",1,true) and
        not message:find("suspended form cancelled:",1,true),"Routine action log spam returned: "..message)
    if message:find("form error:",1,true) then saw_failure=true end
end
assert(saw_failure,"Real runtime/setup errors must remain logged")
-- Largest form duration is accepted and expires without timer overflow.
targets={};start("60000");advance(59999000);assert(active(),"60000-second form expired early")
advance(1000);stopped()
reject(params("60001"),"tushkano_duration_invalid")

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
    assert(math.abs(level.camera_pos.x)<0.001,"Requested default centre camera changed")
    key(DIK_keys.DIK_V);advance(0)
    local side=copy(level.camera_pos)
    assert(side.x>0.1 and side.y<centre.y and side.z>centre.z,"Default camera is not closer/lower/right shoulder")
    local view
    for wnd in pairs(hud.windows) do view=wnd.tushkano_ui end
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
    assert(descriptor.execute({},p)=="executed" and math.abs(level.camera_pos.x)<0.001)
    key(DIK_keys.DIK_ESCAPE);stopped()
    p.camera_mode="side"
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
    p=params();p.camera_mode="invalid";reject(p,"tushkano_camera_mode_invalid")
    p=params();p.camera_key="MOUSE_1";reject(p,"tushkano_ability_keys_duplicate")
end
-- New session-gated F5/F9 behavior, through the actual module callbacks.
assert(loadfile(core.."/../../../tests/lua-fixtures/form_save_roundtrip.lua"))()({
    name="tushkano",key=SAVE,actor=actor,callbacks=callbacks,
    start=function() start("20") end,stopped=stopped,advance=advance,active=active,
    live=function() return custom end,descriptor=function() return descriptor end,
    cancel=function() callbacks.on_before_load_input() end,
    set_memory=function(value) memory=value end,set_paused=function(value) paused=value end,
    elapse_silent=function(ms) now=now+ms end,
    file_exists=function(name) service_files[name..".scop"]=true end,
    reload_common=function() anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common") end
})

print("Tushkano TF-009 smoke passed (mock API): native pose crossfade/unchanged bite timing, passable foliage/ActorObstacle/solid cover/range/camera, visual class, native bite variants, 300% attack/400% HP/250% jump, lease rollback, HUD/input and isolated save/load recovery.")
