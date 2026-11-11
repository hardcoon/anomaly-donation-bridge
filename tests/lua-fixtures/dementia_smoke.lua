local root,_,optional=assert(arg[1]),arg[2],assert(arg[3])
local checks=0
local function check(v,msg)checks=checks+1;assert(v,msg)end
local callbacks,events,commands={},{},{}
local clock,setting,supported,fail_write,fail_command=1000,0,true,false,false
local encoded
local kernel={
    GetEnvironmentVariableA=function(_,buffer)
        if not encoded then return 0 end
        if buffer then buffer.value=encoded;return #encoded end
        return #encoded+1
    end,
    SetEnvironmentVariableA=function(_,value)if fail_write then return 0 end;encoded=value;return 1 end
}
local fake_ffi={cdef=function()end,load=function()return kernel end,
    new=function()return {}end,string=function(buffer)return buffer.value end}
function RegisterScriptCallback(name,fn)callbacks[name]=callbacks[name]or {};table.insert(callbacks[name],fn)end
function UnregisterScriptCallback(name,fn)
    for i=#(callbacks[name]or {}),1,-1 do if callbacks[name][i]==fn then table.remove(callbacks[name],i)end end
end
local handler
anomaly_donation_bridge_registry={register=function(_,value)handler=value;return true end}
local function load(name,folder)
    local env=setmetatable({require=function(name)assert(name=="ffi");return fake_ffi end},{__index=_G})
    local chunk=assert(loadfile(folder.."/"..name..".script"));setfenv(chunk,env);chunk();_G[name]=env;return env
