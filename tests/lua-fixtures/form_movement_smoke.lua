local chunk=assert(loadfile(assert(arg[1]).."/anomaly_donation_bridge_common.script"))
local common=setmetatable({},{__index=_G});setfenv(chunk,common);chunk()
local fields={"walk_accel","run_coef","runback_coef","sprint_koef","walk_strafe_coef"}
local writes=0
local function actor()
    local values={walk_accel=11.5,run_coef=2.41,runback_coef=1.33,sprint_koef=2.08,walk_strafe_coef=1}
    local a={values=values}
    for _,name in ipairs(fields) do
        local key=name
        a["get_actor_"..key]=function() return values[key] end
        a["set_actor_"..key]=function(_,value)
            values[key]=value;writes=writes+1
            if a.fail==key then a.fail=nil;error("setter failed after write") end
        end
    end
    return a
end
local function lease(a)
    local result={}
    local applied={walk_accel=57.6472,run_coef=1,runback_coef=0.55,sprint_koef=1.5,walk_strafe_coef=0.8}
    for _,key in ipairs(fields) do result[#result+1]={key=key,before=a.values[key],applied=applied[key]} end
    return result
end
local function near(a,b) assert(math.abs(a-b)<0.00001,tostring(a).." ~= "..tostring(b)) end
local function manager(a,runBase,sprintBase)
    local keys={};local calls=0;local m={}
    local function update()
        local run,sprint=1,1
        for _,entry in pairs(keys) do if entry.sprint then sprint=sprint*entry.factor else run=run*entry.factor end end
        a.values.run_coef=math.max(1,math.min(10,runBase*run))
        a.values.runback_coef=math.max(1,math.min(10,1.33*run))
        a.values.sprint_koef=math.max(1,math.min(10,sprintBase*sprint))
    end
    function m.add_speed(key,factor,sprint,force)
        if m.fail_add=="before" then error("provider before write") end
        if m.reject or keys[key] and not force then return false end
        keys[key]={factor=factor,sprint=sprint};update()
        if m.fail_add=="after" then error("provider after write") end
        return true
    end
    function m.remove_speed(key)
        calls=calls+1;if m.fail_remove then error("provider temporarily unavailable") end
        keys[key]=nil;update()
    end
    function m.inspect() return keys,calls end
    return m
end

-- No API, or an incomplete API, uses unchanged native semantics. Unknown
-- native writers win without accumulating acceleration or ending the form.
for _,api in ipairs({false,{add_speed=function() error("Incomplete provider invoked") end}}) do
    speed=api or nil
    local a=actor();local l=lease(a);common.form_movement_apply(a,l,"native")
    near(a.values.walk_accel,57.6472);near(a.values.run_coef,1)
    local count=writes;for i=1,20 do assert(common.form_movement_update(a,l)) end
    assert(writes==count,"Native speed forced every frame")
    a.values.run_coef=7;assert(common.form_movement_update(a,l))
    near(a.values.walk_accel,11.5);near(a.values.run_coef,7);near(a.values.sprint_koef,2.08)
    common.form_movement_release(a,l);near(a.values.run_coef,7)
end

-- Detect capabilities, not GAMMA names/constants. Preserve other providers'
-- modifiers and current injuries/zoom penalties, including changes mid-form.
for _,baseline in ipairs({{2.41,2.08},{3.2,1.75}}) do
    local a=actor();a.values.run_coef=baseline[1];a.values.sprint_koef=baseline[2]
    speed=manager(a,baseline[1],baseline[2]);local l=lease(a)
    common.form_movement_apply(a,l,"dynamic-provider")
    assert(l.manager);near(a.values.run_coef,1);near(a.values.sprint_koef,1.5)
    assert(not pcall(common.form_movement_apply,a,l,"dynamic-provider"),"Duplicate activation stacked modifiers")
    local keys=speed.inspect();local owned=0;for key in pairs(keys) do assert(key:match("^adb:"));owned=owned+1 end
    assert(owned==2)
    speed.add_speed("foreign-injury",0.75,false,true);speed.add_speed("foreign-zoom",0.8,true,true)
    assert(common.form_movement_update(a,l) and not l.released)
    near(a.values.walk_accel,57.6472)
    common.form_movement_release(a,l)
    near(a.values.walk_accel,11.5);near(a.values.run_coef,baseline[1]*0.75);near(a.values.sprint_koef,baseline[2]*0.8)
    keys,owned=speed.inspect();assert(keys["foreign-injury"] and keys["foreign-zoom"])
    for key in pairs(keys) do assert(not key:match("^adb:"),"Owned modifier leaked") end
    common.form_movement_release(a,l);local _,after=speed.inspect();assert(after==owned,"Non-idempotent release")
end

-- Partial setup failures roll back even providers/setters that throw AFTER
-- writing. A rejected existing key is never removed by our cleanup.
for _,failure in ipairs({"before","after","native","reject"}) do
    local a=actor();speed=manager(a,2.41,2.08);local l=lease(a)
    if failure=="native" then a.fail="walk_accel"
    elseif failure=="reject" then speed.reject=true else speed.fail_add=failure end
    assert(not pcall(common.form_movement_apply,a,l,"failed"))
    common.form_movement_release(a,l);assert(next((speed.inspect()))==nil)
    near(a.values.walk_accel,11.5);near(a.values.run_coef,2.41)
    if failure=="reject" then local _,count=speed.inspect();assert(count==0) end
end

local a=actor();speed=manager(a,2.41,2.08);local l=lease(a)
common.form_movement_apply(a,l,"retry");speed.fail_remove=true;common.form_movement_release(a,l)
assert(next((speed.inspect()))~=nil)
assert(not pcall(common.form_movement_apply,a,lease(a),"retry-new"),"New lease stacked over failed cleanup")
speed.fail_remove=false;common.form_movement_recover(a)
assert(next((speed.inspect()))==nil);near(a.values.run_coef,2.41)

-- A world load only queues plain Lua ownership data. Defer all API calls
-- until the new actor is safe; do not restore old native field snapshots.
a=actor();speed=manager(a,2.41,2.08);l=lease(a);common.form_movement_apply(a,l,"load")
local _,before=speed.inspect();common.form_movement_defer(l);common.form_movement_recover(nil)
local _,after=speed.inspect();assert(after==before,"Stale-world cleanup touched native APIs")
common.form_movement_recover(a);assert(next((speed.inspect()))==nil)
near(a.values.walk_accel,57.6472) -- loaded journal, not a future runtime lease, restores native fields

a=actor();speed=manager(a,2.41,2.08);l=lease(a);common.form_movement_apply(a,l,"replacement")
local old=speed;local replacement=manager(a,2.41,2.08);speed=replacement
speed.add_speed("replacement-owner",0.7,false,true)
assert(common.form_movement_update(a,l));local keys=speed.inspect();assert(keys["replacement-owner"])
local _,oldCalls=old.inspect();assert(oldCalls==0,"Obsolete provider called against another manager")
speed=nil
print("Form movement smoke passed: capability detection/native fallback, neutral and changing foreign modifiers, no per-frame forcing or stacking, partial setup rollback, ownership-aware cleanup, deferred load cleanup and provider replacement.")
