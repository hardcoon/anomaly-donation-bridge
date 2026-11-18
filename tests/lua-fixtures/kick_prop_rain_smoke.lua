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
function getFS() return {exist=function(_,alias,path) return alias=="$game_meshes$" and models[path]==true end} end
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
local function fresh(native)
    saved,callbacks,descriptors,creates,logs={}, {}, {}, {}, {}
    frame,now,paused,ui,living,present,precache,holder,talking,busy=0,0,false,false,true,true,0,nil,false,false
    jump_speed,gravity,velocity,presses,releases,blocked=5,19.62,{x=2,y=0,z=-3},0,0,false
    models={["dynamics\\barrel.ogf"]=true,["dynamics\\box.ogf"]=true}
    settings={
        explosive_barrel=prop("S_EXPLO","dynamics\\barrel.ogf",{["$spawn"]='"weapons\\explosive\\barrel"',can_take=false,blast="5.4",script_binding=""}),
        crate=prop("O_PHYSIC","dynamics\\box.ogf"),
        mod_crate=prop("O_PHYS_S","dynamics\\box.ogf"),
        absent=prop("P_DSTRBL","dynamics\\missing.ogf"),
        fixed=prop("O_PHYSIC","dynamics\\box.ogf",{fixed_bones="link"}),
        scripted=prop("O_PHYSIC","dynamics\\box.ogf",{script_binding="mod.quest.bind"}),
        story=prop("O_PHYSIC","dynamics\\box.ogf",{story_id="42"}),
        quest=prop("O_PHYSIC","dynamics\\box.ogf",{quest_item=true}),
        unsafe_path=prop("O_PHYSIC","..\\outside.ogf"),
        inventory_explosive=prop("S_EXPLO","dynamics\\barrel.ogf",{can_take=true,blast="5"}),
        physical_base={class="O_PHYSIC",["$spawn"]="physics\\object"}
    }
    excluded={};_G.AnomalyDonationBridgeCatalogV1=nil
    catalog=module(core,"anomaly_donation_bridge_catalog");catalog.invalidate();check(catalog.rebuild(),"Catalogue did not build")
    _GUIs={}
    a={id=function() return 7 end,alive=function() return living end,is_talking=function() return talking end,
        get_current_holder=function() return holder end,position=function() return {x=10,y=20,z=30} end,
        level_vertex_id=function() return 70 end,game_vertex_id=function() return 8 end,
        get_actor_jump_speed=function() return jump_speed end,set_actor_jump_speed=function(_,value) jump_speed=value end,
        get_movement_speed=function() return copy(velocity) end}
    if native then a.set_movement_speed=function(_,value)
        check(not paused and not ui and living and present and precache==0,"Velocity set before playable scene")
        velocity=copy(value)
    end end
    db={actor=a,storage={}}
    level={name=function() return "l01_escape" end,present=function() return present end,
        press_action=function(dik) check(dik==57,"Unconfigured jump binding");presses=presses+1 end,
        release_action=function(dik) check(dik==57,"Wrong released action");releases=releases+1 end}
    function alife() return {create=function(_,section,p,lvid,gvid)
        check(not paused and not ui and living and present and precache==0,"Spawned outside playable scene")
        check(lvid==70 and gvid==8 and catalog.is_spawn_safe(section,"dynamic_prop"),"Unvalidated physical creation")
        creates[#creates+1]={section=section,position=copy(p)}
        if blocked then error("Native creation failed after side effect") end
        return {id=#creates}
    end} end
    anomaly_donation_bridge_registry={register=function(id,desc) descriptors[id]=desc;return true end,
        busy_reason=function() return busy and "another_effect" or nil end}
    kick=module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_kick");kick.on_game_start()
    rain=module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_prop_rain");rain.on_game_start()
    check(descriptors["hardcoon.adb.kick"].is_supported(),"Kick API not detected")
    check(descriptors["hardcoon.adb.prop_rain"].is_supported(),"Rain API not detected")
end
local function tick(frames,ms)
    frame=frame+(frames or 1);now=now+(ms or 16)
    for _,desc in pairs(descriptors) do desc.update() end
end
local function launch(id,params)
    local d=descriptors[id];local context={};local ok,status,why=d.can_execute(context,params or {})
    check(ok,tostring(status)..":"..tostring(why));check(d.execute(context)=="executed","Effect rejected activation")
end
local function reject(id,params)
    check(not descriptors[id].can_execute({},params),"Invalid parameters accepted")
end
local function load_save(snapshot)
    callback("on_before_load_input");saved=copy(snapshot)
    for _,desc in pairs(descriptors) do desc.on_game_load() end
    tick();tick(15)
end
local function rain_params(extra)
    local r={prop_sections="explosive_barrel,crate",catalog_revision=catalog.snapshot().revision}
    for k,v in pairs(extra or {}) do r[k]=v end;return r
end

fresh(true)
local barrel=anomaly_donation_bridge_props.classify(ini,"explosive_barrel")
check(catalog.is_spawn_safe("explosive_barrel","dynamic_prop"),"Barrel absent despite VFS resource: "..tostring(barrel and barrel.quarantine_reason))
check(catalog.is_spawn_safe("crate","dynamic_prop") and catalog.is_spawn_safe("mod_crate","dynamic_prop"),"Movable props missing")
for _,id in ipairs({"absent","fixed","scripted","story","quest","unsafe_path","inventory_explosive","physical_base"}) do
    check(not catalog.is_spawn_safe(id,"dynamic_prop"),"Unsafe prop admitted: "..id)
end
models["dynamics\\box.ogf"]=nil
check(not catalog.validate_section({section_id="crate",catalog_revision=catalog.snapshot().revision},"section_id","dynamic_prop"),"Removed VFS resource accepted")

-- ADB after captures the pending request before the physical launch.
fresh(true);launch("hardcoon.adb.kick",{})
check(velocity.y==0 and presses==0,"Kick was applied before ADB after could save it")
local after=copy(saved);tick(15)
check(math.abs(velocity.y-math.sqrt(2*19.62*5))<.001 and velocity.x==2 and velocity.z==-3,"Ballistic kick or horizontal velocity wrong")
check(jump_speed==5 and saved.adb_kick_v1==nil,"Native kick changed permanent jump settings")
velocity.y=-10;load_save(after)
check(velocity.y>0,"ADB after did not reapply kick")
local completed=copy(saved);velocity.y=0;load_save(completed);check(velocity.y==0,"Completed kick replayed from later save")
for _,value in ipairs({"2","500"}) do launch("hardcoon.adb.kick",{height_meters=value});tick(15);check(velocity.y>0,"Valid height rejected") end
for _,value in ipairs({"1","501","2.5","nan","inf","1e2"}) do reject("hardcoon.adb.kick",{height_meters=value}) end
launch("hardcoon.adb.kick",{});ui=true;tick(30);check(saved.adb_kick_v1~=nil,"Menu lost kick")
ui=false;tick();tick(15);check(saved.adb_kick_v1==nil,"Kick never resumed")

-- Stock fallback releases its key and changes only its owned jump speed.
fresh(false);launch("hardcoon.adb.kick",{});tick(15)
check(presses==1 and jump_speed>5,"Legacy automatic jump not armed")
callback("actor_on_jump");check(jump_speed>5,"Speed restored before native physics consumed it")
tick();check(releases==1 and jump_speed==5 and saved.adb_kick_v1==nil,"Legacy jump leaked key/speed")
launch("hardcoon.adb.kick",{});tick(15);callback("main_menu_on_init")
check(jump_speed==5 and saved.adb_kick_v1~=nil,"Interrupted legacy launch did not restore")
ui=false;tick();tick(15);jump_speed=42;callback("actor_on_jump");tick()
check(jump_speed==42,"Foreign jump setting overwritten during cleanup")
fresh(false);launch("hardcoon.adb.kick",{});velocity.y=-2;tick(15)
check(presses==0,"Stock tried to consume kick in midair")
velocity.y=0;tick();tick(40,501);check(jump_speed==5 and releases==1 and saved.adb_kick_v1~=nil,"Blocked input leaked boosted jump")

fresh(true);launch("hardcoon.adb.prop_rain",rain_params({random_object="false"}))
check(#creates==0,"Props spawned before ADB after")
local rain_after=copy(saved);tick(15)
check(#creates==2,"Spawn work not bounded to two per frame")
check(creates[1].section=="explosive_barrel" and creates[2].section=="crate","Ordered selection not cycled")
check(creates[1].position.x==7 and creates[1].position.y==35 and creates[1].position.z==27 and creates[2].position.x==10,"Grid origin, spacing or height wrong")
local partial=copy(saved);tick(0);check(#creates==2,"Same-frame update duplicated slots")
for i=1,4 do tick() end
check(#creates==9 and saved.adb_prop_rain_v1==nil,"Incorrect total prop count")
creates={};load_save(partial);for i=1,4 do tick() end
check(#creates==7,"Later save did not resume only remaining slots")
creates={};load_save(rain_after);for i=1,4 do tick() end
check(#creates==9,"ADB after did not replay entire rain")
local done=copy(saved);creates={};load_save(done);tick(40);check(#creates==0,"Completed rain replayed from later save")

fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="100"}))
local plan=copy(saved.adb_prop_rain_v1.slots);ui=true;tick(60);check(#creates==0,"Rain ran in inventory/menu")
ui=false;tick();tick(15)
for i=1,49 do tick() end
check(#creates==100,"Maximum count incorrect")
for i,o in ipairs(creates) do check(o.section==plan[i].section,"Random plan rerolled") end
fresh(true)
for _,extra in ipairs({{count="0"},{count="101"},{spacing_meters="0"},{spacing_meters="nan"},{height_meters="501"},
    {prop_sections="crate,crate"},{prop_sections=""},{prop_sections="story"},{catalog_revision="stale"},{random_object="maybe"}}) do
    reject("hardcoon.adb.prop_rain",rain_params(extra))
end
launch("hardcoon.adb.prop_rain",rain_params({count="3"}));excluded.crate=true;catalog.refresh_command_exclusions();tick(15);tick()
check(#creates<=3 and saved.adb_prop_rain_v1==nil,"Exclusion changed in pending plan stalled the effect")
fresh(true);blocked=true;launch("hardcoon.adb.prop_rain",rain_params({count="2"}));tick(15);tick(15)
check(#creates==2,"Uncertain native spawn duplicated an object")
fresh(true);launch("hardcoon.adb.prop_rain",rain_params({count="2"}));saved.adb_prop_rain_v1.slots[1].position.y=0/0
descriptors["hardcoon.adb.prop_rain"].on_game_load();tick();tick(15)
check(#creates==0,"Malformed saved plan spawned objects")
print("PASS kick/prop rain: live VFS catalogue, native and stock jump, exact ballistic speed, ADB after replay, partial save resume, bounded spawning, exclusions and invalid data ("..checks.." checks).")
