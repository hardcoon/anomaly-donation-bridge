local root=assert(arg[1]);local checks=0
local function check(ok,why)checks=checks+1;assert(ok,why)end
local function near(a,b)check(math.abs(a-b)<1e-8,tostring(a).." ~= "..tostring(b))end
local function module(name)
    local env=setmetatable({},{__index=_G});local f=assert(loadfile(root.."/"..name..".script"))
    setfenv(f,env);f();_G[name]=env;return env
end
local function ini(data)return {section_for_each=function(_,fn)for s in pairs(data)do fn(s)end end,
    line_exist=function(_,s,k)return data[s] and data[s][k]~=nil end,r_string=function(_,s,k)return data[s][k]end}end
local function item(class,kind,cost,more)
    local e={inv_name="test",class=class,kind=kind,cost=tostring(cost),visual="test.ogf",inv_grid_width="1",inv_grid_height="1"}
    for k,v in pairs(more or {})do e[k]=v end;return e
end
local data={}
for _,family in ipairs({{"helmet","E_HLMET","o_helmet",100},{"armor","E_STLK","o_heavy",1000},
    {"weapon","WP_AK74","w_rifle",500},{"artefact","SCRPTART","i_arty",10000}})do
    for i=1,10 do for _,variant in ipairs({"a","b"})do
        data[family[1]..i..variant]=item(family[2],family[3],i*family[4])
    end end