end
anomaly_donation_bridge_common=load("anomaly_donation_bridge_common",root)
function time_global()return clock end
function get_console()return {get_string=function(_,name)
    assert(name=="mouse_wheel_invert_change_weapon");return supported and tostring(setting)or nil
end}end
function exec_console_cmd(command)
    commands[#commands+1]=command
    local value=assert(command:match("^mouse_wheel_invert_change_weapon ([01])$"),"no binds/cfg_save or foreign settings")
    if not fail_command then setting=tonumber(value)end
end
key_bindings={kJUMP=11,kCROUCH=17,kACCEL=23,kSPRINT_TOGGLE=29}
local diks={ [11]=101,[17]=103,[23]=107,[29]=109 }
for i=1,6 do key_bindings["kWPN_"..i]=400+i*3;diks[400+i*3]=700+i*17 end
function bind_to_dik(id)return diks[id]or 0 end
level={}
for _,method in ipairs({"press","hold","release"})do
    level[method.."_action"]=function(dik)events[#events+1]={method,dik}end
end
db={actor={}}
local module=load("anomaly_donation_bridge_effect_hardcoon_adb_dementia",optional)
module.on_game_start()
local function callback(name,...)for _,fn in ipairs(callbacks[name]or {})do fn(...)end end
local function options(weapon,stance)
    return {duration_seconds="30",mix_stance=stance and "true"or "false",mix_quick_slots="false",
        mix_equipment="false",mix_weapon_slots=weapon,swap_fire_aim="false",swap_lookout="false",invert_movement="false"}
end
local function start(opts)
    local context={actor=db.actor,now=clock}
    check(handler.can_execute(context,opts)==true,"requested bound group can start")
    return handler.execute(context)
end
local function press(id,dik)
    local flags={ret_value=true};callback("on_before_key_press",dik or diks[id],id,false,flags);return flags
end
check(start(options(nil))=="executed" and setting==1,"new weapon/wheel group is ON by default")
local state=assert(rawget(_G,"__anomaly_donation_bridge_dementia_v1"))
local targets={}
for i=1,6 do
    local id=key_bindings["kWPN_"..i]
    local flags=press(id,2000+i) -- secondary binding of the same named action
    local event=events[#events]
    check(flags.ret_value==false and event[1]=="press" and event[2]==diks[state.mapping[id]] and event[2]~=diks[id],
        "six slots follow assigned binds, including a secondary physical key, never fixed numeric DIKs")
    check(not targets[event[2]],"weapon cycle is a bijection without duplicate targets");targets[event[2]]=true
    callback("on_before_key_release",2000+i,id,true,{ret_value=true})
end
check(encoded:match("^2|31000|0|"),"process-local lease records original wheel direction with deadline")
local changed=state.mapping[key_bindings.kWPN_1]
diks[changed]=9001
press(key_bindings.kWPN_1)
check(events[#events][2]==9001,"target rebinding is resolved live, not cached as digits")
callback("on_before_key_release",diks[key_bindings.kWPN_1],key_bindings.kWPN_1,false,{ret_value=true})
check(start(options("true"))=="executed" and setting==1 and state.wheel_original==0,"repeat donation replaces, never stacks wheel inversion")
local held=key_bindings.kWPN_1;press(held);local expected=events[#events][2]
callback("actor_on_net_destroy")
check(setting==0 and events[#events][1]=="release","disconnect restores direction and releases synthetic keys before config save")
state.active=false;state.mapping={};state.pressed={};state.wheel_original=nil
module=load("anomaly_donation_bridge_effect_hardcoon_adb_dementia",optional);module.on_game_start();handler.on_game_load()
check(setting==1 and state.expires_at==31000,"rebuilt F9 VM reapplies lease but preserves original deadline")
press(held);check(events[#events][2]==expected,"F9 retains exactly the same slot mapping")
for _,name in ipairs({"on_before_key_press","on_before_key_hold","on_before_key_release","actor_on_net_destroy"})do
    check(#callbacks[name]==1,"re-registering on load cannot duplicate callbacks")
end
handler.update(31000)
check(setting==0 and not state.active and encoded==nil,"expiry restores original wheel and clears process state")
check(press(held).ret_value==true,"slot keys return to normal at expiry")
setting=1;clock=40000
check(start(options("true"))=="executed" and setting==0,"already inverted preference is reversed, not forced to ON")
handler.update(70000);check(setting==1,"original inverted preference restored exactly")
setting=0;clock=80000
check(start(options("false",true))=="executed" and setting==0 and state.mapping[held]==nil,
    "checkbox OFF leaves all weapon slots and wheel unchanged while old groups still work")
handler.update(110000)
supported=false;clock=120000
local before=#commands
check(start(options("true"))=="executed" and #commands==before,"missing native wheel command must not break slot shuffle on older engines")
handler.update(150000);supported=true
fail_write=true;clock=160000
check(start(options("true"))=="rejected" and setting==0 and not state.active,"failed session store never publishes an active wheel/key effect")
fail_write=false;fail_command=true
check(start(options("true"))=="rejected" and setting==0 and not state.active and encoded==nil,"failed native flag apply rolls back whole effect")
fail_command=false;clock=170000;start(options("true"))
setting=0;before=#commands;handler.update(200000)
check(setting==0 and #commands==before,"cleanup does not overwrite a foreign change to the runtime preference")
clock=210000;start(options("true"));state.active=false;state.mapping={};state.wheel_original=nil;clock=241000
handler.on_game_load()
check(setting==0 and not state.active and encoded==nil,"expired F9 lease restores native flag even if no live Lua state survived")
clock=250000;encoded="1|260000|403=406,406=403";state.active=false;state.mapping={};state.wheel_original=nil
handler.on_game_load()
check(state.active and state.mapping[403]==406 and state.wheel_original==nil and setting==0,"legacy V1 session stays compatible without inventing wheel state")
handler.update(260000)
clock=270000;supported=false
local assigned={};for id,dik in pairs(diks)do assigned[id]=dik end
for i=3,6 do diks[key_bindings["kWPN_"..i]]=i==3 and diks[key_bindings.kWPN_2]or 0 end
check(start(options("true"))=="executed","unbound/duplicate weapon assignments are filtered, not rejected as a whole")
local count=0;for _ in pairs(state.mapping)do count=count+1 end
check(count==2 and state.mapping[key_bindings.kWPN_1]==key_bindings.kWPN_2 and
    state.mapping[key_bindings.kWPN_2]==key_bindings.kWPN_1,"remaining two assigned slots still form a non-fixed cycle")
handler.update(300000);diks=assigned;supported=true;clock=310000
local old_exec=exec_console_cmd;exec_console_cmd=nil
check(start(options("true"))=="executed" and state.wheel_original==nil,"missing console write API cannot break slot remapping")
handler.update(340000);exec_console_cmd=old_exec
check(handler.can_execute({actor=db.actor},{mix_weapon_slots="yes"})==false,"new option is strictly boolean")
print("Dementia weapon/wheel checks passed: "..checks.." (mock native flag/input APIs, not a game test)")
