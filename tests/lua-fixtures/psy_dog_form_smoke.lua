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
local NAME="adb_psy_dog_form_visual_v1"
local SAVE="adb_psy_dog_form_recovery_v1"
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
local sections={arbitrary_boss_x={class="SM_DOG_P",visual="custom\\independent_psy_dog"},
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
    local object={kind=kind,pos=v(0,0.65,z),living=true,hits={},health=1,object_id=enemy_id}
    setmetatable(object,object_meta)
    function object:id() return self.object_id end
    function object:name() return "world_"..self.object_id end
    function object:relation() return self.friendly and 0 or 2 end
    function object:has_info() return self.companion==true end
    function object:section() return "test_"..kind end
    function object:center(hud) assert(hud==false);return copy(self.pos) end
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
        assert(radius>0 and radius<=31)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) for _,object in ipairs(targets) do if object.id and object:id()==id then return object end end;return test_clones and test_clones[id] end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_psy_dog" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==true and speed==1,"PsyDog pose crossfade must not change playback speed")
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
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.psy_dog_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}

-- Engine-facing clone contract: server packet is edited while offline.
local servers,next_clone_id={},1000
test_clones={}
local fail_create,fail_packet,fail_release=false,false,false
local spawn_fail_at,spawn_count=nil,0
local bad_points=false
local native_orders,capture_calls,release_calls=0,0,0
function actor:team() return 255 end
function actor:game_vertex_id() return 42 end
level.name=function() return "test_level" end
function CAI_Stalker__BeforeHitCallback() end
function CBaseMonster__BeforeHitCallback() end
sections.carrier_dog={class="SM_P_DOG",visual="custom\\ordinary_pseudodog",script_binding="bind_monster.bind"}
local carrier_available=true
anomaly_donation_bridge_catalog.snapshot=function()
    return {entries={{section_id="carrier_dog",kind="mutant",available=carrier_available}}}
end
anomaly_donation_bridge_catalog.is_spawn_safe=function(section,kind) return section=="carrier_dog" and kind=="mutant" and carrier_available end
anomaly_donation_bridge_common.spawn_points=function(_,count)
    local points={}
    for i=1,count do
        local angle=i*math.pi*2/count
        points[i]={vertex_id=bad_points and 1 or i,position=bad_points and v() or v(math.cos(angle)*4,0,math.sin(angle)*4)}
    end
    return points
end
local life={}
function alife() return life end
function life:object(id,quiet) assert(quiet==true);return servers[id] end
function life:release(server,forced)
    assert(forced==true and servers[server.id],"Foreign/repeated release")
    if fail_release then fail_release=false;error("release_failed_once") end
    release_calls=release_calls+1;servers[server.id]=nil;test_clones[server.id]=nil
end
function alife_create(section,pos,vertex,game_vertex)
    spawn_count=spawn_count+1
    if fail_create or spawn_fail_at==spawn_count then return end
    assert(section=="carrier_dog" and vertex>0 and game_vertex==42)
    next_clone_id=next_clone_id+1
    local server={id=next_clone_id,section=section,position=copy(pos),packet={object_flags=4095}}
    function server:name() assert(not self.stale);return "psydog_clone_"..self.id end
    function server:section_name() assert(not self.stale);return self.section end
    servers[server.id]=server
    return server
end
utils_stpk={
    get_monster_data=function(server)
        assert(not server.stale)
        local data={};for k,value in pairs(server.packet) do data[k]=value end;return data
    end,
    set_monster_data=function(data,server)
        assert(not test_clones[server.id],"Packet edited after online spawn")
        if fail_packet then fail_packet=false;error("packet_write_failed") end
        server.packet=data
    end
}
act=setmetatable({attack=1},{__call=function(_,kind,target) assert(kind==1 and target:id()~=actor:id());return {kind=kind,target=target} end})
function action(object,order)
    assert(object.is_clone and object.captured and order.target:alive() and not order.target.friendly)
    object.order=order;native_orders=native_orders+1
end
function materialize_clones()
    for id,server in pairs(servers) do
        if not test_clones[id] and server.packet.custom_data then
            assert(server.packet.object_flags==187 and server.packet.g_team==255 and server.packet.visual_name=="custom\\independent_psy_dog")
            assert(server.packet.smart_terrain_id==65535 and server.packet.health==0.01)
            local object=enemy("mutant",server.position.z);object.pos=copy(server.position);object.object_id=id;object.is_clone=true
            function object:name() return "psydog_clone_"..self.object_id end
            function object:section() return "carrier_dog" end
            function object:relation() return 0 end
            function object:get_script() return self.captured==true end
            function object:script(enabled,owner) assert(enabled and owner=="hardcoon.adb.psy_dog_form");self.captured=true;capture_calls=capture_calls+1 end
            function object:reset_action_queue() self.order=nil end
            function object:set_enemy_callback(filter) self.enemy_filter=filter end
            test_clones[id]=object
            callbacks.monster_on_net_spawn(object,server)
        end
    end
