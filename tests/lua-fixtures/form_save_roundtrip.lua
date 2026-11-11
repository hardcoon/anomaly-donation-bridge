-- Shared integration scenarios executed by all six full native-API fixtures.
-- Only plain data is copied between saves; real module callbacks do all work.
return function(c)
    local function plain(value,seen)
        local kind=type(value)
        assert(kind=="nil" or kind=="string" or kind=="number" or kind=="boolean" or kind=="table","native object entered a form save")
        if kind~="table" then return value end
        assert(getmetatable(value)==nil,"save table has runtime ownership")
        seen=seen or {};assert(not seen[value],"cyclic save record");seen[value]=true
        local result={};for key,item in pairs(value) do result[plain(key,seen)]=plain(item,seen) end
        seen[value]=nil;return result
    end
    local function record(data)
        local r=data[c.key] and data[c.key].form_state
        assert(type(r)=="table" and r.remaining>0,"form snapshot missing")
        plain(data);return r
    end
    local function load(data)
        c.cancel();c.set_memory(plain(data));c.descriptor().on_game_load();c.descriptor().update()
    end
    local function serialize()
        local data={};c.callbacks.save_state(data)
        assert(c.active() and not c.live(),"native form was recreated inside serialization")
        record(data);return data
    end
    local function capture_and_cancel()
        local data=serialize();c.cancel();c.stopped();return data
    end
    c.cancel();c.set_memory({});c.set_paused(false)
    c.start();c.advance(1000)
    -- Native ongoing condition loss is charged to the form, not healed away
    -- by saving/loading. The rider returns to its entry HP before ClientSave.
    c.actor.health=c.actor.health-0.1;c.advance(1)
    c.callbacks.on_before_save_input()
    assert(c.active() and not c.live(),"F5 should suspend the native form, not end its effect")
    local saved=serialize()
    local first=record(saved)
    assert(first.remaining<20000 and first.remaining>18000 and first.form_health<first.form_max_health)
    -- Synchronous disk IO may take longer than the old ten-second watchdog.
    -- No new native frame can enter the form until the writer has returned.
    c.elapse_silent(12000);c.advance(16)
    assert(c.active() and c.live(),"F5 failed to continue after a slow save")
    local continued=record(capture_and_cancel())
    assert(continued.remaining==first.remaining,"saving consumed form time")
    assert(continued.form_health==first.form_health,"saving refilled form HP")
    assert(continued.params.mutant_sections==first.params.mutant_sections,"saving rerolled the model")
    first.cooldowns.resume_probe=5000
    c.advance(5000)
    c.reload_common() -- same process token survives a Lua-module/VM generation
    c.set_paused(true);load(saved)
    assert(c.active() and not c.live(),"load resumed native objects before the UI was ready")
    c.advance(4000)
    c.set_paused(false);c.descriptor().update()
    assert(c.active() and c.live(),"form failed to re-enter after F9")
    local resumed=capture_and_cancel()
    local second=record(resumed)
    assert(second.remaining==first.remaining,"timer ran during loading or used the previous world's clock")
    assert(second.form_health==first.form_health and second.form_max_health==first.form_max_health,"load refilled form HP")
    assert(second.params.mutant_sections==first.params.mutant_sections,"load rerolled the model selection")
    assert(second.cooldowns.resume_probe==5000,"relative cooldown did not survive loading")
    load(resumed);assert(c.live());c.advance(250)
    local elapsed=capture_and_cancel()
    assert(math.abs(record(elapsed).remaining-(second.remaining-250))<0.001,"loaded timer failed to advance once gameplay resumed")
    assert(record(elapsed).cooldowns.resume_probe==4750,"cooldown was applied as an absolute timestamp")
    load(saved);assert(c.live()) -- F9 rolls back to this save's original timer/HP
    local rollback=capture_and_cancel()
    assert(record(rollback).remaining==first.remaining and record(rollback).form_health==first.form_health)
    load({});c.stopped() -- a human save never inherits the current process's form
    local human={};c.callbacks.save_state(human)
    assert(human[c.key]==nil,"inactive form leaked into a later human save")
    c.callbacks.main_menu_on_quit()
    load(saved);c.stopped() -- saved cleanup still runs, resuming permission is gone
    c.start();local new_session=capture_and_cancel()
    assert(record(new_session).owner~=first.owner,"same PID revived a previous session token")
    load(saved);c.stopped()
    -- Invalid saved numbers/foreign handlers are rejected without recreating UI.
    local bad=plain(new_session);bad[c.key].form_state.remaining=0/0
    load(bad);c.stopped()
    bad=plain(new_session);bad[c.key].form_state.handler="other.form"
    load(bad);c.stopped()

    -- Paused time and serialization time do not run the form clock. A paused
    -- save remains suspended until a safe, unpaused actor frame is available.
    c.start();c.set_paused(true);c.elapse_silent(5000)
    c.callbacks.on_before_save_input();local paused_save=serialize()
    assert(record(paused_save).remaining==20000,"F5 charged paused time")
    c.elapse_silent(12000);c.advance(16)
    assert(c.active() and not c.live(),"form resumed into a paused UI")
    c.set_paused(false);c.advance(16);assert(c.live())
    assert(record(capture_and_cancel()).remaining==20000)

    -- Repeated input/save-state notifications coalesce, without a second
    -- movement/visibility lease or a second native form.
    c.start();c.callbacks.on_before_save_input();c.callbacks.on_before_save_input()
    serialize();serialize();c.advance(16);assert(c.live())
    c.cancel();c.stopped()
    c.start();c.callbacks.on_before_save_input({ret=true})
    assert(c.live(),"another mod vetoed saving, but F5 still removed the form")
    c.cancel();c.stopped()

    -- A cancelled native save cannot leave an invisible, blocking effect.
    c.start();c.callbacks.on_before_save_input();c.elapse_silent(10001)
    c.descriptor().update();c.stopped()
    -- F9/quit during a pending save always wins over old continuation RAM.
    c.start();c.callbacks.on_before_save_input();load({});c.stopped()
    local stale={};c.callbacks.save_state(stale);c.advance(16);c.stopped()
    assert(stale[c.key]==nil)
    c.start();c.callbacks.on_before_save_input();serialize()
    c.callbacks.main_menu_on_quit();c.advance(16);c.stopped()

    -- Manual and ADB saves may share a suspended form. Both native save-state
    -- boundaries must be observed before a single continuation is allowed.
    c.start();c.descriptor().on_effect_save("before","continuation-service")
    c.callbacks.on_before_save_input();serialize()
    c.descriptor().on_effect_save("after","continuation-service",true)
    c.file_exists("continuation-service");c.advance(16)
    assert(c.active() and not c.live(),"service save prematurely completed manual saving")
    serialize();c.advance(16);assert(c.live())
    c.cancel();c.stopped()
    c.callbacks.main_menu_on_quit();c.set_memory({})
end
