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
local visual_center_y
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
local particle_data=chunk(1,string.char(1,0))..chunk(3,chunk(0,chunk(2,"monsters\\gigant_wave\0")))
particle_file=particle_data
local NAME="adb_pseudogiant_form_visual_v1"
local SAVE="adb_pseudogiant_form_recovery_v1"
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
actor.set_actor_position=forbidden;actor.invulnerable=forbidden
local velocity_writes=0
function actor:set_movement_speed(speed)
    assert(speed.x==0 and speed.z==0 and speed.y==self.velocity.y,"Stomp changed gravity or added motion")
    velocity_writes=velocity_writes+1;self.velocity=copy(speed)
end
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
local function expected_movement()
    local old_sprint=move_defaults.walk_accel*move_defaults.run_coef*move_defaults.sprint_koef
    assert(math.abs(move_values.walk_accel-old_sprint)<0.00001,"Walk does not match entry sprint")
    assert(move_values.run_coef==1 and move_values.sprint_koef==1,"Run/sprint compounded")
    assert(math.abs(move_values.walk_accel*move_values.run_coef*move_values.sprint_koef-old_sprint)<0.00001)
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
local sections={arbitrary_boss_x={class="SM_GIANT",visual="custom\\independent_pseudogiant"},
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
            return not missing_sounds and (path:match("_0%.ogg$") or path:match("_hold%.ogg$") or path:match("_throw%.ogg$") or path:match("earth4%.ogg$") or path:match("large_step[12]%.ogg$") or path=="device\\door_stop")~=nil
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
        assert(path:find("private\\adb_pseudogiant_particle_probe_",1,true)==1,"Deleted non-owned path")
        copied_files[path]=nil;delete_count=delete_count+1
    end,
    r_open=function() error("Unsafe native IReader must not be acquired") end,
    r_close=function() error("Reproduces native IReader*& C++ exception") end
} end
particles_object=function(name)
    assert(name~=missing_particle,"Missing particle constructor MUST NOT be probed")
    assert(name=="monsters\\gigant_wave")
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
    local object={kind=kind,pos=v(0,1.4,z),living=true,hits={},health=1,object_id=enemy_id}
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
    DIK_G=6,DIK_H=7,DIK_J=8,DIK_K=9,DIK_W=17,DIK_F5=63,DIK_F9=67}
key_bindings={kWPN_1=20,kWPN_2=21,kWPN_3=22,kWPN_FIRE=30,kUSE=31,kCAM_1=32,kINVENTORY=33,
    kACTIVE_JOBS=34,kCUSTOM20=35,kNEXT_SLOT=36,kPREV_SLOT=37,
    kFWD=40,kBACK=41,kL_STRAFE=42,kR_STRAFE=43,kJUMP=44,kCROUCH=45,
    kACCEL=46,kSPRINT_TOGGLE=47,kL_LOOKOUT=48,kR_LOOKOUT=49}
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
        assert(name==NAME and model=="custom\\independent_pseudogiant" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==true and speed==1,"Pseudogiant pose crossfade must not change playback speed")
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
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.pseudogiant_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_pseudogiant_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.pseudogiant_form"])
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
    movement_original()
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
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.pseudogiant_ui.hp.main.text):match("(%d+)%% HP"))
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

-- Independent template metadata/capability guards.
local p=params();p.catalog_revision="stale";reject(p,"catalog_revision_mismatch")
p=params();p.mutant_sections="a_dog";reject(p,"pseudogiant_visual_class_unsupported")
for _,keyname in ipairs({"W","F5","F9","ESCAPE"}) do
    p=params();p.melee_key=keyname;reject(p,"pseudogiant_ability_key_invalid")
end
p=params();p.melee_key="MOUSE_2";reject(p,"pseudogiant_ability_keys_duplicate")
for _,value in ipairs({"0","9","100000","300.5","NaN"}) do
    p=params();p.health_percent=value;reject(p,"pseudogiant_health_percent_invalid")
