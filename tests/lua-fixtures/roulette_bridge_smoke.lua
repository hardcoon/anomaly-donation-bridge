local root=assert(arg[1]);local n=0
local function check(ok,why)n=n+1;assert(ok,why)end
local function module(name)
    local env=setmetatable({},{__index=_G});local f=assert(loadfile(root.."/"..name..".script"))
    setfenv(f,env);f();_G[name]=env;return env
end
local data={}
for i=1,10 do for _,v in ipairs({"a","b"})do data["helmet"..i..v]={class="E_HLMET",kind="o_helmet",cost=tostring(i*100)}end end
data.quest={class="E_HLMET",kind="o_helmet",cost="500",quest_item="true"}
data.trash={class="II_ATTCH",kind="i_misc",cost="200"}
for s,e in pairs(data)do e.inv_name=s;e.visual="test";e.inv_grid_width="1";e.inv_grid_height="1"end
local ini={section_for_each=function(_,fn)for s in pairs(data)do fn(s)end end,
    line_exist=function(_,s,k)return data[s] and data[s][k]~=nil end,r_string=function(_,s,k)return data[s][k]end}
function system_ini()return ini end
function ini_file()error("optional config absent")end
function printf()end
local now=0;function time_global()return now end
local real_now=0;function time_continual()return real_now end
local paused,pause_calls,pause_failure=false,0
local game_device={is_paused=function()return paused end,pause=function(_,value)
    pause_calls=pause_calls+1
    if pause_failure=="release" and not value then error("pause release failed") end
    paused=value
    if pause_failure=="acquire" and value then error("pause enabled before failure") end
end}
function device()return game_device end
game={translate_string=function(s)return s end}
local objects,servers,sequence,callbacks={},{},100,{}
local delayed_id
local actor={id=function()return 0 end,alive=function()return true end,position=function()return {}end,
    level_vertex_id=function()return 1 end,game_vertex_id=function()return 1 end,
    object_by_id=function(_,id)return objects[id]end,iterate_inventory=function(_,fn)for _,o in pairs(objects)do fn(nil,o)end end}
db={actor=actor}
local function item(id,section,story)
    objects[id]={id=function()return id end,section=function()return section end,parent=function()return actor end,
        condition=function()return .4 end,cost=function()return tonumber(data[section].cost)end}
    servers[id]={id=id,parent_id=0,m_story_id=story or 4294967295}
end
local sim={object=function(_,id)return servers[id]end,
    create=function(_,section)sequence=sequence+1;item(sequence,section);return servers[sequence]end,
    release=function(_,se)if se.id~=delayed_id then objects[se.id]=nil end;servers[se.id]=nil end}
function alife()return sim end
function RegisterScriptCallback(name,fn)callbacks[name]=fn end
local cat=module("anomaly_donation_bridge_roulette_catalog");local core=module("anomaly_donation_bridge_roulette_core");local main=module("anomaly_donation_bridge_roulette")
ui_events={WINDOW_KEY_PRESSED=1,WINDOW_KEY_RELEASED=2};key_bindings={kJUMP=4}
DIK_keys={MOUSE_1=337,MOUSE_2=338,MOUSE_3=339}
local registered,pressed=false,0
function Register_UI()registered=true;KEYS_UNLOCK=false end
function Unregister_UI()registered=false;KEYS_UNLOCK=true end
anomaly_donation_bridge_roulette_ui={ensure_class=function()end,adb_roulette_window=function()return {ShowDialog=function()end,HideDialog=function()end,
    OnKeyboard=function(_,key,action)if action==1 then pressed=pressed+1 end end}end}
