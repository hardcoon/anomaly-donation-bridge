local core,optional=assert(arg[1]),assert(arg[3])
local checks=0
local function check(value,message) checks=checks+1;assert(value,message) end
local MODULE="anomaly_donation_bridge_effect_god_mode"
local STATE="__anomaly_donation_bridge_god_mode_v2"
local ENV="ADB_GOD_MODE_SESSION_V2"
local OWNER="ADB_GOD_MODE_OWNER_V1"
local PAUSE="ADB_GOD_MODE_PAUSE_V1"
local SAVE="adb_god_mode_timer_v1"
local clock,pid,encoded=1000,123,nil
local extra_env,loaded_state={},{}
local fail_store,ffi_missing,fail_console,silent_console=false,false,false,false
local god,console_reads,console_writes=false,0,0
local callbacks,handler,tips={},nil,{}
local frame,paused,precache,ready,ui_open=100,false,0,true,false
local root,created,deleted,xml_node,last_text,last_pos={},0,0,true,nil,nil
local text_writes,show_calls,root_reads,eq_calls=0,0,0,0
local root_aliases=false
local aliases=setmetatable({},{__mode="k"})
local ui_errors={}
function printf(format,...)ui_errors[#ui_errors+1]=string.format(format,...)end
local condition_values,condition_writes={},{}
local condition_fail,actor_alive=nil,true
local actor=setmetatable({}, {
    __index=function(_,key)return condition_values[key]end,
    __newindex=function(self,key,value)
        if condition_values[key]==nil then rawset(self,key,value);return end
        check(key~="health","Requested restoration must NOT write regular health or immortality coefficients")
        condition_writes[key]=(condition_writes[key]or 0)+1
        if condition_fail==key then error("mock condition setter failed: "..key)end
        if key=="bleeding" then
            check(value==1,"Native ChangeBleeding needs 1 for 100% wound healing, never 0")
            condition_values.bleeding=0
        else
            check(key=="radiation" and value==0 or key=="psy_health" and value==1,"Exact native state restoration only")
            condition_values[key]=value
        end
    end
})
function actor:alive() return actor_alive end
db={actor=actor}
function time_global() return clock end
function device() return {frame=frame,precache_frame=precache,is_paused=function()return paused end} end
function app_ready() return ready end
function Check_UI() return ui_open end
function se_save_var() error("God mode must never enter a save") end
function se_load_var() error("God mode has no save journal to load") end
alife_storage_manager={get_state=function()return loaded_state end}
local kernel={
    GetCurrentProcessId=function()return pid end,
    GetEnvironmentVariableA=function(name,buffer)
        check(name==ENV or name==OWNER or name==PAUSE,"Only owned process-private variables may be read")
        local value=name==ENV and encoded or extra_env[name]
        if not value then return 0 end
        if not buffer then return #value+1 end
        buffer.value=value;return #value
    end,
    SetEnvironmentVariableA=function(name,value)
        check(name==ENV or name==OWNER or name==PAUSE,"Only owned process-private variables may be written")
        if fail_store then return 0 end
        if name==ENV then encoded=value else extra_env[name]=value end;return 1
    end
}
local fake_ffi={cdef=function()end,load=function()return kernel end,
    new=function()return{}end,string=function(b)return b.value end}
function RegisterScriptCallback(event,fn)
    check(not callbacks[event],"No duplicate callback: "..event);callbacks[event]=fn
end
function UnregisterScriptCallback(event,fn)
    check(callbacks[event]==fn,"Only own callback may be unregistered");callbacks[event]=nil
end
anomaly_donation_bridge_registry={register_builtin=function(id,descriptor)
    check(id=="god_mode","No other handler changed");handler=descriptor;return true
end}
news_manager={send_tip=function(a,text)check(a==actor,"Tips target the actor");tips[#tips+1]=text end}
local console={}
function console:get_bool(name)
    console_reads=console_reads+1;check(name=="g_god","Only the explicitly opted-in legacy flag")
    return god
end
function console:execute(command)
    console_writes=console_writes+1
    check(command=="g_god on" or command=="g_god off","No config/other command")
    if not silent_console then god=command=="g_god on" end
    if fail_console then fail_console=false;error("Native failure AFTER mutation") end
end
function get_console() return console end
function vector2() return {set=function(self,x,y)self.x=x;self.y=y;return self end} end
local root_prototype=newproxy(true)
getmetatable(root_prototype).__eq=function()
    eq_calls=eq_calls+1;error("! No such operator [__eq] defined in class [CUIWindow]")
end
getmetatable(root_prototype).__index=function(proxy,key)return aliases[proxy][key]end
getmetatable(root_prototype).__newindex=function(proxy,key,value)aliases[proxy][key]=value end
function get_hud()return {GetWindow=function()
    root_reads=root_reads+1
    if not root_aliases then return root end
    -- Separate luabind userdata wrappers for the same native CUIWindow.
    local proxy=newproxy(root_prototype);aliases[proxy]=root;return proxy
end}end
local function native_window(parent)
    local window={live=true}
    parent.children=parent.children or {};parent.children[window]=true;created=created+1
    function window:Enable(value)check(self.live and value==false,"Non-modal native message")end
    function window:GetWidth()check(self.live,"No stale UI pointer");return 630 end
    function window:GetHeight()check(self.live,"No stale UI pointer");return 30 end
    function window:SetWndPos(p)check(self.live,"No stale UI pointer");last_pos=p end
    function window:TextControl()
        check(self.live,"No stale UI pointer")
        return {SetText=function(_,text)check(window.live,"No stale UI text");last_text=text;text_writes=text_writes+1 end}
    end
    function window:Show(value)
        check(self.live and type(value)=="boolean","No stale/show modal pointer")
        self.shown=value;show_calls=show_calls+1
    end
    function parent:DetachChild(child)
        check(self.children[child] and child.live,"Only our currently owned native message may be removed")
        self.children[child]=nil;child.live=false;deleted=deleted+1
    end
    return window
end
function CScriptXmlInit()
    return {
        ParseFile=function(_,file)check(file=="ui_custom_msgs.xml","Stock game/mod message style, no custom resource")end,
        NodeExist=function(_,node,index)check(node=="gameplay_notification" and index==0,"Only the stock message template");return xml_node end,
        InitStatic=function(_,node,parent)check(node=="gameplay_notification" and (rawequal(parent,root) or rawequal(aliases[parent],root)),"Own stock-styled instance, never overwrite another message");return native_window(parent)end
    }
end
-- luabind class_<Fvector2>("vector2") / CScriptXmlInit are callable class
-- objects, not Lua functions. Match the native ABI by default; the old
-- function-only mock hid the production timer guard regression.
local constructor_profile=arg[4] or "userdata"
for _,name in ipairs({"vector2","CScriptXmlInit"}) do
    local factory=_G[name]
    if constructor_profile=="userdata" then
        local constructor=newproxy(true)
        getmetatable(constructor).__call=function()return factory()end
        _G[name]=constructor
    elseif constructor_profile=="table" then
        _G[name]=setmetatable({},{__call=function()return factory()end})
    else check(constructor_profile=="function","Unknown constructor profile") end
end
local function load_module(name,folder)
    local env=setmetatable({require=function(name)
        check(name=="ffi","Only process RAM dependency")
        if ffi_missing then error("ffi unavailable")end
        return fake_ffi
    end},{__index=_G})
    local chunk=assert(loadfile(folder.."/"..name..".script"));setfenv(chunk,env);chunk();return env
end
anomaly_donation_bridge_common=load_module("anomaly_donation_bridge_common",core)
local function cp(text)return assert(anomaly_donation_bridge_common.game_text_cp1251(text))end
local function load(fresh)
    if fresh then rawset(_G,STATE,nil);callbacks={}end
    local module=load_module(MODULE,optional);module.on_game_start();return module
end
local function start(options)
    local context={actor=actor,now=clock}
    local ok,status,reason=handler.can_execute(context,options or {duration_seconds="10"})
    if not ok then return status,reason end
    return handler.execute(context)
end
local function hit(already_vetoed)
    local flags={ret_value=not already_vetoed}
    callbacks.actor_on_before_hit({power=100000,type="fire_wound"},0,flags)
    return flags.ret_value
end
local function reset()
    if callbacks.actor_on_net_destroy then callbacks.actor_on_net_destroy() end
    clock,pid,encoded=1000,123,nil;god,console_reads,console_writes=false,0,0
    extra_env={};loaded_state={}
    fail_store,ffi_missing,fail_console,silent_console=false,false,false,false
    frame,paused,precache,ready,ui_open=100,false,0,true,false
    root,created,deleted,xml_node,last_text,last_pos={},0,0,true,nil,nil
    text_writes,show_calls,root_reads,eq_calls=0,0,0,0
    root_aliases=false;ui_errors={}
    condition_values={health=.5,radiation=.4,bleeding=.2,psy_health=.3};condition_writes={}
    condition_fail=nil;actor_alive=true;arszi_psy=nil
    db.actor=actor;tips={};load(true)
end
local function save_for_load()
    loaded_state={};callbacks.save_state(loaded_state)
end
reset()
check(hit()==true,"Inactive default must not veto hits")
check(start()=="executed","Default starts without legacy console")
check(encoded=="2|123|11000|hit|0|1","Exact deadline and checkbox state exist only in process RAM")
check(console_reads==0 and console_writes==0 and not god,"Default must not even read g_god")
check(not hit() and not hit(true),"Default vetoes hits and never undoes another mod's veto")
for _,kind in ipairs({"strike","fire_wound","explosion","burn","chemical_burn","shock","wound","radiation","telepatic","physic_strike"})do
    local flags={ret_value=true};callbacks.actor_on_before_hit({power=1e9,type=kind},0,flags)
    check(flags.ret_value==false,"Every hit type is vetoed when routed through the stock callback: "..kind)
end
check(last_text==cp("Год-мод: 10с") and last_pos.x==197 and last_pos.y==718,
    "Unchanged stock message is horizontally centered, 20 native UI units above bottom")
check(actor.health==.5 and actor.radiation==0 and actor.bleeding==0 and actor.psy_health==1,
    "Requested conditions restored immediately; regular health and permanent coefficients unchanged")
clock=2001;handler.update(clock);check(last_text==cp("Год-мод: 9с"),"Countdown is derived from actual remaining time")
save_for_load();handler.on_game_load();frame=110;handler.update(clock)
check(rawget(_G,STATE).expires_at==11000 and not hit(),"F9 preserves original deadline")
check(type(callbacks.save_state)=="function" and type(callbacks.load_state)=="function","F5/F9 have a timer-only snapshot hook")
callbacks.actor_on_net_destroy();check(deleted>=1,"Destroy callback removes owned HUD before native destruction")
root={};load(true);clock=3000;handler.on_game_load();frame=120;handler.update(clock)
check(not hit() and last_text==cp("Год-мод: 9с"),"Fresh Lua VM resumes the frozen remainder, without consuming load time")
check(rawget(_G,STATE).expires_at==11999,"Loading time is excluded, not charged to the timer")
clock=11998;check(not hit(),"Last millisecond is protected")
clock=11999;check(hit(),"No extra immunity after real expiry even before registry update")
handler.update(clock);check(encoded==nil and not rawget(_G,STATE).active,"Expiry clears RAM only")
check(tips[#tips]==cp("Год-мод закончен"),"Expiration is reported in PDA")
local count=#tips;handler.update(clock+1);check(#tips==count,"PDA completion appears once")
check(console_reads==0 and console_writes==0,"Full default lifecycle never touches the console")

reset();start({duration_seconds="5",show_timer="false"})
check(created==0 and not hit(),"Timer-off checkbox never weakens protection")
clock=6000;handler.update(clock);check(tips[#tips]==cp("Год-мод закончен"),"Timer-off still reports completion")
reset();start();clock=2000;start({duration_seconds="5",show_timer="false"})
check(rawget(_G,STATE).expires_at==11000 and not rawget(_G,STATE).show_timer,"Repeat cannot shorten a longer active lease")
clock=5000;start({duration_seconds="20"});check(rawget(_G,STATE).expires_at==25000,"Longer repeat extends to the real deadline")

reset();check(start({duration_seconds="5",use_g_god="true"})=="executed" and god,"Legacy mode explicitly opts into g_god")
check(hit(),"Legacy mode does not also install hit-veto immunity")
save_for_load();callbacks.actor_on_net_destroy();root={};load(true);clock=2000;handler.on_game_load();frame=120;handler.update(clock)
check(god and rawget(_G,STATE).expires_at==7000,"Legacy state survives VM/F9 without consuming load time")
clock=7000;handler.update(clock);check(not god and encoded==nil,"Legacy expiry restores original OFF flag")
reset();god=true;start({duration_seconds="5",use_g_god="true"});clock=6000;handler.update(clock)
check(god,"Pre-existing external god mode must remain ON")
reset();start({duration_seconds="10",use_g_god="true"});clock=2000;start({duration_seconds="5"})
check(not god and not hit() and rawget(_G,STATE).expires_at==11000,"Switching to new mode restores legacy flag and keeps deadline")
clock=3000;start({duration_seconds="5",use_g_god="true"});check(god and hit(),"Switching back selects only legacy mode")
clock=11000;handler.update(clock);check(not god,"Mode switch must not capture our own flag as external baseline")

reset();fail_console=true
check(start({duration_seconds="5",use_g_god="true"})=="uncertain" and not god and encoded==nil,"Partial native mutation is rolled back")
reset();silent_console=true
check(start({duration_seconds="5",use_g_god="true"})=="uncertain" and encoded==nil,"Silently ignored console command is detected")
reset();fail_store=true
check(start()=="rejected" and hit() and console_writes==0,"No untracked immunity after session write failure")
reset();start({duration_seconds="5",use_g_god="true"});clock=6000;fail_store=true;handler.update(clock)
check(not god,"RAM cleanup failure cannot re-enable expired legacy protection")
fail_store=false;handler.update(clock);check(encoded==nil and tips[#tips]==cp("Год-мод закончен"),"Cleanup retry completes and reports expiration")
reset();ffi_missing=true;load(true)
check(start()=="rejected" and hit(),"No persistence fallback to a game save/config when FFI is missing")
reset();start();encoded=nil;pid=456;load(true);handler.on_game_load()
check(hit() and not rawget(_G,STATE).active,"Crash/new process cannot resurrect immunity")
reset();start();pid=456;load(true);handler.on_game_load()
check(hit() and encoded==nil,"Child process inheriting environment cannot inherit immunity")
for _,bad in ipairs({"garbage","2|123|nan|hit|0|1","2|123|11000|unknown|0|1","2|123|999999999|hit|0|1"}) do
    reset();encoded=bad;load(true);handler.on_game_load();check(hit() and encoded==nil,"Malformed/foreign-future lease is rejected")
end
reset();start();save_for_load();callbacks.actor_on_net_destroy();root={};load(true);clock=12000;handler.on_game_load()
frame=120;handler.update(clock)
check(not hit() and rawget(_G,STATE).expires_at==22000 and tips[#tips]~=cp("Год-мод закончен"),"A long load cannot consume or expire the frozen effect")
reset();start();paused=true;handler.update(clock)
check(created==1 and deleted==0 and not rawget(_G,STATE).hud.visible and not hit(),"Pause hides the SAME UI element, not protection")
paused=false;ui_open=true;handler.update(clock);check(created==1 and deleted==0,"Inventory/dialog cannot rebuild timer UI")
ui_open=false;precache=1;handler.update(clock);check(created==1 and deleted==0,"No UI creation during precache")
precache=0;ready=false;handler.update(clock);check(created==1 and deleted==0,"No UI creation while app not ready")
ready=true;handler.update(clock)
check(created==1 and deleted==0 and rawget(_G,STATE).hud.visible and text_writes==1,"Timer returns on SAME node without rewriting unchanged text")
handler.on_level_changing();save_for_load();check(deleted==created,"Transition releases native UI immediately")
root={};handler.on_game_load();frame=105;handler.update(clock);check(deleted==created,"8-frame safety barrier after F9/transition")
frame=110;handler.update(clock);check(created>deleted,"Timer rebuilt after safe frames")
local before_delete=deleted;handler.on_game_load()
check(deleted==before_delete+1,"Repeated load callback cannot leave a ghost countdown on the same root")
reset();root_aliases=true;start()
for delta=0,999,10 do clock=1000+delta;handler.update(clock)end
check(created==1 and deleted==0 and text_writes==1 and show_calls==1,
    "One native element remains visible; identical second never writes text/Show or deletes it")
check(eq_calls==0 and #ui_errors==0 and root_reads==1,
    "No native CUIWindow equality/comparison or per-update wrapper lookup")
clock=2000;handler.update(clock)
check(text_writes==2 and last_text==cp("Год-мод: 9с") and created==1 and deleted==0 and show_calls==1,
    "Next second updates only the text, without hide/delete/recreate")
for delta=1,999,10 do clock=2000+delta;handler.update(clock)end
check(text_writes==2,"No duplicate text writes within the same remaining second")
clock=5000;handler.update(clock)
check(text_writes==3 and last_text==cp("Год-мод: 6с"),"Delayed update uses actual deadline, not a drifting decrement counter")
paused=true;handler.update(clock);handler.update(clock)
check(show_calls==2 and created==1 and deleted==0,"Pause changes visibility only once, without deleting text")
paused=false;clock=6000;handler.update(clock)
check(show_calls==3 and text_writes==4 and last_text==cp("Год-мод: 5с"),"Resume shows same element with current remainder")
check(pcall(handler.on_game_load) and eq_calls==0,"Load also avoids missing native __eq via raw identity check")
reset();xml_node=false;check(start()=="executed" and created==0 and not hit(),"Missing mod message node cannot break immunity or invoke unsafe InitStatic")
reset();start();load(false);check(not hit(),"Re-registering removes only own callbacks and does not duplicate them")
reset();start();clock=11000;db.actor=nil;handler.update(clock)
check(rawget(_G,STATE).finish_tip_pending,"Missing actor defers PDA completion")
db.actor=actor;handler.update(clock);check(tips[#tips]==cp("Год-мод закончен") and not rawget(_G,STATE).finish_tip_pending,"PDA completion delivered once actor returns")
for _,options in ipairs({{duration_seconds="4"},{duration_seconds="601"},{show_timer="yes"},{use_g_god="1"},{unknown="x"}})do
    reset();check(start(options)=="rejected" and encoded==nil,"Bad template parameters are rejected without mutation")
end
local function hazards()
    condition_values.radiation=.7;condition_values.bleeding=.6;condition_values.psy_health=.2
end
local function still_hazardous()
    return actor.radiation==.7 and actor.bleeding==.6 and actor.psy_health==.2
end
local function restored()
    return actor.radiation==0 and actor.bleeding==0 and actor.psy_health==1 and actor.health==.5
end
reset();start();hazards()
for delta=1,999,10 do clock=1000+delta;handler.update(clock)end
check(still_hazardous() and condition_writes.radiation==1 and condition_writes.bleeding==1 and condition_writes.psy_health==1,
    "One restoration per remaining second, not every update")
clock=2000;handler.update(clock)
check(restored() and condition_writes.radiation==2 and condition_writes.bleeding==2 and condition_writes.psy_health==2,
    "Next timer second fully clears new radiation/wounds and restores psy health")
hazards();clock=11000;handler.update(clock);handler.update(clock+1000)
check(still_hazardous() and condition_writes.radiation==2 and encoded==nil,
    "At/after expiry NOTHING is restored; damage/states can worsen normally")
check(start({duration_seconds="5"})=="executed" and restored(),"A new effect does not inherit stale restoration tick suppression")

reset();start({duration_seconds="5",show_timer="false"});hazards();clock=2000;handler.update(clock)
check(restored() and created==0,"Hidden timer checkbox does not disable per-second restoration")
hazards();clock=6000;handler.update(clock);check(still_hazardous(),"Hidden timer still ends restoration on its real deadline")
reset();start();hazards();ui_open=true;clock=2000;handler.update(clock)
check(restored() and not rawget(_G,STATE).hud.visible,"Inventory/dialog hides text but active-world restoration continues")
hazards();paused=true;clock=3000;handler.update(clock);check(still_hazardous(),"No condition writes while paused")
paused=false;precache=1;handler.update(clock);check(still_hazardous(),"No condition writes during precache")
precache=0;ready=false;handler.update(clock);check(still_hazardous(),"No condition writes before app is ready")
ready=true;handler.update(clock);check(restored(),"Restoration resumes safely without changing expiry")
hazards();actor_alive=false;clock=4000;handler.update(clock);check(still_hazardous(),"Cannot resurrect a dead actor")

reset();start();save_for_load();hazards();callbacks.actor_on_net_destroy();root={};load(true);clock=2000;handler.on_game_load()
check(still_hazardous(),"Fresh F9 VM must wait for safe frames before condition writes")
frame=120;handler.update(clock);check(restored() and rawget(_G,STATE).expires_at==12000,"F9 resumes conditions under the frozen remaining-time lease")
hazards();encoded=nil;pid=456;load(true);handler.on_game_load();frame=140;handler.update(clock)
check(still_hazardous() and not rawget(_G,STATE).active,"Fresh process/loading a normal save cannot resurrect auto-restoration")

reset();local script_psy=.2;local psy_writes=0
arszi_psy={get_psy_health=function()return script_psy end,set_psy_health=function(value)
    check(value==1,"Only restore current script psy health, not resistance/callbacks")
    psy_writes=psy_writes+1;script_psy=value
end}
start();check(script_psy==1 and psy_writes==1,"Stock separate arszi psy-health pool restored through public API")
script_psy=.1;clock=2000;handler.update(clock);check(script_psy==1 and psy_writes==2,"Separate psy scale restores once each second")
script_psy=.1;clock=11000;handler.update(clock);check(script_psy==.1 and psy_writes==2,"No script psy restoration after deadline")
reset();arszi_psy={get_psy_health=function()return nil end,set_psy_health=function()error("Uninitialized pool must not be forced")end}
start();check(restored() and #ui_errors==0,"Uninitialized optional psy module is skipped without breaking native restoration")

reset();condition_fail="bleeding";start();check(condition_writes.bleeding==1,"Setter failure is caught")
for delta=1,999,10 do clock=1000+delta;handler.update(clock)end
check(condition_writes.bleeding==1,"Failure cannot cause unbounded per-frame writes")
condition_fail=nil;clock=2000;handler.update(clock);check(restored(),"Next second retries failed restoration")
reset();fail_store=true;start();check(next(condition_writes)==nil,"Untracked effect cannot start any condition writes")
reset();start({duration_seconds="5",use_g_god="true"});hazards();clock=2000;handler.update(clock)
check(god and restored(),"Opt-in legacy mode also receives requested per-second restoration")
hazards();fail_store=true;clock=6000;handler.update(clock)
check(not god and still_hazardous(),"Expired cleanup failure cannot leave god mode or ongoing restoration")
-- Save points hold a timer snapshot, but never permission to activate it in a
-- different game process. Native flags/health/resistance are not serialized.
local function snapshot()
    local data={other_mod={untouched=true}};callbacks.save_state(data)
    check(data.other_mod.untouched and type(data[SAVE])=="table","Only our own snapshot key is written")
    check(data[SAVE].previous==nil and data[SAVE].expires_at==nil,"No native console baseline/absolute process clock enters save")
    return data
end
local function restore_save(data,new_vm)
    loaded_state=data
    if new_vm then root={};load(true) end
    callbacks.load_state(data)
    local current=data[SAVE]
    callbacks.save_state(data)
    check(data[SAVE]==current,"Early save during load must not overwrite the newly loaded timer")
    handler.on_game_load()
end
reset();start();clock=4000;handler.update(clock)
local saved_seven=snapshot()
check(saved_seven[SAVE].remaining_ms==7000 and saved_seven[SAVE].active,"Exact remaining milliseconds saved at F5")
clock=9000;handler.update(clock);check(last_text==cp("Год-мод: 2с"),"Time advances after save")
callbacks.on_before_load_input();callbacks.actor_on_net_destroy();clock=90000
restore_save(saved_seven,true);precache=10;frame=120;handler.update(clock)
check(rawget(_G,STATE).loading and rawget(_G,STATE).paused_remaining==7000,"Load/precache never consumes saved seconds")
clock=100000;handler.update(clock);precache=0;ready=false;handler.update(clock)
check(rawget(_G,STATE).paused_remaining==7000,"Not-ready engine cannot start the countdown")
ready=true;handler.update(clock)
check(last_text==cp("Год-мод: 7с") and rawget(_G,STATE).expires_at==107000 and not hit(),"F9 rolls the timer back to its save point after loading")
clock=107000;handler.update(clock);check(hit() and encoded==nil,"Rebased countdown still has a hard end")
restore_save(saved_seven,true);frame=140;handler.update(clock)
check(not hit() and rawget(_G,STATE).expires_at==114000,"Earlier save restores its timer even after the effect finished")
encoded=nil;extra_env={};restore_save(saved_seven,true);frame=160;handler.update(clock)
check(hit() and not rawget(_G,STATE).active and encoded==nil,"Fresh process cannot revive a timer snapshot, even with a reused PID")
reset();local before_effect=snapshot();start({duration_seconds="10",use_g_god="true"})
restore_save(before_effect,false);frame=120;handler.update(clock)
check(not god and hit() and not rawget(_G,STATE).active,"Loading a pre-effect save removes protection and restores the owned legacy flag")
reset();start({duration_seconds="10",use_g_god="true"});restore_save({},false);frame=120;handler.update(clock)
check(not god and hit() and not rawget(_G,STATE).active,"Old save without any snapshot cannot inherit protection from an abandoned timeline")
reset();start();local inherited=snapshot();pid=456;restore_save(inherited,true);frame=120;handler.update(clock)
check(hit() and not rawget(_G,STATE).active,"Inherited parent environment cannot authorize a child process snapshot")
reset();start();clock=5000;callbacks.on_before_load_input(nil,nil,{ret_value=false})
handler.update(clock);check(not rawget(_G,STATE).loading,"Rejected quickload cannot freeze the timer")
callbacks.on_before_load_input();clock=6000;frame=120;handler.update(clock)
check(not rawget(_G,STATE).loading and rawget(_G,STATE).expires_at==12000,"Cancelled/no-op load input resumes after safe frames rather than hanging")
for _,bad in ipairs({-1,600001,0/0,math.huge,"garbage"})do
    reset();start();local data=snapshot();data[SAVE].remaining_ms=bad
    restore_save(data,false);frame=120;handler.update(clock)
    check(hit() and not rawget(_G,STATE).active,"Malformed saved timer cannot grant protection")
end
reset();start();local data=snapshot();data[SAVE].owner="different-process"
restore_save(data,false);frame=120;handler.update(clock);check(hit() and not rawget(_G,STATE).active,"Foreign saved ownership rejected")
reset();start();clock=3000;callbacks.actor_on_net_destroy();clock=100000;callbacks.actor_on_net_destroy()
check(rawget(_G,STATE).paused_remaining==8000,"Repeated destruction does not subtract loading time twice")
reset();start({duration_seconds="10",use_g_god="true"});encoded="2|123|999999999|console|0|1"
restore_save({},true);frame=120;handler.update(clock)
check(not god and encoded==nil,"Unexpected native-clock reset cannot leak an owned legacy console flag")
print("God mode smoke passed: "..checks.." checks ("..constructor_profile.." constructors; process/native UI/save-point mocks, not a game session).")