end
for _,value in ipairs({"0","9","9001","200.5","NaN"}) do
    p=params();p.attack_power_percent=value;reject(p,"pseudogiant_attack_power_invalid")
end
missing="stand_kick_0";reject(params(),"pseudogiant_animation_missing_stand_kick_0");missing=nil
local producer=CBeforeHitAfterCalcs;CBeforeHitAfterCalcs=nil
assert(not descriptor.is_supported());reject(params(),"pseudogiant_calculated_damage_api_unavailable")
CBeforeHitAfterCalcs=producer
local movement_setter=actor.set_movement_speed;actor.set_movement_speed=nil
assert(descriptor.is_supported(),"API metadata probe depends on a live actor")
reject(params(),"pseudogiant_movement_api_unavailable");actor.set_movement_speed=movement_setter

local body=start();advance(16)
assert(body.motion=="stand_idle_0" and form_hp()==100)
for wnd in pairs(hud.windows) do
    local ui=wnd.pseudogiant_ui
    assert(ui.keys.melee.main.text==anomaly_donation_bridge_common.game_text_cp1251_lossy("ЛКМ"))
    assert(ui.keys.stomp.main.text==anomaly_donation_bridge_common.game_text_cp1251_lossy("ПКМ"))
    assert(ui.labels.wave==nil and ui.labels.tele==nil and ui.labels.shield==nil)
    assert(math.abs(ui.left+ui.width/2-512)<0.01,"Hint description column is off-centre")
    assert(ui.labels.melee.color==nil and ui.labels.melee.main.color==GetARGB(255,180,185,180))
    assert(ui.ring.texture=="adb_burer_aim_ring")
end
assert(level.camera_pos.y>2.5,"Large model blocks the reticle")
actor.velocity=v(0,0,1);advance(16);assert(body.motion=="stand_walk_fwd_0")
local mouse={ret_value=false};callbacks.on_before_mouse_move(100,0,false,mouse);advance(16)
assert(mouse.ret_value==true and math.abs(math.sin(actor.angles[1])-math.sin(0.25))<0.001,"Mouse direction is inverted")
for _,bind in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20,key_bindings.kNEXT_SLOT}) do
    assert(not key(100,bind).ret_value)
end
key(DIK_keys.DIK_ESCAPE);stopped();actor.velocity=v()
assert(key(100,key_bindings.kACTIVE_JOBS).ret_value)
initial_dir=v(0,0,1)
visual_center_y=3.5;start();advance(16)
assert(level.camera_pos.y>=7.5 and -level.camera_pos.z>12,"Camera did not accommodate a larger visual")
key(DIK_keys.DIK_ESCAPE);stopped();visual_center_y=nil

