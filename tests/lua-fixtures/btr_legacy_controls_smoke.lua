-- CCar and physics_shell from xray-monolith tag 2026.5.15 (including MT TEST).
-- No new governor, CCar.GetSpeed or physics_shell.set_force_exact_integration.
local root=assert(arg[1])
local function v(x,y,z) return {x=x,y=y,z=z} end
local released={}
key_bindings={kFWD=1,kBACK=2,kL_STRAFE=3,kR_STRAFE=4,kJUMP=5}
function bind_to_dik(key) return key end
level={release_action=function(key) released[key]=true end}
move=setmetatable({on=256,fwd=2,back=4,left=8,right=16,handbrake=128},
    {__call=function(_,keys) return keys end})
cond=setmetatable({time_end=1},{__call=function() return {} end})
function action_first(object,keys) object.actions[#object.actions+1]=keys end
CCar={eWpnDesiredPos=1,eWpnFire=2,eWpnActivate=3,
    Action=function() end,SetParam=function() end,HasWeapon=function() return true end,
    StartEngine=function(c) c.engine=true end,HandBreak=function(c) c.brake=true end,
    ReleaseHandBreak=function(c) c.brake=false end,CurrentVel=function(c) return c.velocity end}
local f=assert(loadfile(root..'/anomaly_donation_bridge_vehicle_controls.script'))
local driver=setmetatable({},{__index=_G});setfenv(f,driver);f()
assert(not driver.available() and driver.available(true),'old engine availability was not separated by mode')
local function vehicle(exact_integration)
    local car=setmetatable({velocity=v(0,0,0)},{__index=CCar})
    local shell={set_linear_vel=function(_,value) car.velocity=value end}
    if exact_integration then shell.set_force_exact_integration=function(s) s.exact_calls=(s.exact_calls or 0)+1 end end
    local object={actions={},resets=0,heading=v(0,0,1),get_car=function() return car end,
        get_physics_shell=function() return shell end,get_script=function(o) return o.scripted end,
        get_script_name=function(o) return o.owner end,script=function(o,on,owner) o.scripted=on;o.owner=owner end,
        direction=function(o) return o.heading end,
        reset_action_queue=function(o) o.actions={};o.resets=o.resets+1 end}
    return object,car,shell
end
local object,car=vehicle()
assert(not driver.take_control(object),'default mode silently switched to legacy')
local state=assert(driver.take_control(object,true))
assert(state.legacy_api and car.engine and car.brake and object.actions[1]==384)
assert(state.shell.set_force_exact_integration==nil,'legacy fixture accidentally exposes a newer physics API')
driver.drive(state,1000,4,0.8)
assert(#object.actions==1 and object.actions[1]==274 and not car.brake and car.velocity.z>0,'forward/right failed')
local resets=object.resets
driver.drive(state,1120,4,0.8)
assert(object.resets==resets,'stable input rebuilds the movement queue every tick')
driver.drive(state,1240,-2,-0.8)
assert(#object.actions==1 and object.actions[1]==268,'reverse/left failed')
car.velocity=v(0.3,-0.4,-0.2)
driver.drive(state,1360,0,0.9)
assert(object.actions[1]==384 and car.brake and car.velocity.x==0 and car.velocity.z==0 and car.velocity.y==-0.4,
    'stop left steering/throttle engaged or cancelled gravity')
driver.drive(state,12000,0,0)
assert(#object.actions==1 and object.actions[1]==384,'refresh stacked stale actions')
car.velocity=v(1,2,-3);object.heading=v(0,0.6,0.8)
assert(math.abs(driver.speed(car,object)+1.2)<0.000001,'old GetSpeed replacement differs on a slope/reverse')
driver.release_driver(state)
assert(not object.scripted and #object.actions==0 and not car.brake)
for i=1,5 do assert(released[i],'manual handover retained a movement input') end
assert(not CCar.SetTargetSpeed and not CCar.GetSpeed,'legacy mode modified global bindings')

-- A newer engine may run both modes simultaneously; opting in stays explicit.
CCar.SetTargetSpeed=function(c,value) c.target=value end
CCar.ClearTargetSpeed=function(c) c.target=nil end
CCar.SetThrottle=function(c,value) c.throttle=value end
CCar.SetSteer=function(c,value) c.steer=value end
assert(driver.available() and driver.available(true))
local modern_object,modern_car,modern_shell=vehicle(true)
local modern=assert(driver.take_control(modern_object,false))
assert(modern_shell.exact_calls==1,'native control no longer enables exact integration')
driver.drive(modern,1000,-3,0.4)
assert(modern_car.target==-3 and modern_car.throttle==1 and modern_car.steer==0.4 and modern_object.actions[1]==258,
    'native controller changed after adding legacy mode')
local old_object,old_car=vehicle();local old=assert(driver.take_control(old_object,true))
driver.drive(old,1000,3,-0.5)
assert(old_car.target==nil and old_car.throttle==nil and old_car.steer==nil,'legacy mode called modern governor')
assert(modern_car.target==-3 and modern_car.steer==0.4,'one vehicle changed another vehicle control mode')
local new_legacy,_,new_legacy_shell=vehicle(true)
assert(driver.take_control(new_legacy,true) and new_legacy_shell.exact_calls==1,
    'legacy mode ignored exact integration when available')

-- Optional integration is the only relaxed requirement. Reject broken vehicles
-- before taking script ownership or starting their engine, with a precise reason.
local function reject(reason,legacy,alter)
    local obj,c,s=vehicle()
    alter(obj,c,s)
    local result,detail=driver.take_control(obj,legacy)
    assert(not result and detail==reason,detail or 'unusable vehicle accepted')
    assert(not obj.scripted and not c.engine and #obj.actions==0,'failed preflight changed the vehicle')
end
reject('vehicle_car_unavailable',true,function(obj) obj.get_car=function() return nil end end)
reject('vehicle_weapon_unavailable',true,function(_,c) c.HasWeapon=function() return false end end)
reject('vehicle_physics_shell_unavailable',true,function(obj) obj.get_physics_shell=function() return nil end end)
reject('vehicle_velocity_api_unavailable',true,function(_,_,s) s.set_linear_vel=nil end)
reject('vehicle_exact_integration_unavailable',false,function() end)
local steering=move.left;move.left=nil
assert(not driver.available(true) and driver.available(),'missing legacy input broke native mode or passed preflight')
move.left=steering
print('PASS legacy control: 2026.5.15/MT physics API, optional exact integration, explicit opt-in, reverse/steering/braking, action queue, slope speed, manual handover, mixed native/legacy vehicles and rejected incomplete vehicles')
