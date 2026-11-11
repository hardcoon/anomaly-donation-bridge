local core,optional=assert(arg[1]),assert(arg[3])
local function load_module(root,name)
    local env=setmetatable({},{__index=_G});local chunk=assert(loadfile(root.."/"..name..".script"))
    setfenv(chunk,env);chunk();return env
end
local function copy(t) if type(t)~="table" then return t end;local r={};for k,v in pairs(t) do r[k]=copy(v) end;return r end
local function v(x,y,z)
    return {x=x or 0,y=y or 0,z=z or 0,set=function(self,a,b,c) self.x,self.y,self.z=a,b,c;return self end,
        mul=function(self,n) self.x,self.y,self.z=self.x*n,self.y*n,self.z*n;return self end,
        getH=function(self) return math.atan2(-self.x,self.z) end,
        getP=function(self) return math.atan2(self.y,math.sqrt(self.x*self.x+self.z*self.z)) end}
end
vector=function() return v() end;vector2=function() return {set=function(self,x,y) self.x,self.y=x,y;return self end} end
Frect=function() return {set=function(self,...) return self end} end
local now,paused,blocked,model_missing,spawn_fail,packet_fail,audio_fail,hidden=1000,false,false,false,false,false,false,false
local serial,releases,plays,camera_sets,camera_removes,logs=0,0,0,0,0,0
local actor_bone_writes,legacy_bone_recovery,body_debug_mode,camera_mode_fail=0,false,"0",false
local frame,raycasts,child_iterating,spawn_loadout,obstruction_range=0,0,false,false,0
local delayed_release_id=nil
local defer_all_releases=false
local slot_activation_calls=0
local function make_debug_tools(previous)
    return {CMD={invisible=function() error("No debug UI/console command may be invoked") end},debug_invis=previous}
end
USE_MARSHAL=true;DEV_DEBUG=false;xrs_debug_tools=make_debug_tools()
local function forbidden_slot_activation(_,slot)
    slot_activation_calls=slot_activation_calls+1
    -- Do not bless 65535 as "empty hands" in a mock. In the real engine it
    -- trips a non-recoverable CInventory::Activate assertion, not a Lua error.
    error("Guitar must never activate an inventory slot: "..tostring(slot))
end
local memory,servers,clients,callbacks={},{},{},{}
local legacy_mouse=arg[4]=="legacy-ui"
local native_mouse=function() return false end
level_input=not legacy_mouse and {on_mouse_move=native_mouse} or nil
local MODULE="anomaly_donation_bridge_effect_hardcoon_adb_play_guitar"
local SECTION="adb_guitar_double";local KEY="adb_guitar_scene_v1"
local function skeletal(o)
    o.visible={root=true,bip01_l_hand=true,helmet=false}
    function o:list_bones() return {[0]="root",[1]="bip01_l_hand",[2]="helmet"} end
    function o:bone_visible(id) return self.visible[self:list_bones()[id]] end
    function o:set_bone_visible(id,value,recursive,hud)
        assert(recursive==false and hud==false)
        -- Native LL_SetBoneVisible(true) restores a cached hidden transform,
        -- even for an already visible bone: only write genuine differences.
        assert(self:bone_visible(id)~=value,"Redundant bone visibility write")
        self.visible[self:list_bones()[id]]=value
    end
    return o
end
local actor=skeletal({visual="mod/actor_model.ogf",pos=v(),living=true,velocity=v()})
local actor_set_bone_visible=actor.set_bone_visible
function actor:set_bone_visible(...)
    actor_bone_writes=actor_bone_writes+1
    assert(legacy_bone_recovery,"New guitar scenes must never modify the actor skeleton/bounds")
    actor_set_bone_visible(self,...)
end
function actor:get_visual_name() return self.visual end
function actor:position() return self.pos end
function actor:direction() return v(0,0,1) end
function actor:alive() return self.living end
function actor:is_talking() return false end
function actor:get_current_holder() return self.holder end
function actor:active_slot() return 3 end
actor.activate_slot=forbidden_slot_activation
function actor:level_vertex_id() return 11 end
function actor:game_vertex_id() return 22 end
function actor:get_movement_speed() return self.velocity end
function actor:set_movement_speed(speed)
    assert(_GUIs.ADBGuitarScene and speed.x==0 and speed.y==0 and speed.z==0,"Input must be captured before stopping velocity")
    self.velocity=copy(speed)
end
db={actor=actor};_HUD_STATE=true;_GUIs={}
actor_menu={is_hud_free=function() return true end}
function time_global() return now end
function device() return {frame=frame,is_paused=function() return paused end} end
alife_storage_manager={get_state=function() return memory end}
local sim={}
function sim:object(id) return servers[id] end
function sim:set_switch_online(id,enabled)
    local s=assert(servers[id])
    if enabled and s.sec==SECTION then
        assert(s.packet.community_index==2 and s.packet.g_team==19,"Trader must be assigned BEFORE online")
        assert(s.packet.object_flags%1024<512,"Temporary double must be excluded BEFORE online")
    end
    s.online=enabled