-- Default attacks: 200% of base melee 1.9; native target protections remain.
local target=enemy("mutant",2);targets={target};body=start()
key(DIK_keys.MOUSE_1,99);advance(1)
local attack_cycles,attack_camera=#body.motions,camera_sets
local attack_turn={ret_value=false};callbacks.on_before_mouse_move(8,0,false,attack_turn)
assert(key(DIK_keys.DIK_W,key_bindings.kFWD).ret_value,"Giant melee crossfade blocked native movement")
advance(249);assert(#target.hits==0)
assert(attack_turn.ret_value and camera_sets==attack_camera+1 and #body.motions==attack_cycles,
    "Giant crossfade delayed the camera/restarted the strike")
advance(1);assert(#target.hits==1 and math.abs(target.hits[1].power-3.8)<0.0001)
advance(1000);assert(#target.hits==1,"Melee repeatedly fired")
key(DIK_keys.DIK_ESCAPE);stopped()
for _,strength in ipairs({10,100,350,9000}) do
    target=enemy("npc",2);targets={target};start(nil,tostring(strength))
    key(DIK_keys.MOUSE_1,99);advance(1);advance(251)
    assert(#target.hits==1 and math.abs(target.hits[1].power-1.9*strength/100)<0.0001)
    key(DIK_keys.DIK_ESCAPE);stopped()
end
target=enemy("npc",2);target.no_bone=true;targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(251)
assert(#target.hits==0,"Missing collision bone must not be guessed")
for wnd in pairs(hud.windows) do assert(anomaly_donation_bridge_common.game_text_utf8(wnd.pseudogiant_ui.notice.main.text)=="Не получилось","Verbose failure notice returned") end
key(DIK_keys.DIK_ESCAPE);stopped()
target=enemy("npc",2);target.immune=true;targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(251)
assert(#target.hits==1 and target.health==1,"Native target immunity bypassed")
key(DIK_keys.DIK_ESCAPE);stopped()
wall=1;target=enemy("mutant",2);targets={target};start()
key(DIK_keys.MOUSE_1,99);advance(1);advance(251);assert(#target.hits==0,"Melee crossed a wall")
key(DIK_keys.DIK_ESCAPE);stopped();wall=nil

-- Stomp locks movement for the actual animation, not the 8s cooldown.
-- Input release, camera, cancelled/paused/saved forms cannot leave a lock.
targets={};start()
local movement_binds={"kFWD","kBACK","kL_STRAFE","kR_STRAFE","kJUMP","kCROUCH",
    "kACCEL","kSPRINT_TOGGLE","kL_LOOKOUT","kR_LOOKOUT"}
for _,name in ipairs(movement_binds) do assert(key(100,key_bindings[name]).ret_value) end
actor.velocity=v(2,-1,4);key(DIK_keys.MOUSE_2,99)
key(DIK_keys.DIK_V) -- must not overwrite the queued stomp or its movement lock
for _,name in ipairs(movement_binds) do
    assert(not key(100,key_bindings[name]).ret_value,"Pending stomp allowed movement: "..name)
    assert(not key(100,key_bindings[name],"on_before_key_hold").ret_value)
    assert(key(100,key_bindings[name],"on_before_key_release").ret_value,"Movement release was swallowed")
end
advance(1)
assert(actor.velocity.x==0 and actor.velocity.z==0 and actor.velocity.y==-1,"Old running inertia survived stomp")
key(DIK_keys.DIK_V);advance(0)
assert(not key(100,key_bindings.kFWD).ret_value,"V cancelled stomp movement lock")
assert(key(100,key_bindings.kINVENTORY).ret_value==false)
actor.velocity=v(3,-2,2);advance(430)
assert(actor.velocity.x==0 and actor.velocity.z==0 and actor.velocity.y==-2)
paused=true;advance(5000);paused=false
assert(not key(100,key_bindings.kFWD,"on_before_key_hold").ret_value,"Pause consumed stomp lock")
advance(570)
assert(key(100,key_bindings.kFWD,"on_before_key_hold").ret_value,"Animation finished but movement stayed locked")
local writes=velocity_writes
actor.velocity=v(1,0,2);key(DIK_keys.MOUSE_2,99) -- cooldown rejection is not a new lock
assert(key(100,key_bindings.kFWD).ret_value);advance(1)
assert(velocity_writes==writes and actor.velocity.z==2,"Cooldown rejection stopped movement")
key(DIK_keys.DIK_ESCAPE);stopped();assert(key(100,key_bindings.kJUMP).ret_value);actor.velocity=v()
for _,reason in ipairs({"cancel","save","load","level","error"}) do
    start();key(DIK_keys.MOUSE_2,99);advance(1)
    assert(not key(100,key_bindings.kFWD).ret_value)
    if reason=="cancel" then key(DIK_keys.DIK_ESCAPE)
    elseif reason=="save" then callbacks.on_before_save_input();cancel_saved_form()
    elseif reason=="load" then callbacks.on_before_load_input();descriptor.on_game_load();descriptor.update()
    elseif reason=="level" then descriptor.on_level_changing()
    else
        local setter=actor.set_movement_speed
        actor.set_movement_speed=function() error("test_stomp_brake_failed") end
        actor.velocity=v(1,0,1);advance(16);actor.set_movement_speed=setter;actor.velocity=v()
    end
    stopped();assert(key(100,key_bindings.kFWD).ret_value,"Stomp lock leaked after "..reason)
end
-- Melee is still mobile and does not zero the actor's current velocity.
start();actor.velocity=v(1,0,2);key(DIK_keys.MOUSE_1,99);advance(1)
assert(key(100,key_bindings.kFWD).ret_value and actor.velocity.z==2)
key(DIK_keys.DIK_ESCAPE);stopped();actor.velocity=v()

-- Stomp native timing, radial AoE/falloff, unique victims, vertical/solid guards.
local near=enemy("npc",2);near.pos.x=2
local far=enemy("mutant",10);far.pos.x=-4
local behind=enemy("mutant",-6)
local outside=enemy("mutant",17)
local upstairs=enemy("npc",3);upstairs.pos.y=6
local dead=enemy("npc",4);dead.living=false
targets={near,far,behind,outside,upstairs,dead}
body=start();key(DIK_keys.MOUSE_2,99);advance(1);advance(429)
assert(body.motion=="stand_kick_0" and #near.hits==0)
advance(1)
assert(#near.hits==1 and #far.hits==1 and #behind.hits==1,"Stomp failed to hit multiple radial targets")
assert(near.hits[1].power>far.hits[1].power,"Stomp falloff was lost")
assert(near.hits[1].impulse==6400 and near.hits[1].draftsman:id()==actor:id())
assert(#outside.hits==0 and #upstairs.hits==0 and #dead.hits==0,"Stomp crossed its radius/floor/death guard")
assert(fx_created[#fx_created]=="monsters\\gigant_wave" and sound_events[#sound_events]=="device\\door_stop")
advance(1500);assert(#near.hits==1,"Stomp applied its damage every frame")
key(DIK_keys.MOUSE_2,99);advance(16);assert(body.motion=="stand_idle_0","Stomp cooldown ignored")
key(DIK_keys.DIK_ESCAPE);stopped()
target=enemy("npc",8);targets={target};wall=4;start()
key(DIK_keys.MOUSE_2,99);advance(1);advance(431);assert(#target.hits==0,"Stomp crossed solid cover")
key(DIK_keys.DIK_ESCAPE);stopped();wall=nil
-- Foliage does not absorb melee or the radial stomp; walls/ActorObstacle do.
for _,ability in ipairs({"melee","stomp"}) do
    for _,case in ipairs({
        {flags=128,layers=6,hit=true},{flags=128+4,layers=6,hit=true},
        {flags=128+4096,layers=1,hit=false},{flags=0,layers=1,hit=false},
        {layers=1,hit=false},{flags=128,layers=6,wall=1,hit=false},
        {flags=128,layers=17,hit=true},{flags=128,layers=6,beyond=true,hit=false}
    }) do
        target=enemy("mutant",ability=="melee" and 2 or 8);targets={target};wall=case.wall
        if case.beyond then target.pos.z=ability=="melee" and 4.1 or 16 end
        for i=1,case.layers do ray_layers[i]={z=i*0.04,flags=case.flags} end
        start();key(ability=="melee" and DIK_keys.MOUSE_1 or DIK_keys.MOUSE_2,99)
        advance(1);advance(ability=="melee" and 251 or 431)
        assert((#target.hits==1)==case.hit,"Foliage/solid boundary failed: "..ability.."/"..tostring(case.flags).."/"..case.layers)
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

target=enemy("npc",5);targets={target};start()
key(DIK_keys.MOUSE_2,99);advance(1);paused=true;advance(5000);assert(#target.hits==0)
paused=false;advance(431);assert(#target.hits==1,"Pause consumed/duplicated pending stomp")
key(DIK_keys.DIK_ESCAPE);stopped()

-- Same normalized incoming damage/independent HP as the burer reference.
local first=enemy("npc",2);first.pos.x=2
local second=enemy("mutant",5);second.pos.x=-2
local original_hit=first.hit
function first:hit(h) original_hit(self,h);callbacks.GUI_on_show() end
targets={first,second};start();key(DIK_keys.MOUSE_2,99);advance(1);advance(431)
stopped();assert(#first.hits==1 and #second.hits==0,"Cancelled stomp continued damaging victims")
targets={};actor.health=0.42;protection.immunity=1/3
start();actor_hit(0.9,hit.strike);advance(1)
assert(form_hp()==98 and actor.health==0.42,"Default form capacity is not 1300%")
callbacks.actor_on_update();advance(1);assert(form_hp()==98,"Damage charged twice")
actor_hit(100,hit.strike);actor_hit(100,hit.strike);advance(1);stopped()
assert(actor.health==0.42 and actor:alive(),"Destroyed form killed rider")
protection.immunity=1
for _,health in ipairs({"100","300","1300","9000","99999"}) do
    p=params();p.health_percent=health;assert(descriptor.execute({},p)=="executed")
    actor_hit(tonumber(health)/100/4);advance(1)
    assert(form_hp()==75 and actor.health==0.42)
    key(DIK_keys.DIK_ESCAPE);stopped()
end
start();actor.health=0.12;callbacks.actor_on_update();advance(1)
assert(form_hp()==98 and actor.health==0.42)
key(DIK_keys.DIK_ESCAPE);stopped()
actor.health=0.77

-- Legacy cleanup-only save journal contains no native resumable body.
start();local save={};callbacks.save_state(save);cancel_saved_form()
assert(save[SAVE].actor_health==0.77 and save.adb_burer_form_recovery_v1==nil)
save[SAVE].form_state=nil;memory={[SAVE]=save[SAVE]};actor.health=0.21;camera=0;custom=true
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.77)
start();actor.health=0.29;attachments={};hud.windows={};custom=false;camera=2;memory={}
for key,value in pairs(move_defaults) do move_values[key]=value end -- newly loaded native actor
for _,particle in ipairs(fx) do particle.stale=true;particle.active=false end
descriptor.on_game_load();descriptor.update();stopped();fx={}
assert(actor.health==0.29,"Unrelated loaded save HP overwritten")
actor.health=0.77
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
    start();callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end
end
start("1");paused=true;advance(5000);paused=false;advance(999);assert(active());advance(1);stopped()

-- Service save preserves the pool/timer/custom attack setting without replay.
start(nil,"350");actor_hit(0.6);advance(1000)
key(DIK_keys.MOUSE_2,99);advance(1);advance(100)
descriptor.on_effect_save("before","giant-service");assert(active() and not attachments[NAME] and not custom)
local data={};callbacks.save_state(data)
assert(data[SAVE].form_health==nil and data[SAVE].params==nil)
descriptor.on_effect_save("after","giant-service",true);service_files["giant-service.scop"]=true
advance(16);assert(active() and form_hp()==95)
target=enemy("npc",2);targets={target}
key(DIK_keys.MOUSE_1,99);advance(1);advance(251)
assert(#target.hits==1 and math.abs(target.hits[1].power-6.65)<0.0001,"Service save reset attack multiplier")
assert(#fx_created>0)
key(DIK_keys.DIK_ESCAPE);stopped();targets={}
start();descriptor.on_effect_save("before","load");descriptor.on_game_load();descriptor.update();stopped()
start();descriptor.on_effect_save("before","failed");descriptor.on_effect_save("after","failed",false);stopped()
start();descriptor.on_effect_save("before","timeout");descriptor.on_effect_save("after","timeout",true)
advance(10001);stopped()
start();descriptor.on_level_changing();stopped()
fail_setup=true;local context={};assert(descriptor.can_execute(context,params()))
assert(descriptor.execute(context,params())=="rejected");stopped()
if arg[4] then
    local file=assert(io.open(arg[4],"rb"));particle_file=file:read("*a");file:close()
    module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_pseudogiant_form");module.on_game_start()
    local count=#fx_created;start();key(DIK_keys.MOUSE_2,99);advance(1);advance(431)
    assert(#fx_created>count,"Real particle library lacks the expected stomp effect")
    key(DIK_keys.DIK_ESCAPE);stopped()
end
assert(next(copied_files)==nil and copy_count==delete_count,"Particle probe leaked a private copy")
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
reject(params("60001"),"pseudogiant_duration_invalid")

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
    for wnd in pairs(hud.windows) do view=wnd.pseudogiant_ui end
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
    p=params();p.camera_mode="invalid";reject(p,"pseudogiant_camera_mode_invalid")
    p=params();p.camera_key="MOUSE_1";reject(p,"pseudogiant_ability_keys_duplicate")
end
-- Giant walk/run match the OLD sprint; sprint is NOT multiplied again.
do
    targets={};start();expected_movement()
    local count=#move_writes
    for i=1,100 do advance(16) end
    assert(#move_writes==count,"Movement speed was rewritten every frame")
    descriptor.on_effect_save("before","giant_move",nil);movement_original()
    local saved={};callbacks.save_state(saved);service_files["giant_move.scop"]=true
    descriptor.on_effect_save("after","giant_move",true);advance(16);expected_movement()
    key(DIK_keys.DIK_ESCAPE);stopped()
    for _,field in ipairs({"walk_accel","run_coef","sprint_koef","run_strafe_coef"}) do
        fail_move=field
        assert(descriptor.execute({},params())=="rejected");stopped()
    end
    start();local save={};callbacks.save_state(save);cancel_saved_form()
    for _,entry in ipairs(save[SAVE].movement) do move_values[entry.key]=entry.applied end
    save[SAVE].form_state=nil;memory={[SAVE]=save[SAVE]};descriptor.on_game_load();advance(16);movement_original()
    -- Another addon's independent field write is not overwritten on cleanup.
    start();move_values.walk_accel=77;advance(16)
    assert(active() and move_values.walk_accel==77,"External speed edit cancelled the form")
    key(DIK_keys.DIK_ESCAPE);assert(not active() and move_values.walk_accel==77)
    move_values.walk_accel=move_defaults.walk_accel;stopped();movement_original()
end
-- Listener-relative native-grounded mutant steps, never human boots.
do
    targets={};missing_sounds=nil;start()
    local count=#sound_events;local flags={ret_value=true}
    callbacks.actor_on_footstep("earth",0.1,false,flags)
    assert(not flags.ret_value and #sound_events==count+1)
    local step=sounds[#sounds]
    assert(step.flags==sound_object.s2d and step.volume>=0.7)
    assert(step.name:find("material\\large\\large_step",1,true)==1)
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
    name="pseudogiant",key=SAVE,actor=actor,callbacks=callbacks,
    start=function() start("20") end,stopped=stopped,advance=advance,active=active,
    live=function() return custom end,descriptor=function() return descriptor end,
    cancel=function() callbacks.on_before_load_input() end,
    set_memory=function(value) memory=value end,set_paused=function(value) paused=value end,
    elapse_silent=function(ms) now=now+ms end,
    file_exists=function(name) service_files[name..".scop"]=true end,
    reload_common=function() anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common") end
})

print("Pseudogiant PG-009 smoke passed (mock API): native pose crossfade, passable foliage/solid cover/range/camera, unchanged stomp movement lock/braking/pause/cooldown/cleanup, two bindings, catalog-only visual, native melee/stomp timing/AoE/falloff, separate protected HP and isolated save/load.")

