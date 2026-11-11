local core,optional=assert(arg[1]),assert(arg[3])
local legacy_only=arg[4]=="legacy"
local function load_module(root,name)
    local env=setmetatable({},{__index=_G})
    local chunk=assert(loadfile(root.."/"..name..".script"))
    setfenv(chunk,env)
    chunk()
    return env
end
local function v(x,y,z)
    return {x=x,y=y,z=z,distance_to=function(a,b)
        return math.sqrt((a.x-b.x)^2+(a.y-b.y)^2+(a.z-b.z)^2)
    end}
end
function vector() return {set=function(_,x,y,z) return v(x,y,z) end} end
local module_name="anomaly_donation_bridge_effect_hardcoon_adb_btr_companion"
local key="adb_btr_companion_v1"
local records,objects,servers,callbacks={},{},{},{}
local now,paused,holder,selected=1000,false,nil,nil
local target,order
local actor_pos=v(0,0,0)
local vehicle_pos=v(10,0,0)
local drive_speeds,searches={},0
local level_name="test"
local drive_count,release_count,camera_count,aim_count=0,0,0,0
local spots,spot_adds,spot_changes={},0,0
local car={health=1,GetfHealth=function(self) return self.health end,SetfHealth=function(self,h) self.health=h end,
    GetSpeed=function() return 0 end,Action=function() end,SetTargetSpeed=function() end,SetThrottle=function() end,
    HandBreak=function() end}
local actor={id=function() return 0 end,alive=function() return true end,position=function() return actor_pos end,
    game_vertex_id=function() return 42 end,get_attached_vehicle=function() return holder end,
    attach_vehicle=function(_,obj,forced) assert(forced);holder=obj end,
    detach_vehicle=function() holder=nil end}
local model={visual="test.ogf",fire_bone="gun",box={}}
local shape={x=2,z=4,bottom=0,height=3,radius=5}
db={actor=actor}
level={name=function() return level_name end,present=function() return true end,
    object_by_id=function(id) return objects[id] end,vertex_link=function() end,iterate_nearest=function() end,
    press_action=function() end,release_action=function() end,get_target_obj=function() return target end}
local SPOT="ui_pda2_companion_location"
level.map_has_object_spot=function(id,kind) assert(kind==SPOT);return spots[id] and 1 or 0 end
level.map_add_object_spot=function(id,kind,hint)
    assert(kind==SPOT);spots[id]=hint;spot_adds=spot_adds+1
end
level.map_change_spot_hint=function(id,kind,hint)
    assert(kind==SPOT and spots[id]);spots[id]=hint;spot_changes=spot_changes+1
end
level.map_remove_object_spot=function(id,kind) assert(kind==SPOT);spots[id]=nil end
function CScriptXmlInit() return {ParseFile=function(_,name) assert(name=="map_spots.xml") end,
    NodeExist=function(_,name) return name==SPOT end} end
function bind_to_dik() return 0 end
function time_global() return now end
DIK_keys={DIK_RCONTROL=157,DIK_F=33}
key_bindings={kUSE=1,kTORCH=2}
function device() return {is_paused=function() return paused end,precache_frame=0} end
function system_ini() return {section_exist=function() return true end} end
CCar={eWpnFire=1}
hit={strike=1,wound=2,wound_2=3,fire_wound=4,telepatic=5}
alife_storage_manager={get_state=function() return records end}
utils_stpk={parse_cse_alife_object_properties_packet=function() end,parse_cse_visual_properties_packet=function() end,
    parse_cse_ph_skeleton_properties_packet=function() end,fill_cse_alife_object_properties_packet=function() end,
    fill_cse_visual_properties_packet=function(t) selected=t.visual_name end,fill_cse_ph_skeleton_properties_packet=function() end}
function net_packet()
    return {w_begin=function() end,r_seek=function() end,r_elapsed=function() return 4 end,
        r_float=function() return 1 end,w_float=function() end,w_tell=function() return 10 end}
end
function RegisterScriptCallback(name,callback) callbacks[name]=callback end
local sim={next_id=0}
function sim:create()
    self.next_id=self.next_id+1
    local id=self.next_id
    local server={id=id,name=function() return "btr_"..id end,section_name=function() return "adb_btr_companion" end,
        STATE_Write=function() end,STATE_Read=function() end}
    servers[id]=server
    return server
end
function sim:object(id) return servers[id] end
function sim:release(server) servers[server.id]=nil;objects[server.id]=nil end
function sim:set_switch_offline() end
function sim:set_switch_online(id,enabled)
    if enabled then
        objects[id]={id=function() return id end,name=servers[id].name,section=servers[id].section_name,
            get_car=function() return car end,bounding_box=function() return model.box end,
            direction=function() return v(0,0,1) end,position=function() return vehicle_pos end,
            bone_transform=function() return {c=vehicle_pos} end}
    end
end
function alife() return sim end
function class(name)
    local env=getfenv(2)
    return function() env[name]={} end
end
object_binder={}
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_vehicle_controls={available=function(legacy) return not legacy_only or legacy==true end,
    speed=function(c) return c:GetSpeed() end,
    close_shutters=function() end,
    create_headlights=function() return {} end,
    update_headlights=function(_,state,enabled) if state then state.enabled=enabled==true end end,
    release_headlights=function(state) if state then state.enabled=false end end,
    take_control=function(obj,legacy)
        assert(not legacy_only or legacy==true,"legacy choice was lost on initialization/reload/dismount")
        return {object=obj,car=car,legacy_api=legacy==true}
    end,
    drive=function(state,_,speed) drive_count=drive_count+1;drive_speeds[state.object:id()]=speed end,
    release_driver=function() release_count=release_count+1 end,
    native_camera=function() camera_count=camera_count+1 end,
    aim=function() aim_count=aim_count+1 end}
anomaly_donation_bridge_vehicle_navigation={footprint=function() return shape end,
    vertex=function() return 12 end,recheck_spawn=function() return true end,
    spawn_candidate=function() return {id=12,position=v(10,0,0),heading=0} end,
    ground_position=function(p) return p end,drive_probe=function() return true end,
    swept_drive=function() return true end,turn_radius=function() return 6.5 end,
    control_speed=function(p,h,a,speed,_,s,obj)
        local safe,_,reason=anomaly_donation_bridge_vehicle_navigation.swept_drive(p,h,a,speed,s,obj)
        return safe and speed or 0,reason
    end,
    direct_route=function(origin,point,_,_,_,heading)
        return {start={position=origin,yaw=heading},{position=point,reverse=point.z<origin.z}}
    end,
    track_route=load_module(core,"anomaly_donation_bridge_vehicle_navigation").track_route,
    crosshair_ground=function() return order end,
    start_route=function() searches=searches+1 end}
