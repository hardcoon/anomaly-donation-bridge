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
local wall_flags=0
local camera,custom=2,false
local initial_dir=v(0,0,1)
local camera_fov=67.5
local display_width,display_height=1920,1080
local fx,sounds,fx_created,sound_events={},{},{},{}
local particle_file,missing_particle,missing_sounds
local copied_files,copy_count,delete_count={},0,0
local probe_read_error,probe_copy_error=false,false
local fail_cloud=false
local service_files={}
local servers,zoneclients,spawns,released={},{},{},{}
local next_zone_id,game_level,nav_point=400,"test_level",nil
local invalid_nav,no_floor,fail_create,fail_packet,fail_release=false,false,false,nil,false
local excluded={}
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
    chunk(0,chunk(1,string.char(1,0))..chunk(2,"monsters\\polter_linza\0"))..
    chunk(1,chunk(2,"monsters\\polter_damage\0"))..
    chunk(2,chunk(2,"monsters\\polter_idle\0"))..
    chunk(3,chunk(2,"mod\\configured_fire_lens\0"))..
    chunk(4,chunk(2,"mod\\configured_fire_idle\0")))
particle_file=particle_data
local NAME="adb_poltergeist_form_visual_v1"
local SAVE="adb_poltergeist_form_recovery_v1"
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
local original_walk,walk_accel=11.5,11.5
local fail_move,fail_move_before,fail_move_read=false,false,false
function actor:get_actor_walk_accel()
    if fail_move_read then error("walk_read_failed") end
    return walk_accel
end
function actor:set_actor_walk_accel(value)
    if fail_move_before then fail_move_before=false;error("walk_failed_before_write") end
    walk_accel=value
    if fail_move then fail_move=false;error("walk_failed_after_write") end
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
local sections={arbitrary_boss_x={class="SM_POLTR",visual="custom\\independent_poltergeist"},
    a_dog={class="SM_DOG_S",visual="custom\\dog"},loose_prop={class="O_PHYS_S"}}
sections.zone_pull_a={class="ZS_MINCE",hit_type="strike"}
sections.zone_pull_b={class="ZS_GALAN",hit_type="explosion"}
sections.zone_fire={class="ZS_MBALD",hit_type="light_burn"}
sections.zone_electric={class="ZS_ELECT",hit_type="shock"}
sections.zone_chemical={class="ZS_ACID",hit_type="chemical_burn"}
sections.zone_fuzz={class="ZS_BFUZZ",hit_type="chemical_burn"}
sections.zone_hvatalka={class="ZS_BFUZZ",hit_type="strike"}
sections.zone_rad={class="ZS_MBALD",hit_type="radiation"}
sections.zone_scripted={class="ZS_MBALD",hit_type="burn",custom_data="unsafe"}
sections.zone_torrid={class="ZS_TORRD",hit_type="burn"}
for section,row in pairs(sections) do
    if section:match("^zone_") then row.awaking_time="100";row.blowout_time="2000";row.accamulate_time="250" end
