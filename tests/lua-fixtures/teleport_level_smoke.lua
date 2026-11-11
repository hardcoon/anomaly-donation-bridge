local core, optional = assert(arg[1]), assert(arg[3])
local function load_module(root, name)
    local env=setmetatable({}, {__index=_G})
    local chunk=assert(loadfile(root.."/"..name..".script"));setfenv(chunk,env);chunk();return env
end
local clock, current, state, moves, calls = 1000, "l01_escape", {}, {}, 0
local KEY="adb_random_level_arrival_v1"
local graph_ready, allowed, fail_move = true, true, false
local graph_calls, game_ready, hud_free = 0, true, true
local callbacks={}
local logs={}
function printf(format,...) logs[#logs+1]=string.format(format,...) end
function RegisterScriptCallback(name,fn) callbacks[name]=fn end
local render={frame=1,precache_frame=0,f_time_delta=0.1,paused=false}
function render:is_paused() return self.paused end
function device() return render end
function app_ready() return game_ready end
actor_menu={is_hud_free=function() return hud_free end}
function time_global() return clock end
function vector() return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end} end
-- Engine class_<Fvector>("vector") is callable userdata, NOT a Lua function.
-- Keep the default fixture ABI-shaped so function-only mock factories cannot
-- hide a production constructor-type regression again.
local vector_factory=vector
local vector_profile=arg[4] or "userdata"
if vector_profile=="userdata" then
    vector=newproxy(true)
    getmetatable(vector).__call=function() return vector_factory() end
elseif vector_profile=="table" then
    vector=setmetatable({},{__call=function() return vector_factory() end})
else assert(vector_profile=="function","Unknown constructor profile") end
level={name=function() return current end,
    present=function() return game_ready end,
    vertex_in_direction=function(id,_,distance)
        graph_calls=graph_calls+1
        assert(distance==0);return graph_ready and id>=0 and id<128 and id or 4294967295
    end,
    vertex_position=function(id)
        graph_calls=graph_calls+1
        assert(graph_ready and id>=0 and id<128)
        return vector():set(id*100-6400,id*2,5000)
    end}