end
data.quest=item("E_HLMET","o_helmet",500,{quest_item="true"})
data.story=item("E_HLMET","o_helmet",500,{story_id="42"})
data.junk=item("II_ATTCH","i_misc",100)
data.ammo=item("AMMO_S","w_ammo",100)
data.scope=item("WP_SCOPE","w_scope",100)
data.bolt=item("II_BOLT","w_misc",10)
data.bolt_bullet=item("II_BOLT","w_misc",100)
data.misc_weapon=item("II_ATTCH","w_misc",200)
data.skin=item("SCRPTART","i_mutant_belt",200)
data.plate=item("SCRPTART","i_attach",200)
local cat=module("anomaly_donation_bridge_roulette_catalog");local entries=cat.rebuild(ini(data),function(s)return s end)
check(#entries==82,"only four families, including protected catalogue records")
check(not cat.get("junk") and not cat.get("ammo") and not cat.get("scope"),"junk/ammo/attachments excluded")
check(not cat.get("bolt") and not cat.get("bolt_bullet") and not cat.get("misc_weapon"),"bolts and generic w_misc are not firearms")
check(not cat.get("skin") and not cat.get("plate"),"SCRPTART skins and equipment attachments are not artefacts")
check(cat.get("helmet5a").tier==3 and cat.get("armor5a").tier==3,"prices ranked separately per family")
check(cat.get("helmet5a").tier==cat.get("helmet5b").tier,"equal prices share tier")
check(not cat.get("quest").reward_allowed and cat.get("story").quest,"quest and story protection")
local core=module("anomaly_donation_bridge_roulette_core")
local objects,now,rng,created,nextid,fail={},0,0,0,100,false
local function reset(section)
    core.reset();objects={[1]={id=1,section=section or "helmet5a",value=500}};now=0;created=0;nextid=100;fail=false
end
core.configure({snapshot=function(id)return objects[id]end,now=function()return now end,random=function()return rng end,
    exists=function(id)return objects[id]~=nil end,release=function(id)objects[id]=nil;return true end,
    create=function(section)
        if fail then return nil,"spawn_failed" end
        nextid=nextid+1;created=created+1;objects[nextid]={id=nextid,section=section,value=cat.get(section).price};return nextid
    end},{duration_ms=3500})
reset()
for _,risk in ipairs({"low","high"})do
    reset()
    local q=assert(core.quote_category(1,risk));near(q.probabilities.lower,risk=="low" and .198 or .49)
    near(q.probabilities.same,risk=="low" and .7425 or .196);near(q.probabilities.higher,risk=="low" and .0495 or .294)
    near(q.probabilities.jackpot,risk=="low" and .01 or .02)
    near(q.display_probabilities.same,risk=="low" and .75 or .20)
    local total=0
    for _,c in ipairs(q.candidates)do
        total=total+c.probability;check(cat.get(c.section).category=="helmet" and c.section~="helmet5a" and not cat.get(c.section).quest,"same category and different nonquest section")
    end
    near(total,1)
    for _,ticket in ipairs({0,.55,.99})do
        reset();rng=ticket;local t=assert(core.start_category(1,risk));check(t.win and t.count==1,"guaranteed single item")
        check(not core.claim(),"cannot claim while spinning");now=4000;core.update()
        check(core.current().phase=="await_claim" and core.busy(),"claim gate retains attempt")
        check(not objects[1] and created==1 and objects[t.reward_ids[1]],"atomic swap")
        core.update();core.update();check(created==1,"updates from actor and UI do not duplicate reward")
        check(not core.start_category(t.reward_ids[1],risk),"no reroll before claim")
        check(core.claim().target==t.target and not core.busy(),"claim acknowledges result")
        check(not core.claim(),"claim is idempotent")
    end
end
reset("helmet1a");local q=assert(core.quote_category(1,"low"));near(q.probabilities.lower,.198);near(q.probabilities.same,.7425)
check(q.tiers.lower==0 and q.candidates[1].lost,"white downgrade is an explicit loss")
for _,risk in ipairs({"low","high"})do
    reset("helmet1a");q=assert(core.quote_category(1,risk))
    near(q.probabilities.lower,risk=="high" and .49 or .198)
    rng=0;local loss=assert(core.start_category(1,risk))
    check(loss.lost and not loss.win and loss.target_tier==0,"loss selected only below white")
    local saved={};core.save(saved);core.reset();core.load(saved);core.update()
    check(not objects[1] and created==0 and #core.current().reward_ids==0,"loss consumes stake and spawns nothing")
    check(core.result().lost and core.result().reason=="lost","intentional loss is not a transaction error")
    saved={};core.save(saved);core.reset();core.load(saved);core.update()
    check(created==0 and core.claim().lost and not core.busy(),"saved loss acknowledged once without reward")
    reset("helmet1a");rng=.99;assert(core.start_category(1,risk));now=4000;core.update()
    check(core.result().win and not core.result().lost and created==1,"white can still win a higher tier")
end
reset("helmet10a");q=assert(core.quote_category(1,"high"));near(q.probabilities.higher,0);near(q.probabilities.same,.49)
for _,family in ipairs({"helmet","armor","weapon","artefact"})do
    for _,risk in ipairs({"low","high"})do
        reset(family.."5a");rng=.999999
        local t=assert(core.start_category(1,risk));check(t.outcome=="jackpot" and t.target_tier==5,"jackpot jumps to maximum available tier")
        check(cat.get(t.target).category==family and t.target~=t.input.section,"jackpot stays in category and uses another section")
        local start=t.probabilities.lower+t.probabilities.same+t.probabilities.higher
        check(t.angle>=start*360 and t.angle<360,"jackpot needle lands in red strip")
        now=4000;core.update();check(core.claim().jackpot and created==1 and not objects[1],"jackpot settles one atomic reward")
    end
end
reset("quest");check(not core.quote_category(1,"low"),"protected stake rejected")
reset();check(not core.quote_category(1,"other"),"invalid risk rejected");check(not core.quote_category("one","low"),"invalid native id rejected")
for _,p in ipairs({{lower=.2,same=.75,higher=.05},{lower=.5,same=.2,higher=.3},{lower=0,same=1,higher=0}})do
    local start=0
    for _,outcome in ipairs({"lower","same","higher"})do
        if p[outcome]>0 then for _,r in ipairs({0,.5,.999999})do
            local a=core.category_angle(r,p,outcome)
            check(a>=start*360 and a<(start+p[outcome])*360,"needle inside corresponding colored sector")
            near(core.needle(1,a),a)
        end end
        start=start+p[outcome]
    end
end
reset();rng=.99;local t=assert(core.start_category(1,"high"));local saved={};core.save(saved);core.reset();core.load(saved);core.update()
check(core.current().phase=="await_claim" and created==1,"saved spinning attempt settles once")
saved={};core.save(saved);core.reset();core.load(saved);core.update();check(created==1 and core.claim().target==t.target,"saved result not duplicated")
reset();fail=true;assert(core.start_category(1,"low"));now=4000;core.update()
check(objects[1] and not core.result().win and core.current().phase=="await_claim","spawn failure retains original")
for _,risk in ipairs({"low","high"})do
    for _,value in ipairs({499,501,25})do
        reset();rng=.99;local offer=assert(core.start_category(1,risk))
        local target,input_tier,angle=offer.target,offer.input_tier,offer.angle
        objects[1].value=value;now=4000;core.update()
        check(not objects[1] and created==1 and core.result().reason=="success","price change must not cancel the exchange")
        check(core.result().target==target and core.result().input_tier==input_tier and core.result().angle==angle,
            "price change rerolled or repriced the frozen offer")
        core.update();core.update();check(created==1,"price change duplicated the reward")
    end
end
reset();assert(core.start_category(1,"low"));objects[1]=nil;now=4000;core.update()
check(created==0 and core.result().reason=="input_missing","missing stake still cancels without issuing a reward")
reset();assert(core.start_category(1,"low"));objects[1].section="helmet6a";now=4000;core.update()
check(objects[1] and created==0 and core.result().reason=="input_missing","different item must not replace the original stake")
if arg[2]then
    local stock=assert(loadfile(arg[2]))();entries=cat.rebuild(ini(stock),function(s)return s end)
    check(not cat.get("bolt") and not cat.get("bolt_bullet"),"native stock bolts excluded")
    local counts={};local out=assert(io.open(root.."/work/vanilla-catalogue.tsv","w"))
    out:write("section\tclass\tprice\ttier\tcategory\tquest\n")
    for _,e in ipairs(entries)do
        check(e.category=="weapon" or e.category=="armor" or e.category=="helmet" or e.category=="artefact","vanilla supported category")
        counts[e.category]=(counts[e.category]or 0)+1
        out:write(string.format("%s\t%s\t%d\t%d\t%s\t%s\n",e.section,e.class,e.price,e.tier,e.category,tostring(e.quest)))
    end
    out:close();for k,v in pairs(counts)do print("Vanilla "..k..": "..v)end
end
print("Catalogue/transaction checks passed: "..checks)