end
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
        assert(path:find("private\\adb_poltergeist_particle_probe_",1,true)==1,"Deleted non-owned path")
        copied_files[path]=nil;delete_count=delete_count+1
    end,
    r_open=function() error("Unsafe native IReader must not be acquired") end,
    r_close=function() error("Reproduces native IReader*& C++ exception") end
} end
particles_object=function(name)
    if fail_cloud==name or fail_cloud==true and name=="monsters\\polter_linza" then fail_cloud=false;error("cloud_failed_before_construction") end
    assert(name~=missing_particle,"Missing particle constructor MUST NOT be probed")
    assert(particle_file:find(name.."\0",1,true),"Constructed particle absent from the live game library")
    local is_body=name~="monsters\\polter_damage"
    local p={name=name,transform_updates=0}
    function p:set_direction(dir) assert(not self.stale);self.dir=copy(dir) end
    function p:play_at_pos(pos,hud_mode)
        assert(not is_body,"World-space body emission causes a trailing sprite cloud")
        assert(not self.stale and not hud_mode);self.pos=copy(pos);self.active=true
    end
    function p:play(hud_mode)
        assert(is_body and self.pos and not self.stale and not hud_mode,"Body must have its local transform BEFORE Play")
        assert(not self.active,"Persistent body must not be restarted/re-emitted every frame")
        self.active=true
    end
    function p:set_position(pos)
        assert(not self.stale and is_body);self.pos=copy(pos);self.transform_updates=self.transform_updates+1
        -- SetXFORM carries already emitted particles too, not only the source.
        self.sample_particle=v(pos.x+0.25,pos.y+0.1,pos.z-0.2)
    end
    function p:move_to(pos,velocity)
        assert(not is_body,"Moving only the body emitter leaves world-space sprites behind")
        assert(not self.stale and self.active);self.pos=copy(pos)
    end
    function p:stop() assert(not self.stale);self.active=false end
    fx[#fx+1]=p;fx_created[#fx_created+1]=name
    return p
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
    function object:list_bones(hud) assert(hud==false);return {[0]="zero_damage_root",[7]="modded_collision_bone"} end
    function object:hit(h)
        assert(h.bone=="modded_collision_bone" and h.draftsman:id()==actor:id() and type(h.type)=="number")
        local flags={ret_value=true};local hook=callbacks[self.kind=="npc" and "npc_on_before_hit" or "monster_on_before_hit"]
        if hook then hook(self,h,7,flags) end
        if flags.ret_value==false then return end
        damage_calls=damage_calls+1;self.hits[#self.hits+1]=h
        if not self.immune then self.health=math.max(0,self.health-h.power) end
    end
    function object:position() assert(not self.stale);return copy(self.pos) end
    function object:parent() return self.owner end
    object.script=forbidden;object.force_set_position=forbidden;object.reset_action_queue=forbidden
    object.get_physics_shell=function() error("Living creature physics must not be probed") end
    return object
end
hit=setmetatable({strike=1,fire_wound=2,wound=3,explosion=4,radiation=5,burn=6,light_burn=7,shock=8,chemical_burn=9},{__call=function() return {} end})
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
        assert(radius>0 and radius<=26)
        if visit(actor) then return end
        for _,object in ipairs(targets) do
            if (object.pos.x-origin.x)^2+(object.pos.y-origin.y)^2+(object.pos.z-origin.z)^2<=radius*radius and visit(object) then return end
        end
    end,
    object_by_id=function(id) for _,object in ipairs(targets) do if object.id and object:id()==id then return object end end end,
    add_attachment=function(name,model)
        assert(name==NAME and model=="custom\\independent_poltergeist" and not attachments[name])
        local body={model=model,motions={}}
        function body:play_motion(motion,mixin,speed)
            assert(not self.stale);assert(mixin==false and speed==1)
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
        assert(ignored and type(ignored:id())=="number" and range>0 and range<=80)
        local nearest,object,material_flags=range+1,nil,0
        if not no_floor and dir.y< -0.000001 and origin.y>=0 then
            local ground=-origin.y/dir.y
            if ground>=0 and ground<=range then nearest=ground end
        end
        if camera_side_wall and math.abs(dir.x)>0.00001 then
            local t=(camera_side_wall-origin.x)/dir.x
            if t>=0 and t<=range then nearest=t end
        end
        if wall and math.abs(dir.z)>0.00001 then
            local distance=(wall-origin.z)/dir.z
            if distance>=0 and distance<nearest then nearest=distance;material_flags=wall_flags end
        end
        if mode==rq_target.rqtBoth then
            for _,target in ipairs(targets) do
                local delta=v(target.pos.x-origin.x,target.pos.y-origin.y,target.pos.z-origin.z)
                local distance=delta.x*dir.x+delta.y*dir.y+delta.z*dir.z
                local error2=(delta.x-dir.x*distance)^2+(delta.y-dir.y*distance)^2+(delta.z-dir.z*distance)^2
                if target:id()~=ignored:id() and distance>=0 and distance<nearest and error2<0.3^2 then nearest,object,material_flags=distance,target,0 end
            end
        end
        if nearest<=range then result.range=nearest;result.object=object;result.material_flags=object and 128 or material_flags;result.element=object and (object.no_bone and 65535 or 7);return true end
        return false
    end
}
level.name=function() return game_level end
-- PF-002 must render only particles, never create/hide/scale a skeleton.
level.add_attachment=forbidden;level.get_attachment=forbidden;level.remove_attachment=forbidden
game.motion_exists=forbidden
level.vertex_id=function(pos) nav_point=copy(pos);return invalid_nav and 4294967295 or 42 end
level.vertex_position=function(id) assert(id==42);return copy(nav_point) end
local old_lookup=level.object_by_id
level.object_by_id=function(id) return zoneclients[id] or old_lookup(id) end
function actor:game_vertex_id() return 8 end
function actor:level_vertex_id() return 42 end
function alife() return {
    object=function(_,id) return servers[id] end,
    release=function(_,object)
        if fail_release then fail_release=false;error("release_failed_once") end
        assert(servers[object.id] and servers[object.id]:section_name()==object:section_name())
        servers[object.id]=nil;zoneclients[object.id]=nil;released[#released+1]=object.id
    end
} end
function alife_create(section,pos,vertex,game)
    assert(sections[section].class~="ZS_BFUZZ" and sections[section].class~="ZS_TORRD",
        "Fresh animated zones have no editor MotionIDs and can crash native PlayCycle")
    assert(section:match("^zone_") and sections[section] and vertex==42 and game==8)
    if fail_create then fail_create=false;return nil end
    next_zone_id=next_zone_id+1
    local object={id=next_zone_id,position=copy(pos),section_id=section,data={object_flags=543}}
    function object:section_name() return self.section_id end
    servers[object.id]=object;spawns[#spawns+1]=object
    local client={object_id=object.id,kind="zone",enabled=true}
    function client:id() return self.object_id end
    function client:section() return section end
    function client:is_enabled_anomaly() return self.enabled end
    function client:enable_anomaly()
        assert(not self.enabled,"Enabling an enabled zone queues a late IDLE event that resets awakening")
        self.enable_pending=true
    end
    zoneclients[object.id]=client
    return object
end
local function get_zone(object)
    local data={object_flags=object.data.object_flags,restrictor_type=object.data.restrictor_type,shapes={}}
    for _,shape in ipairs(object.data.shapes or {}) do data.shapes[#data.shapes+1]={shtype=shape.shtype,radius=shape.radius} end
    return data
end
local function set_zone(data,object)
    if fail_packet=="ignore" then fail_packet=nil;return end
    object.data=data
    if fail_packet=="after" then fail_packet=nil;error("packet_failed_after_write") end
end
utils_stpk={get_anom_zone_data=get_zone,set_anom_zone_data=set_zone,
    get_visual_zone_data=get_zone,set_visual_zone_data=set_zone}
-- Native local zone-state event, NOT a fake particle or an external packet.
local activation_events={}
assert(NET_Packet==nil,"Retail Lua exports net_packet, not its C++ class name")
net_packet=function() return {
    w_begin=function(self,value) self.message=value end,
    w_u32=function(self,value) self.timestamp=value end,
    w_u16=function(self,value) self.words=self.words or {};self.words[#self.words+1]=value end,
    w_u8=function(self,value) self.state=value end
} end
level.send=function(packet,reliable,sequential,priority,immediate)
    assert(packet.message==8 and packet.words[1]==28 and packet.state==1 and packet.timestamp==0)
    assert(reliable and sequential and not priority and immediate)
    local client=assert(zoneclients[packet.words[2]],"Event addressed a missing/foreign zone")
    if type(client.is_enabled_anomaly)=="function" then
        assert(client.enabled,"Zone still disabled when forced to awaken")
    else
        -- Native OnStateSwitch(AWAKING) calls Enable() itself. Old engines
        -- have that event handling but no Lua is_enabled_anomaly getter.
        client.enabled=true
    end
    client.activations=(client.activations or 0)+1
    activation_events[#activation_events+1]={id=client:id(),at=now}
end
function CAI_Stalker__BeforeHitCallback() end
function CBaseMonster__BeforeHitCallback() end
function CCustomZone_BeforeActivateCallback() end
function RegisterScriptCallback(name,f) callbacks[name]=f end
function CBeforeHitAfterCalcs(h,target,bone) callbacks.on_before_hit_after_calcs(h,target,bone) end
function printf(fmt,...) logs[#logs+1]=string.format(fmt,...) end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.poltergeist_form");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="mutant_sections" and kind=="mutant")
    if params.catalog_revision~="revision" then return nil,"catalog_revision_mismatch" end
    if params.mutant_sections=="excluded" then return nil,"catalog_section_user_excluded" end
    if not sections[params.mutant_sections] then return nil,"catalog_section_missing" end
    return {{section_id=params.mutant_sections}}
end}
function anomaly_donation_bridge_catalog.is_spawn_safe(section,kind)
    local row=sections[section]
    return kind=="anomaly" and row and row.class~="ZS_TORRD" and not row.custom_data
end
function anomaly_donation_bridge_catalog.is_available(section,kind)
    return not excluded[section] and anomaly_donation_bridge_catalog.is_spawn_safe(section,kind)
end
function anomaly_donation_bridge_catalog.snapshot()
    local entries={}
    for section,row in pairs(sections) do
        if section:match("^zone_") then entries[#entries+1]={section_id=section,kind="anomaly",available=not excluded[section]} end
    end
    table.sort(entries,function(a,b) return a.section_id<b.section_id end)
    return {revision="revision",entries=entries}
end
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_poltergeist_form")
module.on_game_start();assert(descriptor and descriptor.is_supported())
for _,id in ipairs({"spawn_npcs","spawn_mutants","spawn_npc_squads","spawn_mutant_squads",
    "spawn_random_hostile_squad","spawn_random_mutant_pack","spawn_random_npcs","spawn_random_mutants",
    "spawn_named_companion","spawn_mutant_companion","spawn_donor_boss",
    "hardcoon.adb.mutant_companion_v2","hardcoon.adb.helicopter_boss"}) do
    assert(descriptor.allow_while_blocking[id]==true,"Concurrent spawn missing: "..id)
end
assert(not descriptor.allow_while_blocking["hardcoon.adb.play_guitar"] and not descriptor.allow_while_blocking["hardcoon.adb.poltergeist_form"])
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
    assert(descriptor.execute(context,p)=="executed");assert(active() and custom and camera==0 and not next(attachments))
    assert(next(copied_files)==nil,"Private particle probe temporary file was left behind")
    return attachments[NAME]
end
local function stopped(expected_walk)
    assert(not active() and not attachments[NAME] and not custom and camera==2)
    assert(actor.hp==0.77 and actor.visual=="mod/actor" and _HUD_STATE==true)
    assert(math.abs(walk_accel-(expected_walk or original_walk))<0.00001,"Form speed leaked after cleanup")
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
        return tonumber(anomaly_donation_bridge_common.game_text_utf8(wnd.poltergeist_ui.hp.main.text):match("(%d+)%% HP"))
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

local function ui() for wnd in pairs(hud.windows) do return wnd.poltergeist_ui end end
local function text(s) return anomaly_donation_bridge_common.game_text_utf8(s) end
local function begin(options)
    initial_dir=v(0,-0.2,1);local p=params()
    for key,value in pairs(options or {}) do p[key]=value end
    local context={};local allowed,status,why=descriptor.can_execute(context,p)
    assert(allowed,tostring(status)..":"..tostring(why))
    local outcome,reason=descriptor.execute(context,p)
    assert(outcome=="executed",tostring(reason));advance(1);assert(active() and custom and camera==0 and not next(attachments))
end
local function count_zones() local n=0;for _ in pairs(servers) do n=n+1 end;return n end
local function stop()
    key(DIK_keys.DIK_ESCAPE);stopped()
    assert(count_zones()==0,"Anomaly outlived form")
    targets={};wall=nil;wall_flags=0;actor.pos=v();actor.velocity=v()
end
local function cast()
    local count=#spawns;assert(not key(DIK_keys.MOUSE_1,99).ret_value);advance(1)
    assert(#spawns==count+1,"No anomaly spawned")
    local zone=spawns[#spawns]
    assert(zone.data.object_flags==31 and zone.data.object_flags%1024<512,"Zone can enter a native save")
    assert(zone.data.shapes[1].shtype==0 and zone.data.shapes[1].radius==3,"Zone has no real active shape")
    return zone
end
local function near_zone(zone,kind,offset)
    local target=enemy(kind or "npc",zone.position.z+(offset or 0));target.pos.x=zone.position.x
    targets[#targets+1]=target;return target
end
local p=params();p.catalog_revision="old";reject(p,"catalog_revision_mismatch")
p=params();p.mutant_sections="a_dog";reject(p,"poltergeist_visual_class_unsupported")
p=params();p.anomaly_key="R";reject(p,"poltergeist_ability_keys_duplicate")
p=params();p.cycle_key="W";reject(p,"poltergeist_ability_key_invalid")
for _,value in ipairs({"0","61","2.5","NaN"}) do p=params();p.anomaly_lifetime_seconds=value;reject(p,"poltergeist_anomaly_lifetime_invalid") end
local hook=CAI_Stalker__BeforeHitCallback;CAI_Stalker__BeforeHitCallback=nil
assert(not descriptor.is_supported());CAI_Stalker__BeforeHitCallback=hook
local move_setter=actor.set_actor_walk_accel;actor.set_actor_walk_accel=nil
assert(not descriptor.is_supported());reject(params(),"poltergeist_form_api_unavailable");actor.set_actor_walk_accel=move_setter
fail_move_read=true
assert(descriptor.execute({},params())=="deferred" and not active());fail_move_read=false
begin()
assert(math.abs(walk_accel-original_walk*1.5)<0.00001,"Form did not gain 50% movement speed")
assert(form_hp()==100 and text(ui().keys.anomaly.main.text)=="ЛКМ" and text(ui().keys.tele.main.text)=="ПКМ")
assert(text(ui().labels.anomaly.main.text)=="Создать аномалию" and text(ui().labels.tele.main.text)=="Гравитация (удерживать)")
assert(ui().ring.texture=="adb_burer_aim_ring" and math.abs(ui().left+ui().width/2-512)<0.01)
local cloud,idle=fx[#fx-1],fx[#fx]
assert(cloud.active and cloud.name=="monsters\\polter_linza" and cloud.pos.y>1.1 and cloud.pos.y<1.3,"No hovering cloud")
assert(idle.active and idle.name=="monsters\\polter_idle" and idle.pos.y==cloud.pos.y,"Native electrical body layer is missing")
local old_height=cloud.pos.y;advance(900)
assert(cloud.pos.y~=old_height and cloud.pos.y>1.1 and cloud.pos.y<1.3,"Cloud did not bob within safe height bounds")
actor.pos=v(120,7,-45);advance(16)
assert(cloud.pos.x==120 and cloud.pos.z==-45 and cloud.pos.y>8.1 and cloud.pos.y<8.3,"Body has a double-translated initial/world position")
assert(idle.pos.x==cloud.pos.x and idle.pos.y==cloud.pos.y and idle.pos.z==cloud.pos.z,"Body layers split during movement")
assert(cloud.sample_particle.x==120.25 and idle.sample_particle.x==120.25 and cloud.transform_updates>=3 and idle.transform_updates>=3,
    "Already-emitted body particles stayed behind")
paused=true;local saved_height=cloud.pos.y;advance(5000);assert(cloud.pos.y==saved_height)
paused=false;advance(1);assert(math.abs(cloud.pos.y-saved_height)<0.001,"Paused bobbing accumulated into a position jump")
actor_hit(0.5);advance(1);assert(form_hp()==90 and actor.health==0.77,"Default 500% form capacity is wrong")
local names={"Гравитационная","Огненная","Электро","Химическая"}
local colors={{255,220,70},{255,65,55},{100,200,255},{100,230,110}}
for i=1,4 do
    assert(text(ui().labels.cycle.main.text)==names[i])
    assert(ui().labels.cycle.main.color==GetARGB(255,unpack(colors[i])))
    key(DIK_keys.DIK_R,99);advance(1)
end
assert(text(ui().labels.cycle.main.text)==names[1])
key(DIK_keys.DIK_R,99,"on_before_key_hold");advance(1)
assert(text(ui().labels.cycle.main.text)==names[1],"Holding R repeated type changes")
for _,binding in ipairs({key_bindings.kINVENTORY,key_bindings.kACTIVE_JOBS,key_bindings.kCUSTOM20}) do assert(not key(100,binding).ret_value) end
stop()

-- Actual merged hit_type chooses category, not misleading section/class names.
local hit_types={strike=true,explosion=true,light_burn=true,shock=true,chemical_burn=true}
for kind=1,4 do
    begin()
    for _=2,kind do key(DIK_keys.DIK_R,99);advance(1) end
    local zone=cast();assert(hit_types[sections[zone.section_id].hit_type])
    assert(zone.section_id~="zone_scripted" and zone.section_id~="zone_torrid" and zone.section_id~="zone_rad" and
        sections[zone.section_id].class~="ZS_BFUZZ","Animated visual zone was selected for dynamic creation")
    local npc=near_zone(zone,"npc");local mutant=near_zone(zone,"mutant",1);mutant.pos.x=zone.position.x+1
    local immune=near_zone(zone,"npc",-1);immune.immune=true
    advance(500)
    assert(#npc.hits==1 and npc.health<1,"NPC ignored the scripted anomaly")
    assert(#mutant.hits==1 and mutant.health<1,"Mutant ignored the scripted anomaly")
    assert(#immune.hits==1 and immune.health==1,"Native immunity was bypassed")
    assert(npc.hits[1].type==hit[sections[zone.section_id].hit_type],"Wrong elemental hit type")
    assert(math.abs(npc.hits[1].power-0.5)<0.0001,"Attack strength was scaled twice/not scaled")
    local h={power=10,weapon_id=zone.id,draftsman=zoneclients[zone.id]}
    local flags={ret_value=true};callbacks.npc_on_before_hit(npc,h,7,flags)
    assert(not flags.ret_value and h.power==0,"Native zone added duplicate mob damage")
    local foreign={power=10,weapon_id=65000,draftsman=actor};flags={ret_value=true}
    callbacks.npc_on_before_hit(npc,foreign,7,flags)
    assert(flags.ret_value and foreign.power==10,"Unrelated hit was modified")
    flags={ret_value=false};callbacks.anomaly_on_before_activate(zoneclients[zone.id],npc,flags)
    assert(flags.ret_value,"Own zone did not activate native visual effects")
    advance(250);assert(ui().notice.main.text:match("^%-%d+$"),"Damage number missing")
    stop()
end
-- Passable foliage may not intercept aiming/ground/visibility rays; a solid
-- ActorObstacle still blocks even if a material also has the Passable flag.
begin();wall=2;local solid_zone=cast();local solid_z=solid_zone.position.z;stop()
begin();wall=2;wall_flags=128;local through_foliage=cast()
assert(through_foliage.position.z>solid_z+1,"Passable foliage still masks the point under the crosshair");stop()
begin();wall=2;wall_flags=128+4096;local obstacle_zone=cast()
assert(obstacle_zone.position.z<=solid_z+0.1,"ActorObstacle was ignored as if it were foliage");stop()
-- Spawning is not activation. Native awakening must happen without an NPC,
-- once the client exists, and never reset the blowout every render frame.
do
    begin();targets={};local zone=cast();local client=zoneclients[zone.id]
    zoneclients[zone.id]=nil;advance(16)
    assert(not client.activations and active(),"Offline zone was activated by stale userdata")
    zoneclients[zone.id]=client;advance(16)
    assert(client.enabled and client.activations==1,"Empty spawned anomaly stayed dormant")
    for i=1,10 do advance(16) end
    assert(client.activations==1,"Native awakening restarted every frame")
    advance(2450);assert(client.activations==2,"Anomaly did not rearm after its native cycle")
    stop();local count=#activation_events;advance(10000)
    assert(#activation_events==count,"Released anomaly still received activation events")
    begin();zone=cast();client=zoneclients[zone.id];client.enabled=false
    advance(16);assert(client.enable_pending and not client.activations,"Awakening raced native enable/IDLE event")
    advance(16);assert(not client.activations,"Awakening sent before native enable completed")
    client.enabled=true;client.enable_pending=nil;advance(16)
    assert(client.activations==1,"Disabled zone did not awaken after native enable completed")
    stop()
    for _,enabled in ipairs({true,false}) do
        begin();zone=cast();client=zoneclients[zone.id]
        client.is_enabled_anomaly=nil;client.enabled=enabled
        client.enable_anomaly=function() error("Legacy path must not enqueue a later IDLE event") end
        advance(16)
        assert(active() and client.enabled and client.activations==1,"May API cancelled form or left the zone dormant")
        for i=1,10 do advance(16) end
        assert(client.activations==1,"Legacy path restarted awakening every frame")
        advance(2450);assert(client.activations==2,"Legacy path did not rearm")
        stop();local count=#activation_events;advance(10000)
        assert(#activation_events==count and count_zones()==0,"Legacy zone outlived form")
    end
end
-- Even if a broad catalog calls visual zones safe, no animated editor-only
-- native class may enter our pool, under ANY elemental hit_type.
for section in pairs(sections) do if section:match("^zone_") and sections[section].class~="ZS_BFUZZ" then excluded[section]=true end end
reject(params(),"poltergeist_anomaly_pool_empty");excluded={}
begin({duration_seconds="120"})
for _=1,32 do cast();advance(999);key(DIK_keys.DIK_R,99);advance(1) end
assert(active() and count_zones()<=10,"Max-rate anomaly stress leaked zones/state");stop()
-- Shared exact 1s cooldown, no parenthesized timer, no hold/type bypass.
begin();local first=cast();local first_count=#spawns
assert(text(ui().labels.anomaly.main.text)=="Создать аномалию")
key(DIK_keys.DIK_R,99);advance(1);key(DIK_keys.MOUSE_1,99);advance(1)
assert(#spawns==first_count and text(ui().notice.main.text)=="Не готово")
key(DIK_keys.MOUSE_1,99,"on_before_key_hold");advance(997);assert(#spawns==first_count)
cast();assert(#spawns==first_count+1 and text(ui().labels.anomaly.main.text)=="Создать аномалию")
key(DIK_keys.MOUSE_1,99,"on_before_key_hold");advance(1000);assert(#spawns==first_count+1,"Held LMB repeated a cast when ready")
stop()

-- Each cast samples its type's live, non-quarantined pool.
-- At the exact expiration boundary an old zone must free the slot BEFORE cast.
begin()
for i=1,10 do cast();if i<10 then advance(999) end end
local boundary_count=#spawns;advance(999);key(DIK_keys.MOUSE_1,99);advance(1)
assert(#spawns==boundary_count+1,"Expired zone blocked the exact 1s cast boundary");stop()
begin();local random=math.random
math.random=function(first,last) return last and first or first end
local zone=cast();math.random=random
assert(zone.section_id=="zone_pull_b","Cast did not select the other random gravity section");stop()
excluded.zone_pull_b=true
begin();zone=cast();assert(zone.section_id=="zone_pull_a","User-excluded anomaly was spawned");stop();excluded.zone_pull_b=nil
begin();excluded.zone_pull_a=true;excluded.zone_pull_b=true
local before=#spawns;key(DIK_keys.MOUSE_1,99);advance(1)
assert(#spawns==before and text(ui().notice.main.text)=="Не получилось")
stop();excluded={}
-- No damage through walls, across floors, beyond radius, or to dead entities.
begin();zone=cast()
local target=near_zone(zone,"npc",2);local far=near_zone(zone,"mutant",4);local dead=near_zone(zone,"npc",-1);dead.living=false
local upper=near_zone(zone,"npc",0);upper.pos.y=5
wall=zone.position.z+1;advance(500)
assert(#target.hits==0 and #far.hits==0 and #dead.hits==0 and #upper.hits==0,"Anomaly ignored cover/floor/radius/death")
wall=nil;advance(1000);assert(#target.hits==1);stop()
for _,strength in ipairs({10,100,350,9000}) do
    begin({attack_power_percent=tostring(strength)});zone=cast();target=near_zone(zone)
    advance(500);assert(math.abs(target.hits[1].power-0.25*strength/100)<0.00001);stop()
end
-- Expiration, pause, actual lifetime and removal on all form exits.
begin({anomaly_lifetime_seconds="2"});zone=cast()
paused=true;advance(10000);assert(servers[zone.id]);paused=false
advance(1999);assert(servers[zone.id]);advance(1);assert(not servers[zone.id] and active());stop()
for _,event in ipairs({"on_before_save_input","on_before_load_input","GUI_on_show","main_menu_on_quit"}) do
    begin();cast();callbacks[event]();if event=="on_before_save_input" then cancel_saved_form() else stopped() end;assert(count_zones()==0)
end
begin();cast();descriptor.on_level_changing();stopped();assert(count_zones()==0)
begin({duration_seconds="1"});cast();advance(1000);stopped();assert(count_zones()==0)
actor.health=0.42;begin();cast();actor_hit(100);advance(1);stopped()
assert(actor.health==0.42 and count_zones()==0);actor.health=0.77
-- Save callback is safe even if native serialization has already started.
begin();cast();local data={};callbacks.save_state(data);cancel_saved_form();assert(count_zones()==0)
assert(data[SAVE].version==3 and data[SAVE].zones==nil and data[SAVE].movement.before==original_walk and
    data[SAVE].movement.applied==original_walk*1.5,"Missing safe speed repair / temporary anomalies entered save recovery state")
data[SAVE].form_state=nil;memory={[SAVE]=data[SAVE]};walk_accel=data[SAVE].movement.applied;actor.health=0.21;camera=0;custom=true
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.77,"Loaded save failed to restore rider HP")
data[SAVE].form_state=nil;memory={[SAVE]=data[SAVE]};walk_accel=15
descriptor.on_game_load();descriptor.update();stopped(15);walk_accel=original_walk
memory={[SAVE]={version=2,actor_health=0.77,actor_visual="mod/actor"}};actor.health=0.21
descriptor.on_game_load();descriptor.update();stopped();assert(actor.health==0.77,"PF-002 recovery journal compatibility lost")
-- A verified service save briefly removes the form/zones, then restores time/type/cooldown, not full lifetime.
begin();key(DIK_keys.DIK_R,99);advance(1);zone=cast();advance(1000)
descriptor.on_effect_save("before","polter-service");assert(active() and not attachments[NAME] and count_zones()==0 and walk_accel==original_walk)
callbacks.save_state({});descriptor.on_effect_save("after","polter-service",true)
advance(16);assert(count_zones()==0,"Form resumed before matching save file")
service_files["polter-service.scop"]=true;advance(16)
assert(active() and count_zones()==1 and text(ui().labels.cycle.main.text)=="Огненная" and walk_accel==original_walk*1.5)
local restored=spawns[#spawns];assert(restored.id~=zone.id)
advance(8999);assert(servers[restored.id]);advance(1);assert(not servers[restored.id]);stop()
-- Packet/create failures roll back owned entities and don't consume cast cooldown.
for _,mode in ipairs({"ignore","after","create"}) do
    begin();if mode=="create" then fail_create=true else fail_packet=mode end
    key(DIK_keys.MOUSE_1,99);advance(1);assert(count_zones()==0 and active(),"Partial spawn leaked")
    cast();stop()
end
-- Cleanup retry retains ownership instead of forgetting a failed release.
begin();invalid_nav=true;before=#spawns;key(DIK_keys.MOUSE_1,99);advance(1)
assert(#spawns==before and text(ui().notice.main.text)=="Путь перекрыт");invalid_nav=false;cast();stop()
begin();no_floor=true;before=#spawns;key(DIK_keys.MOUSE_1,99);advance(1)
assert(#spawns==before);no_floor=false;cast();stop()
begin({anomaly_lifetime_seconds="60"})
for _=1,10 do cast();advance(1000) end
assert(count_zones()==10);before=#spawns;key(DIK_keys.MOUSE_1,99);advance(1)
assert(#spawns==before and text(ui().notice.main.text)=="Не готово","Live-zone cap was bypassed");stop()
begin();cast();fail_release=true;key(DIK_keys.DIK_ESCAPE);stopped();assert(count_zones()==1)
descriptor.update();assert(count_zones()==0)
begin();cast();fail_release=true;descriptor.on_level_changing();stopped();assert(count_zones()==1)
game_level="another_level";descriptor.update();assert(count_zones()==0,"Old-level cleanup was forgotten")
game_level="test_level"
-- A recycled object ID / unrelated loaded world must never be released.
begin();zone=cast();local replaced=servers[zone.id];replaced.section_id="zone_foreign"
key(DIK_keys.DIK_ESCAPE);stopped();assert(servers[zone.id]);servers[zone.id]=nil;zoneclients[zone.id]=nil
begin();cast();servers={};zoneclients={};attachments={};hud.windows={};custom=false;camera=2;memory={};walk_accel=15
for _,particle in ipairs(fx) do particle.active=false end -- native world teardown
descriptor.on_game_load();descriptor.update();stopped(15);walk_accel=original_walk
-- Physical gravity: living creatures are never seized; free objects are thrown.
begin()
local prop=enemy("prop",4);targets={prop};local shell={}
function shell:get_elements_number() return 1 end
function shell:get_element_by_order() return {is_fixed=function() return false end,get_mass=function() return 50 end} end
function shell:apply_force() end
function shell:set_linear_vel(velocity) self.velocity=copy(velocity) end
function prop:get_physics_shell() return shell end
key(DIK_keys.MOUSE_2,99);advance(1);assert(shell.velocity,"Gravity did not lift a free object")
key(DIK_keys.MOUSE_2,99,"on_before_key_release");advance(1)
assert(math.abs(math.sqrt(shell.velocity.x^2+shell.velocity.y^2+shell.velocity.z^2)-100)<0.0001)
stop()
-- Mandatory cloud failure must reject cleanly, never fall back to a crawler.
fail_cloud=true;local context={};assert(descriptor.can_execute(context,params()))
assert(descriptor.execute(context,params())=="rejected");stopped();assert(not next(attachments))
fail_cloud="monsters\\polter_idle"
assert(descriptor.execute({},params())=="rejected");stopped()
fail_setup=true;assert(descriptor.execute({},params())=="rejected");stopped()
for _,before_write in ipairs({true,false}) do
    fail_move_before=before_write;fail_move=not before_write
    assert(descriptor.execute({},params())=="rejected");stopped()
end
-- Other mods own their changes; ending the form must not overwrite them.
begin();walk_accel=19;advance(1);assert(active() and walk_accel==19,"External speed edit cancelled the form")
key(DIK_keys.DIK_ESCAPE);stopped(19);walk_accel=original_walk
begin();actor.visual="mod/another_actor";advance(1)
assert(not active() and walk_accel==original_walk,"Outfit/visual replacement leaked the form's speed")
actor.visual="mod/actor";stopped()
-- Repeated service saves must never compound the 50% multiplier.
begin()
for i=1,3 do
    local save="polter-speed-"..i
    descriptor.on_effect_save("before",save);assert(walk_accel==original_walk)
    callbacks.save_state({});descriptor.on_effect_save("after",save,true);service_files[save..".scop"]=true;advance(16)
    assert(active() and walk_accel==original_walk*1.5,"Service resume stacked the movement multiplier")
end
descriptor.on_effect_save("before","failed-speed-save");assert(walk_accel==original_walk)
callbacks.save_state({});descriptor.on_effect_save("after","failed-speed-save",false);advance(16);stopped()
-- Configured, verified fire resources from a mod must take precedence over stock fallbacks.
sections.arbitrary_boss_x.Particles_Hidden="mod\\configured_fire_lens"
sections.arbitrary_boss_x.Particles_Idle="mod\\configured_fire_idle"
actor.pos=v(120,3,-40);begin()
assert(fx[#fx-1].name=="mod\\configured_fire_lens" and fx[#fx].name=="mod\\configured_fire_idle")
assert(fx[#fx-1].pos.x==120 and fx[#fx].pos.x==120 and fx[#fx].pos.y>4.1 and fx[#fx].pos.y<4.3)
stop();sections.arbitrary_boss_x.Particles_Hidden=nil;sections.arbitrary_boss_x.Particles_Idle=nil
for _,message in ipairs(logs) do
    assert(not message:find(" damage:",1,true) and not message:find("form ability:",1,true) and
        not message:find("form started:",1,true) and not message:find("form ended:",1,true),"Routine debug spam")
end
assert(next(copied_files)==nil and copy_count==delete_count)
-- No valid cloud resource: reject before acquiring camera/input/health leases.
particle_file=chunk(1,string.char(1,0))..chunk(3,chunk(0,chunk(2,"monsters\\polter_linza\0")))
local lens_only=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_poltergeist_form");lens_only.on_game_start()
reject(params(),"poltergeist_cloud_unavailable");stopped()
particle_file=chunk(1,string.char(1,0))
local fresh=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_poltergeist_form");fresh.on_game_start()
reject(params(),"poltergeist_cloud_unavailable");stopped()
-- Largest form duration is accepted and expires without timer overflow.
particle_file=particle_data
module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_poltergeist_form");module.on_game_start()
targets={};begin({duration_seconds="60000"});advance(59999000);assert(active(),"60000-second form expired early")
advance(1000);stopped()
reject(params("60001"),"poltergeist_duration_invalid")

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
    for wnd in pairs(hud.windows) do view=wnd.poltergeist_ui end
    assert(view and view.keys.camera.main.text=="V")
    local old_type=view.labels.cycle.main.text;key(DIK_keys.DIK_R)
    assert(not key(DIK_keys.DIK_V).ret_value);advance(16)
    assert(view.labels.cycle.main.text~=old_type,"V discarded queued anomaly-type switch")
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
    p=params();p.camera_mode="invalid";reject(p,"poltergeist_camera_mode_invalid")
    p=params();p.camera_key="MOUSE_1";reject(p,"poltergeist_ability_keys_duplicate")
end
-- New session-gated F5/F9 behavior, through the actual module callbacks.
assert(loadfile(core.."/../../../tests/lua-fixtures/form_save_roundtrip.lua"))()({
    name="poltergeist",key=SAVE,actor=actor,callbacks=callbacks,
    start=function() start("20") end,stopped=stopped,advance=advance,active=active,
    live=function() return custom end,descriptor=function() return descriptor end,
    cancel=function() callbacks.on_before_load_input() end,
    set_memory=function(value) memory=value end,set_paused=function(value) paused=value end,
    elapse_silent=function(ms) now=now+ms end,
    file_exists=function(name) service_files[name..".scop"]=true end,
    reload_common=function() anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common") end
})

print("Poltergeist PF-009 smoke passed (mock API): retail net_packet API/native zone awakening, animated-zone MotionID crash preflight, max-rate 32-cast cleanup, passable foliage/ActorObstacle aiming, native two-layer sphere/no trail, configured resources, 150% speed/no save stacking, exact shared 1s cooldown, typed damage/cover/immunity and isolated save/load/service resume.")