local actor={pos=vector():set(0,0,0), vertex=0, living=true}
function actor:position() return self.pos end
function actor:level_vertex_id() return self.vertex end
function actor:alive() return self.living end
function actor:accessible() return allowed end
function actor:set_actor_position(p)
    assert(state[KEY]==nil,"Travel marker must be consumed before native teleport")
    moves[#moves+1]={x=p.x,y=p.y,z=p.z}
    if fail_move=="noop" then return end
    self.pos=vector():set(p.x,p.y,p.z)
    if fail_move then error("Partial setter failure") end
end
db={actor=actor}
alife_storage_manager={get_state=function() return state end}
function JumpToLevel(name)
    assert(state[KEY] and state[KEY].target==name,"Marker not persisted before ChangeLevel")
    calls=calls+1;return true
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_registry=load_module(core,"anomaly_donation_bridge_registry")
anomaly_donation_bridge_catalog={validate_sections=function(params)
    local entries={}
    for section in params.level_sections:gmatch("[^,]+") do entries[#entries+1]={faction=section} end
    return entries
end}
local module=load_module(optional,"anomaly_donation_bridge_effect_teleport_random_level")
module.on_game_start()
local handler=anomaly_donation_bridge_registry.get("teleport_random_level")
local function request()
    local context={}
    assert(handler.can_execute(context,{level_sections="l02_garbage",catalog_revision="rev"}))
    assert(handler.execute(context)=="executed")
end
local function reset()
    state={};handler.on_game_load();current="l01_escape";clock=clock+20000
    logs={}
    actor.pos=vector():set(0,0,0);actor.vertex=0;actor.living=true
    allowed,graph_ready,fail_move=true,true,false
    game_ready,hud_free,render.paused,render.precache_frame=true,true,false,0
end
local function tick(ms,frames)
    clock=clock+ms;render.frame=render.frame+(frames or 1);handler.update(clock)
end
local function settle()
    for _=1,32 do tick(16) end
end
local function arrive()
    handler.on_level_changing();current="l02_garbage"
    handler.on_game_load()
    local before=#moves
    tick(800)
    assert(#moves==before,"Arrival teleported before the post-spawn safety delay")
    settle()
end
-- Arrival scans the actual common helper's entire graph, well beyond 2000m
-- and without the ordinary near-entry height band. No parameters were added.
request();assert(#moves==0);arrive()
assert(#moves==1 and moves[1].z==5000 and state[KEY]==nil)
assert(#logs==0,"Ordinary successful travel must remain quiet")
for _=1,3 do clock=clock+1000;handler.update(clock);handler.on_game_load() end
assert(#moves==1,"F9 repeated an already consumed arrival")
-- Fresh Lua VM/modules recover only plain marker data saved by ChangeLevel.
reset();request();handler.on_level_changing();current="l02_garbage"
anomaly_donation_bridge_registry=load_module(core,"anomaly_donation_bridge_registry")
module=load_module(optional,"anomaly_donation_bridge_effect_teleport_random_level");module.on_game_start()
handler=anomaly_donation_bridge_registry.get("teleport_random_level")
handler.on_game_load();settle();assert(#moves==2)
-- A loaded save is authoritative: never carry the old world's pending request.
reset();request();state={};current="l02_garbage";handler.on_game_load()
clock=clock+1000;handler.update(clock);assert(#moves==2)
reset();request();handler.on_game_load();clock=clock+1000;handler.update(clock)
assert(#moves==2 and state[KEY]==nil,"Loading source save unexpectedly teleported")
reset();request();handler.on_level_changing();current="l03_agroprom"
handler.on_game_load();clock=clock+1000;handler.update(clock)
assert(#moves==2 and state[KEY]==nil,"Wrong destination was randomized")
-- Wait for actual graph/actor readiness. A bad entry LVID may use real node 0.
reset();request();graph_ready=false;game_ready=false
local before_graph=graph_calls;arrive();assert(#moves==2 and state[KEY] and graph_calls==before_graph)
graph_ready=true;game_ready=true;actor.vertex=4294967295;settle()
assert(#moves==2 and state[KEY],"Invalid actor location must not be replaced by an early vertex-zero fallback")
actor.vertex=0;settle()
assert(#moves==3 and state[KEY]==nil)
-- Inaccessible points retry only within a bounded arrival window.
reset();request()
local real_sampler=anomaly_donation_bridge_common.random_safe_point
anomaly_donation_bridge_common.random_safe_point=function() return nil,"safe_teleport_vertex_unavailable" end
arrive()
for _=1,40 do tick(250) end
assert(#moves==3 and state[KEY]==nil)
anomaly_donation_bridge_common.random_safe_point=real_sampler
reset();request();actor.living=false;arrive();assert(#moves==3 and state[KEY]==nil)
-- Native setter's partial/unknown result is never retried.
reset();request();fail_move=true;arrive();assert(#moves==4 and state[KEY]==nil)
clock=clock+1000;handler.update(clock);assert(#moves==4)
-- No graph calls at all while the renderer is loading, precaching or the
-- new actor is absent, even if loading exceeds the old ten-second deadline.
do
    reset();request();handler.on_level_changing();current="l02_garbage"
    local before,probes=#moves,graph_calls
    game_ready=false
    for _=1,300 do tick(100) end
    assert(state[KEY] and #moves==before and graph_calls==probes,"Late load callback lost acknowledged request")
    handler.on_game_load()
    for _=1,300 do tick(100) end
    assert(state[KEY] and #moves==before and graph_calls==probes,"Long loading expired or probed graph")
    game_ready=true;render.precache_frame=20
    for _=1,30 do tick(100) end
    assert(#moves==before and graph_calls==probes,"Precache probed native graph")
    render.precache_frame=0;db.actor=nil
    for _=1,20 do tick(100) end
    db.actor=actor
    handler.update(clock) -- first playable frame starts the frame budget
    for _=1,14 do tick(16) end
    assert(#moves==before and graph_calls==probes,"Fewer than 15 ready frames touched navigation")
    tick(16);assert(#moves==before+1 and state[KEY]==nil)
end
-- Exactly 15 rendered frames, not seconds and not 15 runtime callbacks.
-- Slow/high FPS and the normal 100ms runtime polling use the same counter.
for _,fps in ipairs({1,2,15,30,60,144,240}) do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before,probes=#moves,graph_calls
    handler.update(clock)
    for _=1,14 do tick(1000/fps) end
    assert(#moves==before and graph_calls==probes,"Frame budget was skipped at FPS "..fps)
    tick(1000/fps)
    assert(#moves==before+1 and state[KEY]==nil,"15 ready frames did not finish at FPS "..fps)
end
do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before,probes=#moves,graph_calls
    handler.update(clock);tick(100,8);tick(100,6)
    assert(#moves==before and graph_calls==probes)
    tick(100,1)
    assert(#moves==before+1,"Wait counted 15 polls instead of 15 rendered frames")
end
-- Pause/menu/precache breaks discard old frames and require 15 NEW frames.
for _,interrupt in ipairs({"pause","menu","precache"}) do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before,probes=#moves,graph_calls
    handler.update(clock);tick(100,7)
    if interrupt=="pause" then render.paused=true
    elseif interrupt=="menu" then hud_free=false
    else render.precache_frame=10 end
    for _=1,40 do tick(100) end
    assert(#moves==before and graph_calls==probes,interrupt)
    render.paused=false;hud_free=true;render.precache_frame=0
    handler.update(clock)
    for _=1,14 do tick(16) end
    assert(#moves==before and graph_calls==probes,"Interrupted frames were retained: "..interrupt)
    tick(16);assert(#moves==before+1 and state[KEY]==nil)
end
-- Time alone never advances the budget, and a reset/wrapped frame counter
-- starts a fresh interval. Long frame times do not impose a hidden timer.
do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before,probes=#moves,graph_calls
    handler.update(clock);tick(100,7)
    for _=1,20 do tick(1000,0) end
    assert(#moves==before and graph_calls==probes,"Clock jump satisfied a rendered-frame wait")
    tick(100,7);assert(#moves==before and graph_calls==probes)
    tick(100,1);assert(#moves==before+1)
end
do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before,probes=#moves,graph_calls
    handler.update(clock);tick(16,7)
    render.frame=render.frame-5;handler.update(clock)
    for _=1,14 do tick(16) end
    assert(#moves==before and graph_calls==probes,"Backward frame counter retained old frames")
    tick(16);assert(#moves==before+1)
end
-- Some packs load persisted storage after on_game_load. The stock first
-- actor update recovers it, then still waits 15 playable rendered frames.
do
    reset();request();local saved=state[KEY]
    handler.on_level_changing();current="l02_garbage";state={};handler.on_game_load()
    state[KEY]=saved;callbacks.actor_on_first_update()
    local before=#moves;settle();assert(#moves==before+1 and state[KEY]==nil)
    callbacks.actor_on_first_update();settle();assert(#moves==before+1,"First-update callback repeated consumed travel")
end
-- Recheck readiness after sampling, before consuming the marker/native setter.
do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before=#moves;local original=anomaly_donation_bridge_common.random_safe_point
    anomaly_donation_bridge_common.random_safe_point=function(...)
        local point,reason=original(...);render.precache_frame=10;return point,reason
    end
    settle();assert(#moves==before and state[KEY],"Sampling readiness race invoked setter")
    anomaly_donation_bridge_common.random_safe_point=original;render.precache_frame=0;settle()
    assert(#moves==before+1 and state[KEY]==nil)
end
-- game_object exposes accessible() to CActor, but the native implementation
-- casts only to CCustomMonster and returns false/logs an error for the actor.
-- Both a false result and a throwing binding must therefore never be called.
do
    local original=actor.accessible
    for _,kind in ipairs({"false","error"}) do
        reset();local probes=0
        actor.accessible=function()
            probes=probes+1
            if kind=="error" then error("CRestrictedObject: cannot access class member accessible") end
            return false
        end
        request();local before=#moves;arrive()
        assert(#moves==before+1 and probes==0,"NPC-only accessible() rejected the actor: "..kind)
    end
    actor.accessible=original
end
-- Recover a late marshal marker on a normal playable update too: callback
-- ordering/other mods must not silently discard the second half of travel.
do
    reset();request();local saved=state[KEY]
    handler.on_level_changing();current="l02_garbage";state={};handler.on_game_load()
    callbacks.actor_on_first_update()
    local before,probes=#moves,graph_calls
    game_ready=false;state[KEY]=saved
    for _=1,50 do tick(100) end
    assert(#moves==before and graph_calls==probes,"Late marker probed graph while loading")
    game_ready=true;settle()
    assert(#moves==before+1 and state[KEY]==nil,"Update did not recover late marshal marker")
    settle();assert(#moves==before+1,"Late marker recovery repeated travel")
end
-- A blocked arrival reports its exact guard once, then one terminal reason.
-- Only pending load recovery may adopt a marker. Consumed/cancelled requests
-- cannot reappear during a transient storage outage in the same session.
do
    reset();request();local saved=state[KEY];local before=#moves
    arrive();assert(#moves==before+1)
    state[KEY]=saved;settle()
    assert(#moves==before+1,"Consumed request was adopted twice in one session")
end
do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before=#moves;local original=anomaly_donation_bridge_common.random_safe_point
    local storage=alife_storage_manager.get_state
    anomaly_donation_bridge_common.random_safe_point=function(...)
        local point,reason=original(...)
        alife_storage_manager.get_state=function() return nil end
        return point,reason
    end
    settle();assert(#moves==before and state[KEY],"Setter ran before consuming persistent marker")
    alife_storage_manager.get_state=storage
    anomaly_donation_bridge_common.random_safe_point=original
    tick(250);assert(#moves==before+1 and state[KEY]==nil)
    settle();assert(#moves==before+1)
end
-- A blocked arrival reports its exact guard once, then one terminal reason.
-- No navigation is probed while paused, and no per-frame log spam is added.
do
    reset();request();handler.on_level_changing();current="l02_garbage";handler.on_game_load()
    local before,probes=#moves,graph_calls
    render.paused=true
    for _=1,1100 do tick(100) end
    assert(state[KEY] and #logs==1 and logs[1]:find("waiting",1,true) and logs[1]:find("paused",1,true))
    assert(#moves==before and graph_calls==probes)
    tick(10000)
    assert(not state[KEY] and #logs==2 and logs[2]:find("timeout:paused",1,true))
    for _=1,100 do tick(100) end
    assert(#logs==2 and #moves==before and graph_calls==probes,"Expired arrival spammed or probed navigation")
    render.paused=false
end
do
    reset();request();fail_move="noop";local before=#moves
    arrive()
    assert(#moves==before+1 and state[KEY]==nil and #logs==1 and logs[1]:find("position_confirmation_mismatch",1,true))
    settle();assert(#moves==before+1 and #logs==1,"Uncertain native no-op was repeated")
end
reset();JumpToLevel=function() return false end
local context={};assert(handler.can_execute(context,{level_sections="l02_garbage"}))
assert(handler.execute(context)=="rejected" and state[KEY]==nil)
JumpToLevel=function() error("Travel failed") end
assert(handler.execute(context)=="rejected" and state[KEY]==nil)
-- The ordinary current-map template must accept the SAME native constructor.
-- Exercise its actual support, selection, setter and confirmation functions.
reset()
local ordinary=load_module(optional,"anomaly_donation_bridge_effect_teleport_random")
ordinary.on_game_start()
local local_handler=assert(anomaly_donation_bridge_registry.get("teleport_random"))
local local_context={actor=actor}
assert(local_handler.is_supported(local_context),"Current-map teleport rejects a native vector class")
assert(local_handler.can_execute(local_context,{minimum_meters="10",maximum_meters="2000"}))
local before=#moves
assert(local_handler.execute(local_context)=="executed" and #moves==before+1)
print("Random-level arrival passed ("..vector_profile.." vector constructor): no NPC-only accessible() on actor; callback-independent late marshal recovery; bounded diagnostics; 15 rendered ready frames/no fixed delay or loading graph probes, real actor vertex, whole-map sampler, save/F9/readiness race and at-most-once setter.")
