-- Execute all six actual damage functions with both unmodified native hit
-- semantics and a weapon-aware callback. Never compensate immunities/HP.
local root=assert(arg[3])
local names={"burer","pseudogiant","tushkano","poltergeist","bloodsucker","controller"}
local function read(path)
    local f=assert(io.open(path,"rb"));local text=f:read("*a");f:close();return text
end
local actor={id=function() return 0 end}
local types={strike=1,wound=2,burn=3}
local trials=0
local common=setmetatable({},{__index=_G})
local common_chunk=assert(loadfile(assert(arg[1]).."/anomaly_donation_bridge_common.script"))
setfenv(common_chunk,common);common_chunk()
for _,name in ipairs(names) do
    local source=read(root.."/anomaly_donation_bridge_effect_hardcoon_adb_"..name.."_form.script")
    local body=assert(source:match("(local function damage%b().-)\nlocal function"),name..": damage helper missing")
    assert(body:find("h.weapon_id=65535",1,true),name..": scripted hit lacks invalid weapon ID")
    for _,target_kind in ipairs({"npc","mutant"}) do
        for _,protection in ipairs({0,0.01,0.25,1}) do
            for _,factor in ipairs({1,2,3}) do
                for _,mode in ipairs({"native","weapon-aware"}) do
                    local form={attack_factor=factor,hit_checks={},attack_serial=3}
                    local target={health=1}
                    function target:id() return 42 end
                    function target:section() return target_kind.."_section" end
                    function target:alive() return self.health>0 end
                    function target:list_bones(hud) assert(hud==false);return {[7]="collision_bone"} end
                    local received
                    function target:hit(h)
                        assert(h.draftsman==actor and h.weapon_id==65535)
                        assert(h.bone=="collision_bone" and h.impulse==15)
                        assert(h.direction.x==0 and h.direction.y==0 and h.direction.z==1)
                        assert(h.type==((name=="bloodsucker" or name=="controller") and types.wound or types.strike))
                        assert(math.abs(h.power-0.1*factor)<1e-10,"Attack strength applied more than once")
                        if mode=="weapon-aware" then
                            local function object_by_id(id) if id==0 then return actor end end
                            assert(object_by_id(h.weapon_id)==nil,"Non-weapon entered weapon balance")
                        end
                        received=h
                        self.health=math.max(0,self.health-h.power*protection)
                    end
                    local function point(p) return {x=p.x,y=p.y,z=p.z} end
                    local env=setmetatable({form=form,actor=function() return actor end,
                        anomaly_donation_bridge_common=common,
                        creature=function(o) return o==target end,vec=point,point=point,
                        time_global=function() return 1000 end,
                        finite=function(n) return type(n)=="number" and n==n and math.abs(n)<1e8 end,
                        notice=function() error("Unexpected notice") end,
                        hit=setmetatable(types,{__call=function() return {weapon_id=0} end}),
                    },{__index=_G})
                    local chunk=assert(loadstring(body.."\nreturn damage",name.."_actual_damage"));setfenv(chunk,env)
                    local damage=chunk()
                    assert(damage({object=target,element=7},{x=0,y=0,z=1},0.1,15,"melee"))
                    assert(received and #form.hit_checks==1,"Hit duplicated or feedback lost")
                    assert(math.abs(target.health-(1-0.1*factor*protection))<1e-10,"Native target protection bypassed")
                    if name=="bloodsucker" then
                        assert(form.hit_checks[1].heal and form.hit_checks[1].serial==3,"Bloodsucker heal tracking changed")
                        form.cloaked=true;received=nil
                        assert(not damage({object=target,element=7},{x=0,y=0,z=1},0.1,15,"melee") and not received)
                    end
                    trials=trials+1
                end
            end
        end
    end
end
-- Diagnostics never change an SHit or target HP, are silent without explicit
-- activation, and are bounded even if a caller requests an excessive budget.
local lines={}
common.printf=function(...) lines[#lines+1]={...} end
local check={id=42,section="npc_section",before=1,submitted=0.3,hit_type=1,impulse=15}
local s={actor_id=0,hit_checks={check}}
local target={id=function() return 42 end,section=function() return "npc_section" end,health=1}
local h={draftsman=actor,weapon_id=65535,power=0.003,type=1,impulse=15}
common.form_hit_calculated(s,h,target);common.form_hit_report("test",check,0.997)
assert(#lines==0 and check.calculated_at_adb==nil,"Diagnostics enabled by default")
adb_form_hit_trace=3
common.form_hit_calculated(s,h,target)
assert(check.calculated_at_adb==0.003 and h.power==0.003 and target.health==1)
for i=1,10 do common.form_hit_report("test",check,0.997) end
assert(#lines==3 and adb_form_hit_trace==0)
adb_form_hit_trace=1000
for i=1,30 do common.form_hit_report("test",check,0.997) end
assert(#lines==23 and adb_form_hit_trace==0,"Unbounded diagnostic spam")
adb_form_hit_trace=nil
-- Run all six actual synchronizers and hit callbacks in native order:
-- before-hit -> armor wear -> calculated hit -> separate form HP pool.
local clock,frame,paused,precache,ready,present=1000,10,false,0,true,true
common.time_global=function() return clock end
common.device=function() return {frame=frame,precache_frame=precache,is_paused=function() return paused end} end
common.app_ready=function() return ready end
common.level={present=function() return present end}
local guard_logs={};common.printf=function(...) guard_logs[#guard_logs+1]={...} end
for _,name in ipairs(names) do
    local source=read(root.."/anomaly_donation_bridge_effect_hardcoon_adb_"..name.."_form.script")
    local body=assert(source:match("(local function sync_actor_health%b().-)\nlocal function"),name..": health synchronizer missing")
    local state={actor_id=0,actor_visual='actor',actor_health=0.8,form_health=3,remaining=60000,last=clock}
    local values,writes,alive={bleeding=0.6,radiation=0.8,psy_health=0.2,power=0.15},{},true
    local failed_setter,psy_value,psy_writes=nil,0.3,0
    arszi_psy={get_psy_health=function() return psy_value end,set_psy_health=function(v) psy_value=v;psy_writes=psy_writes+1 end}
    local equipment={}
    local function gear(id,condition)
        return setmetatable({uid=id,identity='item'..id,sec='gear',value=condition,
            id=function(self)return self.uid end,name=function(self)return self.identity end,
            section=function(self)return self.sec end,condition=function(self)return self.value end,
            set_condition=function(self,v)self.value=v end},
            {__eq=function()error('native game_object has no equality operator')end})
    end
    local rider=setmetatable({health=0.6,alive=function() return alive end},{
        __index=function(_,key) return values[key] end,
        __newindex=function(o,key,value)
            if values[key]~=nil then
                writes[key]=(writes[key] or 0)+1
                if key==failed_setter then error('unavailable setter '..key) end
                if key=='bleeding' then assert(value==1,'ChangeBleeding needs 1, not 0');values[key]=0
                else values[key]=value end
            else rawset(o,key,value) end
        end,
    })
    function rider:id()return 0 end
    function rider:get_visual_name()return 'actor' end
    function rider:item_in_slot(slot)return equipment[slot] end
    local env=setmetatable({form=state,
        actor=function()return rider end,time_global=common.time_global,
        anomaly_donation_bridge_common=common,hit={radiation=9},hurt=function()end,
        creature=function()return false end,control_hit=function()end,
        health_actor=function() return rider end,
        finite=function(n) return type(n)=="number" and n==n and math.abs(n)<1e8 end,
        lose_form_health=function(s,power) s.form_health=s.form_health-power;return power end,
        restore_actor_health=function(s) rider.health=s.actor_health;return true end,
    },{__index=_G})
    local chunk=assert(loadstring(body.."\nreturn sync_actor_health",name.."_bleeding_guard"));setfenv(chunk,env)
    local sync=chunk()
    assert(sync(state) and values.bleeding==0 and values.radiation==0 and values.psy_health==1 and psy_value==1)
    assert(values.power==1 and writes.power==1,name..': stamina not restored on entry')
    assert(math.abs(state.form_health-2.8)<1e-8,"Bleeding removal healed or masked actual form damage")
    for i=1,10 do sync(state) end
    assert(writes.power==1,name..': stamina written repeatedly in one frame')
    frame=frame+1;values.power=0.3;sync(state)
    assert(values.power==1 and writes.power==2,name..': stamina waited for the one-second condition timer')
    assert(writes.bleeding==1 and psy_writes==1 and math.abs(state.form_health-2.8)<1e-8,'restoration ran every frame or healed form HP')
    clock=clock+999;values.bleeding=0.7;sync(state);assert(values.bleeding==0.7)
    clock=clock+1;sync(state);assert(values.bleeding==0 and writes.bleeding==2 and psy_writes==2)
    clock=clock+1000;failed_setter='radiation';values.bleeding=0.5;values.psy_health=0.4
    sync(state);assert(values.bleeding==0 and values.psy_health==1 and psy_writes==3,'one optional setter failure blocked other restoration')
    for i=1,5 do sync(state) end;assert(writes.radiation==3,'failing setter retried each frame')
    failed_setter=nil
    local last_writes=writes.bleeding
    local last_power_writes=writes.power
    frame=frame+1;values.power=0.35
    clock=clock+1000;values.bleeding=0.7;env.form=nil;env.suspended=state;sync(state)
    assert(values.bleeding==0.7 and writes.bleeding==last_writes,'guard leaked into suspended/ended/load state')
    env.form={};sync(state);assert(values.bleeding==0.7 and writes.bleeding==last_writes,'previous form still owns protection')
    assert(values.power==0.35 and writes.power==last_power_writes,name..': stamina protection leaked after exit')
    env.form=state;alive=false;sync(state);assert(writes.bleeding==last_writes,'guard changed dead actor')
    alive=true
    for _,condition in ipairs({'pause','precache','not-ready','no-level','expired'}) do
        paused=condition=='pause';precache=condition=='precache' and 2 or 0;ready=condition~='not-ready';present=condition~='no-level'
        state.remaining=condition=='expired' and 0 or 60000
        sync(state);assert(writes.bleeding==last_writes,'restored conditions during '..condition)
        assert(values.power==0.35 and writes.power==last_power_writes,name..': stamina changed during '..condition)
    end
    paused=false;precache=0;ready=true;present=true;state.remaining=60000;env.suspended=nil
    sync(state);assert(writes.bleeding==last_writes+1,'restoration did not resume')
    assert(values.power==1 and writes.power==last_power_writes+1,name..': stamina did not resume')
    frame=frame+1;failed_setter='power';clock=clock+1000;values.bleeding=0.6;sync(state)
    assert(values.bleeding==0,name..': missing stamina API blocked the rest of the form')
    failed_setter=nil
    -- Each form's real registration must capture the pre-hit equipment.
    local before_source
    if name=='burer' then
        before_source=assert(source:match('(local function incoming_hit%b().-)\nlocal function'))..'\nreturn incoming_hit'
    else
        local args,contents=source:match('RegisterScriptCallback%("actor_on_before_hit",function%(([^)]*)%)%s*(.-)%s*end%)')
        before_source='return function('..assert(args,name..' raw hit hook missing')..') '..contents..' end'
    end
    local raw=assert(loadstring(before_source));setfenv(raw,env);raw=raw()
    local after_source=assert(source:match('(local function calculated_hit%b().-)\nlocal function'))..'\nreturn calculated_hit'
    local calculated=assert(loadstring(after_source));setfenv(calculated,env);calculated=calculated()
    local outfit,helmet=gear(7,0.67),gear(12,0.32);equipment[7]=outfit;equipment[12]=helmet
    local h={power=0.2,type=1};local flags={ret_value=true}
    raw(h,4,flags);assert(flags.ret_value and h.power==0.2,'equipment guard changed incoming damage/flags')
    outfit.value=0.17;helmet.value=0.02
    local hp=state.form_health;calculated(h,rider,4)
    assert(outfit.value==0.67 and helmet.value==0.32,'native armor wear survived '..name)
    assert(h.power==0 and math.abs(state.form_health-(hp-0.2))<1e-8,'equipment protection changed form damage')
    -- A second hit must preserve current wear, not repair to initial/100%.
    outfit.value=0.51;helmet.value=0.22;h={power=0.05,type=1};raw(h,4,flags)
    outfit.value=0;helmet.value=0;calculated(h,rider,4)
    assert(outfit.value==0.51 and helmet.value==0.22,'hit restored old condition instead of latest condition')
    -- Further hits in the last frame are protected until form exit too.
    state.depleted=true;h={power=0,type=1};raw(h,4,flags);outfit.value=0.01;calculated(h,rider,4)
    assert(outfit.value==0.51,'protection ended before the depleted form actually exited');state.depleted=nil
    h={power=0,type=1};raw(h,4,flags);outfit.value=0.9;calculated(h,rider,4)
    assert(outfit.value==0.9,'restoring wear downgraded a separately repaired item')
    -- Removing/swapping equipment cannot write through a stale inventory handle.
    h={power=0.01,type=1};raw(h,4,flags);local replacement=gear(7,0.1);replacement.identity='different item'
    equipment[7]=replacement;equipment[12]=nil;calculated(h,rider,4)
    assert(replacement.value==0.1 and helmet.value==0.22,'replacement or removed item was repaired')
    equipment[7]=outfit;equipment[12]=helmet
    h={power=0.01,type=1};raw(h,4,flags);outfit.value=0.11;frame=frame+1;calculated(h,rider,4)
    assert(outfit.value==0.11,'stale snapshot from another frame applied')
    h={power=0.01,type=1};raw(h,4,{ret_value=false});outfit.value=0.09;calculated(h,rider,4)
    assert(outfit.value==0.09,'foreign veto was ignored')
    h={power=0.01,type=1};raw(h,4,flags);outfit.value=0.08
    local foreign={id=function()return 44 end,get_visual_name=function()return 'actor'end}
    calculated(h,foreign,4);assert(outfit.value==0.08 and h.power==0.01,'foreign target consumed equipment snapshot')
    calculated(h,rider,4);assert(outfit.value==0.09)
    -- Suspension/exit immediately stop BOTH new guards; no delayed repair.
    env.form=nil;env.suspended=nil;h={power=0.2,type=1};raw(h,4,flags);outfit.value=0.03;calculated(h,rider,4)
    assert(outfit.value==0.03 and h.power==0.2,'equipment protection leaked after exit')
end
arszi_psy=nil
print("Form hit contracts passed: "..trials.." damage cases + six actual condition/equipment pipelines; native damage unchanged, exact pre-hit armor/helmet wear, one-second conditions, optional API failure isolation and exit/suspension/load guards.")