anomaly_donation_bridge_vehicle_catalog={profile=function(_,s) return {visual=s..".ogf",box={},fire_bone="gun"} end}
anomaly_donation_bridge_catalog={validate_sections=function(_,field,kind)
    assert(field=="vehicle_sections" and kind=="armed_vehicle")
    return {{section_id="veh_a"},{section_id="veh_b"}}
end,is_available=function() return true end}
local descriptor
anomaly_donation_bridge_registry={register=function(id,d) assert(id=="hardcoon.adb.btr_companion");descriptor=d end}
anomaly_donation_bridge_btr_ai=load_module(core,"anomaly_donation_bridge_btr_ai")
local module=load_module(optional,module_name)
module.on_game_start()
local messages={}
news_manager={send_tip=function(_,text) messages[#messages+1]=text end}
assert(not descriptor.update and not descriptor.on_game_load,"AI must not depend on ADB polling")
assert(descriptor.is_supported())
assert(not select(1,descriptor.can_execute({actor=actor},{legacy_api="invalid"})))
if legacy_only then
    local ok,status,reason=descriptor.can_execute({actor=actor},{})
    assert(not ok and status=="rejected" and reason=="btr_legacy_api_required","legacy enabled itself without the checkbox")
end
local function dispatch(params,id,donor)
    if legacy_only and params.legacy_api==nil then params.legacy_api="true" end
    local ctx={actor=actor,now=now,command={command_id=id,donor_name=donor}}
    local ok,status,reason=descriptor.can_execute(ctx,params)
    if not ok then return status,reason end
    return descriptor.execute(ctx)
end
local function advance(ms) now=now+ms;callbacks.actor_on_update() end
assert(dispatch({duration_seconds="9"},"bad")=="rejected")
assert(dispatch({follow_distance="101"},"bad2")=="rejected")
assert(dispatch({},"one","Тестер")=="executed")
local r=assert(records[key]["1"])
assert(r.legacy_api==legacy_only,"selected API was not stored in the vehicle save record")
assert(#messages==0,"arrival was announced before the vehicle became ready")
assert((selected=="veh_a.ogf" or selected=="veh_b.ogf") and r.visual==selected)
assert(r.remaining==60000 and r.distance==20 and r.drive_key=="RCTRL" and r.stage=="arriving")
advance(100)
assert(r.stage=="active" and r.remaining==60000 and r.health==1)
assert(spots[1]==anomaly_donation_bridge_common.game_text_cp1251_lossy("БТР остаток топлива: 60"))
assert(r.donor_name=="Тестер" and #messages==1 and messages[1]==anomaly_donation_bridge_common.game_text_cp1251_lossy(
    "Механик-водитель: Командир, %c[255,238,153,26]Тестер%c[default] из штаба просил вас сопроводить."),
    "arrival lost the highlighted donor name")
advance(1000)
assert(r.remaining==59000)
assert(spot_adds==1 and spot_changes==1 and spots[1]==anomaly_donation_bridge_common.game_text_cp1251_lossy("БТР остаток топлива: 59"),
    "fuel timer recreated its spot or did not refresh the tooltip")
paused=true
advance(5000)
paused=false
advance(5000)
assert(r.remaining==59000,"pause consumed time")
assert(spot_adds==1 and spot_changes==1,"paused fuel marker kept changing")
holder=objects[1]
assert(module.passenger_object())
assert(not module.passenger_command("change_money",{actor=actor},{}))
local ctx={actor=actor}
assert(module.passenger_command("spawn_npcs",ctx,{}) and ctx.spawn_anchor:level_vertex_id()==12)
local before_drive,before_aim=drive_count,aim_count
advance(100)
assert(release_count==1 and camera_count==1 and r.mode=="manual")
assert(drive_count==before_drive and aim_count==before_aim+1,"driver control blocked combat or ran navigation")
local lamp_flags={ret_value=true}
callbacks.on_before_key_press(0,key_bindings.kTORCH,false,lamp_flags)
assert(not lamp_flags.ret_value and r.headlights==true,"owned BTR did not handle the bound light action")
advance(100)
assert(r.headlights==true,"AI update forced the lights back off")
lamp_flags.ret_value=true
callbacks.on_before_key_press(0,key_bindings.kTORCH,false,lamp_flags)
assert(not lamp_flags.ret_value and r.headlights==false,"second press did not switch the lights off")
local mutant={is_monster=function() return true end}
local shield={ret_value=true}
callbacks.actor_on_before_hit({draftsman=mutant,type=hit.wound},0,shield)
assert(not shield.ret_value,"a mutant melee attack bypassed the hull")
shield.ret_value=true
callbacks.actor_on_before_hit({draftsman=mutant,type=hit.telepatic},0,shield)
assert(shield.ret_value,"the hull intercepted psi damage")
car.health=0.4
assert(dispatch({duration_seconds="10"},"extend")=="executed")
assert(r.remaining==68800 and car.health==0.4 and sim.next_id==1)
assert(#messages==2 and messages[2]==anomaly_donation_bridge_common.game_text_cp1251_lossy(
    "Механик-водитель: Штаб послал новый приказ, продолжать следование ещё 10 секунд. Приказ принял. Продолжаю сопровождение цели."),
    "extension did not announce the added duration")
assert(dispatch({extend_current="false"},"blocked")=="deferred")
holder=nil
advance(100)
assert(r.mode=="follow" and drive_count>before_drive)
assert(spots[1]==anomaly_donation_bridge_common.game_text_cp1251_lossy("БТР остаток топлива: 69"),"extension did not refresh fuel tooltip")
callbacks.actor_on_before_hit({draftsman=mutant,type=hit.wound},0,shield)
assert(shield.ret_value,"the hull shield survived dismount")
assert(dispatch({extend_current="false",duration_seconds="15"},"two")=="executed")
assert(records[key]["2"].remaining==15000)
local snapshot=r.remaining
callbacks.on_game_load()
assert(not spots[1],"load retained a stale owned marker")
advance(250)
assert(r.remaining==snapshot and sim.next_id==2,"load duplicated car or spent unloaded time")
assert(spots[1] and spots[2],"loaded BTR markers were not rebuilt")
level_name="other"
callbacks.on_level_changing()
assert(not next(spots),"level change retained local companion markers")
callbacks.on_game_load()
advance(1000)
assert(r.remaining==snapshot and records[key]["1"]==r and servers[1],"BTR was removed or counted down on another level")
level_name="test"
callbacks.on_game_load()
advance(250)
local real=objects[1]
objects[1]={id=real.id,section=real.section,name=function() return "recycled" end}
holder=objects[1]
assert(not module.passenger_object(),"recycled ID inherited passenger permissions")
lamp_flags.ret_value=true
callbacks.on_before_key_press(0,key_bindings.kTORCH,false,lamp_flags)
assert(lamp_flags.ret_value,"foreign transport lost its native light control")
local prior_health=car.health
assert(module.before_hit(objects[1],{power=0.5,impulse=5000,draftsman=mutant,type=hit.wound}),
    "a foreign/recycled vehicle inherited the monster impulse filter")
assert(car.health==prior_health,"foreign vehicle hit changed our BTR health")
local flags={ret_value=true}
callbacks.actor_on_before_hit({weapon_id=1},0,flags)
assert(flags.ret_value,"recycled ID inherited hit interception")
objects[1]=real
holder=nil
flags.ret_value=true
callbacks.actor_on_before_hit({weapon_id=1},0,flags)
assert(not flags.ret_value,"vehicle shot attribution was lost")
local damage=0.5
car.health=1
assert(module.before_hit(real,{power=damage}))
car.health=car.health-damage -- Native CEntity::CalcCondition.
assert(math.abs(car.health-0.95)<1e-6,"hull did not take one tenth of the stock damage")
assert(not module.before_hit(real,{power=damage,impulse=5000,draftsman=mutant,type=hit.wound}),
    "mutant melee kept the native throw impulse")
assert(math.abs(car.health-0.90)<1e-6,"suppressing knockback removed mutant damage")
assert(not module.before_hit(real,{power=0,impulse=5000,draftsman=mutant,type=hit.wound}),
    "zero-damage monster impact still threw the hull")
assert(module.before_hit(real,{power=0,impulse=5000,draftsman=mutant,type=hit.telepatic}),
    "melee impulse filter intercepted a different hit type")
local function press(dik,bind)
    local flag={ret_value=true}
    callbacks.on_before_key_press(dik,bind,false,flag)
    return flag.ret_value
end
order=v(10,0,18)
records[key]["2"].drive_key="HOME"
messages={}
assert(not press(DIK_keys.DIK_RCONTROL,0))
assert(#messages==1 and messages[1]==anomaly_donation_bridge_common.game_text_cp1251_lossy("Механик-водитель: Выдвигаюсь на указанную позицию, командир."),"accepted command has no PDA confirmation")
local old_searches=searches
advance(150)
assert(drive_speeds[1]>0 and searches==old_searches,"a clear ordered route depended on the follow radius or a pedestrian graph")
assert(press(DIK_keys.DIK_F,key_bindings.kUSE),"F was swallowed without an owned car in view")
actor_pos=v(7,0,0)
target=real
assert(not press(DIK_keys.DIK_F,key_bindings.kUSE))
advance(150)
assert(holder==real and drive_speeds[1]==0,"boarding required more than one use press")

-- A right-hand target must steer right, and the reverse manoeuvre uses the
-- opposite steering sign. This caught the native car driving away from goals.
holder=nil
actor_pos=v(0,0,0)
target=nil
order=v(16,0,18)
now=now+1500
local steering
anomaly_donation_bridge_vehicle_controls.drive=function(state,_,speed,angle)
    if state.object:id()==1 then steering=angle;drive_speeds[1]=speed end
end
assert(not press(DIK_keys.DIK_RCONTROL,0))
advance(150)
assert(steering>0,"right-hand destination steered left")
order=v(16,0,-18)
now=now+1500
assert(not press(DIK_keys.DIK_RCONTROL,0))
advance(150)
assert(steering>0,"reverse right-hand destination used the wrong steering sign")

-- Ordered travel uses the same replanner as following. A blocked direct
-- corridor must result in an actual detour, without forgetting the order.
local fake_nav=anomaly_donation_bridge_vehicle_navigation
local saved_drive=fake_nav.swept_drive
local detour_goal,last_search
fake_nav.start_route=function(_,point)
    searches=searches+1;detour_goal=point;last_search={goal=point};return last_search
end
fake_nav.route_step=function(search)
    return "done",{{position=v(4,0,5)},{position=search.goal}}
end
fake_nav.segment_clear=function() return false end
fake_nav.swept_drive=function() return nil,nil,"body_length" end
order=v(35,0,0)
now=now+1500
assert(not press(DIK_keys.DIK_RCONTROL,0))
advance(150)
local replans=searches
advance(2000)
assert(searches>replans and detour_goal and detour_goal:distance_to(order)<0.001,"RCTRL did not replan around an obstruction")
assert(not last_search.avoid or #last_search.avoid==0,"a predicted brake envelope incorrectly sealed the detour")
fake_nav.swept_drive=saved_drive
advance(150)
advance(150) -- completed planning is consumed on the next actor update
assert(steering<0,"RCTRL ignored its detour waypoint and kept aiming straight at the final point")
local plans_before=searches
fake_nav.swept_drive=function(_,_,angle)
    if math.abs(angle)>0.02 then return nil,nil,"body_width" end
    return true
end
advance(150)
assert(steering==0 and drive_speeds[1]>0 and drive_speeds[1]<=0.7 and searches==plans_before,
    "blocked pursuit discarded a safe route curvature instead of following it slowly")
fake_nav.swept_drive=saved_drive
local diag=anomaly_donation_bridge_btr_ai.diagnostics()
assert(r.destination and r.destination.x==35,"replanning lost the saved destination")

-- A collision correction is a driving action along the same route. A
-- temporary blocked steering arc must not erase the committed destination.
fake_nav.route_clear_ahead=function() return true end
fake_nav.retreat=function() return 0 end
local recovery_plans=searches
fake_nav.swept_drive=function(_,_,_,distance)
    if distance<0 then return true end
    return nil,nil,"body_width"
end
advance(150);advance(2000);advance(150)
assert(drive_speeds[1]<0 and searches==recovery_plans,"local reverse discarded the chosen global route")
fake_nav.swept_drive=saved_drive
advance(2300)
assert(drive_speeds[1]>0 and searches==recovery_plans,"recovery recomputed instead of rejoining the route")
fake_nav.route_clear_ahead,fake_nav.retreat=nil,nil

-- A final route node just short of the destination must not be skipped
-- using the larger cruising tolerance while the destination is still far.
local direct_route=fake_nav.direct_route
fake_nav.direct_route=function(origin) return {start={position=origin,yaw=0},{position=v(24.9,0,0)}} end
vehicle_pos=v(24.3,0,0)
now=now+1500
assert(not press(DIK_keys.DIK_RCONTROL,0))
advance(150)
assert(drive_speeds[1]>0,"final waypoint was skipped outside the order arrival radius")
assert(drive_speeds[1]<=1,"BTR approached its guard position at cruising speed")
fake_nav.direct_route=direct_route

-- Reaching an order is a guard position, not an immediate return to the
-- player. It survives load and ends only on a later approach within 10 m.
vehicle_pos=v(26,0,0) -- inside the preferred 10 m arrival area
actor_pos=v(0,0,0)
advance(150)
assert(r.order_wait and r.approach_armed,"arrival did not enter guard mode")
callbacks.on_game_load()
advance(150)
assert(r.mode=="hold" and r.destination.x==35,"load lost the guard position")
local saved_order=r.destination
advance(1000)
assert(r.destination==saved_order,"BTR abandoned its ordered position on its own")
actor_pos=v(20,0,0)
advance(150)
assert(not r.destination and r.mode=="follow","approaching within 10 m did not resume following")

-- A new command issued while nearby is not instantly cancelled. First
-- leave the arrival radius, then approach again.
order=v(35,0,0)
now=now+1500
assert(not press(DIK_keys.DIK_RCONTROL,0))
advance(150)
advance(150)
assert(r.order_wait and not r.approach_armed,"nearby player cancelled a fresh guard order")
actor_pos=v(0,0,0)
advance(150)
actor_pos=v(26,0,0)
advance(150)
assert(not r.destination,"guard order did not clear after a new approach")

-- Follow-target updates must not repeatedly erase a pending search or a
-- valid path. A genuine teleport/reversal may still invalidate it.
local old_start,old_step,old_direct=fake_nav.start_route,fake_nav.route_step,fake_nav.direct_route
local pending,started_count,finish_search=nil,0,false
fake_nav.direct_route=function() end
fake_nav.start_route=function(origin,goal,stop,yaw)
    started_count=started_count+1
    pending={origin=origin,goal=v(goal.x,goal.y,goal.z),stop=stop,yaw=yaw}
    return pending
end
fake_nav.route_step=function(s)
    if not finish_search then return "searching" end
    return "done",{start={position=s.origin,yaw=s.yaw},{position=s.goal,yaw=s.yaw}}
end
actor_pos,vehicle_pos=v(0,0,100),v(0,0,0)
callbacks.on_game_load();advance(150)
local count_before=started_count
local first_search=pending
for i=1,3 do actor_pos=v(0,0,100+i*15);advance(3100) end
assert(started_count==count_before and pending==first_search,"moving actor starved the ongoing route search")
finish_search=true
for _=1,8 do advance(150) end -- shared scheduler services every owned vehicle
assert(drive_speeds[1]>=4,"straight cruising speed was incorrectly capped by the pursuit lookahead")
finish_search=false
actor_pos,vehicle_pos=v(0,0,155),v(0,0,8);advance(3100)
assert(started_count>count_before and pending.continuation and pending.goal.z==155,
    "moving target did not update the continuation while keeping the driven route")
assert(drive_speeds[1]>0,"planning a continuation stopped the current route")
count_before=started_count
local continuation=pending
for i=1,3 do
    actor_pos=v(0,0,155+i*12)
    vehicle_pos=v(0,0,8+i*6) -- the driver keeps advancing while the continuation is pending
    advance(3100)
end
assert(started_count==count_before and pending==continuation,
    "following a moving actor restarted an unfinished continuation: "..started_count.."/"..count_before..
    " goal="..tostring(pending and pending.goal.z).." expected="..continuation.goal.z)
actor_pos=v(0,0,-100);advance(3100)
assert(started_count>count_before,"teleport/reversal left a stale following target")
fake_nav.start_route,fake_nav.route_step,fake_nav.direct_route=old_start,old_step,old_direct
actor_pos,vehicle_pos=v(26,0,0),v(32.6,0,0)

-- Lifecycle announcements survive saved state without greeting spam. The
-- warning counts active game time and extension cancels the previous one.
local message_count=#messages
callbacks.on_game_load();advance(150)
assert(#messages==message_count,"loading an existing BTR repeated its arrival announcement")
local warning=anomaly_donation_bridge_common.game_text_cp1251_lossy("Механик-водитель: Командир, горим! Ща рванёт! Отходите подальше!")
local function warning_count()
    local n=0
    for _,message in ipairs(messages) do if message==warning then n=n+1 end end
    return n
end
r.remaining=3100
local warnings=warning_count()
advance(100)
assert(r.warning_remaining==3000 and warning_count()==warnings+1,"expiration did not warn three seconds ahead")
advance(1000)
paused=true;advance(5000);paused=false;advance(100)
assert(r.warning_remaining==2000,"pause spent the explosion warning time")
callbacks.on_game_load();advance(100)
assert(r.warning_remaining==2000 and warning_count()==warnings+1,"F9 repeated or reset the destruction warning")
holder=objects[1] -- extension selects the occupied BTR even with a second one nearby
assert(dispatch({duration_seconds="10"},"rescue")=="executed")
holder=nil
assert(r.warning_remaining==nil and r.remaining==12000,"extension kept the pending explosion warning")
r.remaining=3100
advance(100)
assert(r.warning_remaining==3000 and warning_count()==warnings+2,"extended BTR did not rearm its eventual warning")
advance(1000);advance(1000)
assert(records[key]["1"] and r.stage=="active","BTR expired before the three-second warning finished")
advance(1000)
assert(not records[key]["1"] and not objects[1],"expired BTR remained after its warning")

-- Defeat follows the same warning delay; a failing modded PDA must not
-- duplicate an extension or prevent the vehicle from retiring.
assert(dispatch({extend_current="false",duration_seconds="10"},"last")=="executed")
advance(100)
local doomed=records[key]["3"]
assert(doomed and doomed.donor_name=="Донатер","manual spawn has no fallback donor")
local send_tip=news_manager.send_tip
news_manager.send_tip=function() error("modded PDA unavailable") end
local before=doomed.remaining
holder=objects[3]
assert(dispatch({duration_seconds="10"},"quiet")=="executed" and doomed.remaining==before+10000)
holder=nil
news_manager.send_tip=send_tip
car.health=0.01
assert(not module.before_hit(objects[3],{power=1}),"lethal hit bypassed controlled destruction")
advance(100)
assert(doomed.stage=="retiring" and doomed.warning_remaining==3000)
advance(1000);advance(1000)
assert(objects[3],"lethal damage exploded the BTR before warning completed")
advance(1000)
assert(not objects[3],"defeated BTR was not retired after its warning")

-- A runtime error points to the real file, not the user_name() alias which
-- the engine may replace with Player. Missing FS APIs remain non-fatal.
car.health=1
assert(dispatch({extend_current="false"},"log-check")=="executed")
advance(100)
local get_health,old_fs,old_get_fs,old_console=car.GetfHealth,FS,getFS,get_console
local flushed=false
FS={FS_ListFiles=1,FS_RootOnly=2,FS_sort_by_modif_down=3}
get_console=function() return {execute=function(_,cmd) assert(cmd=="flush");flushed=true end} end
getFS=function() return {file_list_open_ex=function(_,path,flags,pattern)
    assert(path=="$logs$" and flags==3 and pattern=="xray_*.log")
    return {Size=function() return 1 end,Sort=function(_,sort) assert(sort==3) end,
        GetAt=function(_,i) assert(i==0);return {NameShort=function() return "xray_Tester.log" end} end}
end} end
car.GetfHealth=function() error("simulated vehicle failure") end
advance(100)
assert(flushed and messages[#messages]==anomaly_donation_bridge_common.game_text_cp1251_lossy(
    "БТР: Системная ошибка. Подробности в xray_Tester.log."),"runtime error did not name the actual log")
getFS=function() error("FS unavailable") end
advance(10000)
assert(messages[#messages]==anomaly_donation_bridge_common.game_text_cp1251_lossy(
    "БТР: Системная ошибка. Подробности в xray_*.log."),"missing log API broke the error notification")
car.GetfHealth,FS,getFS,get_console=get_health,old_fs,old_get_fs,old_console

-- Boarding cancels even a pending RCTRL route between control ticks. A new
-- driver must start at the actual dismount pose, not resume a stale job/order.
do
    local previous_actor,previous_vehicle=actor_pos,vehicle_pos
    callbacks.on_game_load()
    records,objects,servers={},{},{}
    sim.next_id=0;car.health=1;holder=nil
    actor_pos,vehicle_pos=v(0,0,0),v(10,0,0)
    assert(dispatch({},"manual-reset")=="executed")
    advance(100)
    local record=records[key]["1"]
    local mock=anomaly_donation_bridge_vehicle_navigation
    local old_start,old_step,old_direct=mock.start_route,mock.route_step,mock.direct_route
    mock.direct_route=function() end
    local stepped,started=0,{}
    mock.start_route=function(origin,goal,stop,heading)
        started[#started+1]={origin=origin,goal=goal}
        return {origin=origin,goal=goal,stop=stop,heading=heading,expanded=17,anchored=true}
    end
    mock.route_step=function() stepped=stepped+1;return "searching" end
    order=v(10,0,100)
    assert(not press(DIK_keys.DIK_RCONTROL,0))
    advance(100)
    local d=anomaly_donation_bridge_btr_ai.diagnostics()[1]
    assert(d.search==17 and d.order and record.destination)
    local previous,remaining=stepped,record.remaining
    holder=objects[1]
    advance(1) -- the 60 ms update is not due, but the route coroutine is
    d=anomaly_donation_bridge_btr_ai.diagnostics()[1]
    assert(stepped==previous and not d.search and not d.path and not d.order and d.manual,
        "boarding left the old route/command alive until the next control tick")
    assert(not record.destination and record.remaining==remaining,"boarding reset fuel or retained RCTRL destination")
    actor_pos,vehicle_pos=v(120,0,0),v(80,0,0)
    holder=nil;advance(100)
    d=anomaly_donation_bridge_btr_ai.diagnostics()[1]
    assert(not d.manual and not d.order and record.mode=="follow")
    assert(started[#started].origin.x==80 and started[#started].goal.x==120,
        "dismount planned from the old car pose or followed the old RCTRL point")
    assert(sim.next_id==1 and record.health==1 and record.remaining==remaining-101)
    -- An id recycled into a foreign object must not lose that object's icon.
    spots[1]="foreign marker"
    servers[1].name=function() return "foreign_btr" end
    callbacks.on_game_load()
    assert(spots[1]=="foreign marker","marker cleanup erased a recycled object's marker")
    spots[1]=nil
    mock.start_route,mock.route_step,mock.direct_route=old_start,old_step,old_direct
    actor_pos,vehicle_pos=previous_actor,previous_vehicle
end

-- The AI must honour the planner's relaxed arrival area in both modes,
-- hold an RCTRL order there once, and retain its target on save/load.
do
    local previous_actor,previous_vehicle=actor_pos,vehicle_pos
    callbacks.on_game_load()
    records,objects,servers={},{},{}
    sim.next_id=0;car.health=1;holder=nil
    actor_pos,vehicle_pos=v(0,0,0),v(10,0,0)
    assert(dispatch({follow_distance="10"},"arrival-area") == "executed")
    advance(100)
    local record=records[key]["1"]
    local mock=anomaly_donation_bridge_vehicle_navigation
    local old_start,old_step,old_direct=mock.start_route,mock.route_step,mock.direct_route
    mock.direct_route=function() end
    mock.start_route=function(origin,goal,stop,heading,limit)
        assert(stop==10 and limit==20,"AI did not distinguish preferred and maximum arrival radius")
        return {origin=origin,goal=goal,stop=limit,preferred_stop=stop,heading=heading,expanded=0,
            frontier={{yaw=heading}}}
    end
    mock.route_step=function(s)
        return "done",{start={position=s.origin,yaw=s.heading},
            {position=v(s.goal.x-18,s.goal.y,s.goal.z),yaw=s.heading},arrival_radius=18}
    end
    order=v(100,0,0)
    local old_speed=car.GetSpeed
    car.GetSpeed=function() return 2 end
    now=now+1500;assert(not press(DIK_keys.DIK_RCONTROL,0));advance(100)
    car.GetSpeed=old_speed
    advance(100) -- re-anchor after braking without promoting the outer tolerance
    vehicle_pos=v(81,0,0);advance(100)
    assert(not record.order_wait,"stopped outside the accepted final radius")
    vehicle_pos=v(82,0,0);advance(200)
    assert(record.order_wait and record.destination.x==100 and drive_speeds[1]==0,
        "driver tried to reach the exact marker after relaxed arrival: "..tostring(record.ai_status)..
        " radius="..tostring(anomaly_donation_bridge_btr_ai.diagnostics()[1].arrival_radius))
    local count=#messages
    advance(4000)
    assert(#messages==count and record.order_wait,"arrival notice or movement repeated in the holding area")
    callbacks.on_game_load();advance(100)
    assert(record.order_wait and record.mode=="hold","reload lost a relaxed-radius holding order")
    holder=objects[1];advance(100);holder=nil
    actor_pos=v(100,0,0);vehicle_pos=v(60,0,0);advance(100)
    advance(3100)
    vehicle_pos=v(82,0,0);advance(3100)
    assert(record.mode=="follow" and record.ai_status=="covering" and drive_speeds[1]==0,
        "follow mode ignored the accepted radius and restarted the final approach")
    mock.start_route,mock.route_step,mock.direct_route=old_start,old_step,old_direct
    actor_pos,vehicle_pos=previous_actor,previous_vehicle
end

local nav=load_module(core,"anomaly_donation_bridge_vehicle_navigation")
local wall=false
rq_target={rqtStatic=1,rqtBoth=2}
function rq_result() return {} end
level.vertex_id=function(p) return math.floor(p.z+0.5) end
level.vertex_position=function(id) return v(0,0,id) end
level.vertex_in_direction=function(id) return id>=0 and id<=10 and id or 4294967295 end
level.vertex_link=function(id,side)
    if side==0 and id<10 then return id+1 end
    if side==1 and id>0 then return id-1 end
    return 4294967295
end
level.ray_pick=function(p,d,_,_,hit)
    if d.y<0 then hit.range=p.y;return true end
    if wall and d.y==0 and p.z<5 and d.z>0 then hit.range=(5-p.z)/d.z;return true end
    return false
end
local search=assert(nav.start_route(v(0,0,0),v(0,0,8),1))
local real_clock,probe_clock=os.clock,0
os.clock=function() probe_clock=probe_clock+0.001;return probe_clock end
assert(nav.route_step(search,shape,nil,1)=="searching","route ignored the frame budget")
os.clock=real_clock
local result,path
repeat result,path=nav.route_step(search,shape,nil,2) until result~="searching"
assert(result=="done" and #path>0 and path[1].position.z>0 and path[#path].position.z>=7,"physical ground did not produce a drivable route")
wall=true
search=assert(nav.start_route(v(0,0,0),v(0,0,8),1))
repeat result=nav.route_step(search,shape,nil,2) until result~="searching"
assert(result=="blocked","route crossed an obstacle through the vehicle footprint")
wall=false
level.vertex_id=function() return 4294967295 end
level.vertex_link=function() error("vehicle navigation consulted pedestrian graph") end
search=assert(nav.start_route(v(0,0,0),v(0,0,15),2,0))
repeat result,path=nav.route_step(search,shape,nil,2) until result~="searching"
assert(result=="done","route required a pedestrian AI mesh")
local right=assert(nav.swept_drive(v(0,0,0),0,0.5,3,shape,nil))
assert(right.x>0 and right.z>0,"positive native steering did not sweep to the right")
do
    -- Destination tolerance is separate from hull clearance and detour
    -- extent. Prefer 10 m on open ground; accept 15-20 m when a solid wall
    -- makes the closer endpoint inaccessible, without crossing that wall.
    local original_ray=level.ray_pick
    local goal=v(0,0,60)
    local job=nav.start_route(v(0,0,0),goal,10,0,20)
    local state,trajectory
    repeat state,trajectory=nav.route_step(job,shape,nil,8) until state~="searching"
    assert(state=="done" and trajectory.arrival_radius==10,
        "open approach did not prefer the smaller arrival area")
    assert(trajectory[#trajectory].position:distance_to(goal)<=10)
    level.ray_pick=function(p,d,length,_,hit)
        local ground=d.y<0 and -p.y/d.y or math.huge
        local barrier=math.abs(d.z)>0.00001 and (49-p.z)/d.z or math.huge
        if barrier<0 then barrier=math.huge end
        local distance=math.min(ground,barrier)
        if distance>=0 and distance<=length then hit.range=distance;return true end
        return false
    end
    job=nav.start_route(v(0,0,0),goal,10,0,20)
    repeat state,trajectory=nav.route_step(job,shape,nil,8) until state~="searching"
    assert(state=="done" and trajectory and job.verified,"blocked endpoint did not accept the outer arrival area")
    local gap=trajectory[#trajectory].position:distance_to(goal)
    assert(gap>10 and gap<=20 and trajectory.arrival_radius>=gap and trajectory.arrival_radius<=20,
        "destination tolerance was ignored or exceeded")
    -- The physical tracker accepts a small stop before the final pose. The
    -- very same stop must count as arrival, not discard this route and make
    -- the driver search/escape again at the destination (native test2).
    local tail=trajectory[#trajectory]
    local near=v(tail.position.x,tail.position.y,tail.position.z-0.4)
    local tracked=nav.track_route(trajectory,#trajectory,near,tail.yaw,0.5)
    assert(not tracked.done or near:distance_to(goal)<=trajectory.arrival_radius,
        "tracker finished outside its accepted arrival area and will restart the route")
    -- A checked curve can end just inside the outer 20 m boundary. Exercise
    -- the planner/controller hand-off independently of which curve A* picks.
    local edge=nav.start_route(v(0,0,38),goal,10,0,20)
    edge.worker=coroutine.create(function()
        return "done",{start={position=v(0,0,38),yaw=0},
            {position=v(0,0,39.8),yaw=0},{position=v(0,0,40.2),yaw=0}}
    end)
    local edge_state,edge_path=nav.route_step(edge,shape,nil,8)
    local edge_stop=v(0,0,40.02)
    local finished=nav.track_route(edge_path,2,edge_stop,0,0.5)
    assert(edge_state=="done" and (not finished.done or edge_stop:distance_to(goal)<=edge_path.arrival_radius),
        "final pose tolerance completed the route before entering the allowed destination area")
    assert(job.expanded==0,"a reachable arrival area needlessly started a global search")
    for _,pose in ipairs(trajectory) do
        assert(pose.position.z+shape.z<49,"arrival tolerance weakened collision clearance")
    end
    -- Already near the destination, boxed in ahead: a zero-distance arrival
    -- is success, not an empty path discarded and searched forever.
    level.ray_pick=function(p,d,length,_,hit)
        local ground=d.y<0 and -p.y/d.y or math.huge
        local barrier=math.abs(d.z)>0.00001 and (shape.z+0.05-p.z)/d.z or math.huge
        if barrier<0 then barrier=math.huge end
        local distance=math.min(ground,barrier)
        if distance>=0 and distance<=length then hit.range=distance;return true end
        return false
    end
    job=nav.start_route(v(0,0,0),v(0,0,19),10,0,20)
    repeat state,trajectory=nav.route_step(job,shape,nil,8) until state~="searching"
    assert(state=="done" and trajectory and #trajectory==1 and trajectory.arrival_radius>=19,
        "already-arrived stationary vehicle kept requesting another route: "..tostring(state).." count="..
        tostring(trajectory and #trajectory).." radius="..tostring(trajectory and trajectory.arrival_radius)..
        " origin="..tostring(job.origin_reason))
    level.ray_pick=original_ray
end
-- A solid obstacle on an otherwise free physical surface. There is no AI
-- mesh here; the planner must turn around it without shrinking the hull.
level.ray_pick=function(p,d,length,_,hit)
    if d.y<0 then hit.range=p.y;return true end
    if p.y>3 then return false end
    local low,high=0,length
    for _,axis in ipairs({"x","z"}) do
        local a,b=axis=="x" and -2 or 9,axis=="x" and 2 or 11
        local delta=d[axis]
        if math.abs(delta)<0.00001 then
            if p[axis]<a or p[axis]>b then return false end
        else
            local t,u=(a-p[axis])/delta,(b-p[axis])/delta
            if t>u then t,u=u,t end
            low,high=math.max(low,t),math.min(high,u)
            if low>high then return false end
        end
    end
    hit.range=low
    return true
end
local small_hull={x=1.4,z=2.8,height=2,bottom=0,radius=3.2}
search=nav.start_route(v(0,0,0),v(0,0,26),3,0)
repeat result,path=nav.route_step(search,small_hull,nil,8) until result~="searching"
assert(result=="done", "vehicle failed to route around an isolated obstacle")
assert(search.verified,"an approximate search route reached the controller without the dense final sweep")
local detour=false
for _,p in ipairs(path) do if math.abs(p.position.x)>3.4 then detour=true end end
assert(detour,"route went through the obstacle")
local previous_gear,gear_changes=nil,0
for _,point in ipairs(path) do
    if previous_gear~=nil and previous_gear~=point.reverse then gear_changes=gear_changes+1 end
    previous_gear=point.reverse
end
assert(gear_changes==0,"a clear broad detour became a series of parking shunts")

-- Wide detours can start by reversing away from the goal. A slow machine
-- must retain its frontier beyond eight wall-clock seconds, not keep
-- returning a tempting short fragment on the blocked side of the wall.
local isolated_ray=level.ray_pick
level.ray_pick=function(p,d,length,_,hit)
    if d.y<0 then hit.range=p.y;return true end
    if p.y>3 then return false end
    local low,high=0,length
    for _,axis in ipairs({"x","z"}) do
        local a,b=axis=="x" and -25 or 8,axis=="x" and 25 or 12
        if math.abs(d[axis])<0.00001 then
            if p[axis]<a or p[axis]>b then return false end
        else
            local t,u=(a-p[axis])/d[axis],(b-p[axis])/d[axis]
            if t>u then t,u=u,t end
            low,high=math.max(low,t),math.min(high,u)
            if low>high then return false end
        end
    end
    hit.range=low;return true
end
local wide=nav.start_route(v(0,0,0),v(0,0,22),3,0)
wide.responsive=true -- old callers cannot turn this back into short prefixes
local wide_result,wide_path
repeat
    now=now+100
    wide_result,wide_path=nav.route_step(wide,small_hull,nil,8)
until wide_result~="searching"
assert(wide_result=="done","planner did not find a 30 m wide detour: "..tostring(wide_result).." nodes="..wide.expanded)
local far_side=false
for _,point in ipairs(wide_path) do
    if math.abs(point.position.x)>27 then far_side=true end
end
assert(far_side,"wide detour was cut through the wall")
for _,wall_half in ipairs({70,170}) do
    -- Passages outside both the old 60 m and 150 m search rectangles. A
    -- complete ground corridor must be found first, then its checked
    -- sections must join all the way to the original destination.
    level.ray_pick=function(p,d,length,_,hit)
        if d.y<0 then hit.range=p.y;return true end
        if p.y>3 then return false end
        local low,high=0,length
        for _,axis in ipairs({"x","z"}) do
            local a,b=axis=="x" and -wall_half or 45,axis=="x" and wall_half or 49
            if math.abs(d[axis])<0.00001 then
                if p[axis]<a or p[axis]>b then return false end
            else
                local t,u=(a-p[axis])/d[axis],(b-p[axis])/d[axis]
                if t>u then t,u=u,t end
                low,high=math.max(low,t),math.min(high,u)
                if low>high then return false end
            end
        end
        hit.range=low;return true
    end
    local origin,heading,target=v(0,0,0),0,v(0,0,100)
    local hint,spine,reached,passed,sections=nil,nil,false,false,0
    local displaced=false
    for section=1,28 do
        local job=nav.start_route(origin,target,3,heading)
        job.corridor_hint,job.spine=hint,spine
        local state,trajectory
        local slices=0
        repeat
            state,trajectory=nav.route_step(job,small_hull,nil,8)
            slices=slices+1
            assert(slices<10000,"corridor refinement stopped making progress")
        until state~="searching"
        assert(state=="done" and trajectory,"disconnected section of wide corridor")
        assert(job.goal:distance_to(target)<0.01,"local anchor replaced final destination")
        if section==2 and displaced then
            assert(trajectory.continue,"rejoining a distant centreline discarded the full connected corridor")
        end
        assert(job.verified or (job.precise and job.precise.verified),"unchecked corridor section")
        for _,pose in ipairs(trajectory) do
            if math.abs(pose.position.x)>wall_half+2 then passed=true end
        end
        local tail=trajectory[#trajectory]
        origin,heading=tail.position,tail.yaw
        hint,spine=job.corridor_hint,job.spine
        if section==1 and trajectory.continue then
            -- Tyre slip or manual positioning may leave the car beside the
            -- coarse centreline. Retain the long connected detour and join
            -- it physically; do not search the whole level's steering space.
            for _,shift in ipairs({{-18,-18},{18,-18},{-22,0},{22,0}}) do
                local p=v(origin.x+shift[1],origin.y,origin.z+shift[2])
                local gap=math.huge
                for _,point in ipairs(spine) do gap=math.min(gap,p:distance_to(point)) end
                if gap>11 and nav.clear(p,math.sin(heading),math.cos(heading),small_hull,nil,false,true) then
                    origin=p;displaced=true;break
                end
            end
            assert(displaced,"centreline rejoin fixture did not leave the coarse corridor")
        end
        sections=section
        if not trajectory.continue then reached=origin:distance_to(target)<=3.01;break end
        assert(spine and #spine>0,"partial trajectory had no full corridor")
    end
    assert(reached and passed and sections>1,"streamed route did not finish the complete wide detour")
end
level.ray_pick=isolated_ray

-- Drive the computed curve, with steering lag, rather than merely assert
-- that A* produced a list. Independent rectangle/box SAT checks contacts.
local function intersects_box(p,yaw,hull)
    local dx,dz=math.sin(yaw),math.cos(yaw)
    local ox,oz=-p.x,10-p.z
    for _,axis in ipairs({{1,0},{0,1},{dx,dz},{dz,-dx}}) do
        local x,z=axis[1],axis[2]
        local r=(hull.z-0.5)*math.abs(x*dx+z*dz)+(hull.x-0.5)*math.abs(x*dz-z*dx)
            +2*math.abs(x)+math.abs(z)
        if math.abs(ox*x+oz*z)>r then return false end
    end
    return true
end
local position,yaw,cursor,velocity,applied=v(0,0,0),0,1,0,0
local arrived=false
for _=1,1600 do
    local command=nav.track_route(path,cursor,position,yaw,velocity)
    cursor=command.index
    assert(not command.blocked,"trajectory controller lost a planned curve: "..tostring(command.blocked))
    if command.done then arrived=true;break end
    if command.stop then velocity=0
    else
        local dx,dz=command.goal.x-position.x,command.goal.z-position.z
        local delta=math.atan2(math.cos(yaw)*dx-math.sin(yaw)*dz,math.sin(yaw)*dx+math.cos(yaw)*dz)
        if command.reverse then delta=delta+(delta>0 and -math.pi or math.pi) end
        local requested=math.max(-1,math.min(1,2*nav.turn_radius(small_hull)*math.sin(delta)/math.max(0.6,math.sqrt(dx*dx+dz*dz))))
        if command.reverse then requested=-requested end
        applied=applied+(requested-applied)*0.6
        velocity=command.reverse and -0.85 or 1.25
        if command.cusp then velocity=velocity*math.min(1,math.max(0.2,command.end_gap)) end
        yaw=yaw+applied/nav.turn_radius(small_hull)*velocity*0.1
        position=v(position.x+math.sin(yaw)*velocity*0.1,0,position.z+math.cos(yaw)*velocity*0.1)
        assert(not intersects_box(position,yaw,small_hull),"driver cut a checked detour and hit the obstacle")
    end
end
assert(arrived and position.z>22,"driver never completed its calculated obstacle detour")

-- Real BTR wheel geometry: the chassis centre is over two metres ahead of
-- its rolling reference. Test the offset, steering headroom and gear cusps
-- against physical integration, not a centre-point bicycle approximation.
local btr_hull={x=2.07777,z=4.60581,height=3.66468,bottom=0,radius=5.0528,
    rear_offset=2.21263,steer_radius=8.65756,turn_radius=11.68771}
search=nav.start_route(v(0,0,0),v(0,0,36),3,0)
repeat result,path=nav.route_step(search,btr_hull,nil,8) until result~="searching"
assert(result=="done","multi-axle chassis did not plan an obstacle detour")
position,yaw,cursor,velocity,applied=v(0,0,0),0,1,0,0
arrived=false
for _=1,2400 do
    local command=nav.track_route(path,cursor,position,yaw,velocity,btr_hull.rear_offset)
    cursor=command.index
    assert(not command.blocked,"multi-axle driver lost the route: "..tostring(command.blocked))
    if command.done then arrived=true;break end
    if command.stop then velocity=0
    else
        local rear=v(position.x-math.sin(yaw)*btr_hull.rear_offset,0,position.z-math.cos(yaw)*btr_hull.rear_offset)
        local dx,dz=command.goal.x-rear.x,command.goal.z-rear.z
        local delta=math.atan2(math.cos(yaw)*dx-math.sin(yaw)*dz,math.sin(yaw)*dx+math.cos(yaw)*dz)
        if command.reverse then delta=delta+(delta>0 and -math.pi or math.pi) end
        local requested=math.max(-1,math.min(1,2*btr_hull.steer_radius*math.sin(delta)/math.max(0.6,math.sqrt(dx*dx+dz*dz))))
        if command.reverse then requested=-requested end
        applied=applied+(requested-applied)*0.6
        velocity=command.reverse and -0.85 or 1.25
        if command.cusp then velocity=velocity*math.min(1,math.max(0.2,command.end_gap)) end
        yaw=yaw+applied/btr_hull.steer_radius*velocity*0.1
        rear.x=rear.x+math.sin(yaw)*velocity*0.1;rear.z=rear.z+math.cos(yaw)*velocity*0.1
        position=v(rear.x+math.sin(yaw)*btr_hull.rear_offset,0,rear.z+math.cos(yaw)*btr_hull.rear_offset)
        assert(not intersects_box(position,yaw,btr_hull),"multi-axle driver clipped the obstacle")
    end
end
assert(arrived and position.z>32,"multi-axle driver did not finish its route")

-- A gear change is reached in the ground plane. The rear axle / suspension
-- height on a hillside must not leave the driver forever creeping at it.
local slope_cusp={start={position=v(0,0,0),yaw=0},
    {position=v(0,0,1),yaw=0,reverse=false},
    {position=v(0,0,2),yaw=0,reverse=false},
    {position=v(0,0,1),yaw=0,reverse=true}}
local stopping=nav.track_route(slope_cusp,2,v(0,0.7,1.7),0,0.9,0)
assert(stopping.stop,"a hillside gear change did not brake before reversing")
local switched=nav.track_route(slope_cusp,2,v(0,0.7,1.7),0,0,0)
assert(switched.reverse and switched.index==3,"suspension height prevented a completed gear change")
local overrun_brake=nav.track_route(slope_cusp,2,v(0.4,0.7,2.8),0,0.5,0)
assert(overrun_brake.stop,"an overshot cusp kept driving away in the previous gear")
local overrun_turn=nav.track_route(slope_cusp,2,v(0.4,0.7,2.8),0,0,0)
assert(overrun_turn.reverse and overrun_turn.index==3,"a stopped cusp overrun could not engage reverse")
local remote_cusp=nav.track_route(slope_cusp,2,v(0,0,5),0,0,0)
assert(not remote_cusp.reverse,"cusp tolerance skipped an unrelated distant manoeuvre")

-- A streamed continuation with the same gear is one continuous trajectory.
local joined={start={position=v(0,0,0),yaw=0},
    {position=v(0,0,1),yaw=0,reverse=false},
    {position=v(0,0,2),yaw=0,reverse=false},
    {position=v(0,0,3),yaw=0,reverse=false},
    {position=v(0,0,4),yaw=0,reverse=false}}
local through=nav.track_route(joined,2,v(0,0,1.9),0,2,0)
assert(not through.stop and not through.done and not through.cusp and through.goal.z>3,
    "joined sections introduced a stop or a short braking target")
local drift=nav.track_route(joined,2,v(1.3,0,1.9),0,1,0)
assert(not drift.blocked and drift.deviation>1 and drift.goal.x==0 and drift.goal.z>2,
    "a small lateral deviation erased the line instead of steering back to it")

-- A U-turn can finish near its starting position despite a long curve
-- remaining. Braking must use route length, not the chord to its endpoint.
local hairpin={start={position=v(0,0,0),yaw=0},
    {position=v(0,0,5),yaw=0},{position=v(5,0,5),yaw=math.pi/2},
    {position=v(5,0,0),yaw=math.pi},{position=v(1,0,0),yaw=-math.pi/2}}
local bend=nav.track_route(hairpin,1,v(0,0,0),0,2,0)
assert(bend.end_gap==1 and bend.end_distance>=18 and bend.goal.z>0,
    "U-turn braking distance used the chord or lost its pursuit target")

-- A low block between the former three length/width rays. It must also
-- be rejected when it is away from all nine original ground samples.
level.ray_pick=function(p,d,length,_,hit)
    local low,high=0,length
    local bounds={x={0.8,1.2},y={0,0.6},z={1.15,1.6}}
    local box=true
    for _,axis in ipairs({"x","y","z"}) do
        local a,b=bounds[axis][1],bounds[axis][2]
        if math.abs(d[axis])<0.00001 then
            if p[axis]<a or p[axis]>b then box=false;break end
        else
            local t,u=(a-p[axis])/d[axis],(b-p[axis])/d[axis]
            if t>u then t,u=u,t end
            low,high=math.max(low,t),math.min(high,u)
            if low>high then box=false;break end
        end
    end
    local floor=d.y<0 and -p.y/d.y or math.huge
    local range=math.min(box and low or math.huge,floor>=0 and floor or math.huge)
    if range<=length then hit.range=range;return true end
    return false
end
assert(not nav.clear(v(0,0,0),0,1,shape,nil,true,true),"small concrete block hid between body probes")

-- A narrow post falls between the cached map's cell probes, but across a
-- full chassis ray. Final verification must reject that tempting straight
-- connection, refine its neighbourhood and return a safe complete detour.
level.ray_pick=function(p,d,length,_,hit)
    local low,high=0,length
    local bounds={x={0.49,0.55},y={0,2},z={9.17,9.23}}
    local box=true
    for _,axis in ipairs({"x","y","z"}) do
        local a,b=bounds[axis][1],bounds[axis][2]
        if math.abs(d[axis])<0.00001 then
            if p[axis]<a or p[axis]>b then box=false;break end
        else
            local t,u=(a-p[axis])/d[axis],(b-p[axis])/d[axis]
            if t>u then t,u=u,t end
            low,high=math.max(low,t),math.min(high,u)
            if low>high then box=false;break end
        end
    end
    local floor=d.y<0 and -p.y/d.y or math.huge
    local distance=math.min(box and low or math.huge,floor>=0 and floor or math.huge)
    if distance<=length then hit.range=distance;return true end
    return false
end
search=nav.start_route(v(0,0,0),v(0,0,30),3,0)
repeat result,path=nav.route_step(search,btr_hull,nil,8) until result~="searching"
assert(result=="done" and search.verified,"fine obstacle refinement did not return a verified route")
assert((search.verification_retries or 0)>0,"the fixture did not exercise rejection of an approximate route")
for _,pose in ipairs(path) do
    assert(nav.clear(pose.position,math.sin(pose.yaw),math.cos(pose.yaw),btr_hull,nil,true,true),
        "cached planning passed an unseen thin obstacle to the driver")
end

-- A bumper already within the old driver's safety margin can back away;
-- actual hull overlap is never permitted as an escape shortcut.
local barrier_z=3.8
level.ray_pick=function(p,d,length,_,hit)
    if d.y<0 then hit.range=p.y;return true end
    if math.abs(d.z)>0.0001 then
        local t=(barrier_z-p.z)/d.z
        if t>=0 and t<=length then hit.range=t;return true end
    end
    return false
end
search=nav.start_route(v(0,0,0),v(0,0,-20),3,0)
repeat result,path=nav.route_step(search,shape,nil,8) until result~="searching"
assert(result=="partial" and path.escape_shape and path[#path].position.z<0,"BTR could not escape an overlapping safety margin")
assert(path.escape_shape.x>=shape.x-0.45 and path.escape_shape.z>=shape.z-0.45,"recovery shrank the physical hull")
assert(nav.clear(path[#path].position,0,1,shape,nil,true,true),"recovery ended with a blocked safety margin")
for _,pose in ipairs(path) do
    if math.abs(path[#path].travel)-math.abs(pose.travel)<=0.8 then
        assert(nav.clear(pose.position,math.sin(pose.yaw),math.cos(pose.yaw),shape,nil,true,true),
            "escape may finish inside the old overlap and oscillate on the next search")
    end
end
local escape_tail=path[#path]
local before_tail=v(escape_tail.position.x,escape_tail.position.y,escape_tail.position.z+0.4)
assert(not nav.track_route(path,1,before_tail,escape_tail.yaw,-0.7).done,
    "short escape finished before reaching its free endpoint")
barrier_z=3.0
search=nav.start_route(v(0,0,0),v(0,0,-20),3,0)
repeat result,path=nav.route_step(search,shape,nil,8) until result~="searching"
assert(not path or not path.escape_shape,"recovery allowed actual body penetration")

-- A vehicle facing a nearby wall must reach an offset point behind it with
-- a checked reverse arc, without first searching for space to turn around.
-- The rolling reference is behind the model origin on a multi-axle BTR.
barrier_z=5
-- One metre in front of the bumper is enough for a slow checked move,
-- not a 3 m/s command. Current speed must still govern braking clearance.
local crawl=nav.control_speed(v(0,0,0),0,0,3,0,shape,nil)
assert(crawl>0 and crawl<=1,"tight clear space did not select a safe crawl")
assert(nav.control_speed(v(0,0,0),0,0,0.5,3,shape,nil)==0,"slow target speed erased the actual stopping distance")
assert(nav.control_speed(v(0,0,0),0,0,0.5,3,shape,nil,0.2)==0,"a close endpoint erased the actual stopping distance")
assert(not nav.clear(v(0,0,1.1),0,1,shape,nil,true,true),"planner lost its 50 cm clearance reserve")
assert(nav.swept_drive(v(0,0,1.1),0,0,0.1,shape,nil),"driver could not consume its reserved tracking clearance")
assert(not nav.swept_drive(v(0,0,1.6),0,0,0.1,shape,nil),"tracking tolerance permitted a real chassis collision")
barrier_z=4.3
assert(nav.control_speed(v(0,0,0),0,0,3,0,shape,nil)==0,"crawl drove into an immediate obstacle")
assert(nav.control_speed(v(0,0,0),0,0,0.5,0,shape,nil,0.2)>0,
    "driver required clearance beyond a reachable short stopping point")
barrier_z=-5
crawl=nav.control_speed(v(0,0,0),0,0,-3,0,shape,nil)
assert(crawl<0 and crawl>=-1,"reverse crawl used the wrong collision direction")
barrier_z=5
local reversing_hull={x=2,z=4,height=3,bottom=0,radius=4.5,
    turn_radius=11.7,steer_radius=8.7,rear_offset=2.16}
search=nav.start_route(v(0,0,0),v(6,0,-20),3,0)
repeat result,path=nav.route_step(search,reversing_hull,nil,8) until result~="searching"
assert(result=="done" and not path.escape_shape,"rear destination did not produce a full route")
assert(search.expanded<=1,"a clear reverse connection triggered a global turning search")
assert(path[#path].position:distance_to(v(6,0,-20))<3,"reverse connection missed its destination")
for _,pose in ipairs(path) do
    assert(pose.reverse,"rear connection unnecessarily turned the vehicle around")
    assert(nav.clear(pose.position,math.sin(pose.yaw),math.cos(pose.yaw),reversing_hull,nil,true,true),
        "reverse connection crossed the front wall")
end

-- Driving over real terrain is different from choosing a flat spawn pad.
-- Check slopes, a gentle crest and an unsafe trench with actual ray ranges.
local terrain=function(x,z) return 0.30*x+0.05*z end
level.ray_pick=function(p,d,length,_,hit)
    if d.x==0 and d.z==0 then
        if math.abs(d.y)<0.00001 then return false end
        local t=(terrain(p.x,p.z)-p.y)/d.y
        if t>=0 and t<=length then hit.range=t;return true end
        return false
    end
    local steps=math.ceil(length/0.05)
    for i=0,steps do
        local t=length*i/steps
        if p.y+d.y*t<=terrain(p.x+d.x*t,p.z+d.z*t)+0.00001 then
            hit.range=t;return true
        end
    end
    return false
end
assert(not nav.clear(v(0,0,0),0,1,shape,nil,false),"spawn accepted an unsuitable sideways slope")
assert(nav.clear(v(0,0,0),0,1,shape,nil,true,true),"drive rejected a modest side slope")
assert(nav.swept_drive(v(0,0,0),0,1,3.2,shape,nil),"turning on a modest slope was blocked")
terrain=function(x,z) return 0.418*x+0.05*z end
assert(nav.clear(v(0,0,0),0,1,shape,nil,true,true),"save51's traversable hillside sealed every route")
terrain=function(_,z) return -0.14*math.abs(z) end
assert(nav.clear(v(0,0,0),0,1,shape,nil,true,true),"a gentle crest was treated as a spawn-pad failure")
terrain=function(_,z) return 0.25*math.abs(z) end
local long_chassis={x=2.0778,z=4.6449,height=3.01,radius=5.09,body_floor=0.48}
assert(nav.clear(v(0,0,0),0,1,long_chassis,nil,true,true),"a shallow hollow under a long chassis sealed the route")
long_chassis.body_floor=0.10
assert(not nav.clear(v(0,0,0),0,1,long_chassis,nil,true,true),"depression allowance ignored the model's clearance")
terrain=function(x) return 0.65*x end
assert(nav.clear(v(0,0,0),0,1,shape,nil,true,true),"an ordinary hillside was treated as an obstacle")
local hillside_speed=nav.control_speed(v(0,0,0),0,0,5,0,shape,nil)
assert(hillside_speed>0 and hillside_speed<=1.6,"hillside driving did not reduce speed")
for _,grade in ipairs({0.35,-0.35,0.6,-0.6}) do
    terrain=function(_,z) return grade*z end
    for _,direction in ipairs({1,-1}) do
        local speed=nav.control_speed(v(0,0,0),0,0,5*direction,0,shape,nil)
        assert(math.abs(speed-5*direction)<0.001,"a clear longitudinal slope still forced a crawl")
    end
end
terrain=function(x) return 1.2*x end
assert(not nav.clear(v(0,0,0),0,1,shape,nil,true,true),"drive accepted a dangerous side slope")
terrain=function(_,z) return math.abs(z)<1 and -1.3 or 0 end
assert(not nav.clear(v(0,-1.3,0),0,1,shape,nil,true,true),"drive accepted a deep trench")

-- Passable vegetation does not collide with the native chassis. Skipping
-- leaves must still detect a solid wall (or a dynamic object) behind them.
local solid_wall,leaf_object=false,nil
level.ray_pick=function(p,d,length,_,hit)
    if d.y<0 then hit.range=p.y;hit.material_flags=0;return true end
    if math.abs(d.z)<0.00001 then return false end
    for _,z in ipairs(solid_wall and {2,4} or {2}) do
        local t=(z-p.z)/d.z
        if t>=0 and t<=length then
            hit.range=t;hit.material_flags=z==2 and 128 or 0
            hit.object=z==2 and leaf_object or nil
            return true
        end
    end
    return false
end
assert(nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"passable foliage blocked a vehicle")
solid_wall=true
assert(not nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"skipping foliage skipped the wall behind it")
solid_wall=false;leaf_object={id=function() return 77 end,is_entity_alive=function() return false end}
assert(not nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"a dynamic object inherited a passable material flag")
leaf_object.is_inventory_item=function() return true end
assert(nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"dropped inventory blocked the vehicle")
solid_wall=true
assert(not nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"dropped inventory hid a wall behind it")
solid_wall=false
leaf_object.is_entity_alive=function() return true end
leaf_object.alive=function() return false end
assert(nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"corpse blocked the vehicle")
leaf_object.alive=function() return true end
assert(not nav.swept_drive(v(0,0,0),0,0,2,shape,nil),"live NPC was mistaken for soft clutter")
level.ray_pick=function(p,d,_,_,hit) if d.y<0 then hit.range=p.y;return true end return false end
local long_road=nav.start_route(v(0,0,0),v(0,0,170),10,0)
repeat result,path=nav.route_step(long_road,btr_hull,nil,8) until result~="searching"
assert(result=="done" and not long_road.corridor_cells,"an empty 170 m road triggered a global flood")
assert(path[#path].position.z>=160,"distant follow route ended prematurely")
-- A passable water surface above physical ground can be driven across even
-- when this entire area has no pedestrian AI vertices. Keep both planning
-- and live steering independent of the spawn/ALife registration helpers.
local old_rays=level.ray_pick
local old_vertex,old_vertex_pos,old_vertex_direction=level.vertex_id,level.vertex_position,level.vertex_in_direction
local function no_ai_mesh() error("vehicle navigation queried pedestrian AI mesh") end
level.vertex_id,level.vertex_position,level.vertex_in_direction=no_ai_mesh,no_ai_mesh,no_ai_mesh
level.ray_pick=function(p,d,length,_,hit)
    if math.abs(d.y)<0.00001 then return false end
    local best,flags
    for _,surface in ipairs({{0,128},{-0.25,0}}) do
        local distance=(surface[1]-p.y)/d.y
        if distance>=0 and distance<=length and (not best or distance<best) then best,flags=distance,surface[2] end
    end
    if best then hit.range=best;hit.material_flags=flags;return true end
    return false
end
search=nav.start_route(v(0,-0.25,0),v(0,-0.25,40),5,0)
repeat result,path=nav.route_step(search,btr_hull,nil,8) until result~="searching"
assert(result=="done" and path[#path].position.z>=35,"shallow water without AI mesh blocked routing")
assert(nav.control_speed(v(0,-0.25,0),0,0,5,0,btr_hull,nil)==5,
    "passable water without AI mesh blocked live driving")
level.ray_pick=old_rays
level.vertex_id,level.vertex_position,level.vertex_in_direction=old_vertex,old_vertex_pos,old_vertex_direction
-- A gravitational anomaly has no solid ray-pick surface. It must influence
-- both the coarse corridor and the swept route before it throws the BTR.
local iterate_nearest=level.iterate_nearest
local hazard_enabled=true
local anomaly_config={gravity="strike",thermal="light_burn",electric="shock",acid="chemical_burn",radiation="radiation"}
local old_ini=system_ini
system_ini=function() return {section_exist=function() return true end,
    line_exist=function(_,section,key) return key=="hit_type" and anomaly_config[section]~=nil end,
    r_string=function(_,section) return anomaly_config[section] end} end
local hazard_section="gravity"
local hazard={id=function() return 23067 end,position=function() return v(0,0,12) end,
    section=function() return hazard_section end,
    clsid=function() return 123 end, -- deliberately the same native class for all families
    is_anomaly=function() return true end,is_enabled_anomaly=function() return hazard_enabled end,
    get_anomaly_radius=function() error("native power multiplier was mistaken for radius in metres") end,
    inside=function(_,p,radius) return p:distance_to(v(0,0,12))<2+radius end}
level.iterate_nearest=function(_,_,visit) visit(hazard) end
local fits,_,reason=nav.clear(v(0,0,12),0,1,small_hull,nil,true,true)
assert(not fits and reason=="active_anomaly","an invisible anomaly was considered drivable")
hazard_enabled=false
assert(nav.clear(v(0,0,12),0,1,small_hull,nil,true,true),"a disabled anomaly blocked the car")
hazard_enabled=true
for _,section in ipairs({"thermal","electric","acid","radiation","unknown_modded"}) do
    hazard_section=section
    assert(nav.clear(v(0,0,12),0,1,small_hull,nil,true,true),section.." wrongly blocked the car")
end
hazard_section="gravity"
search=nav.start_route(v(0,0,0),v(0,0,26),3,0)
repeat result,path=nav.route_step(search,small_hull,nil,8) until result~="searching"
assert(result=="done","the route could not detour around a small anomaly")
for _,point in ipairs(path) do
    assert(nav.clear(point.position,math.sin(point.yaw),math.cos(point.yaw),small_hull,nil,true,true),
        "returned trajectory crosses an active anomaly")
end
level.iterate_nearest=iterate_nearest
system_ini=old_ini
search=nav.start_route(v(0,0,0),v(0,0,26),3,0)
search.avoid={{x=0,z=10,radius=4}}
repeat result,path=nav.route_step(search,small_hull,nil,8) until result~="searching"
assert(result=="done","a remembered failed approach prevented all alternative paths")
detour=false
for _,point in ipairs(path) do if math.abs(point.position.x)>4 then detour=true end end
assert(detour,"replanning reused a remembered failed corridor")
assert(nav.direct_leg(v(0,0,0),v(0,0,40),20,shape,nil).z==10,"a clear road needed pedestrian vertices")
function device()
    return {cam_pos=v(0,1.6,0),cam_dir=v(0,0,1),cam_top=v(0,1,0),cam_right=v(1,0,0),
        fov=60,aspect_ratio=0.56}
end
local site={position=v(15,0.12,2),ground_y=0,dx=0,dz=1}
assert(nav.recheck_spawn(site,actor,shape,20),"a point safely beside the frustum was rejected")
site.position=v(0,0.12,15)
assert(not nav.recheck_spawn(site,actor,shape,20),"a visible BTR was accepted")

-- Native action_first retains the interrupted command. Drive/stop/refresh
-- must replace the owned movement action and never press forward+brake.
local driver=load_module(core,"anomaly_donation_bridge_vehicle_controls")
local wheel_z={2.4,2.4,0.9,0.9,-1.0,-1.0,-2.5,-2.5}
local wheel_angle={30,30,15,15,7,7,0,0}
local previous_game=game
game={get_visual_userdata=function() return {
    line_exist=function() return true end,
    r_string=function() return "1,2,3,4,5,6,7,8" end} end}
local geometry=assert(driver.geometry({position=function() return v(0,0,0) end,
    direction=function() return v(0,0,1) end,
    get_physics_shell=function() return {get_joint_by_bone_name=function(_,name)
        local id=tonumber(name)
        return {get_axes_number=function() return 2 end,
            get_limits=function() return -math.rad(wheel_angle[id]),math.rad(wheel_angle[id]) end,
            get_anchor=function(_,point) point.x=0;point.y=0;point.z=wheel_z[id] end}
    end} end},"dynamic_model.ogf"))
assert(geometry.rear_offset>2 and geometry.rear_offset<2.6,"wheel fit lost the chassis offset")
assert(geometry.steer_radius>8 and geometry.steer_radius<10,"wheel fit ignored rear axle steering")
assert(geometry.turn_radius>geometry.steer_radius,"planner left no steering correction margin")
local hill_angle=math.rad(23)
local hill_geometry=assert(driver.geometry({position=function() return v(100,12,200) end,
    direction=function() return v(0,math.sin(hill_angle),math.cos(hill_angle)) end,
    get_physics_shell=function() return {get_joint_by_bone_name=function(_,name)
        local id=tonumber(name)
        return {get_axes_number=function() return 2 end,
            get_limits=function() return -math.rad(wheel_angle[id]),math.rad(wheel_angle[id]) end,
            get_anchor=function(_,point)
                point.x=100;point.y=12+wheel_z[id]*math.sin(hill_angle)
                point.z=200+wheel_z[id]*math.cos(hill_angle)
            end}
    end} end},"dynamic_model.ogf"))
assert(math.abs(hill_geometry.rear_offset-geometry.rear_offset)<0.00001,"hill changed the rear axle offset")
assert(math.abs(hill_geometry.steer_radius-geometry.steer_radius)<0.00001,"hill changed steering calibration")
game=previous_game
local actions,velocity={},v(0,0,0)
local native_car={
    SetTargetSpeed=function() end,SetThrottle=function() end,SetSteer=function() end,
    ReleaseHandBreak=function() end,HandBreak=function() end,CurrentVel=function() return velocity end}
local controlled={car=native_car,object={
    reset_action_queue=function() actions={} end,direction=function() return v(0,0,1) end},
    shell={set_linear_vel=function(_,value) velocity=value end}}
move=setmetatable({on=256,fwd=2,handbrake=128},{__call=function(_,keys) return keys end})
cond=setmetatable({time_end=1},{__call=function() return {} end})
function action_first(_,keys) table.insert(actions,1,keys) end
driver.drive(controlled,1000,2,0)
assert(#actions==1 and actions[1]==258,"driving did not engage native forward input")
driver.drive(controlled,2000,0,0)
assert(#actions==1 and actions[1]==384,"parking retained a forward command behind the brake")
driver.drive(controlled,13000,0,0)
assert(#actions==1 and actions[1]==384,"refresh accumulated stale native actions")
driver.drive(controlled,14000,-1,0.5)
assert(#actions==1 and actions[1]==258,"departure retained a stale parking brake")
velocity=v(0.3,-0.2,-0.4)
driver.drive(controlled,14120,0,0)
assert(velocity.x==0 and velocity.z==0 and velocity.y==-0.2,"parking slid off its planned origin or cancelled gravity")
velocity=v(0,0,0)
driver.drive(controlled,14240,4,0)
local flat_acceleration=velocity.z
velocity=v(0,0,0)
controlled.object.direction=function() return v(0,0.5,math.sqrt(.75)) end
driver.drive(controlled,14360,4,0)
assert(velocity.z>flat_acceleration and velocity.y==0,"uphill compensation removed traction or levitated the chassis")
velocity=v(0,0,0)
controlled.object.direction=function() return v(0,-0.5,math.sqrt(.75)) end
driver.drive(controlled,14480,-4,0)
assert(-velocity.z>flat_acceleration,"uphill reverse did not receive grade compensation")

-- Stock shutter-as-headlight workaround stays on this instance. Native
-- setups from other models must not be replaced or changed globally.
local light_instances,visible={},{}
local original_script_light=script_light
function script_light()
    local light={enabled=true,set_cone=function(self,value) self.cone=value end,
        set_position=function(self,value) self.position=value end,
        set_direction=function(self,forward,right) self.forward,self.right=forward,right end}
    light_instances[#light_instances+1]=light
    return light
end
local light_config={headlights="f1,f2",f1="front_left_door",f2="front_right_door"}
game={get_visual_userdata=function() return {
    line_exist=function() return true end,
    r_string=function(_,section,key) return section=="lights" and light_config.headlights or light_config[section] end} end}
local bones={front_left_door=1,front_right_door=2,left_light=3,right_light=4}
local lamp_object={get_bone_id=function(_,name) return bones[name] end,
    get_car=function() return {IsDoor=function() return false end} end,
    set_bone_visible=function(_,id,on) visible[id]=on end,
    bone_transform=function() return {c=v(1,2,3),k=v(0,0,1),i=v(1,0,0)} end}
driver.close_shutters(lamp_object)
local lamps=assert(driver.create_headlights(lamp_object,"stock_btr.ogf"))
assert(light_instances[1].volumetric==false and light_instances[2].volumetric==false,
    "owned BTR headlights retained volumetric beams")
driver.update_headlights(lamp_object,lamps,false)
assert(#light_instances==2 and not visible[3] and not visible[4],"headlights were glowing on spawn")
driver.update_headlights(lamp_object,lamps,true)
for _,light in ipairs(light_instances) do
    assert(light.enabled and light.type==2 and light.range==45 and light.position.x==1,
        "enabled headlamp has no correctly positioned light beam")
end
assert(visible[3] and visible[4] and visible[1]==true and visible[2]==true,"light toggle opened the shutters")
driver.update_headlights(lamp_object,lamps,false)
assert(not light_instances[1].enabled and not visible[3],"off lamps kept a light or emissive lens")
driver.update_headlights(lamp_object,lamps,true)
driver.release_headlights(lamps)
assert(not light_instances[1].enabled and not light_instances[2].enabled,"unloading left a stale light in the world")
light_config.headlights="f11,f12";light_config.f11="left_light";light_config.f12="right_light"
assert(not driver.create_headlights(lamp_object,"other_car.ogf") and #light_instances==2,
    "workaround replaced correctly configured native headlights")
game=previous_game;script_light=original_script_light

-- Collision profiles use the physical OGF shapes, not decorative barrel /
-- antenna bounds. A child can precede its parent in the stock bone array.
do
    local ffi=require("ffi")
    local function u32(n)
        return string.char(n%256,math.floor(n/256)%256,math.floor(n/65536)%256,math.floor(n/16777216)%256)
    end
    local function f32(n) return ffi.string(ffi.new("float[1]",n),4) end
    local function floats(a)
        local bytes={};for _,n in ipairs(a) do bytes[#bytes+1]=f32(n) end;return table.concat(bytes)
    end
    local function chunk(id,bytes) return u32(id)..u32(#bytes)..bytes end
    local function box(x,y,z,cx,cy,cz) return {1,0,0,0,1,0,0,0,1,cx or 0,cy or 0,cz or 0,x,y,z} end
    local parts={
        {"child","cab",1,0,box(.2,.2,.2),{0,0,1}},
        {"root","",1,0,box(1,1,3,0,1.6,0),{0,0,0}},
        {"wheel","root",2,0,box(0,0,0),{1.1,.4,2}},
        {"decoration","root",1,4,box(8,8,8),{0,10,0}},
        {"cab","root",1,0,box(.5,.5,.5),{0,1,3}}
    }
    local names,physics={u32(#parts)},{}
    for _,part in ipairs(parts) do
        names[#names+1]=part[1].."\0"..part[2].."\0"..string.rep("\0",60)
        physics[#physics+1]=u32(1).."default\0"..string.char(part[3],0,part[4],0)..floats(part[5])..
            floats({0,0,0,.4})..string.rep("\0",32)..u32(0)..string.rep("\0",68)..f32(1e17)..
            floats({0,0,0})..floats(part[6])..string.rep("\0",16)
    end
    local data=chunk(13,table.concat(names))..chunk(16,table.concat(physics))
    local filename=(os.getenv("TEMP") or ".").."/adb_btr_"..os.tmpname():match("[^/\\]+$")
    local old_fs,old_game=getFS,game
    local function write(bytes)
        local file=assert(io.open(filename,"wb"));file:write(bytes);file:close()
    end
    function getFS() return {exist=function() return {size_real=#data} end,update_path=function() return filename end} end
    game={get_visual_userdata=function() return {section_exist=function() return true end,
        line_exist=function() return true end,r_string=function() return "wheel" end} end}
    write(data)
    local catalog=load_module(core,"anomaly_donation_bridge_vehicle_catalog")
    local physical=catalog.driving_profile("test_shapes.ogf")
    write(data:sub(1,#data-7))
    local truncated=catalog.driving_profile("truncated.ogf")
    os.remove(filename)
    getFS,game=old_fs,old_game
    assert(physical and math.abs(physical.x-1.5)<.001 and math.abs(physical.z-4.2)<.001,
        "collision profile included decoration or lost an out-of-order parent transform")
    assert(math.abs(physical.bottom)<.001 and math.abs(physical.clearance-.5)<.001,
        "wheel contact height was mistaken for chassis ground clearance")
    assert(not truncated,"truncated OGF collision data was accepted")
end

-- A low road fragment under the chassis is not a wall. The same clearance
-- must still reject a taller obstruction and a live creature crossing it.
do
    local old_ray,old_near=level.ray_pick,level.iterate_nearest
    local obstruction=.32
    local creature
    level.iterate_nearest=function() end
    level.ray_pick=function(p,d,length,_,hit)
        if d.y<-.9 then hit.range=p.y;return true end
        if p.y<=obstruction and d.z>.1 and p.z<=0 and p.z+d.z*length>=0 then
            hit.range=-p.z/d.z;hit.object=creature;return true
        end
        return false
    end
    local profile={x=2,z=4,height=3,bottom=0,radius=5,body_floor=.48}
    assert(nav.clear(v(0,0,0),0,1,profile,nil,true,true),"low road detail trapped a chassis above it")
    assert(not nav.clear(v(0,0,0),0,1,profile,nil,true,false),"driving clearance weakened the spawn check")
    obstruction=.9
    assert(not nav.clear(v(0,0,0),0,1,profile,nil,true,true),"ground clearance skipped a tall concrete block")
    creature={is_entity_alive=function() return true end,alive=function() return true end,id=function() return 25023 end}
    local fits,_,reason=nav.clear(v(0,0,0),0,1,profile,nil,true,true)
    assert(not fits and reason=="living_obstacle","passing mutant was recorded as a permanent wall")
    level.ray_pick,level.iterate_nearest=old_ray,old_near
end
print("BTR companion smoke passed: orders detour and hold until approach, saved destination, slopes, passable foliage, hull safety, native driving, independent turret")