end
local function clone_count()
    local count=0;for _ in pairs(servers) do count=count+1 end;return count
end
local function clone_list()
    local list={};for _,object in pairs(test_clones) do list[#list+1]=object end;return list
end
local function begin(values)
    local p={mutant_sections="arbitrary_boss_x",duration_seconds="60",catalog_revision="revision"}
    for k,value in pairs(values or {}) do p[k]=value end
    local ok,status,why=descriptor.can_execute({},p)
    assert(ok,tostring(status)..":"..tostring(why))
    assert(descriptor.execute({},p)=="executed");return attachments[NAME]
end

local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_psy_dog_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.psy_dog_form"])
local function params(duration) return {mutant_sections="arbitrary_boss_x",duration_seconds=duration or "60",catalog_revision="revision"} end
local function active() return descriptor.is_blocking() end
local function advance(ms)
    now=now+ms;frame=frame+1
    materialize_clones()
    local windows={};for wnd in pairs(hud.windows) do windows[#windows+1]=wnd end
    for _,wnd in ipairs(windows) do wnd:Update() end
    descriptor.update()
end
local function key(dik,bind,kind)
    local flags={ret_value=true};callbacks[kind or "on_before_key_press"](dik,bind,false,flags);return flags
end
local function start(duration,attack_power,jump_height)
    local p=params(duration);local context={}
    p.attack_power_percent=attack_power;p.health_percent=jump_height
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
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.psy_dog_ui.hp.main.text):match("(%d+)%% HP"))
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


local function get_ui() for wnd in pairs(hud.windows) do return wnd.psy_dog_ui end end
local function stop()
    key(DIK_keys.DIK_ESCAPE);stopped();assert(clone_count()==0)
    actor.velocity=v();actor.pos=v();initial_dir=v(0,0,1)
end
local function cast() key(DIK_keys.MOUSE_2,99);advance(1) end
local function native_hit(target,draftsman,power)
    local h={power=power,type=hit.wound,draftsman=draftsman}
    local flags={ret_value=true}
    callbacks[IsStalker(target) and "npc_on_before_hit" or "monster_on_before_hit"](target,h,7,flags)
    if flags.ret_value~=false then
        h.power=target.immune and 0 or h.power*0.5 -- a normal native target immunity
        CBeforeHitAfterCalcs(h,target,7)
        target.health=target.health-h.power
    end
    return h,flags