end
function sim:set_switch_offline(id,enabled) servers[id].offline=enabled end
function sim:get_children(parent)
    assert(parent.sec==SECTION)
    local ids={};for id,s in pairs(servers) do if s.parent_id==parent.id then ids[#ids+1]=id end end
    for _,id in ipairs(parent.stale_children or {}) do ids[#ids+1]=id end
    table.sort(ids);local index=0;child_iterating=true
    return function() index=index+1;local id=ids[index];if not id then child_iterating=false end;return id end
end
function sim:release(o)
    assert(not child_iterating,"Release invalidates a live child iterator")
    assert(o.sec==SECTION or o.sec=="guitar_a" or o.parent_id and servers[o.parent_id] and servers[o.parent_id].sec==SECTION)
    releases=releases+1
    if not defer_all_releases and o.id~=delayed_release_id then servers[o.id]=nil;clients[o.id]=nil end
end
function alife() return sim end
local function make_server(sec,parent)
    if spawn_fail then error("spawn_failed") end
    serial=serial+1;local s={id=serial,sec=sec,parent_id=parent,identity=sec..serial,
        packet={object_flags=-1,community_index=0,g_team=4,g_squad=7,g_group=9}}
    function s:name() return self.identity end
    function s:section_name() return self.sec end
    servers[s.id]=s;return s
end
function alife_create(sec,p,lv,gv)
    assert(sec==SECTION and lv==11 and gv==22 and _GUIs.ADBGuitarScene and hidden)
    assert(p.x==actor.pos.x and p.y==actor.pos.y and p.z==actor.pos.z,"Spawn used a stale actor position")
    local s=make_server(sec);s.pos=copy(p)
    if spawn_loadout then make_server("mod_scoped_rifle",s.id);make_server("mod_ammo",s.id) end
    return s
end
function alife_create_item(sec,parent) assert(sec=="guitar_a" and parent.sec==SECTION);return make_server(sec,parent:id()) end
utils_stpk={get_stalker_data=function(s) return copy(s.packet) end,
    set_stalker_data=function(data,s) if packet_fail then error("packet_failed") end;s.packet=copy(data) end}
utils_stpk.get_item_data=utils_stpk.get_stalker_data
utils_stpk.set_item_data=utils_stpk.set_stalker_data
local settings={[SECTION]={class="AI_STL_S",script_binding=MODULE..".bind"},
    game_relations={communities="actor, 4, custom_faction, 83, trader, 19, ecolog, 42"},
    guitar_a={class="II_ATTCH",attach_bone_name="bip01_l_hand",visual="guitar.ogf"}}
function system_ini() return {section_exist=function(_,s) return settings[s]~=nil end,
    line_exist=function(_,s,k) return settings[s] and settings[s][k]~=nil end,
    r_string=function(_,s,k) return settings[s][k] end} end
function getFS() return {exist=function(_,alias,path) return not model_missing end} end
function get_console() return {get_string=function(_,name) assert(name=="show_actor_body");return body_debug_mode end} end
game={motion_exists=function(_,motion) return motion~=_G.missing_motion end}
level={camera=2,object_by_id=function(id) return clients[id] end,
    get_active_cam=function() return level.camera end,set_active_cam=function(n)
        level.camera=n;if camera_mode_fail then camera_mode_fail=false;error("camera_mode_failed") end
    end,
    set_cam_custom_position_direction=function(p,angles,smooth,hud,affect)
        assert(smooth==1 and hud==false and affect==false);camera_sets=camera_sets+1;level.last_eye=p
    end,remove_cam_custom_position_direction=function() camera_removes=camera_removes+1 end,
    ray_pick=function(_,_,_,_,result) raycasts=raycasts+1;result.range=obstruction_range;return blocked end}
rq_target={rqtStatic=1};rq_result=function() return {} end
function show_indicators(shown) _HUD_STATE=shown;hidden=not shown end
function SetCursorPosition() assert(legacy_mouse,"Raw orbit must not warp the desktop cursor") end
function Register_UI(name) _GUIs[name]=true end
function Unregister_UI(name) _GUIs[name]=nil end
function RegisterScriptCallback(name,f) callbacks[name]=f end
function printf() logs=logs+1 end
ui_events={WINDOW_MOUSE_MOVE=1,WINDOW_KEY_PRESSED=2};DIK_keys={DIK_ESCAPE=1,DIK_F5=2,DIK_F9=3}
local sound_ends
local forwarded
function level.press_action(dik) assert(not hidden and not memory[KEY] and next(_GUIs)==nil);forwarded=dik end
sound_object=setmetatable({},{__call=function(_,path)
    assert(path=="characters_voice/human/music/custom_7")
    if audio_fail then error("audio_failed") end
    return {length=function() return 2000 end,play_at_pos=function(_,obj) assert(obj.sec==SECTION);plays=plays+1;sound_ends=now+2000 end,
        stop=function() sound_ends=nil end,playing=function() return sound_ends and now<sound_ends end}
end})
CUIScriptWnd={SetAutoDelete=function() end,SetWndRect=function() end,AllowMovement=function(_,b) assert(not b) end,
    AllowCursor=function(_,b) assert(not b) end,AllowCenterCursor=function() end,
    ShowDialog=function(self) self.shown=true end,HideDialog=function(self) assert(not self.stale);self.shown=false end,
    IsShown=function(self) assert(not self.stale);return self.shown end,Update=function(self) assert(not self.stale) end}
object={idle=0}
object_binder={net_spawn=function() return true end,net_destroy=function() end}
callback={script_animation=7}
local constructing
function super(object) if object then constructing.object=object end end
function class(name)
    local env=getfenv(2);return function(base)
        env[name]=setmetatable({},{__index=base,__call=function(cls,obj)
            local self=setmetatable({},{__index=cls});constructing=self;self:__init(obj);constructing=nil;return self
        end})
    end
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.play_guitar");descriptor=d end}
anomaly_donation_bridge_catalog={validate_sections=function(params,key,kind)
    assert(key=="song_sections" and kind=="guitar_music")
    if params.catalog_revision~="guitar-rev" then return nil,"catalog_revision_mismatch" end
    if params.song_sections~="adb_guitar_custom" then return nil,"catalog_selection_stale_or_quarantined" end
    return {{section_id="adb_guitar_custom"}}
end,guitar_sound=function(id) if id=="adb_guitar_custom" then return "characters_voice/human/music/custom_7" end end}
local module=load_module(optional,MODULE);module.on_game_start();assert(descriptor.is_supported())
if not legacy_mouse then
    assert(type(callbacks.on_before_mouse_move)=="function","Guitar must read raw mouse deltas, not a hidden desktop cursor")
    assert(level_input.on_mouse_move==native_mouse,"Guitar must not replace the game's mouse dispatcher")
else
    assert(callbacks.on_before_mouse_move==nil,"Legacy engine acquired an unavailable callback")
end
local function mouse_move(dx,dy)
    if legacy_mouse then module.GUI:OnMouse(512+dx,384+dy,ui_events.WINDOW_MOUSE_MOVE);return end
    local flags={ret_value=false};callbacks.on_before_mouse_move(dx,dy,false,flags)
    assert(flags.ret_value==true,"Owned guitar scene did not consume its mouse input")
    -- The OS/UI cursor can remain centred despite non-zero DirectInput deltas.
    module.GUI:OnMouse(512,384,ui_events.WINDOW_MOUSE_MOVE)
end
local function start()
    local context={};local ok,state,why=descriptor.can_execute(context,{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
    assert(ok,tostring(state)..":"..tostring(why));assert(descriptor.execute(context)=="executed");return memory[KEY]
end
local function materialize(record)
    local s=assert(servers[record.id]);local o=skeletal({sec=s.sec,object_id=s.id,object_name=s:name(),queue=0,visual="default"})
    function o:id() return self.object_id end
    function o:section() return self.sec end
    function o:name() return self.object_name end
    function o:alive() return true end
    function o:character_community() return self.community end
    function o:invulnerable(b) assert(b) end
    function o:enable_vision(b) assert(not b) end
    function o:movement_enabled(b) assert(not b) end
    function o:set_enable_movement_collision(b) assert(not b) end
    function o:disable_talk() end
    function o:set_item(state) assert(state==object.idle);self.unarmed=true end
    o.activate_slot=forbidden_slot_activation
    function o:set_visual_name(name) self.visual=name end
    function o:get_visual_name() return self.visual end
    function o:clear_animations() self.queue=0 end
    function o:add_animation(name) assert(name~=_G.missing_motion);self.motion=name;self.queue=self.queue+1 end
    function o:animation_count() return self.queue end
    function o:enable_attachable_item(b) assert(b);self.attached=true end
    function o:bind_object(b) self.binder=b end
    function o:set_callback(kind,f,context) assert(kind==callback.script_animation);self.animation_callback=f end
    clients[s.id]=o
    if s.sec==SECTION then
        assert(s.packet.community_index==2 and s.packet.g_team==19)
        o.community="trader";module.bind(o);assert(o.binder:net_spawn(s))
        assert(o.binder:net_save_relevant()==false)
    end
    return o
end
local function render(ms)
    now=now+(ms or 16);frame=frame+1
    if module.GUI then module.GUI:Update();if module.GUI then module.GUI:Update() end end
end
local function tick(ms)
    ms=ms or 100;actor.pos.x=actor.pos.x+actor.velocity.x*ms/1000
    actor.pos.y=actor.pos.y+actor.velocity.y*ms/1000;actor.pos.z=actor.pos.z+actor.velocity.z*ms/1000
    now=now+ms;frame=frame+1;descriptor.update(now)
    if module.GUI then module.GUI:Update() end
end
local function clear()
    if module.GUI then module.GUI:Close() end
    memory={};servers={};clients={};actor.visible={root=true,bip01_l_hand=true,helmet=false}
    actor.pos=v();actor.visual="mod/actor_model.ogf";actor.living=true
    actor.holder=nil;actor.velocity=v();spawn_loadout=false;obstruction_range=0;delayed_release_id=nil;defer_all_releases=false
    DEV_DEBUG=false;xrs_debug_tools=make_debug_tools()
    body_debug_mode="0";legacy_bone_recovery=false;camera_mode_fail=false
    level.camera=2;_GUIs={};_HUD_STATE=true;paused=false;blocked=false;spawn_fail=false;packet_fail=false;audio_fail=false
    descriptor.on_game_load();tick()
end
local function ready_to_play()
    local r=start();local npc=materialize(r.double);tick()
    assert(slot_activation_calls==0,"Unsafe native slot activation attempted in guitar begin")
    assert(npc.motion=="idle_0_to_sit_1")
    npc.queue=0;tick();assert(npc.motion=="sit_1_guitar_0_0")
    npc.queue=0;tick();assert(r.guitar)
    local guitar=materialize(r.guitar);tick();assert(guitar.attached and npc.motion=="sit_1_guitar_0_1")
    npc.queue=0;tick();assert(npc.motion=="guitar_0");return r,npc,guitar
end
local function phase(number)
    local r=start();if number==0 then return r end
    local npc=materialize(r.double);tick();if number==1 then return r end
    npc.queue=0;tick();if number==2 then return r end
    npc.queue=0;tick();if number==3 then return r end
    materialize(r.guitar);tick();if number==4 then return r end
    npc.queue=0;tick();if number==5 then return r end
    tick(2100);if number==6 then return r end
    npc.queue=0;tick();if number==7 then return r end
    npc.queue=0;tick();return r
end

-- Unsupported model/config/UI cannot hide or make the real player immortal.
assert(actor.IsInCar==nil,"Mock must not invent a non-exported engine method")
actor.holder={};local ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
assert(not ok and status=="deferred" and why=="actor_in_vehicle" and not hidden and not memory[KEY])
actor.holder=nil
local holder_method=actor.get_current_holder;actor.get_current_holder=nil
ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
assert(not ok and status=="deferred" and why=="guitar_holder_api_unavailable" and not memory[KEY])
actor.get_current_holder=holder_method
local set_speed_method=actor.set_movement_speed;actor.set_movement_speed=nil
ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
assert(not ok and status=="deferred" and why=="guitar_movement_api_unavailable" and not hidden and not memory[KEY])
actor.set_movement_speed=set_speed_method
actor.velocity=v(0,2,0)
ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
assert(not ok and status=="deferred" and why=="guitar_waiting_for_ground" and not memory[KEY])
actor.velocity=v()
for _,mode in ipairs({"1","2"}) do
    body_debug_mode=mode
    ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
    assert(not ok and status=="deferred" and why=="guitar_actor_body_debug_enabled" and not memory[KEY])
end
body_debug_mode=nil -- Older engines without this optional debug console command.
local debug_context={};assert(descriptor.can_execute(debug_context,{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"}))
body_debug_mode="0"
actor.visible={root=false,bip01_l_hand=false,helmet=false}
ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
assert(not ok and status=="deferred" and why=="guitar_actor_bones_unavailable" and not memory[KEY])
actor.visible={root=true,bip01_l_hand=true,helmet=false}
missing_motion="guitar_0";local ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
assert(not ok and status=="deferred" and why:find("animation_missing",1,true));missing_motion=nil
_HUD_STATE=false;ok,status=descriptor.can_execute({},{});assert(not ok and status=="deferred");_HUD_STATE=true
ok,status=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="old"});assert(not ok and status=="deferred")
assert(not hidden and actor.visible.root and next(servers)==nil)

-- Resolve the technical faction from this build's merged relation list, not
-- from a stock numeric index/team. Missing/malformed entries cannot spawn.
local communities=settings.game_relations.communities
for _,value in ipairs({"actor,0,ecolog,4","actor,0,trader","actor,0,trader,999","actor,0,,19","trader,19,trader,20"}) do
    settings.game_relations.communities=value
    ok,status,why=descriptor.can_execute({},{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"})
    assert(not ok and status=="deferred" and why=="guitar_trader_community_unavailable")
    assert(not hidden and next(servers)==nil)
end
settings.game_relations.communities=communities
local changed_context={};assert(descriptor.can_execute(changed_context,{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"}))
settings.game_relations.communities="actor,0,ecolog,4"
assert(descriptor.execute(changed_context)=="deferred" and not hidden and next(servers)==nil)
settings.game_relations.communities=communities

-- Complete one track, preserving helmet visibility and actual inventory/HP.
local original_position=copy(actor.pos)
DEV_DEBUG=true
local r,npc,guitar=ready_to_play();assert(hidden and actor.visible.root and actor.visible.bip01_l_hand)
assert(xrs_debug_tools.debug_invis==true and not r.debug_invisible and not r.debug_invisible_lease)
assert(r.actor_bones_untouched and actor_bone_writes==0)
assert(actor.pos.x==original_position.x and actor.pos.y==original_position.y and actor.pos.z==original_position.z)
local hit_flags={ret_value=true};callbacks.actor_on_before_hit({},0,hit_flags);assert(hit_flags.ret_value==false)
assert(npc.visual==actor.visual and npc.visible.helmet==false and level.camera==0 and plays==1)
assert(npc.unarmed and slot_activation_calls==0,"Scene changed an inventory slot")
assert(npc:character_community()=="trader")
assert(servers[r.double.id].packet.object_flags==-513 and servers[r.guitar.id].packet.object_flags==-513,
    "Only flCanSave may be cleared from signed flags")
assert(servers[r.double.id].packet.g_squad==0 and servers[r.double.id].packet.g_group==0)
local camera_before=camera_sets;local previous_eye=copy(level.last_eye)
mouse_move(68,16);tick();assert(camera_sets>camera_before)
assert(math.abs(level.last_eye.x-previous_eye.x)>0.01,"Non-zero mouse delta left the orbit camera stationary")
assert(math.abs(level.last_eye.y-previous_eye.y)>0.01,"Vertical mouse delta did not change the orbit pitch")
if not legacy_mouse then
    -- An additional UI event for the SAME movement must not rotate twice.
    previous_eye=copy(level.last_eye)
    module.GUI:OnMouse(700,500,ui_events.WINDOW_MOUSE_MOVE);render()
    assert(level.last_eye.x==previous_eye.x and level.last_eye.y==previous_eye.y and level.last_eye.z==previous_eye.z,
        "Raw and UI input were both applied")
    for _,case in ipairs({"disabled","paused","consumed","invalid","zero"}) do
        local flags={ret_value=case=="consumed"};paused=case=="paused"
        local dx=case=="invalid" and (0/0) or case=="zero" and 0 or 80
        callbacks.on_before_mouse_move(dx,case=="zero" and 0 or 20,case=="disabled",flags)
        paused=false;render()
        assert(level.last_eye.x==previous_eye.x and level.last_eye.y==previous_eye.y and level.last_eye.z==previous_eye.z,
            "Ignored mouse event changed the camera: "..case)
        assert(flags.ret_value==(case=="consumed"),"Ignored mouse event stole input: "..case)
    end
end
-- Render updates must work without a single 100 ms bridge update, once per frame.
local frames_before=camera_sets;local rays_before=raycasts
for _=1,20 do mouse_move(3,1);render(16) end
assert(camera_sets-frames_before==20 and raycasts-rays_before==20,"Camera was throttled or repeated within a frame")
blocked=true;obstruction_range=2;render();assert(memory[KEY],"Normal camera collision cancelled the scene")
local eye=level.last_eye;local dx,dy,dz=eye.x-r.pos.x,eye.y-r.pos.y-0.85,eye.z-r.pos.z
assert(math.sqrt(dx*dx+dy*dy+dz*dz)<=1.801,"Camera passed through the nearest obstacle")
blocked=false
tick(2100);assert(npc.motion=="guitar_0_sit_1_0")
npc.queue=0;tick();assert(not servers[r.guitar and r.guitar.id] and npc.motion=="guitar_0_sit_1_1")
npc.queue=0;tick();assert(npc.motion=="sit_1_to_idle_0")
npc.queue=0;tick();assert(not memory[KEY] and not hidden and actor.visible.root and not actor.visible.helmet and level.camera==2)
assert(next(servers)==nil and camera_removes>0 and plays==1)
if not legacy_mouse then
    local completed_camera_sets=camera_sets
    local flags={ret_value=false};callbacks.on_before_mouse_move(40,10,false,flags);render()
    assert(flags.ret_value==false and camera_sets==completed_camera_sets,
        "Completed guitar scene retained mouse input or updated the camera")
end
assert(xrs_debug_tools.debug_invis==nil,"Completed song left debug invisibility enabled")
assert(actor_bone_writes==0,"Cleanup changed actor bones without hiding them")
hit_flags={ret_value=true};callbacks.actor_on_before_hit({},0,hit_flags);assert(hit_flags.ret_value==true,"Guitar left actor hit immunity behind")

-- Optional debug invisibility: no debug mode, no tools or no command must not
-- block the scene or touch another module/unknown custom state.
for case=1,5 do
    clear();DEV_DEBUG=case~=1
    if case==2 then xrs_debug_tools=nil
    elseif case==3 then xrs_debug_tools.CMD=nil
    elseif case==4 then xrs_debug_tools.CMD.invisible=nil
    elseif case==5 then xrs_debug_tools.debug_invis="custom-owner" end
    r,npc=ready_to_play();assert(not r.debug_invisible_lease)
    if xrs_debug_tools then assert(xrs_debug_tools.debug_invis==(case==5 and "custom-owner" or nil)) end
    module.GUI:Close();assert(not hidden and not memory[KEY])
end
clear();DEV_DEBUG=true;xrs_debug_tools.debug_invis=true;r,npc=ready_to_play()
assert(not r.debug_invisible_lease);module.GUI:Close()
assert(xrs_debug_tools.debug_invis==true,"Effect disabled user-owned invisibility")
clear();DEV_DEBUG=true;xrs_debug_tools.debug_invis=false;r,npc=ready_to_play()
assert(xrs_debug_tools.debug_invis==true and not r.debug_invisible_lease)
module.GUI:Close();assert(xrs_debug_tools.debug_invis==false,"False was not restored exactly")
clear();DEV_DEBUG=true;r,npc=ready_to_play();DEV_DEBUG=false;module.GUI:Close()
assert(xrs_debug_tools.debug_invis==nil,"Turning debug off prevented owned-flag cleanup")
clear();DEV_DEBUG=true;r,npc=ready_to_play();xrs_debug_tools.debug_invis=false;module.GUI:Close()
assert(xrs_debug_tools.debug_invis==false,"Effect overwrote a manual off")
clear();DEV_DEBUG=true;r,npc=ready_to_play();local previous_debug_module=xrs_debug_tools
xrs_debug_tools=make_debug_tools(true);module.GUI:Close()
assert(previous_debug_module.debug_invis==nil and xrs_debug_tools.debug_invis==true,"Replaced debug module was overwritten")

-- Sprint between precondition and execution: stop before anchoring, not after
-- the asynchronously spawned double has already appeared at the old point.
clear();local moving_context={}
assert(descriptor.can_execute(moving_context,{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"}))
actor.pos=v(5,0,7);actor.velocity=v(0,0,8)
assert(descriptor.execute(moving_context)=="executed");r=memory[KEY]
assert(r.pos.x==5 and r.pos.z==7 and actor.velocity.z==0 and hidden and module.GUI)
npc=materialize(r.double);tick(250);assert(memory[KEY] and npc.motion=="idle_0_to_sit_1")
module.GUI:Close();assert(not hidden and not memory[KEY])

-- Dynamic loadouts, including non-weapon supplies, belong only to this prop.
clear();spawn_loadout=true;r=start()
for _,s in pairs(servers) do assert(s.sec==SECTION,"Spawn loadout was not stripped before online") end
npc=materialize(r.double)
local late=make_server("late_mod_weapon",npc:id());local player_item=make_server("player_rifle",0)
servers[r.double.id].stale_children={player_item.id}
tick();assert(not servers[late.id] and servers[player_item.id] and not npc.motion)
tick();assert(npc.motion=="idle_0_to_sit_1" and npc.unarmed)
module.GUI:Close();assert(servers[player_item.id],"A player-owned item was removed")
clear();r=start();npc=materialize(r.double);late=make_server("async_mod_weapon",npc:id())
delayed_release_id=late.id;local requests_before=releases
for _=1,4 do tick() end
assert(releases-requests_before==1 and not npc.motion,"Repeated online GE_DESTROY while waiting")
servers[late.id]=nil;delayed_release_id=nil;tick();assert(npc.motion=="idle_0_to_sit_1")
module.GUI:Close()

-- Esc and save/load input clean up immediately; no future scene over a before save.
clear();r,npc=ready_to_play();module.GUI:OnKeyboard(DIK_keys.DIK_ESCAPE,ui_events.WINDOW_KEY_PRESSED)
assert(not hidden and next(servers)==nil and not memory[KEY])
clear();r,npc=ready_to_play();callbacks.on_before_save_input();assert(not hidden and next(servers)==nil)
clear();r,npc=ready_to_play();module.GUI:OnKeyboard(DIK_keys.DIK_F5,ui_events.WINDOW_KEY_PRESSED)
assert(not hidden and next(servers)==nil);tick();assert(forwarded==DIK_keys.DIK_F5)
clear();r,npc=ready_to_play();module.GUI:OnKeyboard(DIK_keys.DIK_F9,ui_events.WINDOW_KEY_PRESSED)
assert(not hidden and next(servers)==nil);tick();assert(forwarded==DIK_keys.DIK_F9)
for phase_index=0,8 do
    for _,key in ipairs({DIK_keys.DIK_F5,DIK_keys.DIK_F9}) do
        clear();DEV_DEBUG=true;phase(phase_index);assert(xrs_debug_tools.debug_invis==true)
        if module.GUI then module.GUI:OnKeyboard(key,ui_events.WINDOW_KEY_PRESSED)
        elseif key==DIK_keys.DIK_F5 then callbacks.on_before_save_input() else callbacks.on_before_load_input() end
        assert(not hidden and not memory[KEY] and next(servers)==nil and next(_GUIs)==nil,
            "F5/F9 retained a prop or actor lock in animation phase "..phase_index)
        assert(xrs_debug_tools.debug_invis==nil,"F5/F9 retained debug invisibility in phase "..phase_index)
        tick()
    end
end

-- Console save/autosave bypasses the F5 input handler. Reproduce native order:
-- ClientSave first, save_state next, then ALife root/child serialization.
-- Online GE_DESTROY is delayed on purpose: a synchronous-release mock would
-- hide the original defect. No guitar prop may enter the native object save.
local function saved_objects()
    local result={}
    local function visit(s)
        if s.packet.object_flags%1024>=512 then
            result[s.id]=copy(s)
            for _,child in pairs(servers) do if child.parent_id==s.id then visit(child) end end
        end
    end
    for _,s in pairs(servers) do if not s.parent_id or s.parent_id==65535 then visit(s) end end
    return result
end
-- Runtime wraps its own saves with on_effect_save(before/after). In particular,
-- the after-effect save happens BEFORE the double has come online. It must not
-- be mistaken for F5/autosave, otherwise execute reports success but no song starts.
for phase_index=0,8 do
    clear();DEV_DEBUG=true;r=phase(phase_index)
    local menu=module.GUI;local double_id=r.double.id;local guitar_id=r.guitar and r.guitar.id
    local old_releases,old_plays,old_sound=releases,plays,sound_ends
    local saved_camera=level.camera;local snapshot,objects
    for repeat_index=1,2 do
        local name="guitar-service-"..phase_index.."-"..repeat_index
        if descriptor.on_effect_save then descriptor.on_effect_save("before",name) end
        callbacks.save_state(memory)
        snapshot=copy(memory);objects=saved_objects()
        if descriptor.on_effect_save then descriptor.on_effect_save("after",name,true) end
        assert(descriptor.is_blocking() and module.GUI==menu and hidden and memory[KEY]==r,
            "Application service save cancelled the guitar scene in phase "..phase_index)
        assert(releases==old_releases and plays==old_plays and sound_ends==old_sound,
            "Service save recreated a prop or restarted the song")
        assert(servers[double_id] and not objects[double_id] and not objects[guitar_id],
            "Service save serialized a temporary guitar prop")
        local recovery=assert(snapshot[KEY])
        assert(recovery.actor_bones_untouched and recovery.camera==2)
        assert(not recovery.double and not recovery.guitar and not recovery.pos and not recovery.trader)
        assert(not recovery.debug_invisible and xrs_debug_tools.debug_invis==true)
        tick(0);assert(memory[KEY]==r and module.GUI==menu,"Service journal cleanup lost the live scene")
    end
    -- Loading a service save restores normal actor/HUD/camera, not a song,
    -- lock, clone, debug lease or pointer from the world that just disappeared.
    menu.stale=true;servers=objects;clients={};memory=snapshot;level.camera=saved_camera
    descriptor.on_game_load();tick()
    assert(not descriptor.is_blocking() and not memory[KEY] and not hidden and level.camera==2)
    assert(not sound_ends and next(_GUIs)==nil and xrs_debug_tools.debug_invis==nil)
end

-- Match the real startup: an empty before-save, execute, immediate after-save,
-- then the delayed prop spawn/animation updates. The same song must complete.
clear();descriptor.on_effect_save("before","before-command")
callbacks.save_state(memory);descriptor.on_effect_save("after","before-command",true)
r=start();descriptor.on_effect_save("before","after-command")
callbacks.save_state(memory);descriptor.on_effect_save("after","after-command",true)
npc=materialize(r.double);tick();assert(npc.motion=="idle_0_to_sit_1")
npc.queue=0;tick();npc.queue=0;tick();guitar=materialize(r.guitar);tick()
local plays_before_service_song=plays
npc.queue=0;tick();assert(npc.motion=="guitar_0" and plays==plays_before_service_song+1)
tick(2100);npc.queue=0;tick();npc.queue=0;tick();npc.queue=0;tick()
assert(not hidden and not memory[KEY] and next(servers)==nil and not descriptor.is_blocking())

-- Transactions are runtime-only, bounded to their before/after pair and exact
-- scene owner. Failed saves and cancellation/load must never resurrect a song.
for _,observe in ipairs({false,true}) do
    clear();r,npc=ready_to_play();descriptor.on_effect_save("before","failed-save")
    if observe then callbacks.save_state(memory) end
    descriptor.on_effect_save("after","failed-save",false)
    assert(not hidden and not memory[KEY] and not sound_ends and next(servers)==nil)
end
clear();r,npc=ready_to_play();descriptor.on_effect_save("before","no-native-save")
descriptor.on_effect_save("after","no-native-save",true)
callbacks.save_state(memory)
assert(not hidden and not descriptor.is_blocking(),"Unobserved service save exempted a later autosave")
clear();r,npc=ready_to_play();descriptor.on_effect_save("before","old-scene")
callbacks.save_state(memory);module.GUI:Close();local new_record=start();local new_menu=module.GUI
descriptor.on_effect_save("after","old-scene",true)
assert(memory[KEY]==new_record and module.GUI==new_menu and hidden,"Old save resurrected a cancelled scene")
callbacks.save_state(memory);assert(not hidden and not descriptor.is_blocking())
clear();r,npc=ready_to_play();descriptor.on_effect_save("before","load-in-save")
callbacks.save_state(memory);local service_snapshot=copy(memory)
module.GUI.stale=true;servers=saved_objects();clients={};memory=service_snapshot
descriptor.on_game_load();descriptor.on_effect_save("after","load-in-save",true);tick()
assert(not hidden and not memory[KEY] and not sound_ends and level.camera==2 and next(_GUIs)==nil)
clear();r,npc=ready_to_play();descriptor.on_effect_save("before","separate-state")
local separate_state={};callbacks.save_state(separate_state)
descriptor.on_effect_save("after","separate-state",true)
assert(memory[KEY]==r and separate_state[KEY].double==nil and descriptor.is_blocking())
callbacks.on_before_save_input();callbacks.save_state(memory)
assert(not hidden and not memory[KEY] and not descriptor.is_blocking())

for phase_index=0,8 do
    clear();DEV_DEBUG=true;r=phase(phase_index)
    local double_id=r.double.id;local guitar_id=r.guitar and r.guitar.id
    local unrelated=make_server("unrelated_world_item")
    local saved_camera=level.camera
    defer_all_releases=true
    callbacks.save_state(memory)
    assert(not descriptor.is_blocking() and not hidden and next(_GUIs)==nil and not sound_ends)
    assert(xrs_debug_tools.debug_invis==nil and servers[double_id],"Test must retain asynchronous native objects")
    local recovery=assert(memory[KEY]);local snapshot=copy(memory);local objects=saved_objects()
    assert(not objects[double_id] and not objects[guitar_id] and objects[unrelated.id],"Scene prop entered the save")
    assert(not recovery.double and not recovery.guitar and not recovery.trader and not recovery.debug_invisible)
    assert(not recovery.pos and recovery.actor_bones_untouched)
    -- Continue without loading: the one-save marker must not block future songs.
    tick();assert(not memory[KEY] and not hidden)
    -- A fresh native load contains only the saved objects, not the old world.
    servers=objects;clients={};memory=snapshot;level.camera=saved_camera;hidden=true;_HUD_STATE=false
    descriptor.on_game_load();tick()
    assert(not hidden and not memory[KEY] and level.camera==2 and servers[unrelated.id])
    defer_all_releases=false
end
clear();r,npc=ready_to_play();module.GUI:Close()
callbacks.save_state(memory);assert(not memory[KEY],"An ordinary save acquired a guitar recovery marker")

clear();DEV_DEBUG=true;r,npc=ready_to_play();local after=copy(memory);local old_ui=module.GUI;old_ui.stale=true
descriptor.on_game_load()
if not legacy_mouse then
    local flags={ret_value=false};callbacks.on_before_mouse_move(80,20,false,flags)
    assert(flags.ret_value==false,"Loading accessed a stale UI or retained mouse input")
end
memory=after;tick();assert(not hidden and not memory[KEY] and next(servers)==nil and not actor.visible.helmet)
assert(xrs_debug_tools.debug_invis==nil,"Load retained debug invisibility")
clear();DEV_DEBUG=true;r,npc=ready_to_play();old_ui=module.GUI;old_ui.stale=true;memory={}
actor.visible={root=true,bip01_l_hand=true,helmet=false};hidden=false;_HUD_STATE=true
descriptor.on_game_load();tick();assert(actor.visible.root and not memory[KEY],"Future scene restored over before save")
assert(xrs_debug_tools.debug_invis==nil,"Future global invisibility leaked into a before save")

-- Debug flags are not game-save data: loading an old journal after a fresh
-- start must NOT turn off a new, independently user-enabled invisible mode.
clear();DEV_DEBUG=true;r,npc=ready_to_play();after=copy(memory);module.GUI:Close()
memory=after;xrs_debug_tools.debug_invis=true;descriptor.on_game_load();tick()
assert(xrs_debug_tools.debug_invis==true and not memory[KEY] and not hidden)

-- The native fatal auto-save may contain the double and the captured UI
-- journal, but no camera yet (guitar.4 crashed at the start of begin()).
clear();r=start();npc=materialize(r.double);after=copy(memory);after[KEY].slot=3
old_ui=module.GUI;old_ui.stale=true;descriptor.on_game_load();memory=after;tick()
assert(not hidden and not memory[KEY] and next(servers)==nil and next(_GUIs)==nil and level.camera==2)
assert(slot_activation_calls==0,"Fatal-save recovery restored a numeric inventory slot")
-- An unrelated UI during the online-spawn wait cancels cleanly, too.
clear();r=start();module.GUI:OnKeyboard(99,ui_events.WINDOW_KEY_PRESSED)
module.GUI:OnMouse(540,390,ui_events.WINDOW_MOUSE_MOVE);render()
callbacks.GUI_on_show("foreign_menu");assert(not hidden and not memory[KEY] and next(servers)==nil)

-- Bone variants changed externally during a new scene must not be overwritten.
clear();r,npc=ready_to_play();actor.visible.helmet=true;module.GUI:Close()
assert(actor.visible.helmet and actor_bone_writes==0)
-- Recover an old guitar.2 save that already hid all actor bones, once only.
clear();r,npc=ready_to_play();after=copy(memory);after[KEY].actor_bones_untouched=nil
actor.visible={root=false,bip01_l_hand=false,helmet=false};legacy_bone_recovery=true
old_ui=module.GUI;old_ui.stale=true;descriptor.on_game_load();memory=after;tick()
assert(actor.visible.root and actor.visible.bip01_l_hand and not actor.visible.helmet and actor_bone_writes==2)
assert(not hidden and next(servers)==nil and not memory[KEY])
legacy_bone_recovery=false

-- Errors after taking control restore it; errors before taking control do not acquire it.
clear();spawn_fail=true;local context={};assert(descriptor.can_execute(context,{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"}))
assert(descriptor.execute(context)=="rejected" and not hidden and not memory[KEY]);spawn_fail=false
clear();packet_fail=true;context={};assert(descriptor.can_execute(context,{song_sections="adb_guitar_custom",catalog_revision="guitar-rev"}))
assert(descriptor.execute(context)=="rejected" and next(servers)==nil and not memory[KEY]);packet_fail=false
clear();r,npc=ready_to_play();blocked=true;tick();assert(not hidden and next(servers)==nil and not memory[KEY])
clear();r=phase(0);materialize(r.double);camera_mode_fail=true;tick()
assert(level.camera==2 and not hidden and next(servers)==nil and not memory[KEY],"Partial camera setup was not restored")
clear();r,npc=ready_to_play();body_debug_mode="2";tick()
assert(not hidden and next(servers)==nil and not memory[KEY] and level.camera==2)
clear();r=phase(4);audio_fail=true;clients[r.double.id].queue=0;tick()
assert(not hidden and not memory[KEY] and next(servers)==nil,"Audio error retained actor control")
clear();r,npc=ready_to_play();callbacks.GUI_on_show("foreign_menu");assert(not hidden and next(servers)==nil)
clear();r,npc=ready_to_play();descriptor.on_level_changing();assert(not hidden and next(servers)==nil)
clear();DEV_DEBUG=true;r,npc=ready_to_play();descriptor.on_level_changing();assert(xrs_debug_tools.debug_invis==nil)
clear();DEV_DEBUG=true;r,npc=ready_to_play();callbacks.main_menu_on_quit();assert(xrs_debug_tools.debug_invis==nil)
clear();DEV_DEBUG=true;r,npc=ready_to_play();callbacks.actor_on_before_death();assert(xrs_debug_tools.debug_invis==nil)
clear();DEV_DEBUG=true;r=phase(4);audio_fail=true;clients[r.double.id].queue=0;tick()
assert(xrs_debug_tools.debug_invis==nil and not hidden,"Failed setup retained invisibility")
clear();r=start();npc=materialize(r.double);tick();tick(31000);assert(not hidden and not memory[KEY],"Stuck animation retained control")
clear();r,npc=ready_to_play();actor.living=false;tick();assert(not hidden and next(servers)==nil)
-- Recycled IDs must never release another entity.
clear();r,npc=ready_to_play();servers[r.double.id].sec="foreign_npc";module.GUI:Close();assert(servers[r.double.id])
assert(slot_activation_calls==0,"An error path attempted a native slot activation")
print("Guitar smoke passed (mock API; "..(legacy_mouse and "legacy UI orbit" or "raw mouse orbit/centred OS cursor/no double input/no cursor warp").."): application saves preserve all scene phases, manual save/load cleanup, asynchronous no-save props, dynamic trader faction, optional debug invisibility/ownership, no slot activation, moving start, private loadout, per-frame orbit, actor skeleton untouched, native animations/one song, F5/F9 and legacy recovery.")
