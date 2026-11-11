-- Real direct-spawn adapter and protocol registry, bounded mock navigation.
local root,effects,optional=assert(arg[1]),assert(arg[2]),assert(arg[3])
local checks=0
local function check(ok,why)checks=checks+1;assert(ok,why)end
local function module(path,name)
    local env=setmetatable({},{__index=_G});local f=assert(loadfile(path.."/"..name..".script"))
    setfenv(f,env);f();_G[name]=env;return env
end
local now,frame=100,1
function time_global()return now end
function time_continual()return now end
function device()return {frame=frame}end
function vector()return {set=function(self,x,y,z)self.x=x;self.y=y;self.z=z;return self end}end
function printf()end
local callbacks={}
function RegisterScriptCallback(name,fn)
    callbacks[name]=callbacks[name] or {};table.insert(callbacks[name],fn)
end
local positions,links,created,origin={}, {}, {},{x=0,y=0,z=0}
local level_name,gvid="l01_escape",2
local position_calls,link_calls,coordinate_calls=0,0,0
local unsafe_coordinate_calls=0
local wrong_snap=false
level={name=function()return level_name end,present=function()return true end,
    vertex_in_direction=function(id,dir,radius)
        if positions[id]==nil then return 4294967295 end
        return radius==0 and id or 1 -- corridor ray stops at one metre
    end,
    vertex_position=function(id)
        position_calls=position_calls+1;return assert(positions[id],"out-of-range vertex_position")
    end,
    vertex_link=function(id,side)
        link_calls=link_calls+1;assert(side>=0 and side<=3)
        return (links[id] or {})[side+1] or 4294967295
    end,
    vertex_id=function(p)
        coordinate_calls=coordinate_calls+1
        local minx,maxx,minz,maxz=0,0,0,0
        for _,v in pairs(positions)do minx=math.min(minx,v.x);maxx=math.max(maxx,v.x);minz=math.min(minz,v.z);maxz=math.max(maxz,v.z)end
        if not (p.x>=minx and p.x<=maxx and p.z>=minz and p.z<=maxz) then
            unsafe_coordinate_calls=unsafe_coordinate_calls+1;error("unsafe native coordinate call")
        end
        if wrong_snap then return 0 end
        local best,score
        for id,v in pairs(positions)do
            local d=(p.x-v.x)^2+(p.z-v.z)^2
            if not score or d<score then best=id;score=d end
        end
        return best
    end}
local actor={position=function()return origin end,level_vertex_id=function()return 0 end,
    game_vertex_id=function()return gvid end}
