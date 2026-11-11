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
local particle_file,missing_particle,missing_sounds
local copied_files,copy_count,delete_count={},0,0
local probe_read_error,probe_copy_error=false,false
local service_files={}
local protection={artefact=1,outfit=0,immunity=1,health_part=1,bone=1,boost=0}
local real_io,real_os=io,os
io=setmetatable({open=function(path,mode)
    if path=="virtual\\particles.xr" then return nil end
    if path:find("private\\",1,true)==1 then
        local data=copied_files[path]
        if not data then return nil end
        return {read=function(_,limit) if probe_read_error then error("probe_read_failed") end;return data:sub(1,limit) end,close=function() end}
    end
    return real_io.open(path,mode)
end},{__index=real_io})
-- Match Monolith's disable_os_funcs, not unrestricted desktop LuaJIT.
os=setmetatable({},{__index=function(_,key)
    if key~="remove" and key~="rename" and key~="execute" and key~="exit" then return real_os[key] end
end})
assert(os.remove==nil)
local function u32(n) return string.char(n%256,math.floor(n/256)%256,math.floor(n/65536)%256,math.floor(n/16777216)%256) end
local function chunk(id,data) return u32(id)..u32(#data)..data end
local particle_data=chunk(1,string.char(1,0))..chunk(3,
    chunk(0,chunk(1,string.char(1,0))..chunk(2,"anomaly2\\burer_prepare\0"))..
    chunk(1,chunk(2,"anomaly2\\burer_wave\0")))..chunk(4,chunk(0,chunk(2,"anomaly2\\burer_shield_01\0")))
particle_file=particle_data
local NAME="adb_burer_form_visual_v1"
local SAVE="adb_burer_form_recovery_v1"
local actor={pos=v(),living=true,visual="mod/actor",velocity=v(),hp=0.77,health=0.77}
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
local sections={arbitrary_boss_x={class="SM_BURER",visual="custom\\independent_burer"},
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
        if alias=="$game_data$" then return {size_real=#particle_file} end
        if alias=="$game_particles$" then return false end -- exercise binary library preflight
        if alias=="$game_sounds$" then
            return not missing_sounds and (path:match("_0%.ogg$") or path:match("_hold%.ogg$") or path:match("_throw%.ogg$") or path:match("earth4%.ogg$"))~=nil
        end
        return not missing or missing~=path
    end,
    update_path=function(_,alias,path)
        assert(alias=="$game_data$" or alias=="$app_data_root$")
        return alias=="$game_data$" and "virtual\\particles.xr" or "private\\"..path
    end,
    file_copy=function(_,source,destination)
        assert(source=="virtual\\particles.xr" and not copied_files[destination])
        copied_files[destination]=particle_file;copy_count=copy_count+1
        if probe_copy_error then error("copy_failed_after_creation") end
    end,
    file_delete=function(_,path)
        assert(path:find("private\\adb_burer_particle_probe_",1,true)==1,"Deleted non-owned path")
        copied_files[path]=nil;delete_count=delete_count+1
    end,
    r_open=function() error("Unsafe native IReader must not be acquired") end,
    r_close=function() error("Reproduces native IReader*& C++ exception") end
} end
particles_object=function(name)
    assert(name~=missing_particle,"Missing particle constructor MUST NOT be probed")
    assert(name=="anomaly2\\burer_prepare" or name=="anomaly2\\burer_wave" or name=="anomaly2\\burer_shield_01")
    local p={name=name}
    function p:set_direction(dir) assert(not self.stale);self.dir=copy(dir) end
    function p:play_at_pos(pos,hud_mode) assert(not self.stale and not hud_mode);self.pos=copy(pos);self.active=true end
    function p:move_to(pos,velocity) assert(not self.stale and self.active);self.pos=copy(pos) end
    function p:stop() assert(not self.stale);self.active=false end
    fx[#fx+1]=p;fx_created[#fx_created+1]=name
    return p
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
game={motion_exists=function(model,name) return missing~=name end}
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
    function object:list_bones(hud)
        assert(hud==false)
        return self.bones or {[0]="zero_damage_root",[7]="modded_collision_bone"}
    end
    function object:bone_position(index,hud)
        assert(hud==false and self:list_bones(false)[index],"Invalid native bone index")
        return self.bone_positions and self.bone_positions[index] or v(self.pos.x,self.pos.y+0.25,self.pos.z)
    end
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
    DIK_G=6,DIK_H=7,DIK_J=8,DIK_K=9,DIK_W=17,DIK_F5=63,DIK_F9=67}
key_bindings={kWPN_1=20,kWPN_2=21,kWPN_3=22,kWPN_FIRE=30,kUSE=31,kCAM_1=32,kINVENTORY=33,
    kACTIVE_JOBS=34,kCUSTOM20=35,kNEXT_SLOT=36,kPREV_SLOT=37}
level_input={on_mouse_move=function() end}
level={
    iterate_nearest=function(origin,radius,visit)
        assert(radius>0 and radius<=81)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) for _,object in ipairs(targets) do if object.id and object:id()==id then return object end end end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_burer" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==true and speed==1,"Burer pose crossfade must not change playback speed")
            self.motion=motion;self.motions[#self.motions+1]=motion
            return motion=="stand_idle_0" and 0 or 1000
        end
        function body:set_position(pos) assert(not self.stale);self.pos=copy(pos) end
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
        assert(ignored and type(ignored:id())=="number" and range>0 and range<=85) -- camera-to-target ray includes the camera offset
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
                if not target.ray_miss and target:id()~=ignored:id() and t>=0 and t<=range and t<nearest and error2<0.15^2 then nearest,object,material_flags=t,target,0 end
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
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.burer_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_burer_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.burer_form"])
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
local function start(duration,attack_power)
    local p=params(duration);local context={}
    p.attack_power_percent=attack_power
    local allowed,status,why=descriptor.can_execute(context,p)
    assert(allowed,tostring(status)..":"..tostring(why))
    assert(descriptor.execute(context,p)=="executed");assert(active() and custom and camera==0 and attachments[NAME])
    assert(next(copied_files)==nil,"Private particle probe temporary file was left behind")
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
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.burer_ui.hp.main.text):match("(%d+)%% HP"))
    end
end
local function actor_hit(power,kind,draftsman,flags)
    local h={power=power,type=kind or hit.fire_wound,draftsman=draftsman}
    flags=flags or {ret_value=true}
    callbacks.actor_on_before_hit(h,0,flags)
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
local p=params();p.catalog_revision="stale";reject(p,"catalog_revision_mismatch")
p=params();p.wave_key="W";reject(p,"burer_ability_key_invalid")
p=params();p.wave_key="F5";reject(p,"burer_ability_key_invalid")
p=params();p.wave_key="F";reject(p,"burer_ability_keys_duplicate")
p=params();p.mutant_sections="excluded";reject(p,"catalog_section_user_excluded")
p=params();p.mutant_sections="a_dog";reject(p,"burer_visual_class_unsupported")
for _,duration in ipairs({"0","60001","1.2","NaN"}) do reject(params(duration),"burer_duration_invalid") end
for _,health in ipairs({"0","9","100000","300.5","NaN"}) do
    p=params();p.health_percent=health;reject(p,"burer_health_percent_invalid")
end
missing="stand_power_attack_0";reject(params(),"burer_animation_missing_stand_power_attack_0");missing=nil
for _,power in ipairs({"0","9","201","1.5","NaN"}) do
    p=params();p.tele_power_percent=power;reject(p,"burer_tele_power_invalid")
end
for _,power in ipairs({"0","9","9001","1.5","NaN","inf",""}) do
    p=params();p.attack_power_percent=power;reject(p,"burer_attack_power_invalid")
end
missing="custom\\independent_burer.ogf";reject(params(),"burer_visual_missing");missing=nil
body_debug="1";reject(params(),"burer_actor_body_debug_enabled");body_debug="0"
actor.holder={};reject(params(),"burer_waiting_for_free_actor");actor.holder=nil
actor.item={kind="weapon",get_state=function() return 5 end};reject(params(),"burer_waiting_for_free_actor");actor.item=nil
local producer=level_input.on_mouse_move;level_input.on_mouse_move=nil
assert(not descriptor.is_supported());level_input.on_mouse_move=producer
local after_calcs=CBeforeHitAfterCalcs;CBeforeHitAfterCalcs=nil
assert(not descriptor.is_supported());reject(params(),"burer_calculated_damage_api_unavailable")
assert(descriptor.execute({},params())=="deferred");CBeforeHitAfterCalcs=after_calcs
assert(anomaly_donation_bridge_common.form_health_api_version==nil,"Template must work with the unmodified core")

local body=start();local before=camera_sets
actor.pos=v(5,0,0);actor.velocity=v(1,0,0);advance(16)
assert(body.pos.x==5 and body.motion=="stand_walk_fwd_0" and actor.pos.x==5)
actor.velocity=v(4,0,0);advance(16);assert(body.motion=="stand_run_fwd_0")
local mouse={ret_value=false};callbacks.on_before_mouse_move(100,0,false,mouse);advance(16)
assert(mouse.ret_value==true and math.abs(math.sin(body.rotation*math.pi/180)+math.sin(0.25))<0.001)
assert(math.abs(math.sin(actor.angles[1])-math.sin(0.25))<0.001)
assert(camera_sets>=before+3,"Camera updates must run at render cadence, not Bridge's 100ms tick")
assert(key(DIK_keys.DIK_W,99).ret_value==true,"Native WASD must remain untouched")
assert(key(100,key_bindings.kINVENTORY).ret_value==false,"Inventory must not interrupt the form")
assert(key(100,key_bindings.kINVENTORY,"on_before_key_hold").ret_value==false)
actor.item={kind="weapon",get_state=function() return 0 end} -- idle knife
for _,binding in ipairs({key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20,key_bindings.kNEXT_SLOT,key_bindings.kPREV_SLOT}) do
    assert(key(100,binding).ret_value==false,"PDA/slot input interrupted knife-entry form")
    assert(key(100,binding,"on_before_key_hold").ret_value==false)
    assert(key(100,binding,"on_before_key_release").ret_value==true,"Native STOP/release must remain available")
    busy=true;assert(key(100,binding).ret_value==false,"Busy frame leaked PDA input");busy=false
end
actor.item=nil
assert(key(DIK_keys.DIK_F5,99).ret_value==true and key(DIK_keys.DIK_F9,99).ret_value==true,"Save/load keys were blocked")
assert(key(100,key_bindings.kWPN_FIRE).ret_value==false)
assert(key(100,key_bindings.kWPN_FIRE,"on_before_key_release").ret_value==true,"Native weapon STOP must not be trapped")
key(DIK_keys.DIK_ESCAPE);stopped()
assert(key(100,key_bindings.kACTIVE_JOBS).ret_value==true and key(100,key_bindings.kCUSTOM20).ret_value==true,"PDA input stayed blocked after form")
actor.pos=v();actor.velocity=v()

-- Independent native reference: FirstEye transposes rotation(quaternion),
-- GetWorldYaw=-yaw; movement applies rotateY(-GetWorldYaw). FPCam uses HPB.
local function native_forward(yaw,pitch)
    local qx,qy,qz,qw=math.sin(pitch/2)*math.cos(yaw/2),math.cos(pitch/2)*math.sin(yaw/2),
        -math.sin(pitch/2)*math.sin(yaw/2),math.cos(pitch/2)*math.cos(yaw/2)
    return v(2*(qx*qz+qw*qy),2*(qy*qz-qw*qx),1-2*(qx*qx+qy*qy))
end
local function custom_forward()
    local h,p=level.camera_angles.x,level.camera_angles.y
    return v(-math.sin(h)*math.cos(p),math.sin(p),math.cos(h)*math.cos(p))
end
local function dot(a,b) return a.x*b.x+a.y*b.y+a.z*b.z end
for _,heading in ipairs({0,math.pi/2,math.pi,-math.pi/2,0.43,-2.7}) do
    initial_dir=v(-math.sin(heading),0,math.cos(heading));start()
    local yaw,pitch=actor.angles[1],actor.angles[2]
    assert(dot(native_forward(yaw,pitch),initial_dir)>0.9999,"Actor and camera look in opposite directions")
    local right=v(initial_dir.z,0,-initial_dir.x)
    for _,input in ipairs({{0,1,initial_dir},{0,-1,v(-initial_dir.x,0,-initial_dir.z)},
        {-1,0,v(-right.x,0,-right.z)},{1,0,right}}) do
        local x,z=input[1],input[2]
        local moved=v(math.cos(yaw)*x+math.sin(yaw)*z,0,-math.sin(yaw)*x+math.cos(yaw)*z)
        assert(dot(moved,input[3])>0.9999,"WASD is not screen-relative")
    end
    callbacks.on_before_mouse_move(30,20,false,{ret_value=false});advance(1)
    local look=custom_forward()
    assert(dot(look,right)>0 and look.y<0,"Mouse right/down is inverted")
    assert(dot(look,native_forward(actor.angles[1],actor.angles[2]))>0.9999,"Pitch conversion incorrect")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
initial_dir=v(0,0,1)

-- Remapped abilities and actual HP are displayed as a centred block with
-- one left-aligned hint per line; the original defaults are not hardwired.
p=params();p.wave_key="G";p.tele_key="H";p.melee_key="J";p.shield_key="K"
assert(descriptor.execute({},p)=="executed")
key(DIK_keys.DIK_J,99);advance(1);assert(attachments[NAME].motion=="stand_attack_0")
local window;for wnd in pairs(hud.windows) do window=wnd end
local ui=assert(window.burer_ui)
assert(ui.hp.pos.y>=580 and ui.keys.wave.main.text=="G" and ui.keys.melee.main.text=="J")
for index,kind in ipairs({"wave","tele","melee","shield","camera","footer"}) do
    local key_label,label,separator=ui.keys[kind],ui.labels[kind],ui.separators[kind]
    assert(key_label.main.alignment==1 and label.main.alignment==0 and separator.main.text=="|")
    assert(label.pos.x==ui.labels.wave.pos.x and key_label.pos.x==ui.keys.wave.pos.x)
    assert(label.pos.y==608+(index-1)*20+(kind=="footer" and 6 or 0))
    assert(#label.controls==9 and label.controls[1].color==GetARGB(245,0,0,0),"Readable text outline missing")
    assert(label.main.color==GetARGB(255,180,185,180) and key_label.main.color==GetARGB(255,238,205,125),
        "Only key labels should be coloured; descriptions must be grey")
    assert(math.abs(label.pos.x+ui.width/2-512)<0.001,"Description column is not centred independently of keys")
    if kind~="shield" and kind~="footer" then assert(not label.main.text:find(" с",1,true),"Ability cooldown must not be shown") end
end
assert(ui.ring.texture=="adb_burer_aim_ring" and ui.ring.pos.x+ui.ring.size.x/2==512 and
    ui.ring.pos.y+ui.ring.size.y/2==384,"Aim ring is not centred")
assert(ui.ring.size.x*1920/1024==ui.ring.size.y*1080/768,"Aim ring is stretched in widescreen")
assert(ui.notice.pos.y>440 and ui.notice.pos.y<510 and ui.notice.main.shown==false,"Damage must be under aim")
local textures=0;for _,child in ipairs(window.children) do if child.texture then textures=textures+1;assert(child.texture=="adb_burer_aim_ring") end end
assert(textures==1,"Text still has a black DDS backdrop")
assert(level.camera_pos.y-actor.pos.y>=2.2,"Camera still points through burer's head")
key(DIK_keys.DIK_ESCAPE);stopped()

for _,size in ipairs({{1024,768},{1920,1080},{3440,1440},{5120,1440}}) do
    display_width,display_height=size[1],size[2];start()
    for wnd in pairs(hud.windows) do
        local widgets=wnd.burer_ui
        assert(math.abs(widgets.left+widgets.width/2-512)<0.001)
        assert(widgets.width>0 and widgets.width<=440*math.min(1,(display_height/display_width)/(768/1024)),
            "Hint width must be measured and bounded in widescreen")
        assert(widgets.notice.size.x<=56 and widgets.notice.size.y==24)
    end
    key(DIK_keys.DIK_ESCAPE);stopped()
end
display_width,display_height=1920,1080

-- Telekinesis moves only loose, unfixed physical items through native
-- velocity/collisions; release throws, cancellation drops, never freezes.
local prop={kind="prop",pos=v(0,1.1,4),mass=5,fixed=false}
setmetatable(prop,object_meta)
function prop:id() return 200 end
function prop:section() return "loose_prop" end
function prop:parent() return self.owner end
local shell={}
function shell:apply_force(x,y,z) assert(x==0 and y==0.001 and z==0);self.awake=true end
function shell:get_elements_number() return 1 end
function shell:get_element_by_order(index)
    assert(index==0)
    return {is_fixed=function() return prop.fixed end,get_mass=function() return prop.mass end}
end
function shell:set_linear_vel(velocity) assert(self.awake,"Sleeping prop not woken before velocity update");self.velocity=copy(velocity) end
function prop:get_physics_shell() return shell end
function prop:position() return self.pos end
function prop:center(hud) assert(hud==false);return copy(self.pos) end
targets={prop};start();key(DIK_keys.MOUSE_2,key_bindings.kWPN_ZOOM);advance(1)
assert(attachments[NAME].motion=="telekinesis_0" and shell.velocity and shell.velocity.y>0)
advance(701);assert(attachments[NAME].motion=="telekinesis_1")
key(DIK_keys.DIK_V);advance(0)
assert(attachments[NAME].motion=="telekinesis_1" and shell.velocity.y~=nil,"V released held telekinesis")
key(DIK_keys.DIK_V);advance(0)
key(DIK_keys.MOUSE_2,key_bindings.kWPN_ZOOM,"on_before_key_release");advance(1)
assert(math.abs(shell.velocity.z-75)<0.001 and sound_events[#sound_events]:match("burer_tele_throw$"),"Default throw strength is not 150% / 75 m/s")
key(DIK_keys.DIK_ESCAPE);stopped()
for _,power in ipairs({10,50,100,200}) do
    p=params();p.tele_power_percent=tostring(power)
    assert(descriptor.execute({},p)=="executed");key(DIK_keys.MOUSE_2,99);advance(16)
    key(DIK_keys.MOUSE_2,99,"on_before_key_release");advance(16)
    assert(math.abs(shell.velocity.z-50*power/100)<0.001,"Configured telekinesis strength was ignored")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
start();key(DIK_keys.MOUSE_2,99);advance(1);callbacks.on_before_save_input();cancel_saved_form()
assert(shell.velocity.x==0 and shell.velocity.y==0 and shell.velocity.z==0,"Held item was not safely dropped for save")
for _,reason in ipairs({"fixed","heavy","parent","quest"}) do
    prop.fixed=reason=="fixed";prop.mass=reason=="heavy" and 1000 or 5;prop.owner=reason=="parent" and actor or nil
    sections.loose_prop.quest_item=reason=="quest" and "true" or nil
    shell.velocity=nil;start();key(DIK_keys.MOUSE_2,99);advance(1)
    assert(shell.velocity==nil,"Telekinesis moved protected item: "..reason)
    key(DIK_keys.DIK_ESCAPE);stopped()
end
sections.loose_prop.quest_item=nil;targets={}

-- Live NPCs/monsters may never be moved, captured, or converted to ragdolls.
for _,kind in ipairs({"npc","mutant"}) do
    local living=enemy(kind,4);targets={living};start();key(DIK_keys.MOUSE_2,99);advance(16)
    assert(active() and attachments[NAME].motion~="telekinesis_0" and living.pos.z==4)
    key(DIK_keys.DIK_ESCAPE);stopped()
end
-- Real corpse shells can contain >16 bones. Never detach inventory or
-- overwrite AI; all movement goes through native shell velocities only.
local function dead_body(kind)
    local object=enemy(kind,4);object.living=false;object.mass=100;object.elements=24
    local physics={}
    function physics:apply_force(x,y,z) assert(not self.stale and x==0 and y==0.001 and z==0);self.awake=true end
    function physics:get_elements_number() assert(not self.stale);return object.elements end
    function physics:get_element_by_order(index)
        assert(index>=0 and index<object.elements)
        return {is_fixed=function() return object.fixed==true end,get_mass=function() return object.mass/object.elements end}
    end
    function physics:set_linear_vel(velocity) assert(not self.stale and self.awake);self.velocity=copy(velocity) end
    function object:get_physics_shell() assert(not self.stale);return not self.no_shell and physics or nil end
    return object,physics
end
for _,kind in ipairs({"npc","mutant"}) do
    local corpse,physics=dead_body(kind);targets={corpse};start();key(DIK_keys.MOUSE_2,99);advance(16)
    assert(active() and attachments[NAME].motion=="telekinesis_0" and physics.velocity.y>0,"Corpse failed to lift: "..kind)
    assert(math.sqrt(physics.velocity.x^2+physics.velocity.y^2+physics.velocity.z^2)<=18.001)
    key(DIK_keys.MOUSE_2,99,"on_before_key_release");advance(16)
    assert(physics.velocity.z>49,"Corpse throw did not double momentum")
    key(DIK_keys.DIK_ESCAPE);stopped()
    for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit","actor_on_before_death"}) do
        start();key(DIK_keys.MOUSE_2,99);advance(16);callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end
        assert(physics.velocity.x==0 and physics.velocity.y==0 and physics.velocity.z==0,"Corpse not dropped on "..event)
    end
    for _,reason in ipairs({"fixed","no_shell","story","too_many_elements","heavy"}) do
        corpse.fixed=reason=="fixed";corpse.no_shell=reason=="no_shell";stories[corpse:id()]=reason=="story" and "quest_body" or nil
        corpse.elements=reason=="too_many_elements" and 129 or 24;corpse.mass=reason=="heavy" and 3001 or 100
        physics.velocity=nil;start();key(DIK_keys.MOUSE_2,99);advance(16)
        assert(active() and not physics.velocity,"Protected corpse was moved: "..reason)
        key(DIK_keys.DIK_ESCAPE);stopped()
    end
end
-- Native NPC/corpse collisions may be disabled. Own thrown bodies sweep the
-- actual travelled segment, ignore themselves, hit a real target bone once,
-- and stop on the FIRST solid obstacle. No global console setting is used.
for _,kind in ipairs({"npc","mutant"}) do
    local body,physics=dead_body(kind)
    local victim=enemy(kind=="npc" and "mutant" or "npc",7)
    targets={body,victim};start();key(DIK_keys.MOUSE_2,99);advance(16)
    key(DIK_keys.MOUSE_2,99,"on_before_key_release");advance(16)
    assert(physics.velocity.z>49 and #victim.hits==0,"Throw inflicted speculative damage before contact")
    body.pos.z=9;advance(100)
    local tele_damage=0.65*math.min(2,((9-4)/0.1)/50)*math.min(1,body.mass/50)
    assert(#victim.hits==1 and math.abs(victim.hits[1].power-tele_damage)<0.0001 and physics.velocity.z==0,
        "Corpse swept impact did not hit/stop or was scaled by the melee/gravi multiplier")
    advance(300);assert(#victim.hits==1,"Corpse impact repeatedly damaged the same target")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
for _,reason in ipairs({"wall","prop","slow","timeout","pause","jump","vertical","missing_bone","exit","load"}) do
    local body,physics=dead_body("mutant");local victim=enemy("npc",7)
    targets={body,victim};start();key(DIK_keys.MOUSE_2,99);advance(16)
    key(DIK_keys.MOUSE_2,99,"on_before_key_release");advance(16)
    if reason=="wall" then wall=6
    elseif reason=="prop" then
        local obstacle=enemy("prop",6);targets={body,obstacle,victim}
    elseif reason=="slow" then body.pos.z=4.1
    elseif reason=="timeout" then advance(3001)
    elseif reason=="pause" then paused=true;advance(6000);paused=false
    elseif reason=="missing_bone" then victim.no_bone=true
    elseif reason=="exit" then key(DIK_keys.DIK_ESCAPE);stopped()
    elseif reason=="load" then
        body.stale=true;physics.stale=true;attachments={};hud.windows={};custom=false;camera=2;memory={}
        for _,particle in ipairs(fx) do particle.stale=true;particle.active=false end
        descriptor.on_game_load();descriptor.update();stopped();fx={}
    end
    if reason=="slow" then advance(100) end
    if reason=="vertical" then body.pos.y=6;advance(100) end -- no zero horizontal-direction crash
    body.pos.z=reason=="jump" and 40 or 9;advance(100)
    assert(active()==(reason~="exit" and reason~="load"),"Projectile cleanup broke the form: "..reason)
    assert(#victim.hits==(reason=="pause" and 1 or 0),"Unsafe corpse hit: "..reason)
    wall=nil
    if active() then key(DIK_keys.DIK_ESCAPE);stopped() end
end
local corpse,physics=dead_body("mutant");corpse.pos=v(1.1,1.1,14);targets={corpse}
start();key(DIK_keys.MOUSE_2,99);advance(16);assert(physics.velocity,"Dead-target aim assistance was disabled")
paused=true;advance(6000);paused=false;advance(16);assert(attachments[NAME].motion=="telekinesis_0")
advance(5000);assert(physics.velocity.x==0 and physics.velocity.y==0 and physics.velocity.z==0)
key(DIK_keys.DIK_ESCAPE);stopped()
corpse.pos=v(0,1.1,4);start();key(DIK_keys.MOUSE_2,99);advance(16)
corpse.stale=true;physics.stale=true;attachments={};hud.windows={};custom=false;camera=2;targets={};memory={}
for _,particle in ipairs(fx) do particle.stale=true;particle.active=false end
descriptor.on_game_load();descriptor.update();stopped();fx={}
-- Rewrapped actor still refers to ID 0. Equality is not a safe lifetime test;
-- the load callback above is the world boundary, even when IDs are reused.
local actor_wrapper=setmetatable({},{__index=actor,__newindex=actor,__eq=object_meta.__eq})
db.actor=actor_wrapper;start();advance(16);assert(active())
key(DIK_keys.DIK_ESCAPE);stopped();db.actor=actor

local target=enemy("mutant",2);targets={target};body=start(nil,"100")
assert(key(DIK_keys.DIK_1,key_bindings.kWPN_1).ret_value==false);advance(1)
assert(body.motion=="stand_attack_0")
local attack_cycles,attack_camera=#body.motions,camera_sets
local attack_turn={ret_value=false};callbacks.on_before_mouse_move(8,0,false,attack_turn)
assert(key(DIK_keys.DIK_W,99).ret_value,"Burer crossfade blocked native movement")
advance(399);assert(#target.hits==0)
assert(attack_turn.ret_value and camera_sets==attack_camera+1 and #body.motions==attack_cycles,
    "Burer crossfade delayed the camera/restarted the strike")
advance(1)
assert(#target.hits==1 and target.hits[1].power==0.5)
assert(target.health==0.5,"Melee was only visual and did not change target HP")
assert(fx_created[#fx_created]=="anomaly2\\burer_wave" and sound_events[#sound_events]:match("burer_attack_0$"))
advance(30);assert(#target.hits==1,"Attack event fired repeatedly")
for wnd in pairs(hud.windows) do
    assert(not anomaly_donation_bridge_common.game_text_utf8(wnd.burer_ui.labels.melee.main.text):find(" с",1,true),"Melee cooldown timer reappeared")
end
advance(250)
for wnd in pairs(hud.windows) do
    local popup=wnd.burer_ui.notice.main
    assert(popup.shown and not popup.text:find("?",1,true),"Damage still contains an unsupported glyph")
    assert(popup.text=="-50" and popup.color==GetARGB(255,224,155,155),"Damage must be a pale-red negative number without percent/text")
    assert(wnd.burer_ui.notice.size.x<=56 and #wnd.burer_ui.notice.controls==9,"Damage outline is missing")
end
assert(key(DIK_keys.DIK_1,key_bindings.kWPN_1,"on_before_key_hold").ret_value==false)
advance(1000);assert(#target.hits==1,"Holding the key must not repeat attacks")
wall=1;key(DIK_keys.DIK_1,key_bindings.kWPN_1);advance(1);advance(401);assert(#target.hits==1,"Melee passed through a wall")
key(DIK_keys.DIK_ESCAPE);stopped();wall=nil

target=enemy("npc",10);targets={target};body=start(nil,"100")
key(DIK_keys.DIK_2,key_bindings.kWPN_2);advance(1);assert(body.motion=="stand_power_attack_0")
assert(fx_created[#fx_created]=="anomaly2\\burer_prepare" and sound_events[#sound_events]:match("burer_gravi_attack_0$"))
advance(801);assert(#target.hits==1 and target.hits[1].power==0.65,"Swept gravi hit failed at low frame rate")
assert(math.abs(target.health-0.35)<0.001,"Gravi wave was only visual")
advance(1000);assert(#target.hits==1)
key(DIK_keys.DIK_ESCAPE);stopped()
-- Responsive, non-homing projectile: 160ms release, 128m/s flight. A running
-- target has only a short travel-time window; keep the same charge/flight.
do
    target=enemy("mutant",10);targets={target};start(nil,"100")
    key(DIK_keys.DIK_2,key_bindings.kWPN_2);advance(1)
    advance(159);assert(#target.hits==0,"Wave fired before the charge")
    advance(1);assert(#target.hits==0,"Wave became an instant hitscan")
    for i=1,8 do
        target.pos.x=target.pos.x+7*0.016 -- running across the aim, no homing
        advance(16)
    end
    assert(#target.hits==1 and target.hits[1].power==0.65,"Fast wave missed the running target")
    key(DIK_keys.DIK_ESCAPE);stopped()
    target=enemy("mutant",10);targets={target};wall=6;start(nil,"100")
    key(DIK_keys.DIK_2,key_bindings.kWPN_2);advance(1);advance(160);advance(128)
    assert(#target.hits==1,"Solid cover stopped the supernatural wave")
    key(DIK_keys.DIK_ESCAPE);stopped();wall=nil
end
-- 80m is shared by aim selection and swept flight, not just a HUD limit.
-- Test both native target types, the old 25m boundary, range end and cover.
for _,kind in ipairs({"npc","mutant"}) do
    for _,distance in ipairs({24,25,26,50,79,80,81}) do
        target=enemy(kind,distance);targets={target};start(nil,"100")
        key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
        assert((#target.hits==1)==(distance<=80),"Wave range disagrees: "..kind.." "..distance)
        if #target.hits==1 then assert(target.hits[1].power==0.65 and target.hits[1].impulse==300) end
        key(DIK_keys.DIK_ESCAPE);stopped()
    end
    target=enemy(kind,70);targets={target};wall=45;start(nil,"100")
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert(#target.hits==1,"80m wave was blocked by solid cover")
    key(DIK_keys.DIK_ESCAPE);stopped();wall=nil
end
do
    local near,far=enemy("npc",40),enemy("mutant",70);targets={near,far};start(nil,"100")
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert(#near.hits==1 and #far.hits==0,"Wave became an area/piercing attack")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
-- Missing parameter (old saved variants) defaults to 200%, explicitly
-- configured strength is linear, and target immunities still stay native.
for _,strength in ipairs({false,10,50,100,200,350,9000}) do
    for _,ability in ipairs({"melee","wave"}) do
        target=enemy("mutant",ability=="melee" and 2 or 10);targets={target}
        local setting=strength and tostring(strength) or nil
        body=start(nil,setting)
        key(ability=="melee" and DIK_keys.DIK_F or DIK_keys.MOUSE_1,99)
        advance(1);advance(ability=="melee" and 401 or 801)
        local expected=(ability=="melee" and 0.5 or 0.65)*(strength or 200)/100
        assert(#target.hits==1 and math.abs(target.hits[1].power-expected)<0.00001,
            "Attack strength was ignored/doubled: "..ability.."/"..tostring(strength))
        assert(target.hits[1].impulse==(ability=="melee" and 100 or 300),"Damage multiplier changed native knockback")
        assert(math.abs(target.health-math.max(0,1-expected))<0.00001,"Configured attack was only visual")
        key(DIK_keys.DIK_ESCAPE);stopped()
    end
end
wall=4;target=enemy("mutant",10);targets={target};start()
key(DIK_keys.DIK_2,key_bindings.kWPN_2);advance(1);advance(500);assert(#target.hits==1,"Gravi hit stopped at cover")
key(DIK_keys.DIK_ESCAPE);stopped();wall=nil;targets={}

-- The wave has a real cross-section and uses each model's struck bone,
-- including targets slightly beside the centre line and behind cover.
target=enemy("mutant",8);target.pos.x=0.5;targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(801);assert(#target.hits==1 and target.health<1)
key(DIK_keys.DIK_ESCAPE);stopped()
-- Selection is a screen-space circle, not an unrelated fixed world radius.
-- Same 60/768 vertical screen radius, tested against native projection math.
for _,case in ipairs({{fov=67.5,x=1.1,hit=true},{fov=67.5,x=3.5,hit=false},
    {fov=30,x=1.1,hit=false},{fov=110,x=3.5,hit=true},{fov=67.5,x=1.1,wall=7,hit=true}}) do
    camera_fov=case.fov;wall=case.wall
    target=enemy("mutant",14);target.pos.x=case.x;targets={target};start()
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert((#target.hits==1)==case.hit,"Aim circle/FOV/occlusion disagrees: "..case.fov..","..case.x)
    key(DIK_keys.DIK_ESCAPE);stopped()
end
camera_fov=67.5;wall=nil
-- Regression from the video: the reticle contains a skeleton, but a fence,
-- pole or a gap between animation bones masks every collision ray. A gravi
-- area is independent of all of those; ordinary abilities retain collision.
do
    local regressions=0
    for _,camera_mode in ipairs({"side","center"}) do
        for _,obstacle in ipairs({"bone_gap","fence","prop","corpse","layers","loot_cloud"}) do
            local victim=enemy("npc",14);victim.ray_miss=true
            targets={victim};wall=obstacle=="fence" and 7 or nil
            if obstacle=="prop" then
                prop.pos=v(0,1.1,6);targets={prop,victim}
            elseif obstacle=="corpse" then
                local corpse=enemy("npc",6);corpse.living=false;targets={corpse,victim}
            elseif obstacle=="layers" then
                for i=1,80 do ray_layers[i]={z=i*.08,flags=4096} end
            elseif obstacle=="loot_cloud" then
                for i=1,200 do
                    local loot=enemy("prop",2+i*.02)
                    table.insert(targets,1,loot)
                end
            end
            local settings=params();settings.camera_mode=camera_mode;settings.attack_power_percent="100"
            assert(descriptor.execute({},settings)=="executed")
            -- Actual native camera/reticle placement, including side offset.
            victim.pos.x=level.camera_pos.x;victim.pos.y=level.camera_pos.y
            key(DIK_keys.MOUSE_1,99);advance(1);advance(160)
            assert(#victim.hits==0,"Area hit skipped wave travel time")
            for _=1,20 do advance(16) end
            assert(#victim.hits==1 and math.abs(victim.health-.35)<.001,
                "Reticle area failed: "..camera_mode.."/"..obstacle)
            advance(1000);assert(#victim.hits==1,"Area repeated the native hit")
            key(DIK_keys.DIK_ESCAPE);stopped();wall=nil;ray_layers={};regressions=regressions+1
        end
    end
    prop.pos=v(0,1.1,4)
    -- In both views, distance from the centre on screen is what matters,
    -- independent of the character's origin or display aspect ratio.
    for _,screen in ipairs({{1024,768},{1920,1080},{2560,1080}}) do
        display_width,display_height=screen[1],screen[2]
        for _,camera_mode in ipairs({"side","center"}) do
            for _,x in ipairs({1.1,4}) do
                local victim=enemy("mutant",14);targets={victim};victim.ray_miss=true
                local settings=params();settings.camera_mode=camera_mode
                assert(descriptor.execute({},settings)=="executed")
                victim.pos.x=level.camera_pos.x+x;victim.pos.y=level.camera_pos.y
                key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
                assert((#victim.hits==1)==(x==1.1),"Screen area drifted with aspect ratio/shoulder camera")
                key(DIK_keys.DIK_ESCAPE);stopped();regressions=regressions+1
            end
        end
    end
    display_width,display_height=1920,1080
    -- Head/torso overlap is enough; the model centre need not lie in ring.
    local victim=enemy("mutant",14);victim.pos.x=4;victim.ray_miss=true
    victim.bone_positions={[0]=v(4,1.1,14),[7]=v(1,1.35,14)};targets={victim};start(nil,"100")
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert(#victim.hits==1,"Visible body part in reticle lost to centre-only check")
    key(DIK_keys.DIK_ESCAPE);stopped();regressions=regressions+1
    -- A released cast does not chase a target outside the original area,
    -- even when the player turns the camera to follow it during flight.
    victim=enemy("mutant",30);targets={victim};start()
    key(DIK_keys.MOUSE_1,99);advance(1);advance(160);victim.pos.x=25
    callbacks.on_before_mouse_move(150,0,false,{ret_value=true});advance(1001)
    assert(#victim.hits==0,"Released wave homed outside original reticle")
    key(DIK_keys.DIK_ESCAPE);stopped();initial_dir=v(0,0,1);regressions=regressions+1
    -- Candidate order must not select a farther victim or hit a second one.
    local near,far=enemy("npc",14),enemy("mutant",25);near.ray_miss=true;far.ray_miss=true
    targets={far,near};wall=7;start(nil,"100")
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert(#near.hits==1 and #far.hits==0,"Wave did not stop at first live target")
    key(DIK_keys.DIK_ESCAPE);stopped();wall=nil;regressions=regressions+1
    -- Body bone comes from the actual model/config. A helper next to the
    -- centre has zero hit scaling, and neither it nor a guessed root is used.
    sections.test_npc={damage="test_gravi_damage",bone_torso="modded_collision_bone"}
    sections.test_gravi_damage={default="1, -1, 1",helper="0, -1, 0",modded_collision_bone="1, -1, 1"}
    victim=enemy("npc",14);victim.ray_miss=true;victim.bones={[0]="zero_damage_root",[1]="helper",[7]="modded_collision_bone"}
    victim.bone_positions={[0]=copy(victim.pos),[1]=copy(victim.pos),[7]=v(0,1.35,14)}
    targets={victim};start(nil,"100");key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert(#victim.hits==1 and victim.hits[1].bone=="modded_collision_bone","Wrong bone for gravi area")
    key(DIK_keys.DIK_ESCAPE);stopped();sections.test_npc=nil;sections.test_gravi_damage=nil;regressions=regressions+1
    victim=enemy("npc",14);victim.immune=true;targets={victim};start()
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001);advance(101)
    assert(#victim.hits==1 and victim.health==1,"Cover penetration bypassed native immunity")
    for wnd in pairs(hud.windows) do assert(wnd.burer_ui.notice.main.text=="0","Immune hit lost native zero feedback") end
    key(DIK_keys.DIK_ESCAPE);stopped();regressions=regressions+1
    victim=enemy("npc",14);victim.bones={[0]="zero_damage_root"};targets={victim};start()
    key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
    assert(#victim.hits==0 and active(),"Broken target skeleton crashed form or used fake root")
    key(DIK_keys.DIK_ESCAPE);stopped();targets={};regressions=regressions+1
    print("Burer gravi regression: "..regressions.." cases passed (mock API, not an in-game test).")
end
-- Native Passable materials must not mask aim, melee, or the swept wave.
-- ActorObstacle wins, unknown flags stay solid, and traversal is bounded.
for _,ability in ipairs({"melee","wave"}) do
    for _,case in ipairs({
        {flags=128,layers=6,hit=true},{flags=128+4,layers=6,hit=true},
        {flags=128+4096,layers=1,hit=false},{flags=0,layers=1,hit=false},
        {layers=1,hit=false},{flags=128,layers=6,wall=1,hit=false},
        {flags=128,layers=24,hit=true},{flags=128,layers=65,hit=false},{flags=128,layers=6,beyond=true,hit=false}
    }) do
        target=enemy("mutant",ability=="melee" and 2 or 10);targets={target};wall=case.wall
        if case.layers==65 and ability=="melee" then target.pos.z=2.65 end
        if case.beyond then target.pos.z=ability=="melee" and 3.5 or 81 end
        for i=1,case.layers do ray_layers[i]={z=i*0.04,flags=case.flags} end
        start();key(ability=="melee" and DIK_keys.DIK_F or DIK_keys.MOUSE_1,99)
        advance(1);advance(ability=="melee" and 401 or 1001)
        local expected=ability=="wave" and not case.beyond or ability=="melee" and case.hit
        assert((#target.hits==1)==expected,"Foliage/solid boundary failed: "..ability.."/"..tostring(case.flags).."/"..case.layers)
        key(DIK_keys.DIK_ESCAPE);stopped();wall=nil;ray_layers={}
    end
end
targets={};start();local clear_camera_z=level.camera_pos.z;key(DIK_keys.DIK_ESCAPE);stopped()
ray_layers={{z=-1,flags=128}};start()
assert(math.abs(level.camera_pos.z-clear_camera_z)<0.001,"Passable foliage pushed the camera in")
key(DIK_keys.DIK_ESCAPE);stopped()
ray_layers={{z=-1,flags=128+4096}};start()
assert(level.camera_pos.z>clear_camera_z+0.5,"Camera crossed ActorObstacle")
key(DIK_keys.DIK_ESCAPE);stopped();ray_layers={}
-- Free corpse/prop telekinesis uses the same material-aware targeting path.
prop.fixed=false;prop.mass=5;prop.owner=nil;prop.pos=v(0,1.1,4);targets={prop}
for _,flags in ipairs({128,128+4096}) do
    shell.velocity=nil;ray_layers={{z=1,flags=flags}}
    start();key(DIK_keys.MOUSE_2,99);advance(1)
    assert((shell.velocity~=nil)==(flags==128),"Telekinesis foliage/ActorObstacle regression")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
ray_layers={}
target=enemy("mutant",-2);target.pos.y=2.25;targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(1001)
assert(#target.hits==0,"Aim assist turned an attack backwards behind the actor")
key(DIK_keys.DIK_ESCAPE);stopped()
prop.fixed=false;prop.mass=5;prop.owner=nil;prop.pos=v(1.1,1.1,14);shell.velocity=nil;targets={prop}
start();key(DIK_keys.MOUSE_2,99);advance(1)
assert(shell.velocity~=nil and attachments[NAME].motion=="telekinesis_0","Telekinesis did not select the free prop inside aim circle")
key(DIK_keys.DIK_ESCAPE);stopped();prop.pos=v(0,1.1,4)
target=enemy("npc",2);target.immune=true;targets={target};start()
key(DIK_keys.DIK_F,99);advance(1);advance(401);advance(251)
assert(#target.hits==1 and target.health==1,"Native immunity was bypassed")
for wnd in pairs(hud.windows) do assert(wnd.burer_ui.notice.main.text=="0","Zero damage feedback was lost") end
key(DIK_keys.DIK_ESCAPE);stopped()
target=enemy("mutant",2);target.no_bone=true;targets={target};start()
key(DIK_keys.DIK_F,99);advance(1);advance(401)
assert(#target.hits==0,"Missing collision bone silently fell back to an arbitrary root")
for wnd in pairs(hud.windows) do assert(anomaly_donation_bridge_common.game_text_utf8(wnd.burer_ui.notice.main.text)=="Не получилось","Verbose failure notice returned") end
key(DIK_keys.DIK_ESCAPE);stopped();targets={}

body=start();key(DIK_keys.DIK_3,key_bindings.kWPN_3);advance(1);assert(body.motion=="stand_shield_0")
for wnd in pairs(hud.windows) do assert(anomaly_donation_bridge_common.game_text_utf8(wnd.burer_ui.labels.shield.main.text):find("8 с",1,true),"Shield timer missing") end
assert(fx_created[#fx_created]=="anomaly2\\burer_shield_01" and sound_events[#sound_events]:match("burer_tele_hold$"))
local attacker=enemy("npc",8)
local flags={ret_value=true};callbacks.actor_on_before_hit({power=0.3,type=hit.fire_wound,draftsman=attacker},0,flags)
assert(flags.ret_value==false)
advance(0);assert(form_hp()==100,"Shield spent form HP on a blocked projectile")
local radiation_hit=actor_hit(0.15,hit.radiation,attacker);advance(0)
assert(form_hp()==100 and radiation_hit.power==0.15,"Radiation exposure was confused with instant HP loss")
actor_hit(0.3,hit.fire_wound,actor);advance(0);assert(form_hp()==90,"Self-inflicted damage was incorrectly shielded")
advance(800);assert(body.motion=="stand_shield_idle_0")
advance(2201);actor_hit(0.3,hit.fire_wound,attacker);advance(0)
assert(form_hp()==90,"Shield still ended after only 3 seconds")
advance(4998);actor_hit(0.3,hit.fire_wound,attacker);advance(0)
assert(form_hp()==90,"Shield did not protect through the eighth second")
advance(1);actor_hit(0.3,hit.fire_wound,attacker);advance(0)
assert(form_hp()==80 and actor.health==0.77,"Expired shield must spend form HP, not hero HP")
for _,p in ipairs(fx) do assert(not p.active,"Shield visual outlived protection") end
callbacks.on_before_save_input({ret=false});cancel_saved_form()
flags={ret_value=true};callbacks.actor_on_before_hit({type=hit.fire_wound,draftsman=attacker},0,flags);assert(flags.ret_value)

start();key(DIK_keys.DIK_3,key_bindings.kWPN_3);advance(1)
paused=true;advance(5000);paused=false;advance(1)
flags={ret_value=true};callbacks.actor_on_before_hit({type=hit.fire_wound,draftsman=attacker},0,flags);assert(not flags.ret_value,"Pause consumed the shield timer")
callbacks.on_before_save_input();cancel_saved_form()

start("1");paused=true;advance(5000);assert(active());paused=false;advance(999);assert(active());advance(1);stopped()
-- A full independent pool, NOT current actor HP or native incoming scaling.
for _,health in ipairs({"10","100","300","9000","99999"}) do
    p=params();p.health_percent=health;assert(descriptor.execute({},p)=="executed")
    assert(form_hp()==100,"Wounded hero did not enter a full-health form")
    local power=tonumber(health)/100/4
    local incoming,hit_flags=actor_hit(power,hit.fire_wound,enemy("npc",4))
    advance(1)
    assert(hit_flags.ret_value and incoming.power==0 and form_hp()==75)
    assert(actor.health==0.77 and actor.hp==0.77,"Form damage leaked to the rider")
    actor_hit(power,hit.fire_wound,nil,{ret_value=false});advance(1)
    assert(form_hp()==75,"Another addon's cancelled hit was charged to form HP")
    key(DIK_keys.DIK_ESCAPE);stopped()
    incoming={power=power,type=hit.fire_wound};flags={ret_value=true};callbacks.actor_on_before_hit(incoming,0,flags)
    CBeforeHitAfterCalcs(incoming,actor,0)
    assert(flags.ret_value and incoming.power==power,"Form protection leaked after exit")
end
start();actor_hit(0.3,hit.strike);advance(1)
assert(form_hp()==90 and actor.health==0.77,"Default capacity is not a separate 300% pool")
callbacks.on_before_save_input();cancel_saved_form()

-- Final native damage, not raw power: a boar-like raw 0.9 hit whose native
-- protections leave 0.3 loses 10% of 300%, NOT the regressed 30%.
protection.immunity=1/3
start();local native_producer=CBeforeHitAfterCalcs
local raw={power=0.9,type=hit.strike};flags={ret_value=true}
callbacks.actor_on_before_hit(raw,0,flags)
assert(flags.ret_value and raw.power==0.9,"Raw hit must reach native calculations unchanged")
advance(1);assert(form_hp()==100,"Raw callback still charged the HP pool")
actor_hit(0.9,hit.strike,enemy("mutant",4));advance(1)
assert(form_hp()==90 and actor.health==0.77,"Native immunity was lost in boar-like damage")
callbacks.actor_on_update();advance(1);assert(form_hp()==90,"Calculated damage was charged twice by actor HP guard")
assert(CBeforeHitAfterCalcs==native_producer,"Template replaced the global native callback producer")
key(DIK_keys.DIK_ESCAPE);stopped()
local outside={power=0.9,type=hit.strike};CBeforeHitAfterCalcs(outside,actor,0)
assert(outside.power==0.9,"Calculated actor hits outside the form were changed")

-- Nonlinear outfit protection, belt, immunity boosters, health_hit_part
-- and the bone coefficient are accounted for ONCE, by the native pipeline.
protection.artefact=0.8;protection.outfit=0.1;protection.immunity=0.7
protection.boost=0.2;protection.health_part=0.5;protection.bone=2
local normalized=(1*0.8-0.1)*(0.7-0.2)*0.5*2
for _,capacity in ipairs({100,300,900,9000}) do
    p=params();p.health_percent=tostring(capacity);assert(descriptor.execute({},p)=="executed")
    actor_hit(1,hit.fire_wound);advance(1)
    local expected=math.floor((1-normalized/(capacity/100))*100+0.5)
    assert(form_hp()==expected and actor.health==0.77,"Native protection/capacity was applied incorrectly: "..capacity)
    local npc_hit={power=0.5,type=hit.fire_wound}
    CBeforeHitAfterCalcs(npc_hit,enemy("npc",4),7)
    assert(npc_hit.power==0.5,"Form changed damage received by another NPC")
    local wrong_visual={id=function() return actor:id() end,get_visual_name=function() return "other_actor" end}
    CBeforeHitAfterCalcs(npc_hit,wrong_visual,7)
    assert(npc_hit.power==0.5,"Form crossed the actor-visual identity guard")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
protection.artefact=1;protection.outfit=0;protection.immunity=0
protection.boost=0;protection.health_part=1;protection.bone=1
start();actor_hit(100);advance(1);assert(form_hp()==100,"A fully immune hit cost form HP")
key(DIK_keys.DIK_ESCAPE);stopped();protection.immunity=1
start()
for _,power in ipairs({-1,0,0/0}) do
    local invalid={power=power,type=hit.strike};CBeforeHitAfterCalcs(invalid,actor,0)
    assert(invalid.power==0,"Invalid/negative hit was passed into native rider damage")
end
advance(1);assert(form_hp()==100 and actor.health==0.77,"Invalid/negative hit healed or damaged the form")
key(DIK_keys.DIK_ESCAPE);stopped()

-- A lethal form hit ends only the form, including its final overkill hit.
actor.health=0.42;p=params();p.health_percent="100";assert(descriptor.execute({},p)=="executed")
actor_hit(0.2);advance(1);assert(form_hp()==80 and actor.health==0.42)
actor_hit(20);actor_hit(20);assert(actor.health==0.42 and actor:alive());advance(1);stopped()
assert(actor.health==0.42,"Destroyed form failed to restore exact entry HP")
start();busy=true;actor_hit(0.3);busy=false;advance(1)
assert(actor.health==0.42 and form_hp()==90,"Busy UI leaked a hit into rider HP")
key(DIK_keys.DIK_ESCAPE);stopped()
-- Scripted/native metabolic loss and passive regeneration are separate too.
start();actor.health=0.12;callbacks.actor_on_update();advance(1)
assert(actor.health==0.42 and form_hp()==90,"Direct health loss was not redirected to the form")
actor.health=0.9;callbacks.actor_on_update();advance(1)
assert(actor.health==0.42 and form_hp()==90,"Native regeneration healed the form/rider snapshot")
key(DIK_keys.DIK_ESCAPE);stopped();assert(actor.health==0.42)
start("1");actor_hit(0.6);advance(1000);stopped();assert(actor.health==0.42,"Timed exit changed entry HP")
start();actor.health=-0.1;local dying={ret_value=true};callbacks.actor_on_before_death(123,dying)
assert(not dying.ret_value and actor.health==0.42 and actor:alive(),"Native condition death killed the rider")
advance(1);assert(active() and form_hp()==83,"Native condition death did not charge its actual HP loss")
actor.health=-20;dying={ret_value=true};callbacks.actor_on_before_death(123,dying)
assert(not dying.ret_value and actor.health==0.42);advance(1);stopped()
start();dying={ret_value=true};callbacks.actor_on_before_death(123,dying)
advance(1);assert(not dying.ret_value and not active() and actor.health==0.42,"Explicit native kill did not consume the stand-in form")
-- Never restore an old actor snapshot into an unrelated F9-loaded world.
start();actor.health=0.29;attachments={};hud.windows={};custom=false;camera=2;memory={}
for _,particle in ipairs(fx) do particle.stale=true;particle.active=false end
descriptor.on_game_load();descriptor.update();stopped();fx={}
assert(actor.health==0.29,"Stale form overwrote the loaded save's health")
actor.health=0.77
start();callbacks.on_before_load_input();stopped();descriptor.on_game_load();descriptor.update();stopped()
start();local sound_before=#sound_events
flags={ret_value=true};callbacks.actor_on_footstep("earth",0.7,false,flags)
assert(not flags.ret_value and #sound_events==sound_before+1 and sound_events[#sound_events]:match("earth4$"))
paused=true;flags={ret_value=true};callbacks.actor_on_footstep("earth",1,false,flags)
assert(flags.ret_value and #sound_events==sound_before+1);paused=false
callbacks.actor_on_hit_callback();assert(sound_events[#sound_events]:match("burer_telekinetic_0$"))
advance(8001);assert(sound_events[#sound_events]:match("burer_idle_0$"))
key(DIK_keys.DIK_ESCAPE);stopped()
start();local data={};callbacks.save_state(data);cancel_saved_form();assert(data[SAVE].camera==2 and data[SAVE].camera_owned)
data[SAVE].form_state=nil;memory={[SAVE]=data[SAVE]};actor.health=0.22;camera=0;custom=true;descriptor.on_game_load();descriptor.update();stopped()
assert(memory[SAVE]==nil and actor.health==0.77,"Console save recovery failed to restore original rider HP")
memory={[SAVE]={version=1,camera=2,camera_owned=true}};actor.health=0.35
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.35,"Legacy camera-only save fabricated HP")
for _,invalid in ipairs({0,-1,"0.77",0/0}) do
    memory={[SAVE]={version=2,actor_health=invalid,actor_visual=actor.visual}}
    descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.35,"Malformed recovery wrote actor HP")
end
memory={[SAVE]={version=2,actor_health=0.77,actor_visual="different_player"}}
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.35,"Recovery crossed actor identity")
actor.health=0.77
for _,event in ipairs({"GUI_on_show","main_menu_on_quit","actor_on_before_death"}) do start();callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end end
start();descriptor.on_level_changing();stopped()
start();actor.living=false;advance(1);stopped();actor.living=true
-- Service autosaves suspend BEFORE native ClientSave, and resume only
-- after save_state + return + the matching file exist. F9 cancels old RAM state.
local function suspended_clean()
    assert(active() and not attachments[NAME] and not custom and camera==2)
    assert(next(hud.windows)==nil,"Save still contains custom render UI")
end
start("60");actor_hit(0.6);advance(1000);key(DIK_keys.DIK_R,99);advance(1);advance(2000)
descriptor.on_effect_save("before","service-before");suspended_clean()
local save_hit={type=hit.strike,power=0.3,draftsman=enemy("mutant",4)};local save_flags={ret_value=true}
callbacks.actor_on_before_hit(save_hit,0,save_flags);assert(not save_flags.ret_value,"Service save disabled active shield")
local save_data={};callbacks.save_state(save_data)
assert(save_data[SAVE].version==2 and save_data[SAVE].actor_health==0.77 and
    save_data[SAVE].camera_owned==false and save_data[SAVE].form_health==nil and save_data[SAVE].params==nil,
    "Service save must store only rider HP recovery, never resumable monster state")
descriptor.on_effect_save("after","service-before",true);advance(100);suspended_clean()
service_files["service-before.scop"]=true;advance(16)
assert(active() and attachments[NAME] and custom and camera==0,"Form failed to resume after confirmed service save")
assert(form_hp()==80,"Service save refilled the independent form pool")
for wnd in pairs(hud.windows) do
    local text=anomaly_donation_bridge_common.game_text_utf8(wnd.burer_ui.labels.shield.main.text)
    assert(text:find("6 с",1,true),"Service save reset or consumed shield timer")
    assert(wnd.burer_ui.labels.footer.main.text:find("57",1,true),"Service save reset form duration")
end
key(DIK_keys.DIK_ESCAPE);stopped()
targets={prop};start();key(DIK_keys.MOUSE_2,99);advance(16)
descriptor.on_effect_save("before","held-service");suspended_clean()
assert(shell.velocity.x==0 and shell.velocity.y==0 and shell.velocity.z==0,"Service save did not drop held physics")
local scaled,scaled_flags=actor_hit(0.3,hit.strike)
assert(scaled_flags.ret_value and scaled.power==0 and actor.health==0.77,"Suspension lost calculated HP protection")
callbacks.save_state({});service_files["held-service.scop"]=true;descriptor.on_effect_save("after","held-service",true);advance(16)
assert(active() and attachments[NAME].motion=="stand_idle_0" and shell.velocity.z==0,"Save replayed a hold/throw")
assert(form_hp()==90,"Service suspension lost damage accumulated by the independent pool")
key(DIK_keys.DIK_ESCAPE);stopped();targets={}
-- The selected attack multiplier must survive a service-save suspension.
target=enemy("mutant",2);targets={target};start(nil,"350")
descriptor.on_effect_save("before","attack-strength-service");suspended_clean()
callbacks.save_state({});service_files["attack-strength-service.scop"]=true
descriptor.on_effect_save("after","attack-strength-service",true);advance(16)
key(DIK_keys.DIK_F,99);advance(1);advance(401)
assert(#target.hits==1 and math.abs(target.hits[1].power-1.75)<0.0001,"Service save reset the configured attack strength")
key(DIK_keys.DIK_ESCAPE);stopped();targets={}
-- Depleted suspension must cancel its pending resume, not create a fresh pool.
start();descriptor.on_effect_save("before","destroyed-service");suspended_clean()
actor_hit(50);callbacks.save_state({});service_files["destroyed-service.scop"]=true
descriptor.on_effect_save("after","destroyed-service",true);advance(16);stopped()
assert(actor.health==0.77,"Destroyed suspended form leaked overkill into actor HP")
-- Two saves can be queued in one batch before either native serialization.
start();descriptor.on_effect_save("before","batch-before");descriptor.on_effect_save("after","batch-before",true)
descriptor.on_effect_save("before","batch-after");descriptor.on_effect_save("after","batch-after",true)
callbacks.save_state({});service_files["batch-before.scop"]=true;advance(16);suspended_clean()
callbacks.save_state({});service_files["batch-after.scop"]=true;advance(16)
assert(attachments[NAME] and active());key(DIK_keys.DIK_ESCAPE);stopped()
-- A late/console load must discard all in-memory resume state, including
-- on fresh worlds which reuse actor ID 0. No form data goes into the save.
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit","actor_on_before_death"}) do
    start();descriptor.on_effect_save("before","cancel-"..event);suspended_clean()
    callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end;callbacks.save_state({})
    service_files["cancel-"..event..".scop"]=true;descriptor.on_effect_save("after","cancel-"..event,true)
    advance(16);stopped()
end
start();descriptor.on_effect_save("before","load-service");suspended_clean()
descriptor.on_game_load();descriptor.update();stopped()
start();descriptor.on_effect_save("before","failed-service");descriptor.on_effect_save("after","failed-service",false);stopped()
start();descriptor.on_effect_save("before","no-save-event");descriptor.on_effect_save("after","no-save-event",true)
advance(10001);stopped()
start();now=now+1600;descriptor.update();stopped()
start();local foreign={userdata={owner="other_mod",lease={}}};attachments[NAME]=foreign
advance(1);assert(not active() and attachments[NAME]==foreign,"Cleanup removed a replacement owned by another mod")
attachments={};stopped()

-- A console load can bypass on_before_load_input. Old HUD/model userdata is
-- already destroyed; do not dereference it or overwrite a before-save camera.
local old=start();key(DIK_keys.DIK_2,key_bindings.kWPN_2);advance(1)
old.stale=true;for wnd in pairs(hud.windows) do wnd.stale=true end
for _,p in ipairs(fx) do p.stale=true;p.active=false end -- native destroys particle visuals on load
attachments={};hud.windows={};custom=false;camera=2;memory={}
descriptor.on_game_load();descriptor.update();stopped()
fx={}
fail_setup=true;local context={};assert(descriptor.can_execute(context,params()))
assert(descriptor.execute(context,params())=="rejected");stopped()

-- A changed source section is revalidated immediately before side effects.
assert(descriptor.can_execute(context,params()));sections.arbitrary_boss_x.class="SM_DOG_S"
assert(descriptor.execute(context,params())=="rejected");stopped();sections.arbitrary_boss_x.class="SM_BURER"

-- Fail-closed VFX lookup: no renderer calls on malformed/missing libraries.
for _,failure in ipairs({"read","copy"}) do
    particle_file=particle_data;probe_read_error=failure=="read";probe_copy_error=failure=="copy"
    module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_burer_form");module.on_game_start()
    local created=#fx_created
    start();key(DIK_keys.DIK_R,99);advance(1)
    assert(#fx_created==created and next(copied_files)==nil,"Failed probe leaked a copy or called renderer")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
probe_read_error=false;probe_copy_error=false
particle_file=chunk(3,u32(0)..u32(999999));missing_particle="anomaly2\\burer_wave";missing_sounds=true
module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_burer_form");module.on_game_start()
start();key(DIK_keys.DIK_2,key_bindings.kWPN_2);advance(1);advance(500)
flags={ret_value=true};callbacks.actor_on_footstep("earth",1,false,flags)
assert(not flags.ret_value,"Missing replacement must not restore human boots")
key(DIK_keys.DIK_ESCAPE);stopped()
if arg[4] then
    local file=assert(io.open(arg[4],"rb"));particle_file=file:read("*a");file:close()
    missing_sounds=false;missing_particle=nil
    module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_burer_form");module.on_game_start()
    local count=#fx_created;start();key(DIK_keys.DIK_R,99);advance(1)
    assert(#fx_created>count,"Real base particle library preflight did not find burer shield")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
assert(copy_count>0 and delete_count==copy_count and next(copied_files)==nil,"VFS probe did not clean up its owned temporary copies")
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
reject(params("60001"),"burer_duration_invalid")

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
    for wnd in pairs(hud.windows) do view=wnd.burer_ui end
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
    p=params();p.camera_mode="invalid";reject(p,"burer_camera_mode_invalid")
    p=params();p.camera_key="MOUSE_1";reject(p,"burer_ability_keys_duplicate")
end
-- Listener-relative native-grounded mutant steps, never human boots.
do
    targets={};missing_sounds=nil;start()
    local count=#sound_events;local flags={ret_value=true}
    callbacks.actor_on_footstep("earth",0.1,false,flags)
    assert(not flags.ret_value and #sound_events==count+1)
    local step=sounds[#sounds]
    assert(step.flags==sound_object.s2d and step.volume>=0.45)
    assert(step.name:find("material\\medium\\step_paw_earth",1,true)==1)
    key(DIK_keys.DIK_V);advance(16);flags={ret_value=true}
    callbacks.actor_on_footstep("earth",1,false,flags);assert(not flags.ret_value and sounds[#sounds].flags==sound_object.s2d)
    key(DIK_keys.DIK_ESCAPE);stopped();flags={ret_value=true};count=#sound_events
    callbacks.actor_on_footstep("earth",1,false,flags);assert(flags.ret_value and #sound_events==count)
    missing_sounds=true;start();flags={ret_value=true}
    callbacks.actor_on_footstep("earth",1,false,flags);assert(not flags.ret_value,"Human boots leaked when asset missing")
    key(DIK_keys.DIK_ESCAPE);stopped();missing_sounds=nil
end
-- New session-gated F5/F9 behavior, through the actual module callbacks.
assert(loadfile(core.."/../../../tests/lua-fixtures/form_save_roundtrip.lua"))()({
    name="burer",key=SAVE,actor=actor,callbacks=callbacks,
    start=function() start("20") end,stopped=stopped,advance=advance,active=active,
    live=function() return custom end,descriptor=function() return descriptor end,
    cancel=function() callbacks.on_before_load_input() end,
    set_memory=function(value) memory=value end,set_paused=function(value) paused=value end,
    elapse_silent=function(ms) now=now+ms end,
    file_exists=function(name) service_files[name..".scop"]=true end,
    reload_common=function() anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common") end
})

print("Burer smoke passed: screen-space gravi area through all cover, 80m boundary, 160ms release/128m/s swept flight, native bone/damage, unchanged melee/shield/telekinesis, crossfade, camera, throw/save isolation, protected HP and HUD.")
