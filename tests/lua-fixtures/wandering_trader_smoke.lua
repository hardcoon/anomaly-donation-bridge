local root=assert(arg[1])
local function load_module(name)
    local e=setmetatable({},{__index=_G});local f=assert(loadfile(root.."/anomaly_donation_bridge_"..name..".script"))
    setfenv(f,e);f();return e
end
local clock,paused,trade_open,locations=1,false,false,"cordon"
local callbacks,kill_count,menu_mode={},0,0
local in_death_callback=false
actor_menu={get_last_mode=function()return menu_mode end}
function time_global() return clock end
function device() return {is_paused=function() return paused end,precache_frame=0} end
function app_ready() return true end
local fs={exist=function(_,alias,path) return path~="missing.ogf" end}
function getFS() return fs end
local defs={adb_wandering_trader={},sim_default_stalker_1={}}
local function item(s,class,kind,extra)
    defs[s]={class=class,kind=kind,inv_name=s,visual=s..".ogf",cost="1000",inv_grid_width="1",inv_grid_height="1"}
    for k,v in pairs(extra or {}) do defs[s][k]=v end
end
item("rifle","WP_AK74","w_rifle",{ammo_class="ammo, invalid_ammo"})
item("pistol","WP_PM","w_pistol",{ammo_class="ammo"})
item("ammo","AMMO","w_ammo",{box_size="30"})
item("helm","E_HLMET","o_helmet")
item("helm_native","EQ_HLMET","o_helmet")
item("outfit","E_STLK","o_medium")
-- Vanilla novice/Sunrise/SEVA use EQU_STLK, unlike scripted exoskeletons.
item("outfit_light","EQU_STLK","o_light")
item("outfit_medium","EQU_STLK","o_medium")
item("outfit_scientific","EQU_SCIE","o_scientific")
item("outfit_military","EQU_MLTR","o_heavy")
item("outfit_exo","EQU_EXO","o_heavy")
item("arty","SCRPTART","i_arty")
item("med","II_MEDKI","i_medical",{max_uses="3"})
item("bread","II_FOOD","i_food",{max_uses="2"})
item("pda","II_PDA","i_device",{slot="7"})
item("pack","II_ATTCH","i_backpack",{slot="12"})
item("pack_native","EQ_BAKPK","i_backpack",{slot="6"})
item("pack_mislabelled","EQ_BAKPK","o_light",{slot="6"})
item("quest_outfit","EQU_STLK","o_light",{quest_item="true"})
item("untradable_outfit","EQU_STLK","o_light",{can_trade="false"})
item("story","E_HLMET","o_helmet",{story_id="123"})
item("technical","WP_AK74","w_rifle",{can_trade="false"})
item("quest","II_FOOD","i_quest",{quest_item="true"})
item("missing_model","II_FOOD","i_food",{visual="missing"})
item("invalid_ammo","AMMO","w_ammo",{box_size="65536"})
local ini={}
function ini:section_exist(s) return defs[s]~=nil end
function ini:line_exist(s,k) return defs[s] and defs[s][k]~=nil end
function ini:r_string(s,k) assert(self:line_exist(s,k));return defs[s][k] end
local enumerations=0
function ini:section_for_each(f) enumerations=enumerations+1;for s in pairs(defs) do assert(f(s)==false,"native callback requires bool") end end
function system_ini() return ini end
anomaly_donation_bridge_roulette_catalog=load_module("roulette_catalog")
local cat=load_module("trader_catalog");anomaly_donation_bridge_trader_catalog=cat
local o=assert(cat.options({}))
assert(o.duration==180 and o.radius==150 and o.discount==80 and o.marker)
assert(not cat.options({discount_percent="100"}))
assert(not cat.options({ammo_count="51"}))
local disabled={};for k in pairs(o.stock) do disabled[k.."_enabled"]="false" end
assert(not cat.options(disabled));disabled.food_enabled="true"
local pools=cat.collect(ini)
assert(#pools.weapons==2 and #pools.helmets==2 and #pools.armor==6 and #pools.devices==1,
    'catalogue must include native light/medium/scientific outfits and both helmet classes')