db={actor=actor}
function alife()return {release=function()end}end
function alife_create(section,p,vertex,game_vertex)
    created[#created+1]={section=section,position={x=p.x,y=p.y,z=p.z},vertex=vertex,gvid=game_vertex}
    return {id=#created,set_character_name=function()end}
end
local common=module(root,"anomaly_donation_bridge_common")
local registry=module(root,"anomaly_donation_bridge_registry")
anomaly_donation_bridge_catalog={capable=function()return true end,is_spawn_safe=function()return true end,
    validate_revision=function()return true end,
    validate_section=function(p,k,kind)return {section_id=p[k],kind=kind}end,
    validate_sections=function(p,k,kind)return {{section_id="a",kind=kind},{section_id="b",kind=kind}}end}
local direct=module(root,"anomaly_donation_bridge_effects_direct_spawn")
for _,id in ipairs({"spawn_mutants","spawn_npcs"})do module(effects,"anomaly_donation_bridge_effect_"..id).on_game_start()end
for _,id in ipairs({"spawn_random_mutants","spawn_random_npcs"})do module(optional,"anomaly_donation_bridge_effect_"..id).on_game_start()end
local function reset(count)
    registry.on_game_load();positions={};links={};created={};origin={x=0,y=0,z=0};level_name="l01_escape";gvid=2;wrong_snap=false
    for i=0,(count or 16)-1 do positions[i]={x=i==0 and 0 or 1000+i,y=0,z=0}end
    positions[1]={x=1,y=0,z=0}
    positions[2]={x=97,y=0,z=0};positions[3]={x=98,y=0,z=0};positions[4]={x=99,y=0,z=0}
    positions[5]={x=0,y=0,z=30};positions[6]={x=50,y=0,z=30}
    positions[7]={x=-200,y=0,z=-200};positions[8]={x=200,y=0,z=200}
    links[0]={1,5};links[1]={0};links[5]={0,6};links[6]={5,2};links[2]={6,3};links[3]={2,4};links[4]={3}
    position_calls,link_calls,coordinate_calls=0,0,0
    unsafe_coordinate_calls=0
    math.randomseed(1234)
end
local function tick()
    now=now+16;frame=frame+1
    for _,fn in ipairs(callbacks.actor_on_update or {})do fn()end
    local after=position_calls
    registry.update(now)
    check(position_calls==after,"shared update must run at most once per frame across all four descriptors")
end
local serial=0
local function context(id,params,donation)
    serial=serial+1
    return {actor=actor,now=now,command={donation_id=donation,command_id="attempt"..serial,action_id=id,batch_index=1,params=params}}
end
local function run(id,method,params)
    params=params or {};params.section_id=params.section_id or "a";params.count=params.count or "3"
    params.radius_meters=params.radius_meters or "100";params.catalog_revision="rev"
    if method then params.spawn_point_method=method end
    local donation="donation"..(serial+1);local saves=0
    for attempt=1,700 do
        local ctx=context(id,params,donation)
        local status,reason=registry.dispatch(id,ctx,params,function()saves=saves+1;return true end)
        if status~="deferred" then return status,reason,ctx,saves end
        check(saves==0 and #created==0,"searching must not spawn or make effect saves")
        tick()
    end
    error("search did not terminate")
end

for _,method in ipairs({"boss_sample","graph","scan","coordinates"})do
    reset()
    local status,reason,ctx,saves=run("spawn_mutants",method)
    check(status=="executed" and #created==3 and saves==1,"method failed: "..method.." / "..tostring(reason))
    for _,v in ipairs(created)do
        local d=math.sqrt(v.position.x^2+v.position.z^2)
        check(d>=95 and d<=100,"new method spawned outside requested ring: "..method)
    end
    if method=="graph" then check(link_calls>0,"connected search must traverse real neighbour links")end
    if method=="coordinates" then check(coordinate_calls>0,"coordinate method must really snap candidates")end
    check(unsafe_coordinate_calls==0,"no method may call native vertex_id outside the AI bounds")
end
reset()
local status,reason=run("spawn_mutants","legacy")
check(status=="executed" and reason:find("reused_nearby_vertex",1,true),"old method must remain available for comparison")
check(created[1].position.x==1,"legacy fallback behavior changed")
reset()
status,reason=run("spawn_npcs",nil)
check(status=="executed" and reason:find("method_boss_sample",1,true),"absent setting must use boss sample default")

-- Preserve one candidate in the requested ring rather than discard all
-- progress because a group requests three objects.
reset();positions[3]={x=2,y=0,z=0};positions[4]={x=3,y=0,z=0}
status=run("spawn_mutants","scan")
check(status=="executed" and #created==3 and created[1].vertex==2 and created[2].vertex==2,
    "one suitable distant vertex must serve a group without a near-player fallback")
reset()
for id in pairs(positions)do positions[id]={x=id==0 and 0 or 1,y=0,z=0}end
status,reason=run("spawn_mutants","boss_sample")
check(status=="rejected" and #created==0,"boss method cannot fall back below ten metres")
reset()
positions[2]={x=50,y=0,z=0};positions[3]={x=60,y=0,z=0};positions[4]={x=70,y=0,z=0}
status,reason=run("spawn_mutants","boss_sample")
check(status=="executed" and reason:find("closest_available_vertex",1,true) and created[1].vertex==4,
    "boss fallback must choose tested point closest to the desired distance")
reset();wrong_snap=true
status=run("spawn_mutants","coordinates")
check(status=="rejected" and #created==0,"snapping to the actor must be rejected")
reset();origin={x=3000,y=0,z=0}
status=run("spawn_mutants","coordinates")
check(status=="rejected" and unsafe_coordinate_calls==0 and coordinate_calls==0,
    "an actor outside the AI rectangle must not expand safe native coordinate bounds")

-- Dense reachable corridor at the new 500 m maximum. Search stays bounded,
-- and the connected traversal prefers outward branches instead of flooding
-- the whole level before reaching the distant ring.
reset(4000)
for id=0,3999 do positions[id]={x=id*.25,y=0,z=0};links[id]={id-1,id+1}end
links[0]={1};links[3999]={3998}
status,reason=run("spawn_mutants","graph",{radius_meters="500"})
check(status=="executed" and created[1].position.x>=495 and created[1].position.x<=500,"500 m graph traversal failed")
reset(6000)
local params={section_id="a",count="3",radius_meters="100",spawn_point_method="scan",catalog_revision="rev"}
local ctx=context("spawn_mutants",params,"large")
check(registry.dispatch("spawn_mutants",ctx,params)=="deferred","large scan must defer")
position_calls=0;tick();check(position_calls<=1024,"a scan exceeded the per-frame vertex budget")

-- Replacement command IDs and random wrapper re-rolls must keep the same
-- request's chosen section/count while its navigation job is pending.
for _,id in ipairs({"spawn_random_mutants","spawn_random_npcs"})do
    reset(4000)
    local p={minimum_count="2",maximum_count="3",radius_meters="100",spawn_point_method="scan",catalog_revision="rev"}
    p[id=="spawn_random_mutants" and "mutant_sections" or "npc_sections"]="a,b"
    local first=context(id,p,id)
    local h=registry.get(id)
    local allowed,first_state,first_reason=h.can_execute(first,p)
    check(not allowed and first_state=="deferred","random search should be pending on first call: "..tostring(first_reason))
    local chosen,total=first.options.rows[1].section,first.options.total
    for attempt=1,700 do
        tick();local next_ctx=context(id,p,id)
        local ready,state=h.can_execute(next_ctx,p)
        if ready then
            check(next_ctx.options.rows[1].section==chosen and next_ctx.options.total==total and next_ctx.random_section==chosen,
                "deferred random request changed its section/count")
            check(h.execute(next_ctx)=="executed" and #created==total,"random wrapper dropped method or count")
            break
        end
        check(attempt<700 and state=="deferred","random request failed")
    end
end

reset()
params={section_id="a",count="3",radius_meters="100",spawn_point_method="graph",catalog_revision="rev"}
ctx=context("spawn_mutants",params,"reload")
check(registry.dispatch("spawn_mutants",ctx,params)=="deferred","prepare reload case")
tick();registry.on_game_load()
check(registry.dispatch("spawn_mutants",context("spawn_mutants",params,"reload"),params)=="deferred" and #created==0,
    "F9 must restart read-only search without restoring native pointers or spawning duplicates")
registry.on_level_changing();level_name="l02_garbage";gvid=3
check(registry.dispatch("spawn_mutants",context("spawn_mutants",params,"reload"),params)=="deferred","level transition must restart search")
registry.on_game_load()
reset()
params={section_id="a",count="3",radius_meters="100",spawn_point_method="graph",catalog_revision="rev"}
ctx=context("spawn_mutants",params,"moving")
local move_handler=registry.get("spawn_mutants")
local ready=move_handler.can_execute(ctx,params)
for i=1,100 do
    if ready then break end
    tick();ctx=context("spawn_mutants",params,"moving");ready=move_handler.can_execute(ctx,params)
end
check(ready,"moving-actor case did not finish searching")
origin={x=97,y=0,z=0}
local move_status,move_reason=move_handler.execute(ctx)
check(move_status=="deferred" and move_reason=="spawn_point_actor_moved" and #created==0,
    "actor movement between prepare and execute must not turn a distant point into a spawn at the feet")
reset()
local h=registry.get("spawn_mutants")
-- Reproduce a completed result waiting for the application's next poll:
-- moving three metres must revalidate vertices, not discard the whole job.
for _,method in ipairs({"boss_sample","graph","scan","coordinates"})do
    reset()
    local p={section_id="a",count="3",radius_meters="100",spawn_point_method=method,catalog_revision="rev"}
    local handler=registry.get("spawn_mutants")
    local ctx=context("spawn_mutants",p,"walk_ready_"..method)
    local ready=handler.can_execute(ctx,p)
    for attempt=1,100 do
        if ready then break end
        tick();ctx=context("spawn_mutants",p,"walk_ready_"..method);ready=handler.can_execute(ctx,p)
    end
    check(ready,"ready walking fixture did not finish: "..method)
    origin={x=3,y=0,z=0}
    local before=position_calls
    ctx=context("spawn_mutants",p,"walk_ready_"..method)
    check(handler.can_execute(ctx,p),"walking discarded a usable completed result: "..method)
    check(position_calls==before,"walking restarted native vertex scanning: "..method)
    for _,point in ipairs(ctx.spawn_points)do
        local d=math.sqrt((point.position.x-origin.x)^2+(point.position.z-origin.z)^2)
        check(d>=(method=="boss_sample" and 10 or 95) and d<=100,
            "completed walking result was not checked against the new actor position")
    end
end

-- Walking during a multi-frame graph search must preserve its frontier.
reset(7000)
for i=0,6999 do positions[i]={x=i*.1,y=0,z=0};links[i]={i-1,i+1}end
links[0]={1};links[6999]={6998}
local p={section_id="a",count="1",radius_meters="500",spawn_point_method="graph",catalog_revision="rev"}
local handler=registry.get("spawn_mutants")
local ready=false
for attempt=1,20 do
    local ctx=context("spawn_mutants",p,"walk_during_search")
    ready=handler.can_execute(ctx,p)
    if ready then
        check(ctx.spawn_points[1].distance>=495 and ctx.spawn_points[1].distance<=500,
            "walking graph search returned a stale distance")
        break
    end
    origin={x=attempt*3,y=0,z=0};tick()
end
check(ready,"walking repeatedly erased the graph search frontier")

-- Every new method terminates when the requested distance has no vertices.
for _,method in ipairs({"boss_sample","graph","scan","coordinates"})do
    reset()
    for id in pairs(positions)do positions[id]={x=id==0 and 0 or 1,y=0,z=0}end
    local status,reason=run("spawn_mutants",method)
    check(status=="rejected" and reason=="spawn_point_not_found_"..method and #created==0,
        "missing navigation points must terminate without spawning or retrying forever: "..method)
end
reset()
check(not h.can_execute({actor=actor},{spawn_point_method="invalid"}),"unknown method accepted")
check(not h.can_execute({actor=actor},{radius_meters="501"}),"radius above 500 accepted")
local old_link=level.vertex_link;level.vertex_link=nil
local ready,state,why=h.can_execute({actor=actor},{section_id="a",spawn_point_method="graph",catalog_revision="rev"})
check(not ready and state=="rejected" and why=="spawn_vertex_link_unsupported","missing method API must not silently change algorithm")
level.vertex_link=old_link
print("Spawn-point method checks passed: "..checks.." (mock API; not a native game run)")