local played={}
sound_object=setmetatable({s2d=2},{__call=function(_,path)return {play=function()played[#played+1]=path end,stop=function()end}end})
item(1,"helmet5a");item(2,"quest");item(3,"helmet5b",42);item(4,"trash")
math.random=function()return .6 end
main.on_game_start()
callbacks.actor_on_update();check(not main.stats().items,"idle installation does not scan inventory/catalogue")
callbacks.on_key_press(64);check(not main.GUI,"private integration does not steal standalone F6")
for _,count in ipairs({0,-1,1.5,51,math.huge,"1"})do check(not main.request_batch(count,"both"),"invalid request count")end
check(not main.request_batch(1,"invalid"),"invalid policy rejected")
check(not main.request_batch(1,"both","true"),"public API accepts only boolean pause")
local ui_busy=false;function Check_UI()return ui_busy end
ui_busy=true
check(not main.request_batch(2,"high") and not main.request_active(),"busy UI leaves no pending side effects")
ui_busy=false
check(main.request_batch(3,"both") and main.GUI and not core.busy() and servers[1],"request opens without consuming stake or spinning")
check(main.default_count()==3 and main.risk_mode()=="both" and main.request_active(),"N and policy reach game UI")
check(not main.request_batch(1,"low"),"concurrent request cannot replace series")
check(not main.close(),"player must complete initial attempt")
local flags={ret=false};callbacks.on_before_save_input(flags);check(flags.ret,"native quicksave blocked inside roulette")
local saved={};callbacks.save_state(saved)
check(saved.anomaly_donation_bridge_roulette_window.request.count==3 and not saved.hc_upgrader,
    "unstarted request uses private save namespace")
callbacks.load_state(saved);callbacks.actor_on_update()
check(main.default_count()==3 and main.GUI and not core.busy(),"unstarted N survives load")
for index=1,3 do
    local risk=index==2 and "high" or "low"
    local t=assert(main.start_batch(main.default_count(),risk));check(t and main.batch_status().index==index,"exact attempt index")
    now=now+4000;callbacks.actor_on_update()
    check(core.current().phase=="await_claim","result requires acknowledgement")
    if index==2 then
        saved={};callbacks.save_state(saved);local reward=t.reward_ids[1]
        callbacks.load_state(saved);callbacks.actor_on_update()
        check(core.current().reward_ids[1]==reward and servers[reward],"loaded result does not duplicate reward")
    end
    assert(main.claim_reward())
    if index<3 then
        check(not main.GUI and not core.busy() and main.batch_status().waiting,"claim does not invisibly start next attempt")
        saved={};callbacks.save_state(saved);callbacks.load_state(saved);callbacks.actor_on_update()
        check(main.GUI and not core.busy(),"next attempt reopens after claim/load without F6")
    end
end
check(not main.request_active() and select(2,main.batch_status()).completed==3,"exactly N attempts then release modal and queue")
check(servers[2] and servers[3] and servers[4],"quest, story and junk preserved")
for _,mode in ipairs({"low","high"})do
    assert(main.request_batch(1,mode));saved={};callbacks.save_state(saved);callbacks.load_state(saved);callbacks.actor_on_update()
    check(main.risk_mode()==mode,"fixed policy persists before first spin")
    check(not main.start_batch(1,mode=="low" and "high" or "low") and not core.busy(),"API rejects other risk")
    assert(main.start_batch(999,nil));check(main.batch_status().total==1,"template N cannot be overridden by UI count")
    saved={};callbacks.save_state(saved);callbacks.load_state(saved);callbacks.actor_on_update()
    check(main.risk_mode()==mode and main.batch_status().risk==mode,"fixed policy persists during spin/result")
    now=now+4000;callbacks.actor_on_update();assert(main.claim_reward())
    check(not main.request_active(),"single forced-risk series ends")
end
for id in pairs(objects)do if id~=2 and id~=3 and id~=4 then objects[id]=nil;servers[id]=nil end end
check(not main.request_batch(1,"both") and not main.request_active(),"empty inventory cleanly refuses request")
item(1001,"helmet1a");math.random=function()return 0 end
assert(main.request_batch(2,"low"));assert(main.start_batch(2,"low"));now=now+4000;callbacks.actor_on_update()
check(core.result().lost and not servers[1001],"loss consumes wager once")
assert(main.claim_reward());callbacks.actor_on_update()
check(not main.GUI and not main.request_active() and select(2,main.batch_status()).reason=="no_candidates",
    "loss of last eligible item terminates remaining attempts without trapping player")
item(1002,"helmet5a");math.random=function()return .6 end;delayed_id=1002
assert(main.request_batch(1,"high"));local t=assert(main.start_batch(1,"high"));now=now+4000;callbacks.actor_on_update()
check(core.current().phase=="consuming" and not main.claim_reward(),"asynchronous client removal gates reward")
saved={};callbacks.save_state(saved);callbacks.load_state(saved);callbacks.actor_on_update()
local reward=core.current().reward_ids[1];check(#core.current().reward_ids==1 and servers[reward],"pending consume resumes without duplicate")
objects[1002]=nil;callbacks.actor_on_update();assert(main.claim_reward());delayed_id=nil
saved={};callbacks.save_state(saved);callbacks.actor_on_net_destroy();callbacks.load_state(saved);callbacks.actor_on_update()
check(not main.request_active(),"completed request not replayed after load/destroy")
check(pause_calls==0 and not paused,"default/legacy series never reads or changes simulation pause")

-- Paused mode is driven only by UI updates: simulation time and actor update
-- callbacks do not advance. Native destruction events may complete next frame.
item(1100,"helmet5a");math.random=function()return .6 end
real_now=500000
assert(main.request_batch(2,"both",true))
local paused_window=main.GUI
check(paused and main.animation_time()==real_now and not core.busy(),"pause covers the initial stake and risk selection")
flags={ret=false};callbacks.on_before_load_input(0,0,flags);check(flags.ret,"quickload blocked in paused modal")
flags={ret=false};callbacks.on_before_save_input(flags);check(flags.ret,"quicksave blocked in paused modal")
local paused_stamp=now
for index=1,2 do
    local clicks=#played
    delayed_id=main.preview("low").input.id
    local attempt=assert(main.start_batch(2,index==1 and "low" or "high"))
    check(attempt.started==real_now,"paused attempt uses continual time")
    real_now=real_now+100;main.update()
    real_now=real_now+100;main.update()
    check(core.current().phase=="spinning" and #played>clicks and now==paused_stamp,
        "paused UI advances rotation sound without actor callbacks or game time")
    real_now=real_now+4000;main.update()
    check(core.current().phase=="consuming" and paused and not main.claim_reward(),
        "paused transaction still waits for real client destruction")
    objects[delayed_id]=nil;delayed_id=nil;main.update()
    check(core.current().phase=="await_claim" and paused,"result confirmation remains paused")
    assert(main.claim_reward())
    if index==1 then
        check(main.GUI==paused_window and paused and main.batch_status().waiting,
            "same modal and pause survive acknowledgement between attempts")
    end
end
check(not paused and not main.GUI and not main.request_active() and pause_calls==2,
    "whole paused series owns exactly one pause and releases it after final claim")

-- Unchecked mode retains the original simulation-clock behavior, even when
-- continual time exists and advances independently.
local before_calls=pause_calls
assert(main.request_batch(1,"low",false));assert(main.start_batch(1,"low"))
real_now=real_now+10000;main.update()
check(core.current().phase=="spinning" and not paused and pause_calls==before_calls,
    "unchecked mode ignores wall clock and never requests pause")
now=now+4000;main.update();assert(main.claim_reward())

-- Restore the option together with the existing request/batch state; never
-- carry the old holder or a pause across destruction/load.
assert(main.request_batch(1,"high",true));saved={};callbacks.save_state(saved)
check(saved.anomaly_donation_bridge_roulette_window.request.pause_game==true,"unstarted paused policy saved")
callbacks.actor_on_net_destroy();check(not paused and not main.GUI,"net_destroy releases owned pause")
callbacks.load_state(saved);callbacks.actor_on_update()
check(paused and main.GUI and main.risk_mode()=="high","saved unstarted pause restored only with a new window")
assert(main.start_batch(1,"high"));saved={};callbacks.save_state(saved)
check(saved.anomaly_donation_bridge_roulette_batch.pause_game==true,"active batch saves pause policy")
callbacks.load_state(saved);check(not paused and not main.GUI,"load discards old modal and releases own pause")
callbacks.actor_on_update();check(paused and main.GUI,"loaded spin reacquires pause for new holder")
main.update();assert(main.claim_reward());check(not paused,"loaded paused spin finishes and releases pause")

before_calls=pause_calls;paused=true
check(not main.request_batch(1,"both",true) and paused and pause_calls==before_calls and not main.request_active(),
    "foreign pause is neither acquired nor released")
callbacks.actor_on_net_destroy();check(paused,"destroy never clears a foreign pause")
paused=false
local continual=time_continual;time_continual=nil
check(not main.request_batch(1,"both",true) and not main.request_active() and not paused,
    "paused mode refuses missing continual timer without trapping the player")
time_continual=continual
pause_failure="acquire"
check(not main.request_batch(1,"both",true) and not paused and not main.GUI and not registered and KEYS_UNLOCK,
    "partially successful pause acquisition rolls back modal and pause")
pause_failure=nil
assert(main.request_batch(1,"both",true));assert(main.start_batch(1,"low"))
real_now=real_now+4000;main.update();pause_failure="release";assert(main.claim_reward())
check(paused and main.GUI,"failed pause release retains UI for a cleanup retry")
pause_failure=nil;main.update();check(not paused and not main.GUI,"UI retries failed pause release without actor updates")

for id in pairs(objects)do if id~=2 and id~=3 and id~=4 then objects[id]=nil;servers[id]=nil end end
item(1200,"helmet1a");math.random=function()return 0 end
assert(main.request_batch(3,"low",true));assert(main.start_batch(3,"low"));real_now=real_now+4000;main.update()
check(core.result().lost,"paused last-item loss uses existing outcome")
assert(main.claim_reward());check(not paused and not main.request_active() and select(2,main.batch_status()).reason=="no_candidates",
    "losing final eligible stake ends paused batch and releases player")
item(1201,"helmet5a");math.random=function()return .6 end
-- Real extension registry contract: discovery is read-only, failures never
-- consume inventory, accepted UI is exclusively blocking until acknowledged.
module("anomaly_donation_bridge_common");local registry=module("anomaly_donation_bridge_registry")
local effectRoot=assert(arg[2]);local env=setmetatable({},{__index=_G})
local f=assert(loadfile(effectRoot.."/anomaly_donation_bridge_effect_hardcoon_adb_spin_roulette.script"));setfenv(f,env);f();env.on_game_start()
check(#registry.supported_ids()==1 and not main.request_active(),"capability probing does not open roulette")
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="0",risk_mode="both"})=="rejected","invalid protocol count rejected")
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1",risk_mode="no"})=="rejected","invalid protocol risk rejected")
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1",pause_game="yes"})=="rejected","invalid pause value rejected")
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1",risk_mode="both",extra="x"})=="rejected","unknown params rejected")
ui_busy=true
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1",risk_mode="both"})=="deferred" and not main.request_active(),"busy UI defers cleanly")
ui_busy=false
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1",risk_mode="high"})=="executed" and main.risk_mode()=="high","extension starts correct request")
check(registry.busy_reason()=="effect_in_progress" and registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1"})=="deferred","no overlap or duplicate series")
assert(main.start_batch(1,"high"));now=now+4000;callbacks.actor_on_update();assert(main.claim_reward())
check(not registry.busy_reason(),"queue unblocked after final claim")
check(registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1",risk_mode="low",pause_game="true"})=="executed" and paused,
    "template checkbox reaches paused UI through real registry")
assert(main.start_batch(1,"low"));real_now=real_now+4000;main.update();assert(main.claim_reward())
check(not paused and not registry.busy_reason(),"paused template completion releases queue and simulation")
local originalWindow=anomaly_donation_bridge_roulette_ui.adb_roulette_window
anomaly_donation_bridge_roulette_ui.adb_roulette_window=function()return {
    ShowDialog=function()error("simulated UI construction failure")end,HideDialog=function()end}end
check(not main.request_batch(1,"both") and not main.request_active() and not registered and KEYS_UNLOCK,
    "partial UI construction releases registration and leaves request retryable")
anomaly_donation_bridge_roulette_ui.adb_roulette_window=originalWindow
local privateApi=anomaly_donation_bridge_roulette;anomaly_donation_bridge_roulette=nil
check(#registry.supported_ids()==0 and registry.dispatch("hardcoon.adb.spin_roulette",{},{count="1"})=="rejected",
    "missing private addon is reported unsupported without touching actor or standalone addon")
anomaly_donation_bridge_roulette=privateApi
print("Roulette Bridge checks passed: "..n)