end
-- Dynamic appearance only; actual SM_DOG_F is never spawned or parented to Actor.
local p=params();p.mutant_sections="a_dog";reject(p,"psy_dog_visual_class_unsupported")
p=params();p.phantom_key="MOUSE_1";reject(p,"psy_dog_ability_keys_duplicate")
carrier_available=false;reject(params(),"psy_dog_no_safe_pseudodog_carrier");carrier_available=true
for _,value in ipairs({"0","60001","1.5","NaN"}) do reject(params(value),"psy_dog_duration_invalid") end
p=params();p.phantom_count="9";reject(p,"psy_dog_phantom_count_invalid")
p=params();p.phantom_duration_seconds="60001";reject(p,"psy_dog_phantom_duration_invalid")
p=params();p.phantom_cooldown_seconds="0";reject(p,"psy_dog_phantom_cooldown_invalid")
local body=begin({duration_seconds="60000"});advance(16)
assert(form_hp()==100 and get_ui().labels.phantom and not get_ui().labels.jump)
assert(math.abs(get_ui().left+get_ui().width/2-512)<0.01 and #get_ui().labels.phantom.controls==9)
assert(get_ui().ring.texture=="adb_burer_aim_ring" and #jump_writes==0)
key(DIK_keys.MOUSE_1,99);advance(1)
assert(body.motion=="stand_attack_0" and key(DIK_keys.DIK_W,99).ret_value)
local cycles=#body.motions;local camera_before=camera_sets
local turn={ret_value=false};callbacks.on_before_mouse_move(16,0,false,turn);advance(16)
assert(turn.ret_value and camera_sets==camera_before+1 and #body.motions==cycles)
for _,binding in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20}) do assert(not key(100,binding).ret_value) end
stop()
-- Exact bite event, physical wound type, configured damage, immunity and foliage.
local target=enemy("npc",1);target.health=100;targets={target};body=begin()
key(DIK_keys.MOUSE_1,99);advance(1);advance(249);assert(#target.hits==0)
advance(1);assert(#target.hits==1 and math.abs(target.hits[1].power-1.04)<0.00001 and target.hits[1].impulse==35)
advance(1000);assert(#target.hits==1);stop()
target=enemy("mutant",1);target.health=100;targets={target};ray_layers={{z=0.2,flags=128},{z=0.4,flags=128}}
begin();key(DIK_keys.MOUSE_1,99);advance(1);advance(250);assert(#target.hits==1);stop();ray_layers={}
target=enemy("mutant",1);targets={target};wall=0.5
begin();key(DIK_keys.MOUSE_1,99);advance(1);advance(250);assert(#target.hits==0);stop();wall=nil
-- Native clones get ONE attack order each, target identity locked at click.
target=enemy("npc",12);target.health=100;targets={target};begin();cast()
assert(clone_count()==4,"No initial wave: "..tostring(get_ui() and get_ui().notice.main.text).." / "..table.concat(logs,";"))
advance(1);local copies=clone_list();assert(#copies==4)
local orders=native_orders;local captures=capture_calls
for _,object in ipairs(copies) do
    assert(object.order.target:id()==target:id() and object.enemy_filter(object,target))
    assert(not object.enemy_filter(object,actor))
end
advance(500);assert(native_orders==orders and capture_calls==captures,"Attack queue reset on every frame")
local camera_flags={ret_value=false};callbacks.on_before_mouse_move(60,0,false,camera_flags);advance(16)
for _,object in ipairs(copies) do assert(object.order.target:id()==target:id(),"Aim movement retargeted the pack") end
local original=target.health;local h=native_hit(target,copies[1],0.4)
assert(math.abs(original-target.health-0.4)<0.0001,"Clone damage was visual or bypassed normal immunity")
target.immune=true;original=target.health;native_hit(target,copies[1],0.4);assert(target.health==original);target.immune=nil
local other=enemy("npc",10);other.health=100
h=native_hit(other,copies[1],0.4);assert(h.power==0 and other.health==100,"Phantom attacked an unassigned target")
local incoming={power=5,type=hit.wound,draftsman=copies[1]};local hit_flags={ret_value=true}
callbacks.actor_on_before_hit(incoming,0,hit_flags);assert(incoming.power==0 and hit_flags.ret_value==false)
local late={power=5,type=hit.wound,draftsman=copies[1]};CBeforeHitAfterCalcs(late,actor,0);assert(late.power==0)
local ally=enemy("npc",2);ally.friendly=true
local flags={};callbacks.on_enemy_eval(ally,copies[1],flags);assert(flags.override and flags.result==false)
native_hit(copies[1],target,0.01);advance(201);assert(clone_count()==3,"A hit did not dissolve the phantom")
target.living=false;advance(201);assert(clone_count()==0,"Dead target left a hostile autonomous pack")
stop()
-- Queued hits from an already removed wave are cancelled; a reused ID with
-- a different server lease is not captured, deleted or treated as our clone.
target=enemy("npc",10);targets={target};begin();cast();advance(1);copies=clone_list()
local removed=copies[1];stop()
local late_target_hit={power=0.4,type=hit.wound,draftsman=removed}
local late_flags={ret_value=true};callbacks.npc_on_before_hit(target,late_target_hit,7,late_flags)
assert(late_target_hit.power==0 and late_flags.ret_value==false,"A removed wave left a damaging queued hit")
local recycled={id=removed:id(),section="carrier_dog",packet={custom_data="[foreign_mod]\nowner=not_adb\n"}}
function recycled:name() return removed:name() end
function recycled:section_name() return "carrier_dog" end
servers[recycled.id]=recycled
local foreign_hit={power=0.4,type=hit.wound,draftsman=removed};local foreign_flags={ret_value=true}
callbacks.npc_on_before_hit(target,foreign_hit,7,foreign_flags)
assert(foreign_hit.power==0.4 and foreign_flags.ret_value and servers[recycled.id]==recycled,"Recycled foreign entity was claimed")
servers[recycled.id]=nil
target=enemy("npc",10);targets={target};begin();cast();advance(1)
callbacks.npc_on_death(target)
local replacement_target=enemy("npc",10);replacement_target.object_id=target:id();targets={replacement_target}
advance(201);assert(clone_count()==0,"A dead target's recycled ID inherited the pack")
stop()
-- No valid target / wall / friendly actor companion -> no side effects.
for _,mode in ipairs({"empty","wall","friendly","companion"}) do
    target=enemy("npc",10);targets=mode=="empty" and {} or {target};wall=mode=="wall" and 5 or nil
    target.friendly=mode=="friendly";target.companion=mode=="companion"
    begin();cast();assert(clone_count()==0,mode.." incorrectly summoned phantoms");stop();wall=nil
end
-- Rollback partial packet/spawn failures, no saved/orphan clones.
for _,mode in ipairs({"packet","create","partial","bad_points"}) do
    target=enemy("npc",10);targets={target};begin()
    fail_packet=mode=="packet";fail_create=mode=="create";spawn_fail_at=mode=="partial" and spawn_count+3 or nil;bad_points=mode=="bad_points"
    cast();assert(active() and clone_count()==0,"Partial wave was not rolled back: "..mode)
    fail_create=false;spawn_fail_at=nil;bad_points=false;stop()
end
target=enemy("npc",10);targets={target};begin();cast();advance(1)
local count_before=clone_count();cast();assert(clone_count()==count_before,"Cooldown accumulated another wave")
advance(8001);cast();advance(1);assert(clone_count()==4,"Repeat wave accumulated clones")
advance(20001);assert(active() and clone_count()==0,"Phantom lifetime did not expire");stop()
-- Pauses freeze expiry; every normal exit deletes native doubles.
for _,event in ipairs({"cancel","save","load","level","menu","quit","depleted"}) do
    target=enemy("npc",10);targets={target};actor.health=0.42;begin();cast();advance(1);assert(clone_count()==4)
    if event=="cancel" then key(DIK_keys.DIK_ESCAPE)
    elseif event=="save" then callbacks.on_before_save_input()
    elseif event=="load" then callbacks.on_before_load_input()
    elseif event=="level" then descriptor.on_level_changing()
    elseif event=="menu" then callbacks.GUI_on_show()
    elseif event=="quit" then callbacks.main_menu_on_quit()
    else actor_hit(100);advance(1) end
    stopped();assert(clone_count()==0 and actor.health==0.42,"Leaked clones/rider HP after "..event)
end
target=enemy("npc",10);targets={target};begin();cast();advance(1);paused=true;advance(30000);paused=false;advance(201)
assert(clone_count()==4,"Pause consumed clone lifetime");stop()
-- Failure to release cannot leave a damaging hidden wave or spawn another.
target=enemy("npc",10);targets={target};begin();cast();advance(1)
copies=clone_list();fail_release=true;key(DIK_keys.DIK_ESCAPE)
assert(active() and clone_count()==1,"Failed release was not retained for cleanup")
h=native_hit(target,copies[1],0.4)
advance(1001);stopped();assert(clone_count()==0)
-- Service save strips entities BEFORE native serialization, same-session
-- resume recreates the surviving count/remaining life, not serialized userdata.
target=enemy("npc",10);targets={target};begin();cast();advance(1);advance(3000)
descriptor.on_effect_save("before","psy-service")
assert(active() and not attachments[NAME] and clone_count()==0)
local data={};callbacks.save_state(data);assert(data[SAVE].form_health==nil and data[SAVE].clones==nil)
descriptor.on_effect_save("after","psy-service",true);service_files["psy-service.scop"]=true
advance(16);assert(active() and attachments[NAME] and clone_count()==4)
advance(17001);assert(clone_count()==0,"Service autosave reset the clone timer")
stop()
-- F9 never dereferences old native objects or releases a recycled ID.
target=enemy("npc",10);targets={target};begin();cast();advance(1)
for _,server in pairs(servers) do server.stale=true end
servers={};test_clones={};attachments={};hud.windows={};custom=false;camera=2;memory={}
descriptor.on_game_load();descriptor.update();stopped()
for _,message in ipairs(logs) do
    assert(not message:find("form started",1,true) and not message:find("ability:",1,true),"Routine log spam")
end
assert(#jump_writes==0 and #fx_created==0,"Unrelated actor physics/particle changes")
-- Largest form duration is accepted and expires without timer overflow.
targets={};begin({duration_seconds="60000"});advance(59999000);assert(active(),"60000-second form expired early")
advance(1000);stopped()
reject(params("60001"),"psy_dog_duration_invalid")
print("Psy-dog PDF-001 smoke passed (mock API): native bite/crossfade/HUD/separate HP, bounded no-save real AI doubles, locked target/no friendly fire, native hit scaling/immunity, one-hit dissolve, spawn rollback/cleanup, cooldown/lifetime/pause and same-session service-save recovery.")

