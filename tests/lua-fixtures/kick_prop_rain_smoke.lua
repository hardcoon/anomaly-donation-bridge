local core,optional=assert(arg[1]),assert(arg[3])
local checks=0
local function check(v,why) checks=checks+1;assert(v,why) end
local function copy(v)
    if type(v)~="table" then return v end
    local r={};for k,x in pairs(v) do r[k]=copy(x) end;return r
end
local function module(root,name)
    local env=setmetatable({},{__index=_G})
    local chunk=assert(loadfile(root.."/"..name..".script"));setfenv(chunk,env);chunk();_G[name]=env;return env
end
local callbacks,descriptors,saved,frame,now,paused,ui,living,present,precache,holder,talking,busy
local a,velocity,jump_speed,gravity,creates,presses,releases,blocked,logs,models,settings,excluded
local sounds,sound_plays,sound_failure
function vector() return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end} end
function device() return {frame=frame,precache_frame=precache,f_time_delta=1/60,is_paused=function() return paused end} end
function time_global() return now end
function app_ready() return present end
function Check_UI() return ui end
function bind_to_dik(action) assert(action==key_bindings.kJUMP);return 57 end
key_bindings={kJUMP=3}
function get_console() return {get_string=function(_,key) assert(key=="ph_gravity");return tostring(gravity) end} end
function printf(fmt,...) logs[#logs+1]=string.format(fmt,...) end
function RegisterScriptCallback(name,callback)
    callbacks[name]=callbacks[name] or {};table.insert(callbacks[name],callback)
end
local function callback(name,...)
    for _,f in ipairs(callbacks[name] or {}) do f(...) end
end
alife_storage_manager={get_state=function() return saved end}
anomaly_donation_bridge_common=module(core,"anomaly_donation_bridge_common")
local c=anomaly_donation_bridge_common
local ini={}
function ini:section_exist(id) return settings[id]~=nil end
function ini:line_exist(id,key) return settings[id] and settings[id][key]~=nil end
function ini:r_string(id,key) assert(self:line_exist(id,key));return tostring(settings[id][key]) end
function ini:r_bool(id,key) assert(self:line_exist(id,key));return settings[id][key]==true or settings[id][key]=="false" and false or settings[id][key]=="true" end
function ini:section_for_each(f) for id in pairs(settings) do check(f(id)==false,"Native section callback must return bool") end end
function system_ini() return ini end
game={translate_string=function(value) return value end}
function getFS() return {exist=function(_,alias,path)
    return alias=="$game_meshes$" and models[path]==true or alias=="$game_sounds$" and sounds[path]==true
end} end
-- Luabind constructors are callable tables, not necessarily Lua functions.
sound_object=setmetatable({s2d=2},{__call=function(_,name)
    check(sounds[name..".ogg"]==true,"Pain sound was not resolved through the game VFS")
    if sound_failure=="constructor" then error("Sound device unavailable") end
    return {play_no_feedback=function(_,actor,flags,delay,p,volume,frequency)
        check(actor==a and flags==sound_object.s2d and delay==0 and p.x==0 and p.y==0 and p.z==0 and
            volume==1 and frequency==1,"Native no-feedback pain playback arguments changed")
        if sound_failure=="playback" then error("Sound playback failed") end
        sound_plays[#sound_plays+1]=name
    end}
end})
function ini_file(path)
    return {line_exist=function(_,section,key)
        if path:find("exclusions",1,true) then return section=="excluded_sections" and excluded[key]==true end
        -- The stock debug spawner blacklists barrels as inventory items. The
        -- separate physical-prop kind must still discover the real object.
        return section=="ignore_sections" and key=="explosive_barrel"
    end}
end
anomaly_donation_bridge_transport={process_id=function() return 42 end}
anomaly_donation_bridge_protocol=module(core,"anomaly_donation_bridge_protocol")
anomaly_donation_bridge_sha256=module(core,"anomaly_donation_bridge_sha256")
anomaly_donation_bridge_props=module(core,"anomaly_donation_bridge_props")
local function prop(class,visual,extra)
    local r={class=class,visual=visual,["$spawn"]="physics\\props\\test",script_binding="bind_physic_object.init"}
    for k,v in pairs(extra or {}) do r[k]=v end;return r
end
local kick,rain,catalog
local world,next_id,hero_y,held,jump_event,allow_takeoff
local online_delay,shell_delay,falls,detonations,placements
local function make_body(record)
    local b=copy(record)
    b.created=b.created or frame;b.awake=b.awake or false;b.offline=b.offline~=false
    b.pos=b.pos or copy(b.target);b.vy=b.vy or 0
    local id=b.id
    b.server={id=id,name=function() return b.name end,section_name=function() return b.section end,
        use_ai_locations=function(_,flag) check(flag==false,"AI snap was not disabled");b.ai=false end,
        can_switch_offline=function() return b.offline end}
    b.client={id=function() return id end,name=function() return b.name end,section=function() return b.section end,
        parent=function() return nil end,position=function() return copy(b.pos) end,
        force_set_position=function(_,p,enabled)
            check(enabled==true,"Shell must be enabled after positioning")
            check(frame>=b.created+shell_delay,"Moved object before its physical shell existed")
            b.pos=copy(p);placements=placements+1
        end,
        explode=function(_,delay,...)
            check(select("#",...)==0,"Explosion received an explicit damage initiator")
            check(delay==0 and b.pos.y<b.target.y-.05,"Exploded at spawn instead of after a fall")
            check(b.vy>=0,"Exploded in free fall")
            check(not b.exploded,"Duplicate native explosion")
            b.exploded=true;detonations=detonations+1;world[id]=nil
        end,
        get_physics_shell=function()
            if frame<b.created+shell_delay then return end
            local valid_frame=frame
            local function fresh_shell() check(frame==valid_frame,"Cached native shell across updates") end
            return {get_elements_number=function() fresh_shell();return 1 end,
                apply_force=function(_,x,y,z)
                    fresh_shell();check(x==0 and y==-.001 and z==0,"Wake applied a damaging impulse");b.awake=true
                end,
                set_linear_vel=function(_,v) fresh_shell();b.vy=v.y end,
                get_linear_vel=function(_,v) fresh_shell();v:set(0,b.vy,0) end}
        end}
    world[id]=b;return b
end
local function fresh(native)
    saved,callbacks,descriptors,creates,logs={}, {}, {}, {}, {}
    sounds,sound_plays,sound_failure={["actor\\test_drop_a.ogg"]=true,["actor\\test_drop_b.ogg"]=true},{},nil
    frame,now,paused,ui,living,present,precache,holder,talking,busy=0,0,false,false,true,true,0,nil,false,false
    jump_speed,gravity,velocity,presses,releases,blocked=5,19.62,{x=2,y=0,z=-3},0,0,false
    world,next_id,hero_y,held,jump_event,allow_takeoff={},100,20,false,false,true
    online_delay,shell_delay,falls,detonations,placements=2,4,0,0,0
    models={["dynamics\\barrel.ogf"]=true,["dynamics\\box.ogf"]=true}
    local barrel={["$spawn"]='"weapons\\explosive\\barrel"',can_take=false,blast="5.4",script_binding=""}
    settings={
        explosive_barrel=prop("S_EXPLO","dynamics\\barrel.ogf",barrel),
        explosive_barrel_low=prop("S_EXPLO","dynamics\\barrel.ogf",barrel),
        fuel_can=prop("S_EXPLO","dynamics\\barrel.ogf",{["$spawn"]="weapons\\explosive\\fuelcan",can_take=false,blast="5"}),
        gas_cylinder=prop("S_EXPLO","dynamics\\barrel.ogf",{["$spawn"]="weapons\\explosive\\gaz_balon",can_take=false,blast="5"}),
        tank=prop("S_EXPLO","dynamics\\barrel.ogf",{["$spawn"]="weapons\\explosive\\mobiltank",can_take=false,blast="5"}),
        powder_can=prop("S_EXPLO","dynamics\\barrel.ogf",{["$spawn"]="weapons\\explosive\\metalcan_powder",can_take=false,blast="5"}),
        crate=prop("O_PHYSIC","dynamics\\box.ogf"),
        absent=prop("S_EXPLO","dynamics\\missing.ogf",barrel),
        fixed=prop("S_EXPLO","dynamics\\barrel.ogf",barrel),
        story=prop("S_EXPLO","dynamics\\barrel.ogf",barrel),
        inventory_explosive=prop("S_EXPLO","dynamics\\barrel.ogf",barrel),
        actor_test={hit_sounds="custom_actor_hits"},
        custom_actor_hits={strike=' "actor/test_drop_a.ogg",actor\\test_drop_b,actor\\missing_drop '}}
    settings.fixed.fixed_bones="link";settings.story.story_id="42";settings.inventory_explosive.can_take=true
    excluded={};catalog=module(core,"anomaly_donation_bridge_catalog");catalog.invalidate()
    check(catalog.rebuild(),"Catalogue did not build");_GUIs={}
    a={id=function() return 7 end,section=function() return "actor_test" end,
        alive=function() return living end,is_talking=function() return talking end,
        get_current_holder=function() return holder end,position=function() return {x=10,y=hero_y,z=30} end,
        level_vertex_id=function() return 70 end,game_vertex_id=function() return 8 end,
        get_actor_jump_speed=function() return jump_speed end,set_actor_jump_speed=function(_,value) jump_speed=value end,
        get_movement_speed=function() return copy(velocity) end,hit=function() error("Kick must never hit the actor") end}
    if native then a.set_movement_speed=function() error("Direct velocity does not initialize native jump state") end end
    db={actor=a,storage={}}
    level={name=function() return "l01_escape" end,present=function() return present end,
        press_action=function(dik) check(dik==57,"Unconfigured jump binding");presses=presses+1;held=true;jump_event=false end,
        release_action=function(dik) check(dik==57,"Wrong released action");releases=releases+1;held=false end,
        object_by_id=function(id) local b=world[id];return b and frame>=b.created+online_delay and b.client or nil end}
    function alife() return {
        object=function(_,id,no_assert) check(no_assert==true,"Missing lookup must not assert");return world[id] and world[id].server end,
        set_switch_online=function(_,id,value) check(value==true,"Disabled online spawn");world[id].forced=true end,
        set_switch_offline=function(_,id,value) world[id].offline=value end,
        create=function(_,section,p,lvid,gvid)
            check(not paused and not ui and living and present and precache==0,"Spawned outside playable scene")
            check(lvid==70 and gvid==8 and catalog.is_spawn_safe(section,"dynamic_prop"),"Unvalidated barrel creation")
            next_id=next_id+1
            local b=make_body({id=next_id,name=section..next_id,section=section,target=copy(p),pos={x=p.x,y=20,z=p.z},ai=true})
            creates[#creates+1]={section=section,position=copy(p),body=b}
            if blocked then error("Native creation failed after side effect") end
            return b.server
        end} end
    anomaly_donation_bridge_registry={register=function(id,desc) descriptors[id]=desc;return true end,
        busy_reason=function() return busy and "another_effect" or nil end}
    kick=module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_kick");kick.on_game_start()
    rain=module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_prop_rain");rain.on_game_start()
    check(descriptors["hardcoon.adb.kick"].is_supported(),"Jump API not detected")
    check(descriptors["hardcoon.adb.prop_rain"].is_supported(),"Rain API not detected")
end
local function physics()
    if paused or ui or not present or precache>0 then return end
    if held and not jump_event then
        jump_event=true;callback("actor_on_jump")
        -- The Lua callback can precede a refused PHActorCharacter::Jump.
        if allow_takeoff and math.abs(velocity.y)<.1 then velocity.y=jump_speed;hero_y=hero_y+.05 end
    end
    for _,b in pairs(world) do
        if b.awake and not b.exploded then
            b.vy=b.vy-gravity/60;b.pos.y=b.pos.y+b.vy/60
            if b.pos.y<20 then b.pos.y=20;b.vy=0 end
            falls=falls+1
        end
    end
end
local function tick(frames,ms)
    frame=frame+(frames or 1);now=now+(ms or 16)
    for _,desc in pairs(descriptors) do desc.update() end
    physics()
end
local function launch(id,params)
    local d=descriptors[id];local context={};local ok,status,why=d.can_execute(context,params or {})
    check(ok,tostring(status)..":"..tostring(why));check(d.execute(context)=="executed","Effect rejected activation")
end
local function reject(id,params) check(not descriptors[id].can_execute({},params),"Invalid parameters accepted") end
local function take_save()
    local snapshot={data=copy(saved),world={},next_id=next_id}
    for id,b in pairs(world) do
        snapshot.world[id]={id=id,name=b.name,section=b.section,target=copy(b.target),pos=copy(b.pos),
            awake=b.awake,offline=b.offline,vy=b.vy,created=frame-shell_delay}
    end
    return snapshot
end
local function load_save(snapshot)
    callback("on_before_load_input");saved=copy(snapshot.data);world={};next_id=snapshot.next_id
    for _,b in pairs(snapshot.world) do make_body(b) end
    for _,desc in pairs(descriptors) do desc.on_game_load() end
    tick();tick(15)
end
local function finish_rain(limit)
    for i=1,limit or 2000 do if not saved.adb_prop_rain_v1 then return end;tick() end
    error("Rain did not complete")
end
local function rain_params(extra)
    local r={prop_sections="explosive_barrel,explosive_barrel_low",catalog_revision=catalog.snapshot().revision}
    for k,v in pairs(extra or {}) do r[k]=v end;return r
end

fresh(true)
check(catalog.is_spawn_safe("explosive_barrel","dynamic_prop"),"Barrel missing from VFS scan")
check(catalog.is_spawn_safe("explosive_barrel_low","dynamic_prop"),"Second barrel variant missing")
for _,id in ipairs({"fuel_can","gas_cylinder","tank","powder_can"}) do
    check(catalog.is_spawn_safe(id,"dynamic_prop"),"Physical explosive missing from VFS catalogue: "..id)
end
for _,id in ipairs({"crate","absent","fixed","story","inventory_explosive"}) do
    check(not catalog.is_spawn_safe(id,"dynamic_prop"),"Non-explosive or unsafe section admitted: "..id)
end
models["dynamics\\barrel.ogf"]=nil
check(not catalog.validate_section({section_id="explosive_barrel",catalog_revision=catalog.snapshot().revision},"section_id","dynamic_prop"),"Removed VFS resource accepted")
for _,native in ipairs({true,false}) do
    fresh(native);launch("hardcoon.adb.kick",{})
    check(velocity.y==0 and presses==0 and #sound_plays==0,"Jump/sound started before ADB after")
    local after=take_save();tick(15)
    check(presses==1 and math.abs(velocity.y-math.sqrt(2*gravity*10))<.001,"Native jump did not consume 10m default")
    check(#sound_plays==0,"Pain sound played before physical takeoff was confirmed")
    check(jump_speed>5,"Speed restored before native jump consumed it")
    tick();check(jump_speed==5 and releases==1 and saved.adb_kick_v1==nil,"Jump did not restore speed/key after takeoff")
    check(#sound_plays==1 and sound_plays[1]:match("^actor\\test_drop_[ab]$"),"Custom native strike profile was not played once")
    hero_y=20;velocity.y=0;load_save(after);tick()
    check(presses==2 and saved.adb_kick_v1==nil and #sound_plays==2,"ADB after did not repeat jump and pain sound")
    callback("actor_on_jump");tick();check(#sound_plays==2,"Unrelated ordinary jump played kick sound")
    local done=take_save();hero_y=20;velocity.y=0;load_save(done);tick()
    check(presses==2 and #sound_plays==2,"Completed jump/sound replayed")
end
for _,height in ipairs({2,500}) do
    fresh(true);launch("hardcoon.adb.kick",{height_meters=tostring(height)});tick(15)
    check(math.abs(velocity.y-math.sqrt(2*gravity*height))<.001,"Jump height formula incorrect");tick()
end
fresh(true);allow_takeoff=false;launch("hardcoon.adb.kick",{});tick(15);tick()
check(saved.adb_kick_v1~=nil and jump_speed>5 and #sound_plays==0,"Lua callback falsely confirmed physical takeoff")
tick(60,1001);check(jump_speed==5 and saved.adb_kick_v1==nil and not held,"Refused attempt leaked speed/input")
allow_takeoff=true
for i=1,120 do tick(1,100) end
check(presses==1 and #sound_plays==0,"Refused attempt automatically repeated after loading/landing")
fresh(true);launch("hardcoon.adb.kick",{});tick(15)
local during_jump=take_save();load_save(during_jump)
for i=1,120 do velocity.y=0;hero_y=20;tick(1,100) end
check(presses==1 and saved.adb_kick_v1==nil and jump_speed==5 and not held,
    "Save during issued jump rearmed the same effect")
fresh(true);launch("hardcoon.adb.kick",{});tick(15);tick();velocity.y=0;hero_y=20
for i=1,120 do tick(1,100) end
check(presses==1 and #sound_plays==1 and saved.adb_kick_v1==nil,"Finished jump repeated after landing")
for _,interruption in ipairs({"inventory","main_menu","level_change"}) do
    fresh(true);launch("hardcoon.adb.kick",{});tick(15)
    if interruption=="inventory" then ui=true;tick();ui=false
    elseif interruption=="main_menu" then callback("main_menu_on_init")
    else descriptors["hardcoon.adb.kick"].on_level_changing() end
    velocity.y=0;hero_y=20
    for i=1,120 do tick(1,100) end
    check(presses==1 and saved.adb_kick_v1==nil and not held and jump_speed==5,
        "Interrupting an issued attempt rearmed its input: "..interruption)
end
fresh(true);launch("hardcoon.adb.kick",{});callbacks.actor_on_jump={};tick(15);tick()
check(presses==1 and #sound_plays==1 and saved.adb_kick_v1==nil and not held,"Missing callback retried a real physical jump")
fresh(true);saved.adb_kick_v1={version=1,height=10,previous=5,applied=20};jump_speed=20
descriptors["hardcoon.adb.kick"].on_game_load();tick(30)
check(presses==0 and jump_speed==5 and saved.adb_kick_v1==nil,"Legacy issued save restarted its input")
for _,failure in ipairs({"constructor","playback","missing_resource","missing_profile"}) do
    fresh(true);sound_failure=failure
    if failure=="missing_resource" then sounds={} end
    if failure=="missing_profile" then settings.actor_test.hit_sounds=nil end
    launch("hardcoon.adb.kick",{});tick(15);tick()
    check(saved.adb_kick_v1==nil and jump_speed==5 and releases==1 and #sound_plays==0,
        "Unavailable pain sound leaked input/speed or blocked completion: "..failure)
end
fresh(true);settings.actor_test.hit_sounds=nil;settings.actor={hit_sounds="custom_actor_hits"}
launch("hardcoon.adb.kick",{});tick(15);tick();check(#sound_plays==1,"Global actor hit-sound fallback failed")
fresh(true);launch("hardcoon.adb.kick",{});tick(15);jump_speed=42;tick();check(jump_speed==42,"Foreign speed overwritten")
fresh(true);launch("hardcoon.adb.kick",{});ui=true;tick(30);check(presses==0,"Jump ran inside inventory")
ui=false;tick();tick(15);tick();check(saved.adb_kick_v1==nil,"Jump did not resume after menu")
fresh(true);launch("hardcoon.adb.kick",{});velocity.y=-2;tick(15);check(presses==0,"Jump attempted in midair")
velocity.y=0;tick();tick();check(saved.adb_kick_v1==nil,"Jump did not resume after landing")
fresh(true)
for _,height in ipairs({"1","501","2.5","nan","inf","1e2"}) do reject("hardcoon.adb.kick",{height_meters=height}) end

fresh(true);launch("hardcoon.adb.prop_rain",rain_params({random_object="false"}))
check(#creates==0,"Rain spawned before ADB after")
local after=take_save();tick(15)
check(#creates==2 and placements==0 and detonations==0,"Spawn budget or deferred activation wrong")
check(creates[1].position.x==6 and creates[1].position.y==70 and creates[1].position.z==26,"50m height / 4m grid defaults wrong")
check(creates[1].section=="explosive_barrel" and creates[2].section=="explosive_barrel_low","Selection did not cycle")
local partial=take_save();finish_rain()
check(#creates==9 and placements==9 and detonations==9,"Sleeping/snapped barrels not positioned, woken and detonated")
for _,o in ipairs(creates) do check(o.body.pos.y==20 and o.body.exploded,"Barrel detonated before impact") end
creates={};detonations=0;load_save(after);finish_rain();check(#creates==9 and detonations==9,"ADB after did not replay rain")
creates={};detonations=0;load_save(partial);finish_rain()
check(#creates==7 and detonations==9,"Partial save duplicated barrels or lost existing tracking")
local done=take_save();creates={};load_save(done);tick(30);check(#creates==0,"Completed rain replayed")
fresh(true)
launch("hardcoon.adb.prop_rain",rain_params({prop_sections="gas_cylinder,fuel_can,tank,powder_can",count="4",random_object="false"}))
tick(15);finish_rain()
check(#creates==4 and placements==4 and detonations==4,"Restored explosive family did not fall/detonate")
for i,id in ipairs({"gas_cylinder","fuel_can","tank","powder_can"}) do
    check(creates[i].section==id and creates[i].body.exploded,"Selected explosive was skipped or replaced: "..id)
end
fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="3",detonate_on_impact="false"}));tick(15);finish_rain()
check(#creates==3 and detonations==0 and falls>0,"Unchecked detonation altered native behavior or stopped falling")
for _,b in pairs(world) do check(b.pos.y==20 and b.offline==true,"Online lease not restored on landing") end
fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="1",height_meters="500"}));tick(15);finish_rain()
check(detonations==1 and placements==1,"High-altitude barrel expired or never became physical")
for _,detonate in ipairs({"true","false"}) do
    fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="1",detonate_on_impact=detonate}));tick(15)
    for i=1,14 do tick() end
    check(saved.adb_prop_rain_v1.waiting[1].falling,"Falling phase was not saved")
    local airborne=take_save();creates={};placements=0;detonations=0
    for _,b in pairs(airborne.world) do b.awake=false;b.vy=0 end
    load_save(airborne)
    check(detonations==0,"Cold-loaded stationary body exploded in midair")
    finish_rain()
    check(#creates==0 and placements==0,"Reload respawned or lifted an already falling barrel")
    check(detonations==(detonate=="true" and 1 or 0),"Reload lost detonation checkbox or duplicated impact")
end
fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="100",detonate_on_impact="false"}))
local plan=copy(saved.adb_prop_rain_v1.slots);ui=true;tick(60);check(#creates==0,"Spawn occurred in inventory")
ui=false;tick();tick(15);finish_rain();check(#creates==100 and placements==100,"Large rain duplicated or skipped objects")
for i,o in ipairs(creates) do check(o.section==plan[i].section,"Saved random selection rerolled") end
fresh(true);shell_delay=12;launch("hardcoon.adb.prop_rain",rain_params({count="1"}));tick(15)
tick();check(placements==0,"Touched incomplete client physics");finish_rain();check(detonations==1,"Deferred body forgotten")
fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="1"}));tick(15)
local old=world[101];world[101]=nil
make_body({id=101,name="different-owner",section=old.section,target=old.target,pos=old.pos})
finish_rain();check(placements==0 and detonations==0,"Reused ID touched a foreign object")
fresh(true)
for _,extra in ipairs({{count="0"},{count="101"},{spacing_meters="0"},{spacing_meters="nan"},{height_meters="501"},
    {prop_sections="explosive_barrel,explosive_barrel"},{prop_sections="crate"},{prop_sections="inventory_explosive"},
    {prop_sections=""},{catalog_revision="stale"},{random_object="maybe"},{detonate_on_impact="maybe"}}) do
    reject("hardcoon.adb.prop_rain",rain_params(extra))
end
fresh(true);blocked=true;launch("hardcoon.adb.prop_rain",rain_params({count="2"}));tick(15);tick(15)
check(#creates==2,"Uncertain creation duplicated objects")
fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="2"}));saved.adb_prop_rain_v1.slots[1].position.y=0/0
descriptors["hardcoon.adb.prop_rain"].on_game_load();tick();tick(15);check(#creates==0,"Malformed saved plan executed")
print("PASS kick/explosive rain physics: native takeoff/pain sound, restored explosives, ground snap, sleeping/delayed shells, impact-only detonation, checkbox, saves, ownership and bounded spawning ("..checks.." checks).")