assert(#pools.food==1 and #pools.ammo==1 and #pools.medicine==1)
local function counts(plan) local r={};for _,s in ipairs(plan) do r[s]=(r[s] or 0)+1 end;return r end
local n=counts(cat.stock_plan(ini,o,nil,function() return 1 end))
assert(n.rifle==1 and n.pistol==1 and n.ammo==20 and n.bread==5 and n.med==5)
assert(n.helm==1 and n.outfit==1 and not n.pack and not n.story and not n.technical)
local armor_only={armor_enabled="true",armor_count="10"}
for key in pairs(o.stock) do if key~="armor" then armor_only[key.."_enabled"]="false" end end
local all_armor=counts(cat.stock_plan(ini,assert(cat.options(armor_only)),nil,function(low,high)
    assert(low==1 and high>=1);return high
end))
for _,section in ipairs({"outfit","outfit_light","outfit_medium","outfit_scientific","outfit_military","outfit_exo"}) do
    assert(all_armor[section]==1,'wearable missing from random stock: '..section)
end
assert(not all_armor.pack_native and not all_armor.pack_mislabelled and not all_armor.pda and
    not all_armor.quest_outfit and not all_armor.untradable_outfit,'unsafe/non-outfit entered armor pool')
assert(math.floor(1000*cat.price_factor(80))==200)
local policy=cat.trade_rules(ini,{"rifle"})
assert(policy:find("rifle = 1, 1",1,true) and not policy:find("bread = 1",1,true))
local pos={x=0,y=0,z=0,distance_to_sqr=function() return 100 end}
local actor={}
function actor:position() return pos end
function actor:alive() return true end
function actor:id() return 0 end
function actor:game_vertex_id() return 100 end
function actor:has_info(s) assert(s=="trade_wnd_open");return trade_open end
function actor:item_in_slot(slot) assert(slot==2 or slot==3);if slot==2 then return {section=function() return "rifle" end} end end
local only_ammo={};for k in pairs(o.stock) do only_ammo[k.."_enabled"]="false" end
only_ammo.ammo_enabled="true"
assert(counts(cat.stock_plan(ini,cat.options(only_ammo),actor)).ammo==10)
assert(not cat.stock_plan(ini,cat.options(only_ammo),nil))
assert(not cat.prepare(ini));cat.advance();assert(cat.prepare(ini).devices[1]=="pda")
assert(#cat.prepare(ini).armor==6 and #cat.prepare(ini).helmets==2,'batched catalogue lost native equipment')
local srv,cli,nextid,created,spots={},{},100,0,{}
-- These are the native Anomaly XML node names, not the NPC community name.
local trader_spot='ui_pda2_trader_location'
local spot_nodes={[trader_spot]=true,[trader_spot..':level_map']=true}
local xml_reads,map_adds=0,0
function CScriptXmlInit()
    return {ParseFile=function(_,path) assert(path=='map_spots.xml');xml_reads=xml_reads+1 end,
        NodeExist=function(_,path,index) assert(index==0);return spot_nodes[path]==true end}
end
local native={}
local policy_builds,binds,opens=0,0,0
function native:section() return self.sec end
function native:name() return self.identity end
function native:id() return self.uid end
function native:position() return pos end
function native:level_vertex_id() return 42 end
function native:game_vertex_id() return 100 end
function native:alive() return not self.dead end
function native:deadbody_can_take() error('no special corpse loot restriction') end
function native:deadbody_closed() error('no special corpse search restriction') end
function native:deadbody_closed_status() return self.corpse_closed==true end
function native:get_visual_name() return self.visual or "actors\\original" end
function native:set_visual_name(p) self.visual=p end
function native:invulnerable(v) self.invincible=v end
function native:set_character_community(v) self.community=v end
function native:force_set_goodwill() end
function native:set_start_dialog(v) self.dialog=v end
function native:enable_talk(...) assert(select('#',...)==0);self.talk=true end
function native:disable_talk() error('trader must not forcibly disable dialogue') end
function native:enable_trade(...) assert(select('#',...)==0);self.trade=true end
function native:disable_trade() error('trader must not forcibly disable trade') end
function native:buy_condition(r,s) self.buy=r.source..s end
function native:sell_condition(r,s) self.sell=r.source..s end
function native:buy_item_condition_factor() end
function native:sell_item_exponent() end
function native:inactualize_patrol_path() end
function native:accessible() return true end
function native:is_talking() return self.talking end
function native:start_trade(a) assert(a:id()==0);opens=opens+1;self.talking=false;trade_open=true end
function hide_hud_inventory() return true end
function native:set_condition(c) self.cond=c end
function native:condition() return self.cond or .5 end
function native:cost() return self.sec=="rifle" and 1250 or 1000 end
function native:set_ammo_elapsed(n) self.magazine=n end
function native:set_remaining_uses(n) self.uses=n end
function native:get_remaining_uses() return self.uses end
function native:ammo_get_count() return self.ammo end
setmetatable(actor,{__index=native})
db={actor=actor,storage={}}
local life={}
local function native_death_loot(npc)
    -- Model the game's death_manager dont_keep_items path, NOT addon cleanup.
    for id,item in pairs(srv) do
        if item.parent_id==npc:id() then srv[id]=nil;cli[id]=nil end
    end
end
function native:kill(who)
    assert(who:id()==self:id() and not self.invincible and not trade_open and not self.talking and menu_mode==0)
    self.dead=true;srv[self.uid].dead=true;kill_count=kill_count+1
    in_death_callback=true;callbacks.npc_on_death_callback(self,who);in_death_callback=false
    native_death_loot(self)
end
function life:object(id) return srv[id] end
function life:set_switch_online(id,value) assert(value==true);srv[id].online_allowed=value end
function life:set_switch_offline(id,value) srv[id].offline_allowed=value end
function alife() return life end
function alife_create(section,position,lv,gv,parent)
    nextid=nextid+1;created=created+1
    local s={id=nextid,identity=section..nextid,sec=section,parent_id=parent or 65535,m_smart_terrain_id=65535}
    function s:name() return self.identity end
    function s:section_name() return self.sec end
    function s:set_character_name(v) self.donor=v end
    function s:force_set_goodwill() end
    function s:alive() return not self.dead end
    function s:kill() assert(not trade_open and menu_mode==0);self.dead=true;kill_count=kill_count+1 end
    srv[s.id]=s
    cli[s.id]=setmetatable({uid=s.id,identity=s.identity,sec=section},{__index=native,__eq=function() error('userdata equality') end})
    if section=="adb_wandering_trader" or section=="sim_default_stalker_1" then db.storage[s.id]={} end
    return s
end
function life:create_ammo(section,p,lv,gv,parent,count)
    assert(count==30);local s=alife_create(section,p,lv,gv,parent);cli[s.id].ammo=count;return s
end
function alife_release(s)
    error('private trader must not destroy NPC or stock objects: '..s.sec)
end
level={object_by_id=function(id) return cli[id] end,name=function() return locations end,vertex_position=function() return pos end,
    map_has_object_spot=function(id) return spots[id] and 1 or 0 end,
    map_add_object_spot=function(id,kind,hint)
        assert(spot_nodes[kind], 'native map location would assert: XML node not found: '..kind)
        assert(kind==trader_spot);map_adds=map_adds+1;spots[id]=hint
    end,
    map_change_spot_hint=function(id,kind,hint) assert(kind==trader_spot and spots[id]);spots[id]=hint end,
    map_remove_object_spot=function(id,kind) assert(kind==trader_spot);spots[id]=nil end}
local other_calls=0
trade_manager={get_sell_discount=function() return 2.5 end,get_buy_discount=function() return .4 end,
    update=function() other_calls=other_calls+1 end,setup_buy_sell_conditions=function() other_calls=other_calls+1 end,trade_init=function() end,
    get_trade_profile=function(id,key) return "foreign-profile" end,get_trade_cfg=function(path) return "foreign-config" end}
xr_conditions={};modules={stype_stalker=1};axr_beh={}
xr_logic={parse_condlist=function(_,_,_,v) return v end,
    configure_schemes=function(n) binds=binds+1;db.storage[n:id()].beh={} end,activate_by_section=function() end}
function ini_file() return ini end
function create_ini_file(s)
    policy_builds=policy_builds+1
    local t={source=s,sections={}}
    local section
    for line in s:gmatch('[^\r\n]+') do
        local name,parent=line:match('^%[([^%]]+)%]:?(.*)$')
        if name then
            section={};t.sections[name]=section
            if parent~='' then for k,v in pairs(assert(t.sections[parent])) do section[k]=v end end
        else
            local k,v=line:match('^([^%s=]+)%s*=%s*(.*)$')
            if k then section[k]=v end
        end
    end
    function t:line_exist(s,k) return self.sections[s] and self.sections[s][k]~=nil end
    function t:r_string_ex(s,k) local v=self.sections[s] and self.sections[s][k];return v~='' and v or nil end
    return t
end
function RegisterScriptCallback(name,fn)callbacks[name]=fn end
anomaly_donation_bridge_common={trim=function(s) return (s or ''):match('^%s*(.-)%s*$') end,
    truncate_utf8=function(s,n) return s:sub(1,n) end,game_text_cp1251_lossy=function(s) return s end,
    safe_points=function() return {{position=pos,vertex_id=42,distance=5}} end,
    random_safe_point=function() return {position=pos,vertex_id=43,distance=150} end}
anomaly_donation_bridge_catalog={validate_sections=function(_,key,kind)
    assert(key=='visuals' and kind=='npc','must use the existing companion/boss catalogue')
    return {{section_id="sim_default_stalker_1"}}
end}
local impl=load_module("wandering_trader");impl.start()
local ctx={actor=actor,command={command_id="donation-1"}}
local params={donor_name="Tester",duration_seconds="10"}
local function tick(n)
    for _=1,n or 1 do clock=clock+50;impl.update(clock) end
end
local function state() local s={};impl.save_state(s);return s end
assert(impl.supported(ctx))
local close_body=native.deadbody_closed
native.deadbody_closed=nil
local has_api,api_reason=impl.supported(ctx)
assert(has_api,'corpse restrictions must no longer be required APIs')
native.deadbody_closed=close_body
local ok,status,why=impl.can_execute(ctx,params)
assert(not ok and status=="deferred" and why=="trader_waiting_spawn_point")
tick();assert(impl.can_execute(ctx,params))
assert(impl.execute(ctx)=="deferred")
local record=state().adb_wandering_traders_v1['donation-1'];local npc=cli[record.id]
tick();assert(not npc.talk and not npc.trade)
local partial=state();local created_partial=created
impl.load_state(partial);impl.on_game_load();tick()
assert(created<=created_partial+4,'load duplicated stock batch')
tick(25)
local committed=state();record=committed.adb_wandering_traders_v1['donation-1']
assert(record.phase=="active" and npc.invincible and npc.trade and npc.community=="trader")
assert(not npc.visual and record.section=='sim_default_stalker_1' and srv[record.id].donor=="Tester")
assert(npc.lootable==nil and not npc:deadbody_closed_status(),'corpse flags have no effect while alive')
assert(spots[record.id]:find('Tester',1,true))
assert(spots[record.id]:find('Tester\\nОсталось:',1,true),'PDA needs literal backslash-n, not a raw linefeed')
assert(xml_reads==1 and map_adds==1,'marker schema must be checked once before native marker creation')
local marker_before=spots[record.id]
tick(20)
assert(spots[record.id]~=marker_before and map_adds==1 and xml_reads==1,'timer must update the existing marker')
committed=state();record=committed.adb_wandering_traders_v1['donation-1']
assert(db.storage[record.id].beh.goto_target=='lvid:43')
local inventory_count=created
assert(impl.execute(ctx)=="executed");assert(impl.can_execute(ctx,params));assert(impl.execute(ctx)=="executed")
assert(created==inventory_count,'retry duplicated NPC/goods')
assert(trade_manager.get_sell_discount(record.id)==1)
assert(trade_manager.get_sell_discount(999)==2.5 and trade_manager.get_buy_discount(999)==.4)
trade_manager.update(npc);assert(other_calls==0)
-- ui_inventory's TMode_InitProfile must receive usable cfg/list/exponents.
local profile={mode=2,cfg=trade_manager.get_trade_profile(record.id,'cfg_ltx'),
    list=trade_manager.get_trade_profile(record.id,'current_sell_condition'),
    cond_exponent=trade_manager.get_trade_profile(record.id,'current_sell_item_exponent'),
    discount=trade_manager.get_sell_discount(record.id)}
local cfg=trade_manager.get_trade_cfg(profile.cfg)
assert(profile.list=='sell' and profile.cond_exponent==1 and profile.discount==1)
assert(trade_manager.get_trade_profile(record.id,'unknown')==nil)
assert(trade_manager.get_trade_profile(999,'cfg_ltx')=='foreign-profile' and trade_manager.get_trade_cfg('foreign')=='foreign-config')
local total=0
for _,entry in ipairs(record.goods) do
    assert(cfg:line_exist(profile.list,entry.section) and cfg:r_string_ex(profile.list,entry.section),'stock hidden by ui_inventory')
    local ret={};impl.on_get_item_cost('condition_based',cli[entry.id],profile,999,ret)
    assert(ret.new_cost==200,'scripted trade must charge nominal discounted price')
    total=total+ret.new_cost
end
assert(total==#record.goods*200,'stack prices accumulated rounding margins')
if arg[2] then
    -- Optional integration check against the unmodified game's real UI helpers.
    local f=assert(io.open(arg[2],'rb'));local source=f:read('*a');f:close()
    local first=assert(source:find('function get_item_cost',1,true))
    local last=assert(source:find('\n-- Upgrades',first,true))
    local env=setmetatable({trade_manager=trade_manager,
        IsItem=function(kind,sec)
            if kind=='multiuse' then return tonumber(defs[sec].max_uses) end
            if kind=='ammo' then return tonumber(defs[sec].box_size) end
        end,
        SYS_GetParam=function(_,sec,key) return defs[sec][key]=='true' and true or nil end,
        str_explode=function(s) local t={};for v in s:gmatch('[^,]+') do t[#t+1]=v end;return t end,
        printe=function(msg) error(msg) end,
        SendScriptCallback=function(name,...) assert(name=='on_get_item_cost');impl.on_get_item_cost(...) end}, {__index=_G})
    local fns=assert(loadstring(source:sub(first,last-1)));setfenv(fns,env);fns()
    for _,entry in ipairs(record.goods) do
        assert(env.get_item_trade_status(cli[entry.id],profile)==4,'actual game UI hides the product')
        assert(env.get_item_cost(cli[entry.id],profile)==200,'actual game UI price differs')
    end
    print('PASS unmodified Anomaly utils_item: all stock visible; exact discounted per-item/stack prices')
end
assert(cfg:line_exist('buy','rifle') and not cfg:r_string_ex('buy','rifle'),'NPC must not buy actor goods')
assert(not cfg:r_string_ex('sell','pack'),'original NPC equipment must not enter the shop')
local unrelated={};impl.on_get_item_cost('condition_based',npc,{mode=2,cfg='foreign'},123,unrelated)
assert(unrelated.new_cost==nil,'foreign merchant price changed')
-- The dialogue answer, not only the separate trade button, opens trade.
local actions={}
impl.init_dialog({AddPhrase=function(_,text,id,parent)
    return {GetPhraseScript=function() return {SetScriptText=function() end,
        AddAction=function(_,action) actions[id]=action end} end}
end})
assert(actions['1']=='anomaly_donation_bridge_wandering_trader.open_trade')
impl.open_trade(actor,npc);assert(opens==1 and trade_open);trade_open=false
local builds_at,enumerations_at,binds_at=policy_builds,enumerations,binds
for _=1,10 do trade_manager.update(npc);trade_manager.setup_buy_sell_conditions(npc) end
assert(policy_builds==builds_at and enumerations==enumerations_at,'unchanged shop rebuilt the full catalogue')
tick(20)
assert(policy_builds==builds_at and binds==binds_at,'idle update rebound the NPC or rebuilt trade rules')
for _,discount in ipairs({0,80,99}) do
    local equipped={cost=function() return 1250 end,condition=function() return 1 end}
    local factor=cat.nominal_sale_factor(ini,'rifle',equipped,discount)
    local expected=math.floor(1000*(100-discount)/100)
    assert(math.floor(1250*factor)==expected,'native weapon price differs from nominal discounted price')
end
assert(npc.sell:find('rifle = 0.1602, 0.1602',1,true),'private shop did not compensate native attachment cost')
trade_manager.update(setmetatable({uid=999,sec="trader",identity="other"},{__index=native}));assert(other_calls==1)
for _,entry in ipairs(record.goods) do
    assert(cli[entry.id]:condition()==1)
    if entry.section=='med' then assert(cli[entry.id].uses==3) end
    if entry.section=='rifle' then assert(cli[entry.id].magazine==0) end
end
committed=state();record=committed.adb_wandering_traders_v1['donation-1']
local before=record.remaining
paused=true;tick(400);paused=false;tick()
assert(state().adb_wandering_traders_v1['donation-1'].remaining>=before-50)
impl.load_state(committed);impl.on_game_load();tick()
assert(created==inventory_count and state().adb_wandering_traders_v1['donation-1'].remaining==before)
impl.on_level_changing();clock=clock+90000;impl.on_game_load();locations='jupiter';cli[record.id]=nil;tick(20)
assert(state().adb_wandering_traders_v1['donation-1'].remaining==before-950,'offline lifetime/loading time')
-- Full process restart: hooks reinstalled, no stock generation.
local restart=state();rawset(_G,'__adb_wandering_traders_v1',nil)
impl=load_module('wandering_trader');impl.start();impl.load_state(restart);impl.on_game_load();tick()
assert(created==inventory_count and trade_manager.get_sell_discount(record.id)==1)
local purchased=record.goods[1];srv[purchased.id].parent_id=0
trade_open=true;tick(300);assert(srv[record.id]:alive() and kill_count==0,'expired NPC killed during trade')
assert(xr_conditions.adb_trader_ready(nil,npc),'expiry deactivated the meet/trade condlist while UI is open')
trade_open=false;menu_mode=2;tick(20);assert(kill_count==0,'scripted trade window must also postpone death')
menu_mode=0;tick(20)
assert(srv[record.id] and not srv[record.id]:alive() and srv[purchased.id] and kill_count==1,
    'expiry must kill once, retain the native body and preserve purchased stock')
assert(state().adb_wandering_traders_v1['donation-1'].removed,'offline death must not wait for corpse flags')
cli[record.id]=npc;npc.dead=true;native_death_loot(npc);tick(2)
assert(not npc:deadbody_closed_status() and srv[purchased.id])
-- Identity guards must not alter/delete a different NPC reusing the ID.
impl.load_state(committed);impl.on_game_load()
srv[record.id]={id=record.id,section_name=function() return 'other_npc' end,name=function() return 'new identity' end}
assert(trade_manager.get_sell_discount(record.id)==2.5)
tick();assert(srv[record.id] and state().adb_wandering_traders_v1['donation-1'].removed)
-- Forced death: no native calls inside the callback and no second stock cleanup.
locations='cordon'
local death_ctx={actor=actor,command={command_id='death-test'}}
local _,death_status=impl.can_execute(death_ctx,{donor_name='Death Test',duration_seconds='0'})
assert(death_status=='deferred');tick();impl.execute(death_ctx);tick(25)
local dead_record=state().adb_wandering_traders_v1['death-test']
assert(dead_record.phase=='active')
local victim=cli[dead_record.id];local bought=dead_record.goods[1];local dropped=dead_record.goods[2]
local dead_profile={mode=2,cfg=trade_manager.get_trade_profile(dead_record.id,'cfg_ltx')}
srv[bought.id].parent_id=0
victim.dead=true;srv[dead_record.id].dead=true
in_death_callback=true;impl.on_death(victim);in_death_callback=false
assert(not callbacks.npc_on_item_drop,'must not intercept native item destruction/drop')
assert(victim.lootable==nil and not victim:deadbody_closed_status(),'native corpse flags changed inside callback')
assert(srv[dead_record.id],'NPC was released inside native death callback')
local stale_price={};impl.on_get_item_cost('condition_based',cli[dead_record.goods[3].id],dead_profile,999,stale_price)
assert(stale_price.new_cost==nil and trade_manager.get_sell_discount(dead_record.id)==2.5,
    'dead trader retained private trading hooks')
assert(trade_manager.get_trade_cfg(dead_profile.cfg)=='foreign-config','dead trader retained private config hook')
native_death_loot(victim)
-- A scripted inventory may open immediately, before the next scheduled update.
-- Let it close normally; do not mutate corpse/stock while it holds references.
menu_mode=4;tick(20)
assert(not victim:deadbody_closed_status() and not srv[dropped.id] and srv[bought.id])
menu_mode=0;actor_menu.dead_body_searching=true;tick(20)
assert(not victim:deadbody_closed_status())
actor_menu.dead_body_searching=false
local dead_save=state();impl.load_state(dead_save);impl.on_game_load();tick(20)
assert(srv[dead_record.id] and not srv[dropped.id] and srv[bought.id],
    'native cleanup must leave the body and bought items intact')
assert(victim.lootable==nil and not victim:deadbody_closed_status(),'ordinary empty body must remain searchable')
assert(state().adb_wandering_traders_v1['death-test'].removed)
local dead_loaded=state();impl.load_state(dead_loaded);impl.on_game_load();tick(2)
assert(not victim:deadbody_closed_status() and srv[bought.id],'F9 must not lock bodies or remove purchases')
-- The update fallback catches direct script kills without a death callback.
local fallback_ctx={actor=actor,command={command_id='direct-kill'}}
impl.can_execute(fallback_ctx,{donor_name='Direct kill'});tick();impl.execute(fallback_ctx);tick(25)
local fallback_record=state().adb_wandering_traders_v1['direct-kill']
cli[fallback_record.id].dead=true;tick(25)
assert(srv[fallback_record.id] and state().adb_wandering_traders_v1['direct-kill'].removed,
    'direct kill without callback left trade stock or removed native body')
-- Online dialogue and native trade remain intact until the player closes them.
local online_ctx={actor=actor,command={command_id='online-expiry'}}
impl.can_execute(online_ctx,{donor_name='Online expiry',duration_seconds='3'});tick();impl.execute(online_ctx);tick(25)
local online_record=state().adb_wandering_traders_v1['online-expiry']
local online_npc=cli[online_record.id];online_npc.talking=true
local before_kills=kill_count;tick(80)
assert(online_npc:alive() and kill_count==before_kills and xr_conditions.adb_trader_ready(nil,online_npc))
local expiry_save=state();impl.load_state(expiry_save);impl.on_game_load();tick(5)
assert(online_npc:alive(),'loading an expired trader interrupted dialogue')
online_npc.talking=false;menu_mode=10;tick(10);assert(online_npc:alive())
menu_mode=0;tick(25)
assert(not online_npc:alive() and srv[online_record.id] and kill_count==before_kills+1,
    'online expiry must use one ordinary death and leave the body')
assert(not online_npc:deadbody_closed_status() and online_npc.lootable==nil,
    'expiry must leave ordinary native corpse interaction')
-- Modded map XML may omit the native trader icon. Reject before creating the
-- NPC, and never use map_add_object_spot as a resource-existence probe.
spot_nodes={}
impl=load_module('wandering_trader')
local created_before,adds_before=created,map_adds
local no_icon_ctx={actor=actor,command={command_id='missing-map-icon'}}
local can_run,rejection,reason=impl.can_execute(no_icon_ctx,{donor_name='No icon'})
assert(not can_run and rejection=='rejected' and reason=='trader_map_spot_missing_ui_pda2_trader_location')
assert(created==created_before and map_adds==adds_before,'missing icon must not create anything')
local checked_at=xml_reads
impl.can_execute(no_icon_ctx,{donor_name='No icon'});tick(5)
assert(xml_reads==checked_at,'invalid map XML must not be parsed every update')
-- The optional marker can be disabled, without disabling the whole template.
local hidden_ctx={actor=actor,command={command_id='marker-disabled'}}
local _,hidden_status=impl.can_execute(hidden_ctx,{donor_name='Hidden',map_marker='false'})
assert(hidden_status=='deferred');tick();impl.execute(hidden_ctx);tick(25)
local hidden_save=state();local hidden=hidden_save.adb_wandering_traders_v1['marker-disabled']
assert(hidden.phase=='active' and not spots[hidden.id] and map_adds==adds_before)
-- Restore an old active record with markers enabled into an incompatible game:
-- it must survive rather than call the fatal native marker constructor.
hidden.options.marker=true
impl.load_state(hidden_save);impl.on_game_load();tick(20)
assert(cli[hidden.id] and cli[hidden.id].trade and not spots[hidden.id] and map_adds==adds_before)
local xml_factory=CScriptXmlInit
CScriptXmlInit=nil;impl=load_module('wandering_trader')
local _,no_xml_status,no_xml_reason=impl.can_execute(no_icon_ctx,{donor_name='No API'})
assert(no_xml_status=='rejected' and no_xml_reason=='trader_map_xml_api_unavailable')
tick(20);assert(map_adds==adds_before,'restored NPC accessed unavailable map API')
CScriptXmlInit=xml_factory
local original_exist=fs.exist
fs.exist=function(self,alias,path) return path~='ui\\map_spots.xml' and original_exist(self,alias,path) end
impl=load_module('wandering_trader')
local _,missing_status,missing_reason=impl.can_execute(no_icon_ctx,{donor_name='No XML file'})
assert(missing_status=='rejected' and missing_reason=='trader_map_spots_xml_missing')
fs.exist=original_exist
spot_nodes={[trader_spot]=true};impl=load_module('wandering_trader')
local _,broken_status,broken_reason=impl.can_execute(no_icon_ctx,{donor_name='Incomplete XML'})
assert(broken_status=='rejected' and broken_reason=='trader_map_spot_missing_ui_pda2_trader_location')
-- Once the schema is available on a fresh game process, a loaded trader gets
-- one marker again and continues to update its timer without respawning.
spot_nodes={[trader_spot]=true,[trader_spot..':level_map']=true}
impl=load_module('wandering_trader');impl.on_game_load();tick()
assert(spots[hidden.id] and map_adds==adds_before+1)
assert(spots[hidden.id]:find('Hidden',1,true))
-- Real Anomaly launchers have w_explosive (also used by hand grenades),
-- and their inventory rounds use CWeaponAmmo factories without AMMO prefix.
do
    item('wpn_rpg7','WP_RPG7','w_explosive',{ammo_class='ammo_og7b'})
    item('wpn_rg6','WP_RG6','w_explosive',{ammo_class='ammo_vog25'})
    item('wpn_m79','WP_RG6','w_explosive',{ammo_class='ammo_m209'})
    item('ammo_og7b','S_OG7B','w_ammo',{box_size='1'})
    item('ammo_vog25','S_VOG25','w_ammo',{box_size='1'})
    item('ammo_m209','S_M209','w_ammo',{box_size='1'})
    item('native_og7b','A_OG7B','w_ammo',{box_size='1'})
    item('native_vog25','A_VOG25','w_ammo',{box_size='1'})
    item('native_m209','A_M209','w_ammo',{box_size='1'})
    item('grenade','G_F1','w_explosive')
    item('missile','G_RPG7','w_base')
    item('fake_missile','G_FAKE','w_base')
    item('scope','WP_SCOPE','w_misc')
    item('knife','WP_KNIFE','w_melee')
    item('bolt','II_BOLT','w_misc')
    item('artefact_container','SCRPTART','i_arty_cont')
    item('binocular','WP_BINOC','w_misc')
    item('drink','II_FOOD','i_drink')
    item('cooked','II_FOOD','i_mutant_cooked')
    item('medicine_native','II_FOOD','i_medical')
    item('detector','DET_ELIT','i_device')
    item('arti_native','ARTEFACT','i_arty')
    local audited=load_module('trader_catalog')
    local p=audited.collect(ini)
    assert(#p.weapons==5 and #p.ammo==7,'launchers/rounds missing from trade stock')
    assert(#p.devices==3 and #p.food==3 and #p.medicine==2 and #p.artifacts==2,
        'native inventory kind/class mismatch')
    for _,s in ipairs({'grenade','missile','fake_missile','scope','knife','bolt','artefact_container'}) do
        assert(not audited.safe_item(ini,s),'unrelated/technical object entered stock: '..s)
    end
    local choices={weapons_count='20',ammo_count='2'}
    for k in pairs(o.stock) do if k~='weapons' and k~='ammo' then choices[k..'_enabled']='false' end end
    local plan=counts(assert(audited.stock_plan(ini,assert(audited.options(choices)),nil,function(_,high) return high end)))
    for _,s in ipairs({'rifle','pistol','wpn_rpg7','wpn_rg6','wpn_m79'}) do
        assert(plan[s]==1,'weapon must be available once without section duplicates: '..s)
    end
    assert(plan.ammo==4 and plan.ammo_og7b==2 and plan.ammo_vog25==2 and plan.ammo_m209==2,
        'each launcher must receive its own matching full-box ammunition')
    local launcher_actor={item_in_slot=function(_,slot)
        if slot==2 then return {section=function() return 'wpn_rpg7' end} end
    end}
    local rounds=counts(assert(audited.stock_plan(ini,cat.options(only_ammo),launcher_actor)))
    assert(rounds.ammo_og7b==10,'ammo-only stock must support the actor\'s launcher')
    assert(not audited.prepare(ini));audited.advance()
    assert(#audited.prepare(ini).weapons==5 and #audited.prepare(ini).ammo==7,
        'batched scan differs from direct scan')
end
print('PASS wandering trader: live stock/types/ammo, native prices, staged save/replay, pause/offline/restart, ordinary searchable corpse, native loot cleanup, no custom destruction/locks, bought items, identity guards, native/missing map XML and timer marker')
