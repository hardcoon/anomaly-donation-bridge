local core,_,optional=assert(arg[1]),assert(arg[2]),assert(arg[3])
local function load_module(root,name)
    local chunk=assert(loadfile(root.."/"..name..".script"))
    local env=setmetatable({},{__index=_G}); setfenv(chunk,env); chunk(); _G[name]=env; return env
end
anomaly_donation_bridge_common=load_module(core,"anomaly_donation_bridge_common")
anomaly_donation_bridge_registry=load_module(core,"anomaly_donation_bridge_registry")
anomaly_donation_bridge_sha256=load_module(core,"anomaly_donation_bridge_sha256")
local function clone(t) if type(t)~="table" then return t end local r={} for k,v in pairs(t) do r[k]=clone(v) end return r end
local now=0
function time_global() return now end
local logs={}
function printf(format,...) logs[#logs+1]=string.format(format,...) end
local function pos(x,y,z)
    local t={x=x or 0,y=y or 0,z=z or 0}
    function t:set(a,b,c) self.x,self.y,self.z=a,b,c; return self end
    function t:distance_to(v) return math.sqrt((self.x-v.x)^2+(self.y-v.y)^2+(self.z-v.z)^2) end
    return t
end
vector=function() return pos() end
vector2=function() return {set=function(self,x,y) self.x,self.y=x,y;return self end} end
local graph,next_vertex={},9
local function vertex_at(p)
    for id,q in pairs(graph) do if q:distance_to(p)<0.05 then return id end end
    next_vertex=next_vertex+1;graph[next_vertex]=pos(p.x,p.y,p.z);return next_vertex
end
local invalid_native_calls={actor_enemy=0,dead_enemy=0,relation=0,prop_alive=0,bone_warning=0}
local function object(id,section,x)
    local o={_id=id,_section=section,_name=section..id,_pos=pos(x or 0,0,0),health=1,_team=0}
    function o:id() return self._id end
    function o:name() return self._name end
    function o:section() return self._section end
    function o:is_entity_alive() return not self.nonliving end
    function o:alive() return self.health>0 end
    function o:relation()
        if self.nonliving then invalid_native_calls.relation=invalid_native_calls.relation+1;return 3 end
        return 2
    end
    function o:position() return self._pos end
    function o:center(hud) assert(hud==false);return pos(self._pos.x,self._pos.y+0.9,self._pos.z) end
    function o:bounding_box(hud) assert(hud==false);return {max={y=self.visual_height or 1.8}} end
    function o:level_vertex_id() return vertex_at(self._pos) end
    function o:game_vertex_id() return 5 end
    function o:team() return self._team end
    function o:squad() return 0 end
    function o:group() return 0 end
    function o:change_team(t) self._team=t end
    function o:best_enemy() return self.enemy end
    function o:get_enemy()
        -- Native GetEnemy logs and returns nil, it DOES NOT throw for pcall.
        if self.health<=0 then invalid_native_calls.dead_enemy=invalid_native_calls.dead_enemy+1;return nil end
        return self.enemy
    end
    function o:enable_memory_object(e,v) self.enemy=nil end
    function o:set_enemy_callback(fn) self.enemy_filter=fn end
    function o:see(other) return self.visible and self.visible[other:id()]==true end
    function o:memory_visible_objects()
        local index=0;local seen=self.visible_objects or {}
        return function() index=index+1;local other=seen[index];if other then return {object=function() return other end} end end
    end
    function o:get_bone_id(bone) return self.bones and self.bones[bone] or 65535 end
    function o:bone_position(bone) assert(type(bone)=="number" and bone<65535,"invalid bone reached engine");return pos(1,2,3) end
    function o:remove_all_restrictions() self.cleared=true end
    function o:remove_home() self.home=nil end
    function o:set_home(vertex,r_min,r_max,aggressive,r_mid)
        assert(graph[vertex] and r_min==3 and r_max==30 and aggressive==false and r_mid==24,"invalid native camp setup")
        self.home={vertex=vertex,radius=r_max};self.home_sets=(self.home_sets or 0)+1
    end
    function o:get_script() return self.script_control==true end
    function o:accessible(v) return graph[v]~=nil and not self.no_access and not (self.blocked and self.blocked[v]) end
    function o:accessible_nearest(p,b) b:set(p.x,p.y,p.z); return vertex_at(p),b end
    function o:action() return self.current_action end
    function o:reset_action_queue() self.queue={};self.current_action=nil;self.resets=(self.resets or 0)+1 end
    function o:poltergeist_set_actor_ignore(v) self.actor_ignore=v end
    return o
end
local a=object(1,"actor",0); a._team=7
function a:get_enemy() invalid_native_calls.actor_enemy=invalid_native_calls.actor_enemy+1;return nil end
function a:best_enemy() invalid_native_calls.actor_enemy=invalid_native_calls.actor_enemy+1;return nil end
db={actor=a,offline_objects={},spawned_vertex_by_id={}}
game_object={enemy=2}
function IsStalker(o) return o.stalker==true end
function IsMonster(o) return o.monster==true end
local data={}
local tips={}
news_manager={send_tip=function(who,text) assert(who==a);tips[#tips+1]=text end}
alife_storage_manager={get_state=function() return data end}
local objects,servers={},{}
local sim={teleports={}}
function sim:object(id) return servers[tonumber(id)] end
function sim:actor() return {m_game_vertex_id=99} end
function sim:release(o) servers[o.id]=nil;objects[o.id]=nil end
function sim:teleport_object(id,g,v,p) self.teleports[#self.teleports+1]=id end
function alife() return sim end
local spawned=0
function alife_create(section,p,lvid,gvid)
    spawned=spawned+1; local id=100+spawned
    local o=object(id,section,p.x); objects[id]=o
    local server={id=id,position=p,m_level_vertex_id=lvid,m_game_vertex_id=gvid,_name=o._name}
    function server:name() return self._name end
    servers[id]=server; return server
end
local markers={}
level={object_by_id=function(id) return objects[id] end,vertex_position=function(v) return assert(graph[v],"invalid graph vertex") end,
    vertex_in_direction=function(v,d,r) local p=assert(graph[v]);return vertex_at(pos(p.x+d.x*r,p.y,p.z+d.z*r)) end,
    map_has_object_spot=function(id) return markers[id] and 1 or 0 end,
    map_add_object_spot=function(id,spot,text) markers[id]=text end,map_remove_object_spot=function(id) markers[id]=nil end}
function game_graph() return {vertex=function() return {level_vertex_id=function() return 20 end,level_point=function() return pos() end} end} end
local function action_type(name) return function(...) return {kind=name,args={...}} end end
move=setmetatable({run_fwd=2},{__call=function(_,...) return action_type("move") (...) end})
act=setmetatable({attack=2,rest=1},{__call=function(_,kind,...) return action_type(kind==1 and "rest" or "attack") (kind,...) end})
anim=setmetatable({stand_idle=0},{__call=function(_,...) return action_type("idle") (...) end})
cond=setmetatable({time_end=1,move_end=2},{__call=function(_,...) return action_type("condition") (...) end})
-- Real _g.action appends to a native queue. A replacement-only mock concealed
-- old V2 commands accumulating behind an unreachable movement action.
function action(o,what,condition)
    if what.kind=="attack" then
        assert(what.args[2] and what.args[2]:is_entity_alive() and what.args[2]:alive(),"invalid forced target")
    end
    o.queue=o.queue or {};o.queue[#o.queue+1]=what
    o.current_action=o.queue[1];o.commands=(o.commands or 0)+1
    o.last_condition=condition
end
xr_logic={mob_capture=function(o,reset) if reset then o:reset_action_queue() end;o.script_control=true;o.captures=(o.captures or 0)+1 end,
    mob_release=function(o) o.script_control=false;o.releases=(o.releases or 0)+1 end}
local original_bone_calls=0
utils_obj={safe_bone_pos=function(o,bone,extra)
    original_bone_calls=original_bone_calls+1
    -- Native stock fallback warns rather than throwing. pcall around it cannot
    -- prevent a stream of Incorrect bone_id stack traces for a monster corpse.
    if o.stock_bone_warning and o:get_bone_id(bone)==65535 then
        invalid_native_calls.bone_warning=invalid_native_calls.bone_warning+1
    end
    return o:position(),extra
end}
DIK_keys={DIK_HOME=199,DIK_END=207,DIK_PRIOR=201,DIK_NEXT=209,DIK_F5=63,DIK_F9=67,DIK_ESCAPE=1,DIK_F6=64}
local callbacks={}
function RegisterScriptCallback(name,fn) callbacks[name]=fn end
local alias="adb_mc2_"..anomaly_donation_bridge_sha256.digest("gigant_strong"):sub(1,24)
local config={gigant_strong={class="SM_GIANT",script_binding="bind_monster.bind"},adb_mc2_no_steps={}}
config[alias]={class="SM_GIANT",species="adb_mc2_companion",adb_mc2_source="gigant_strong",adb_mc2_profile="5",adb_mc2_native_class="SM_GIANT",step_params="adb_mc2_no_steps"}
for _,aura in ipairs({"psy","fire","radiation","base"}) do
    for _,key in ipairs({"linear_factor","quadratic_factor","max_power","max_distance"}) do config[alias][aura.."_"..key]="0" end
end
local ini={}
function ini:section_exist(s) return config[s]~=nil end
function ini:line_exist(s,k) return config[s] and config[s][k]~=nil end
function ini:r_string(s,k) return config[s][k] end
function system_ini() return ini end
anomaly_donation_bridge_catalog={validate_sections=function() return {{section_id="gigant_strong"}} end,is_spawn_safe=function() return true end,
    companion_relations_ready=function() return true end}
anomaly_donation_bridge_common.spawn_points=function() return {{position=pos(20,0,0),vertex_id=vertex_at(pos(20,0,0))}},"requested_ring" end
local ui={free=true,opens=0,hides=0,xml_loads=0,labels=0,width=1024,height=768,windows={},frame=0,paused=false}
local camera={pos=pos(20,1.6,-15),dir=pos(0,0,1),right=pos(1,0,0),top=pos(0,1,0)}
actor_menu={is_hud_free=function() return ui.free end}
function Check_UI() return next(_GUIs)~=nil end
function device() return {frame=ui.frame,width=ui.width,height=ui.height,cam_pos=camera.pos,cam_dir=camera.dir,cam_right=camera.right,cam_top=camera.top,fov=67.5,is_paused=function() return ui.paused end} end
function get_hud()
    return {AddDialogToRender=function(_,wnd) assert(not wnd.stale);ui.windows[wnd]=true;ui.labels=ui.labels+1 end,
        RemoveDialogToRender=function(_,wnd) assert(not wnd.stale,"Stale pre-load HUD accessed");if ui.windows[wnd] then ui.windows[wnd]=nil;ui.labels=ui.labels-1 end end}
end
_GUIs={}
function Register_UI(name,path) _GUIs[name]={path=path};KEYS_UNLOCK=false end
function Unregister_UI(name) _GUIs[name]=nil;KEYS_UNLOCK=next(_GUIs)==nil end
-- Native constructors are callable tables, not Lua functions.
CUIScriptWnd={SetAutoDelete=function() end,SetWndRect=function() end,Register=function() end,AddCallback=function() end,
    ShowDialog=function() ui.opens=ui.opens+1 end,HideDialog=function() ui.hides=ui.hides+1 end,OnKeyboard=function() return false end,
    Show=function(self,shown) self.shown=shown end,Enable=function(self,enabled) self.enabled=enabled end,
    Update=function(self) assert(not self.stale) end,
    AttachChild=function(self,child) self.children=self.children or {};self.children[#self.children+1]=child end}
setmetatable(CUIScriptWnd,{__call=function(c) return setmetatable({},{__index=c}) end})
function class(name)
    return function(base)
        local cls=setmetatable({},{__index=base,__call=function(c,...)
            local self=setmetatable({},{__index=c}); self:__init(...);if name~="adb_mutant_v2_nicknames" then ui.last=self end;return self
        end})
        _G[name]=cls
    end
end
function super() end
function Frect() return {set=function(self) return self end} end
ui_events={LIST_ITEM_SELECT=1,BUTTON_CLICKED=2,WINDOW_KEY_PRESSED=3,WINDOW_KEY_RELEASED=4}
local function window_control()
    return {x=0,y=0,width=0,height=0,GetWndPos=function(self) return {x=self.x,y=self.y} end,
        GetWidth=function(self) return self.width end,GetHeight=function(self) return self.height end,
        SetWndPos=function(self,v) self.x,self.y=v.x,v.y end,SetWndSize=function(self,v) self.width,self.height=v.x,v.y end,
        Show=function(self,v) self.shown=v end}
end
local function text_control()
    local t=window_control()
    function t:SetText(v) self.text=v end;function t:SetTextColor(v) self.color=v end
    function t:SetEllipsis(v) self.ellipsis=v end;function t:SetFont(v) self.font=v end
    function t:SetAutoDelete() end;function t:SetTextAlignment(v) self.alignment=v end
    function t:SetTextComplexMode(v) assert(v==false) end
    return t
end
CUITextWnd=function() return text_control() end
local function list_control()
    local t=window_control();t.rows={};t.selected=0
    function t:ShowSelectedItem() end
    function t:GetSize() return #self.rows end
    function t:Clear() self.rows={} end
    function t:AddTextItem(text)
        local c=text_control();c.text=text
        local row={fields={c},GetTextItem=function() return c end,GetWidth=function() return self.width-18 end}
        function row:AddTextField(text,width) local field=text_control();field.text,field.width=text,width;self.fields[#self.fields+1]=field;return field end
        self.rows[#self.rows+1]=row;return row
    end
    function t:SetSelectedIndex(i) self.selected=i end
    function t:GetSelectedIndex() return self.selected end
    function t:GetItemByIndex(i) return self.rows[i+1] end
    return t
end
function GetFontLetterica16Russian() return "letterica16" end
CUIButton=setmetatable({},{__call=function()
    local text=text_control();local button=window_control()
    function button:TextControl() return text end;function button:SetAutoDelete() end
    function button:SetStretchTexture(v) self.stretch=v end
    function button:InitTexture(v) self.texture=v;self.texture_width=self.width end
    function button:Enable(v) self.enabled=v end
    return button
end})
local layout={}
local xml_file=assert(io.open(core.."/../configs/ui/anomaly_donation_bridge_mutant_v2.xml","rb"))
local xml_text=xml_file:read("*a");xml_file:close()
for name,attributes in xml_text:gmatch('<([%w_]+)%s+([^>]+)>') do
    local rect={};for key,value in attributes:gmatch('([%w_]+)="([^"]+)"') do rect[key]=tonumber(value) or value end
    if rect.x then layout[name]=rect end
end
local function init_layout(key,control)
    local r=assert(layout[key],key);control:SetWndPos(vector2():set(r.x,r.y));control:SetWndSize(vector2():set(r.width,r.height));return control
end
-- Stock Anomaly exports CScriptXmlInit, not ScriptXMLInit. Do not invent a
-- second alias in mocks: both the support probe and actual UI must use it.
CScriptXmlInit=setmetatable({},{__call=function()
    return {ParseFile=function(_,path) assert(path=="anomaly_donation_bridge_mutant_v2.xml");ui.xml_loads=ui.xml_loads+1 end,
        InitStatic=function(_,key) return init_layout(key,window_control()) end,
        InitTextWnd=function(_,key,parent) local c=init_layout(key,text_control());if key=="aim_label" and parent then parent.hover_mock=c end;return c end,InitListBox=function(_,key) return init_layout(key,list_control()) end,
        InitWindow=function(_,key,index,control) assert(index==0);return init_layout(key,control) end}
end})
local module=load_module(optional,"anomaly_donation_bridge_effect_hardcoon_adb_mutant_companion_v2")
module.on_game_start()
local d=assert(anomaly_donation_bridge_registry.get("hardcoon.adb.mutant_companion_v2"))
local load_world=d.on_game_load
d.on_game_load=function()
    load_world();ui.windows={};ui.labels=0
end
local real_tick=d.update
d.update=function(time) ui.frame=ui.frame+3;real_tick(time) end
assert(ScriptXMLInit==nil,"nonexistent engine API leaked into fixture")
assert(d.is_supported({actor=a}),"stock CScriptXmlInit constructor was rejected")
local stock_xml=CScriptXmlInit
CScriptXmlInit=nil
local supported,unsupported_reason=d.is_supported({actor=a})
assert(not supported and unsupported_reason=="mutant_v2_api_unsupported","missing UI API was accepted")
CScriptXmlInit=stock_xml
local release_api=xr_logic.mob_release;xr_logic.mob_release=nil
assert(not d.is_supported({actor=a}),"native combat requires the stock release API")
xr_logic.mob_release=release_api
local defaults={actor=a}
assert(d.can_execute(defaults,{mutant_sections="gigant_strong",donor_name="Defaults"}))
assert(defaults.options.health_percent==300 and defaults.options.follow_distance==15 and defaults.options.show_3d_nick==false and defaults.options.nickname_key=="NEXT","runtime spawn defaults must be 300%, 15m and hidden 3D nickname on PageDown")
assert(not d.can_execute({actor=a},{mutant_sections="gigant_strong",donor_name="test",show_3d_nick="maybe"}),"Invalid nickname checkbox accepted")
local function spawn(overrides)
    local p={mutant_sections="gigant_strong",health_percent="200",menu_key="HOME",donor_name="Донатер"}
    for k,v in pairs(overrides or {}) do p[k]=v end
    local c={actor=a}
    local ok,_,why=d.can_execute(c,p);assert(ok,why)
    local status,why2=d.execute(c); assert(status=="executed",why2)
    return 100+spawned
end
local id=spawn()
local first_pet=objects[id]
local records=data.adb_mutant_companions_v2
assert(records[tostring(id)].donor_name=="Донатер" and records[tostring(id)].health_percent==200)
assert(records[tostring(id)].show_3d_nick==false,"New pet did not persist the default-hidden label setting")
assert(records[tostring(id)].follow_distance==15 and records[tostring(id)].combat_mode=="assist","template behavior defaults changed")
local hit={draftsman=objects[id],power=1};local flags={ret_value=true}
callbacks.actor_on_before_hit(hit,0,flags);assert(flags.ret_value==true and hit.power==1,"pet friendly fire was cancelled/changed")
flags={ret_value=false};callbacks.actor_on_before_hit(hit,0,flags);assert(flags.ret_value==false,"other addon's cancelled hit was re-enabled")
local foe=object(900,"enemy",2); objects[900]=foe
hit={draftsman=foe,power=1}; flags={ret_value=true}
callbacks.actor_on_before_hit(hit,0,flags);assert(flags.ret_value==true,"enemy damage globally blocked")
callbacks.monster_on_before_hit(objects[id],hit,0,flags);assert(hit.power==0.5,"HP scale failed")
local normal=object(901,"ordinary",2);local untouched={power=1}
callbacks.monster_on_before_hit(normal,untouched,0,{ret_value=true});assert(untouched.power==1)
local ally=object(910,"npc",2);ally.stalker=true;function ally:relation() return 0 end
flags={};callbacks.on_enemy_eval(objects[id],ally,flags);assert(flags.result==false)
local v1=object(911,"v1_mutant",2);data.adb_mutant_companions_v1={[911]={donor_name="V1"}}
flags={};callbacks.on_enemy_eval(objects[id],v1,flags);assert(flags.result==false)
local before=data.adb_mutant_companions_v1
assert(module.set_command({id},"follow"))
d.update(0);assert(objects[id].current_action.kind=="move","must follow")
assert(ui.labels==0,"Default-hidden pet created a world label without hover or PageDown")
local default_toggle={ret_value=true};callbacks.on_before_key_press(DIK_keys.DIK_NEXT,nil,false,default_toggle)
assert(not default_toggle.ret_value and data.adb_companion_nickname_hud_v1.mutant_v2.NEXT==true and records[tostring(id)].show_3d_nick==false,"PageDown must enable labels without rewriting the hidden spawn default")
level.get_target_obj=function() error("World nicknames must not depend on the hover target") end
now=250;d.update(now);assert(ui.labels==1 and ui.xml_loads==1,"World/hover label HUD missing")
local nickname_window=next(ui.windows)
assert(#nickname_window.children==9)
local tag=nickname_window.children[9]
assert(tag.text==anomaly_donation_bridge_common.game_text_cp1251_lossy("Донатер") and tag.shown and tag.color==4278190080+150*65536+196*256+148)
assert(not tag.text:find("HP",1,true) and not tag.text:find("%%"),"HP leaked into world nickname")
local tag_x=tag.x
objects[id]._pos.x=objects[id]._pos.x+1;nickname_window:Update()
assert(tag.x>tag_x,"Nickname did not follow world movement between AI ticks")
objects[id]._pos.x=objects[id]._pos.x-1
level.get_target_obj=function() return nil end
now=500;d.update(now);assert(ui.labels==1 and tag.shown,"Nickname disappeared after looking away")
objects[id]._pos=pos(0,0,0);now=1000;d.update(now);assert(objects[id].current_action.kind=="idle","must use stationary idle")
assert(module.set_command({id},"stay"));assert(records[tostring(id)].anchor.x==0)
a._pos=pos(20,0,0);now=2000;d.update(now);assert(objects[id].current_action.kind=="idle","stay followed actor")
objects[id]._pos=pos(10,0,0);now=3000;d.update(now);assert(objects[id].current_action.kind=="move","stay didn't return")
assert(not module.set_command({id},"distance",51));assert(module.set_command({id},"distance",50))
assert(module.set_command({id},"combat","passive"));a.enemy=foe
objects[id]._pos=pos(0,0,0);now=4000;d.update(now);assert(objects[id].current_action.kind=="idle","passive attacked")
assert(module.set_command({id},"combat","defend"));foe._pos=pos(2,0,0)
now=5000;d.update(now);assert(objects[id].current_action.kind=="idle","defend acquired unsolicited enemy")
callbacks.monster_on_before_hit(objects[id],{power=1,draftsman=foe},0,{ret_value=true})
objects[id].enemy=foe
now=6000;d.update(now);assert(not objects[id]:get_script() and #objects[id].queue==0,"defend didn't hand retaliation to native AI")
assert(#logs==0,"routine combat/preparation produces log spam")
assert(module.set_command({id},"combat","passive"));a.enemy=nil
-- Busy inventory, PDA, registered UI: key must not interrupt them.
ui.free=false;flags={ret_value=true};callbacks.on_before_key_press(199,nil,false,flags);assert(ui.opens==0 and flags.ret_value)
ui.free=true;_GUIs.Other={};callbacks.on_before_key_press(199,nil,false,flags);assert(ui.opens==0);_GUIs={}
callbacks.on_before_key_press(199,nil,false,flags);assert(ui.opens==1 and flags.ret_value==false,"menu didn't open")
assert(ui.xml_loads==2,"menu didn't use stock XML constructor")
assert(ui.last.fields.health_value.text=="100%" and ui.last.fields.order_value.text~=nil,"separate status fields missing")
assert(not ui.last.title.text:find("V2") and ui.last.buttons.close:TextControl().text=="x","old title/close hint remained")
assert(ui.last.buttons.stay.texture=="adb_mc2_button_orange_e" and ui.last.buttons.passive.texture=="adb_mc2_button_red_e","active order/combat colors missing")
assert(ui.last.buttons.follow.texture=="adb_mc2_button_e" and ui.last.buttons.rest.texture=="adb_mc2_button_e","inactive order falsely highlighted")
assert(ui.last.fields.health_scale.text==anomaly_donation_bridge_common.game_text_cp1251_lossy("Множитель начального здоровья: 200"),"initial scale confused with current HP")
assert(#ui.last.list.rows[2].fields==3 and ui.last.list.rows[2].fields[2].text=="| gigant_strong" and
    ui.last.list.rows[2].fields[3].text=="| 100%","technical section/HP columns missing")
ui.last:OnDefend();assert(records[tostring(id)].combat_mode=="defend" and ui.last.buttons.defend.texture=="adb_mc2_button_orange_e")
ui.last:OnAssist();assert(records[tostring(id)].combat_mode=="assist" and ui.last.buttons.assist.texture=="adb_mc2_button_green_e")
ui.last:OnPassive();assert(records[tostring(id)].combat_mode=="passive" and ui.last.buttons.passive.texture=="adb_mc2_button_red_e")
ui.last:OnReset();assert(records[tostring(id)].follow_distance==15 and records[tostring(id)].initial_follow_distance==15,"reset lost template baseline")
ui.last:OnMore();assert(records[tostring(id)].follow_distance==16);ui.last:OnLess();assert(records[tostring(id)].follow_distance==15)
local old_anchor=clone(records[tostring(id)].anchor)
ui.last:OnRefresh()
assert(records[tostring(id)].order=="stay" and records[tostring(id)].anchor.x==old_anchor.x and objects[id].health==1,"refresh changed order/anchor/HP")
assert(KEYS_UNLOCK==false and Check_UI(),"other addons cannot see our menu")
ui.last:OnKeyboard(199,ui_events.WINDOW_KEY_RELEASED);assert(ui.hides==0,"opening key release instantly closed menu")
local load_flags={ret=false}
assert(callbacks.on_before_load_input,"stock F9 bypasses generic key press")
callbacks.on_before_load_input(67,901,load_flags);assert(ui.hides==1,"old HUD retained across F9")
assert(load_flags.ret==false,"load hook blocked the actual load")
assert(KEYS_UNLOCK==true and not Check_UI(),"menu leaked global input lock")
local id2=spawn({menu_key="F6",follow_distance="17",combat_mode="passive"});assert(module.set_command({id2},"follow"))
assert(records[tostring(id2)].follow_distance==17 and records[tostring(id2)].combat_mode=="passive","template behavior settings ignored")
d.on_level_changing();assert(#sim.teleports==1 and sim.teleports[1]==id2,"waiting pet teleported")
d.on_game_load();ui.frame=ui.frame+3 -- complete the simulated level/quick-load transition
objects[id]=nil;assert(module.set_command({id},"distance",15),"offline command failed")
assert(records[tostring(id)].follow_distance==15)
flags={ret_value=true};callbacks.on_before_key_press(64,nil,false,flags)
assert(ui.last.buttons.follow.texture=="adb_mc2_button_e" and ui.last.buttons.stay.texture=="adb_mc2_button_e","mixed orders have a false common highlight")
ui.last:OnReset()
assert(records[tostring(id)].follow_distance==15 and records[tostring(id2)].follow_distance==17,"all-pet reset must use each original template distance")
ui.last:OnClose();assert(module.set_command({id},"distance",15))
local save_after=clone(data)
data={};d.on_game_load();now=7000;d.update(now);assert(next(data)==nil,"before save resurrected pet")
data=save_after;d.on_game_load();now=8000;d.update(now);assert(spawned==2,"F9 duplicated spawn")
assert(data.adb_mutant_companions_v2[tostring(id)].order=="stay")
assert(data.adb_mutant_companions_v2[tostring(id2)].follow_distance==17 and
    data.adb_mutant_companions_v2[tostring(id2)].combat_mode=="passive" and
    data.adb_mutant_companions_v2[tostring(id2)].health_percent==200,"saved behavior settings reset by F9")
objects[id2].health=0
assert(module.set_command({id2},"refresh"),"dead pet command should not touch unsupported AI API")
now=9000;d.update(now);assert(data.adb_mutant_companions_v2[tostring(id2)]==nil,"dead pet remained managed")
local expected_tip="%c[255,238,153,26]"..anomaly_donation_bridge_common.game_text_cp1251_lossy("Донатер")..
    "%c[default]"..anomaly_donation_bridge_common.game_text_cp1251_lossy(" погиб")
assert(#tips==1 and tips[1]==expected_tip,"death tip must color only donor name gold and retain CP1251")
now=9250;d.update(now);assert(#tips==1,"death tip repeated after record removal")
flags={ret_value=true}; callbacks.actor_on_before_hit({draftsman=objects[id2],power=1},0,flags);assert(flags.ret_value,"dead/stale ID still protected")
-- Regression from the actual log: corpse detection asks a deceased chimera
-- for bip01_spine AFTER its party record has been retired by tick().
local retired_body=objects[id2];retired_body.stock_bone_warning=true
local bone_calls_before_corpse=original_bone_calls
for i=1,100 do
    assert(utils_obj.safe_bone_pos(retired_body,"bip01_spine")==retired_body:position(),"retired V2 corpse lost safe missing-bone fallback")
end
assert(original_bone_calls==bone_calls_before_corpse and invalid_native_calls.bone_warning==0,
    "retired V2 body delegates a missing human bone to the noisy stock fallback")
retired_body.bones={existing_monster_bone=7}
assert(utils_obj.safe_bone_pos(retired_body,"existing_monster_bone").y==2,"retired corpse's real bone was replaced by its origin")
local corpse_tips,corpse_spawns=#tips,spawned
d.on_game_load() -- a save may contain the body but no companion record
assert(utils_obj.safe_bone_pos(retired_body,"bip01_spine")==retired_body:position() and
    data.adb_mutant_companions_v2[tostring(id2)]==nil and #tips==corpse_tips and spawned==corpse_spawns,
    "F9 reintroduced a dead companion or lost corpse compatibility")
local unrelated_reused=object(id2,"ordinary_recycled_npc",3)
local reused_pos,reused_extra=utils_obj.safe_bone_pos(unrelated_reused,"bip01_spine","recycled")
assert(reused_pos==unrelated_reused:position() and reused_extra=="recycled" and original_bone_calls==bone_calls_before_corpse+1,
    "corpse compatibility leaked through a recycled ID")
-- Restore counters used by later live-pet checks; native warning count remains
-- cumulative and must stay zero through all lifecycle tests.
original_bone_calls=0
config[alias].radiation_max_power="1"
local ok,_,why=d.can_execute({actor=a},{donor_name="X",mutant_sections="gigant_strong"})
assert(not ok and why=="mutant_v2_unsafe_aura_configuration")
config[alias].radiation_max_power="0";local saved_profile=config[alias];config[alias]=nil
ok,_,why=d.can_execute({actor=a},{donor_name="X",mutant_sections="gigant_strong"});assert(not ok and why:find("restart_game"))
config[alias]=saved_profile
assert(before[911].donor_name=="V1","V1 state changed")
-- Navigation regressions: accessible offset, stable orders, blocked action,
-- changed destination, immediate wait, manual refresh and unreachable goal.
objects[id]=first_pet;first_pet._pos=pos(40,0,0);a._pos=pos()
assert(module.set_command({id},"follow"))
now=10000;d.update(now)
assert(first_pet.current_action.kind=="move" and #first_pet.queue==1)
local first_goal=first_pet.current_action.args[2]
assert(first_goal~=a:level_vertex_id(),"pet aims at actor's occupied vertex")
local sent=first_pet.commands
now=10250;d.update(now);assert(first_pet.commands==sent,"unchanged path churns every tick")
now=15000;d.update(now);assert(#first_pet.queue==0,"stalled native action not cancelled")
now=15250;d.update(now)
assert(#first_pet.queue==1 and first_pet.current_action.args[2]~=first_goal,"stuck follower reused rejected assist point")
first_pet._pos=pos(35,0,0);a._pos=pos(-6,0,0)
now=17000;d.update(now);assert(#first_pet.queue==1,"moving target appended another native action")
assert(module.set_command({id},"stay"));now=17250;d.update(now)
assert(first_pet.current_action.kind=="idle" and #first_pet.queue==1,"stay queued behind obsolete follow")
first_pet._pos=pos(40,0,0);now=18000;d.update(now)
assert(first_pet.current_action.kind=="move","waiting pet did not return to anchor")
local saved_order=clone(data.adb_mutant_companions_v2[tostring(id)])
assert(module.set_command({id},"refresh"))
assert(data.adb_mutant_companions_v2[tostring(id)].anchor.x==saved_order.anchor.x and
    data.adb_mutant_companions_v2[tostring(id)].follow_distance==saved_order.follow_distance)
first_pet.no_access=true;assert(module.set_command({id},"follow"));now=19000;d.update(now)
assert(first_pet.current_action.kind=="idle","no accessible goal should idle, not walk into a wall")
assert(#first_pet.queue==1 and first_pet.health==1 and spawned==2,"repath healed/respawned pet")
first_pet.no_access=false;assert(module.set_command({id},"follow"))
local teleports_before=#sim.teleports
for t=20000,47000,250 do now=t;d.update(now);assert(#first_pet.queue<=1,"recovery grew native action queue") end
assert(#sim.teleports==teleports_before+1 and sim.teleports[#sim.teleports]==id,"stock in-place physical reset not bounded/scoped")
assert(first_pet._pos.x==40 and first_pet._pos.z==0,"recovery teleported through an obstacle")
-- Native per-monster camp, bounded rest/chase and save/load lifecycle.
local save_before_camp=clone(data)
a._pos=pos();first_pet._pos=pos(40,0,0)
assert(module.set_command({id},"rest"))
local camp=data.adb_mutant_companions_v2[tostring(id)].camp
assert(camp.radius==30 and camp.x==0,"camp should be anchored at actor's current location")
now=48000;d.update(now);assert(first_pet.home and first_pet.current_action.kind=="move","out-of-camp pet didn't return")
first_pet._pos=pos(0,0,0);now=48250;d.update(now);assert(first_pet.current_action.kind=="rest","rest does not use native act.rest")
local rest_commands=first_pet.commands;local homes=first_pet.home_sets
a._pos=pos(500,0,0);now=49000;d.update(now)
assert(first_pet.current_action.kind=="rest" and first_pet.commands==rest_commands and first_pet.home_sets==homes,"camp follows actor or rest timers reset every tick")
assert(module.set_command({id},"combat","assist"));foe._pos=pos(70,0,0)
first_pet.visible={[900]=true};first_pet.visible_objects={foe}
now=49250;d.update(now);assert(first_pet.current_action.kind=="rest","camp chased an enemy outside 30m")
flags={};callbacks.on_enemy_eval(first_pet,foe,flags);assert(flags.result==false,"camp enemy evaluation ignored leash")
foe._pos=pos(10,0,0);first_pet.enemy=foe
now=49500;d.update(now);assert(not first_pet:get_script() and #first_pet.queue==0,"camp lost normal in-radius native combat")
first_pet.visible_objects={};first_pet._pos=pos(31,0,0);now=49750;d.update(now);assert(first_pet.current_action.kind=="move")
first_pet._pos=pos(26,0,0);now=50000;d.update(now);assert(first_pet.current_action.kind=="move","camp border oscillates every tick")
first_pet._pos=pos(23,0,0);now=50250;d.update(now);assert(first_pet.current_action.kind=="rest")
local rest_save=clone(data);local old_camp_vertex=camp.vertex;local hp_before=first_pet.health
assert(module.set_command({id},"refresh"));now=50500;d.update(now)
assert(data.adb_mutant_companions_v2[tostring(id)].camp.vertex==old_camp_vertex and first_pet.health==hp_before,"refresh reset camp or healed pet")
data=rest_save;first_pet.home=nil;d.on_game_load();now=51000;d.update(now)
assert(first_pet.home and first_pet.home.vertex==old_camp_vertex and data.adb_mutant_companions_v2[tostring(id)].order=="rest","F9 lost native camp")
data=save_before_camp;d.on_game_load();now=51250;d.update(now)
assert(first_pet.home==nil and data.adb_mutant_companions_v2[tostring(id)].camp==nil,"loading before camp replayed later rest state")
data=rest_save;d.on_game_load();now=51500;d.update(now);assert(first_pet.home,"rest save stopped restoring after a pre-camp load")
local teleports_in_rest=#sim.teleports;d.on_level_changing();assert(#sim.teleports==teleports_in_rest,"resting pet moved to next level")
assert(module.set_command({id},"follow"));assert(first_pet.home==nil and data.adb_mutant_companions_v2[tostring(id)].camp==nil,"follow didn't remove camp")
objects[id]=nil;assert(module.set_command({id},"rest"))
assert(data.adb_mutant_companions_v2[tostring(id)].camp.x==servers[id].position.x,"offline camp used actor vertex on another level")
objects[id]=first_pet;first_pet._pos=pos(20,0,0);now=52000;d.update(now);assert(first_pet.home,"offline camp didn't restore on online")
assert(module.set_command({id},"stay"));assert(first_pet.home==nil and data.adb_mutant_companions_v2[tostring(id)].camp==nil,"stay left a roaming home")
local legacy=data.adb_mutant_companions_v2[tostring(id)];legacy.initial_follow_distance=nil;legacy.follow_distance=21
assert(module.set_command({id},"distance_reset"));assert(legacy.follow_distance==21 and legacy.initial_follow_distance==21,"legacy reset invented unknown template value")
assert(first_pet.health==hp_before and spawned==2 and before[911].donor_name=="V1","camp changed HP, spawned objects or touched V1")
-- The native binder runs mob_release AFTER the public monster_on_update hook.
-- Cached enemies must not take control away from a passive follower.
a._pos=pos();a.enemy=nil;first_pet._pos=pos(40,0,0)
assert(module.set_command({id},"combat","passive") and module.set_command({id},"follow"))
now=54000;d.update(now)
assert(first_pet.enemy_filter and first_pet.enemy_filter(first_pet,foe)==false,"native monster memory bypassed passive combat filter")
local passive_commands=first_pet.commands
for t=54250,55250,250 do
    first_pet.enemy=foe
    callbacks.monster_on_update(first_pet)
    xr_logic.mob_release(first_pet)
    assert(first_pet:get_script(),"stock binder released our follower on a cached enemy")
    now=t;d.update(now)
    assert(first_pet.current_action.kind=="move" and #first_pet.queue==1,"passive follower stopped to fight")
end
assert(first_pet.commands==passive_commands,"cached combat enemy restarted follow every tick")
normal.script_control=true;xr_logic.mob_release(normal);assert(not normal:get_script() and normal.releases==1,"ordinary monster release was intercepted")
-- Native useful() gates species BEFORE Lua. The private community admits an
-- arbitrary actor-hostile mutant; a Lua callback alone cannot do that.
local dog=object(920,"dog_weak",35);dog.monster=true;function dog:relation() return 2 end;objects[920]=dog
first_pet.enemy=nil;first_pet.visible={[920]=true};first_pet.visible_objects={dog}
assert(module.set_command({id},"combat","assist"))
now=55500;d.update(now)
local function native_select(pet,candidate,native_relation)
    if native_relation<2 then return false end -- engine rejects before callback
    if pet.enemy_filter(pet,candidate) then pet.enemy=candidate;return true end
    return false
end
assert(not native_select(first_pet,dog,0) and first_pet.enemy==nil,"neutral native relation was bypassed by Lua")
assert(config[alias].species=="adb_mc2_companion" and native_select(first_pet,dog,2))
now=55550;d.update(now) -- allow next tick below to adopt native memory
now=55600;callbacks.monster_on_update(first_pet)
now=55750;d.update(now)
assert(not first_pet:get_script() and #first_pet.queue==0,"private native enemy didn't receive native Think ownership")
local native_commands,native_captures,native_resets=first_pet.commands,first_pet.captures,first_pet.resets
xr_logic.mob_release(first_pet)
assert(not first_pet:get_script() and first_pet.commands==native_commands and first_pet.captures==native_captures and
    first_pet.resets==native_resets,"binder/update restarted native attack")
assert(first_pet.enemy_filter(first_pet,a)==false and first_pet.enemy_filter(first_pet,v1)==false,"native filter accepted actor/companion")
assert(module.set_command({id},"combat","defend"));now=56000;d.update(now)
assert(first_pet.current_action.kind=="move","defense proactively attacks visible dogs")
assert(module.set_command({id},"combat","assist"));first_pet.visible[920]=false;now=56250;d.update(now)
assert(first_pet.current_action.kind=="move","pet sees a dog through walls")
first_pet.visible[920]=true;first_pet.enemy=dog;function dog:relation() return 0 end;now=56500;d.update(now)
assert(first_pet.current_action.kind=="move","neutral modded monster was treated as actor-hostile")
local custom=object(921,"completely_unknown_mod_creature",34);objects[921]=custom
first_pet.visible[921]=true;first_pet.visible_objects={dog,custom}
first_pet.enemy=custom;now=56750;d.update(now)
assert(not first_pet:get_script() and #first_pet.queue==0,"native combat depends on a known monster name/class list")
first_pet.visible_objects={}
-- CEntity turrets can report alive(), but are NOT CEntityAlive. Props also
-- occur in visible memory. Neither may reach relation()/AI/alive-only APIs.
local turret=object(922,"unrelated_stationary_weapon",39);turret.nonliving=true
local prop=object(923,"unrelated_physics_prop",39);prop.nonliving=true
function prop:alive() invalid_native_calls.prop_alive=invalid_native_calls.prop_alive+1;return false end
assert(module.set_command({id},"follow"))
first_pet.visible[922],first_pet.visible[923]=true,true
first_pet.visible_objects={turret,prop};now=57000;d.update(now)
assert(first_pet.current_action.kind=="move","non-living visible object became a combat target")
assert(first_pet.enemy_filter(first_pet,turret)==false,"native enemy filter accepted non-living entity")
callbacks.monster_on_before_hit(first_pet,{power=1,draftsman=prop},0,{ret_value=true})
first_pet.visible_objects={}
first_pet.enemy=custom;now=57250;d.update(now)
assert(not first_pet:get_script() and #first_pet.queue==0,"valid pet-native enemy memory stopped working")
assert(module.set_command({id},"combat","passive"));now=57500;d.update(now)
assert(first_pet.current_action.kind=="move" and first_pet.enemy==nil,"ignore combat retained stale native enemy")
-- Model the stock binder order: monster_on_update then mob_release. The native
-- Think path runs ONLY without script capture; mock action() isn't an attack.
-- Check continuous native ownership AND automatic resumption after combat.
first_pet.visible={[921]=true};first_pet.visible_objects={custom};first_pet.enemy=custom
assert(module.set_command({id},"combat","assist") and module.set_command({id},"follow"))
first_pet.enemy=custom
now=58000;d.update(now)
local attack_commands,attack_resets,attack_captures=first_pet.commands,first_pet.resets,first_pet.captures
assert(not first_pet:get_script() and first_pet.current_action==nil and #first_pet.queue==0)
local native_thinks=0
for t=58250,63000,250 do
    now=t;callbacks.monster_on_update(first_pet);xr_logic.mob_release(first_pet);d.update(now)
    if not first_pet:get_script() then native_thinks=native_thinks+1 end
    assert(first_pet.current_action==nil and first_pet.commands==attack_commands and first_pet.resets==attack_resets and first_pet.captures==attack_captures,
        "binder/update captured or reset native combat before it could finish")
    assert(#first_pet.queue==0 and not first_pet:get_script(),"combat retained script ownership")
end
assert(native_thinks==20,"native Think never ran (queued actions alone do not prove combat)")
-- Native callback keeps one legal target instead of accepting every closer foe.
local rival=object(924,"another_mod_enemy",39);objects[924]=rival
first_pet.visible[924]=true;first_pet.visible_objects={rival,custom}
for t=63250,64000,250 do
    now=t;assert(not first_pet.enemy_filter(first_pet,rival),"closer alternative churned a valid native target")
    d.update(now)
    assert(not first_pet:get_script() and first_pet.resets==attack_resets and first_pet.captures==attack_captures,
        "native target change restarted combat")
end
custom.health=0;first_pet.enemy=nil;first_pet.visible[921]=false;first_pet.visible_objects={}
now=64250;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move" and data.adb_mutant_companions_v2[tostring(id)].order=="follow",
    "native combat ended but follower needs a manual follow command")
local resumed_commands=first_pet.commands
now=64500;callbacks.monster_on_update(first_pet);xr_logic.mob_release(first_pet);d.update(now)
assert(first_pet:get_script() and first_pet.commands==resumed_commands,"resumed follow churns or loses control")
custom.health=1;first_pet.enemy=custom;now=64750;d.update(now);assert(not first_pet:get_script())
custom.health=0;now=65000;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move","dead native enemy didn't automatically resume follow")
custom.health=1;first_pet.enemy=custom;now=65250;d.update(now);assert(not first_pet:get_script())
assert(module.set_command({id},"combat","passive"));now=65500;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move","passive order didn't immediately reclaim control")
assert(module.set_command({id},"combat","assist"));first_pet.enemy=custom;now=65750;d.update(now)
assert(not first_pet:get_script())
first_pet:reset_action_queue();now=66000;d.update(now)
assert(not first_pet:get_script() and #first_pet.queue==0,"empty queue during native combat was mistaken for lost action")
function custom:relation() return 0 end
now=66250;d.update(now);assert(first_pet:get_script() and first_pet.current_action.kind=="move","newly friendly native enemy retained combat ownership")
function custom:relation() return 2 end
assert(module.set_command({id},"combat","defend"))
callbacks.monster_on_before_hit(first_pet,{power=1,draftsman=custom},0,{ret_value=true})
first_pet.enemy=custom;now=66500;d.update(now);assert(not first_pet:get_script())
now=82000;d.update(now);assert(first_pet:get_script() and first_pet.current_action.kind=="move","defense stayed native after threat expiry")
-- Follow leash must recall all the way to the leader, without repeated clicks
-- or oscillation around the maximum combat distance.
assert(module.set_command({id},"combat","assist"));first_pet.enemy=custom;now=82250;d.update(now)
a._pos=pos(-70,0,0);now=82500;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move","combat ignored follow leash")
first_pet._pos=pos(20,0,0);first_pet.enemy=custom;now=82750;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move","recalled follower resumed fighting at 100m boundary")
first_pet._pos=pos(-60,0,0);first_pet.enemy=nil;now=83000;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="idle","recall didn't finish near leader")
-- Waiting and resting resume their own order, not an unconditional follow.
a._pos=pos();first_pet._pos=pos(0,0,0);custom._pos=pos(2,0,0)
assert(module.set_command({id},"stay"));first_pet.enemy=custom;now=83250;d.update(now);assert(not first_pet:get_script())
first_pet._pos=pos(4,0,0);custom.health=0;now=83500;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move" and data.adb_mutant_companions_v2[tostring(id)].order=="stay","battle forgot wait anchor")
custom.health=1;first_pet._pos=pos();assert(module.set_command({id},"rest"));first_pet.enemy=custom;now=83750;d.update(now)
assert(not first_pet:get_script());custom.health=0;first_pet.enemy=nil;now=84000;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="rest" and first_pet.home,"battle forgot rest camp")
-- F9 restores saved orders, not transient native-combat ownership from the
-- later timeline. Reloading a combat save must also permit a normal return.
custom.health=1;first_pet.enemy=custom;now=84250;d.update(now);assert(not first_pet:get_script())
local combat_save=clone(data)
d.on_game_load();now=84500;d.update(now)
assert(not first_pet:get_script() and spawned==2,"loading active native combat captured/duplicated pet")
custom.health=0;now=84750;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="rest","loaded battle didn't resume camp after enemy death")
custom.health=1
assert(module.set_command({id},"combat","passive"))
local passive_save=clone(data)
assert(module.set_command({id},"combat","assist"));first_pet.enemy=custom;now=85000;d.update(now);assert(not first_pet:get_script())
data=passive_save;d.on_game_load();now=85250;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="rest","load leaked later native combat into passive save")
data=combat_save;first_pet.enemy=nil;d.on_game_load();now=85500;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="rest" and spawned==2,"saved combat ownership was replayed without a native enemy")
assert(module.set_command({id},"follow"));first_pet._pos=pos(40,0,0)
assert(module.set_command({id},"combat","passive"))
first_pet.visible_objects={};first_pet.enemy=nil
-- Missing human bones must not reach native bone_position, while existing
-- bones and all non-V2 calls retain their normal behavior and arguments.
assert(utils_obj.safe_bone_pos(first_pet,"bip01_spine").x==40 and original_bone_calls==0,"V2 missing human spine didn't use safe position")
first_pet.bones={spine=7};assert(utils_obj.safe_bone_pos(first_pet,"spine").y==2,"existing monster bone was not used")
local ordinary_pos,extra=utils_obj.safe_bone_pos(normal,"bip01_spine","untouched")
assert(ordinary_pos==normal:position() and extra=="untouched" and original_bone_calls==1,"ordinary target bone handling changed")
-- Geometry proof with real XML rects: one physical scale on both axes, centered
-- panel, backgrounds matching hit boxes, and list columns within the row.
for _,resolution in ipairs({{1024,768},{1280,1024},{1920,1080},{2560,1080},{3440,1440},{5120,1440},{3840,2160}}) do
    ui.width,ui.height=resolution[1],resolution[2]
    local menu=anomaly_donation_bridge_mutant_v2_menu()
    local k=math.min(1,(ui.height/ui.width)/(768/1024))
    assert(math.abs(menu.aspect_k-k)<0.000001)
    assert(math.abs(menu.panel.x+menu.panel.width/2-512)<0.0001,"wide panel lost centering")
    local b=menu.buttons.follow
    assert(math.abs(b.width-124*k)<0.0001 and b.height==32 and b.texture_width==b.width and b.stretch,"visual button and hit area disagree")
    if ui.width/ui.height>=4/3 then
        assert(math.abs((b.width*ui.width/1024)/(b.height*ui.height/768)-124/32)<0.0001,"button still stretched on widescreen")
    end
    local row=menu.list.rows[2]
    assert(row.fields[1].width+row.fields[2].width+row.fields[3].width<=row:GetWidth(),"HP column overflows wide list")
    local scale=menu.panel.width;menu:Refresh();menu:Refresh()
    assert(menu.panel.width==scale,"repeated refresh shrinks the menu")
    menu:OnClose()
end
ui.width,ui.height=1024,768
local page_records=data.adb_mutant_companions_v2
for extra=1,25 do page_records[tostring(1000+extra)]=clone(page_records[tostring(id)]) end
ui.width,ui.height=3440,1440
local pages=anomaly_donation_bridge_mutant_v2_menu()
assert(pages.pages==3 and #pages:Targets()==26 and pages.list:GetSize()==11,"large party overflowed fixed native viewport")
assert(pages.buttons.next_page.shown and pages.fields.page_number.text=="1 / 3")
pages:OnNextPage();pages.list:SetSelectedIndex(1);pages:OnSelect()
assert(pages.selected==pages.ids[11] and #pages:Targets()==1,"paged selection targets wrong pet")
local selected=pages.selected;pages:OnNextPage()
assert(pages.list:GetSize()==7 and pages.selected==selected,"paging silently changed command target")
assert(pages.list:GetSelectedIndex()==4294967295,"all-pets row is highlighted while a hidden individual is selected")
pages.list:SetSelectedIndex(0);pages:OnSelect();assert(#pages:Targets()==26,"all pets means only visible page")
pages:OnPreviousPage();assert(pages.fields.page_number.text=="2 / 3")
pages:OnClose();for extra=1,25 do page_records[tostring(1000+extra)]=nil end
ui.width,ui.height=1024,768
-- Profile 5 keeps native attacks with an installed private relations table.
-- old profile must ask for regeneration/restart rather than silently nerf pets.
anomaly_donation_bridge_catalog.validate_sections=function(params) return {{section_id=params.mutant_sections}} end
config.adb_mc2_presence_neutral={}
local function native_profile(section,native)
    config[section]={class=native,script_binding="bind_monster.bind"}
    local alias="adb_mc2_"..anomaly_donation_bridge_sha256.digest(section):sub(1,24)
    local values={class=native,species="adb_mc2_companion",adb_mc2_source=section,adb_mc2_profile="5",adb_mc2_native_class=native}
    for _,aura in ipairs({"psy","fire","radiation","base"}) do
        for _,key in ipairs({"linear_factor","quadratic_factor","max_power","max_distance"}) do values[aura.."_"..key]="0" end
    end
    config[alias]=values
    return values
end
local burer=native_profile("unknown_mod_burer","SM_BURER")
burer.Gravi_MaxDist,burer.tele_max_distance,burer.Tele_Find_Radius="18","30","20"
assert(d.can_execute({actor=a},{mutant_sections="unknown_mod_burer",donor_name="Native attacks"}),"restored burer attacks were rejected as unsafe")
burer.adb_mc2_profile="1"
local profile_ok,_,profile_reason=d.can_execute({actor=a},{mutant_sections="unknown_mod_burer",donor_name="Old profile"})
assert(not profile_ok and profile_reason:find("restart_game"),"old profile didn't request config regeneration and restart")
for _,profile in ipairs({"2","3","4"}) do
    burer.adb_mc2_profile=profile
    assert(not d.can_execute({actor=a},{mutant_sections="unknown_mod_burer",donor_name="Bootstrap"}),"unprepared legacy profile spawned")
end
burer.adb_mc2_profile="5"
anomaly_donation_bridge_catalog.companion_relations_ready=function() return false end
assert(not d.can_execute({actor=a},{mutant_sections="unknown_mod_burer",donor_name="No relations"}),"unprepared native table bypassed restart check")
anomaly_donation_bridge_catalog.companion_relations_ready=function() return true end
local psy=native_profile("unknown_mod_psy","SM_DOG_P")
psy.aura_effector,psy.Phantoms_Count="adb_mc2_presence_neutral","4"
local psy_context={actor=a}
assert(d.can_execute(psy_context,{mutant_sections="unknown_mod_psy",donor_name="Psy"}) and psy_context.options.native_class=="SM_DOG_P",
    "native psy dog/phantoms were replaced with an ordinary pseudodog")
psy.class="SM_P_DOG"
assert(not d.can_execute({actor=a},{mutant_sections="unknown_mod_psy",donor_name="Wrong class"}),"old class replacement was accepted")
psy.class="SM_DOG_P"
local polter=native_profile("unknown_mod_polter","SM_POLTR")
polter.detection_pp_effector_name,polter.flame_scan_effector_section="","adb_mc2_presence_neutral"
polter.detection_max_level,polter.detection_success_level,polter.flames_count="100","4","5"
local polter_id=spawn({mutant_sections="unknown_mod_polter"})
local pet_polter=objects[polter_id];pet_polter.actor_ignore=true
callbacks.monster_on_update(pet_polter)
assert(pet_polter.actor_ignore==true,"poltergeist special attacks ignored non-combat follow order")
pet_polter.enemy=custom;now=85750;d.update(now)
assert(not pet_polter:get_script() and pet_polter.actor_ignore==false,"old poltergeist flag kept native abilities disabled during combat")
assert(module.set_command({polter_id},"combat","passive"))
assert(pet_polter.actor_ignore==true,"poltergeist native abilities bypassed passive command")
local wild_polter=object(9999,"wild_mod_polter",0);wild_polter.actor_ignore=true
callbacks.monster_on_update(wild_polter)
assert(wild_polter.actor_ignore==true,"ordinary poltergeist was modified")
polter.detection_pp_effector_name="unwanted_presence_overlay"
assert(not d.can_execute({actor=a},{mutant_sections="unknown_mod_polter",donor_name="Presence"}),"poltergeist presence overlay was allowed")
assert(before[911].donor_name=="V1","V1 modified while restoring native attacks")
-- A private-looking prefix alone is not proof of ownership. Neither a foreign
-- object nor a forged/mismatched config may be intercepted.
local fake_section="adb_mc2_"..string.rep("a",24)
config[fake_section]={adb_mc2_source="unrelated_source",adb_mc2_native_class="SM_CHIMS",adb_mc2_profile="2"}
local foreign=object(9988,fake_section,8)
local calls_before_foreign=original_bone_calls
local foreign_pos,foreign_extra=utils_obj.safe_bone_pos(foreign,"bip01_spine","foreign")
assert(foreign_pos==foreign:position() and foreign_extra=="foreign" and original_bone_calls==calls_before_foreign+1,
    "private-looking foreign section was intercepted")
local private_prop=object(9989,pet_polter:section(),8);private_prop.nonliving=true
utils_obj.safe_bone_pos(private_prop,"bip01_spine")
assert(original_bone_calls==calls_before_foreign+2,"non-entity object received corpse-only handling")
-- Configuration recognition works even when the save has NO V2 party table.
local party_before_corpse_load=data
data={};d.on_game_load()
local polter_body=object(9990,pet_polter:section(),8);polter_body.health=0;polter_body.stock_bone_warning=true
local calls_before_empty=original_bone_calls
assert(utils_obj.safe_bone_pos(polter_body,"bip01_spine")==polter_body:position() and
    original_bone_calls==calls_before_empty and next(data)==nil,"corpse fallback depends on or creates saved party state")
data=party_before_corpse_load;d.on_game_load()
for name,count in pairs(invalid_native_calls) do assert(count==0,"unsupported native call reached engine: "..name.."="..count) end
-- Native battle survives brief empty memory and does not fall back to transient
-- act.attack, recapture a slow attack, or spam every enemy transition.
objects[id]=first_pet;a._pos=pos();first_pet._pos=pos(20,0,0)
local explicit=object(1200,"unknown_pack_creature_987",28);objects[1200]=explicit
local closer=object(1201,"other_unknown_pack_creature",21);objects[1201]=closer
first_pet.enemy=nil;first_pet.visible={[1200]=true};first_pet.visible_objects={explicit}
assert(module.set_command({id},"follow") and module.set_command({id},"combat","assist") and module.set_command({id},"distance",15))
now=200000;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move","visible object was forged into a native enemy")
first_pet.enemy=explicit;now=200250;d.update(now)
assert(not first_pet:get_script() and first_pet.current_action==nil)
local start_commands,start_resets,start_captures=first_pet.commands,first_pet.resets,first_pet.captures
local start_logs=#logs
local function native_step(t)
    now=t
    callbacks.monster_on_update(first_pet);xr_logic.mob_release(first_pet);d.update(t)
    assert(not first_pet:get_script() and #first_pet.queue==0 and first_pet.commands==start_commands and
        first_pet.resets==start_resets and first_pet.captures==start_captures,"native battle recaptured/reset or queued act.attack")
end
for t=200500,205000,250 do native_step(t) end
first_pet.visible[1201]=true;first_pet.visible_objects={closer,explicit}
assert(not first_pet.enemy_filter(first_pet,closer),"closer foe cancelled a legal attack")
first_pet.enemy=nil;first_pet.visible[1200]=false
for t=205250,206500,250 do native_step(t) end
first_pet.enemy=explicit;first_pet.visible[1200]=true
for t=206750,230000,250 do native_step(t) end
for i=start_logs+1,#logs do
    assert(not logs[i]:find("native combat",1,true) and not logs[i]:find("act.attack",1,true) and
        not logs[i]:find("prepared:",1,true),"ordinary native battle emits a stream of info messages")
end
-- Lost memory is bounded: no endless combat when the engine has no target.
first_pet.enemy=nil;first_pet.visible[1200]=false
native_step(230250)
now=231750;d.update(now)
assert(first_pet:get_script() and first_pet.current_action.kind=="move","expired native memory didn't resume follow")
-- Diplomacy/commands/death bypass the grace period immediately.
first_pet.enemy=explicit;first_pet.visible[1200]=true;now=232000;d.update(now)
assert(not first_pet:get_script())
function explicit:relation() return 0 end
now=232250;d.update(now);assert(first_pet.current_action.kind=="move","friendly target was still attacked")
function explicit:relation() return 2 end
first_pet.enemy=explicit;now=232500;d.update(now);assert(not first_pet:get_script())
local saved_order=clone(data)
assert(module.set_command({id},"combat","passive"))
d.on_game_load();data=saved_order;first_pet.visible[1200]=false;first_pet.visible_objects={};first_pet.enemy=nil
now=233000;d.update(now)
assert(first_pet.current_action.kind=="move" and spawned==3,"F9 replayed an unsaved attack or respawned a pet")
assert(before[911].donor_name=="V1")
for name,value in pairs(invalid_native_calls) do assert(value==0,"new native API misuse: "..name) end
assert(#logs==0,"routine V2 updates, repathing and manual refresh still produce debug log spam")
-- All owned online pets get nick-only labels, independently of hover and AI.
do
    data={};d.on_game_load();a.health=1
    ui.width,ui.height=1920,1080
    camera.pos=pos(20,1.6,-15)
    local left,right=spawn({donor_name="Левый",nickname_visibility="show"}),spawn({donor_name="Правый",nickname_visibility="show"})
    objects[left]._pos=pos(19,0,0);objects[right]._pos=pos(21,0,0);objects[right].visual_height=4
    now=240000;d.update(now)
    local wnd=next(ui.windows);assert(wnd and #wnd.children==18)
    local function visible_tags()
        local result={};for i=9,#wnd.children,9 do local c=wnd.children[i];if c.shown then result[c.text]=c end end
        return result
    end
    local left_name=anomaly_donation_bridge_common.game_text_cp1251_lossy("Левый")
    local right_name=anomaly_donation_bridge_common.game_text_cp1251_lossy("Правый")
    local tags=visible_tags()
    assert(tags[left_name] and tags[right_name] and tags[left_name].x<tags[right_name].x)
    assert(tags[right_name].y<tags[left_name].y,"Different skeleton heights ignored")
    assert(math.abs(tags[left_name].width-320*0.75)<0.001,"World nickname stretched on widescreen")
    objects[left]._pos.z=-20;wnd:Update();tags=visible_tags();assert(not tags[left_name] and tags[right_name])
    objects[left]._pos=pos(10000,0,0);wnd:Update();assert(not visible_tags()[left_name],"Offscreen nickname remained")
    objects[left]._pos=pos(19,0,0);objects[left].health=0;wnd:Update();assert(not visible_tags()[left_name],"Dead pet nickname remained")
    objects[left].health=1;objects[left]._name="reused_id";wnd:Update();assert(not visible_tags()[left_name],"Recycled ID inherited nickname")
    local other=objects[right];objects[right]=nil;wnd:Update();assert(next(visible_tags())==nil,"Offline pet nickname remained")
    objects[right]=other;wnd:Update();assert(visible_tags()[right_name])
    local saved=clone(data);d.on_game_load();data=saved
    wnd:Update() -- abandoned pre-F9 window must not update / touch its controls
    now=240001;d.update(now);assert(ui.labels==1 and next(ui.windows)~=wnd)
    callbacks.on_before_key_press(DIK_keys.DIK_HOME,nil,false,{ret_value=true})
    assert(ui.labels==0,"World nicknames overlapped the command menu");ui.last:OnClose()
    now=240002;d.update(now);assert(ui.labels==1)
    d.on_level_changing();assert(ui.labels==0,"Nickname HUD leaked across level transition")
end
-- Hover name+HP is independent of the per-spawn 3D nickname checkbox.
do
    data={};d.on_game_load();a.health=1
    local id=spawn({donor_name="Без 3D",show_3d_nick="false"})
    assert(data.adb_mutant_companions_v2[tostring(id)].show_3d_nick==false)
    objects[id]._pos=pos(20,0,0);objects[id].health=.63
    level.get_target_obj=function() return objects[id] end
    now=250000;d.update(now)
    local wnd=next(ui.windows);assert(wnd and #(wnd.children or {})==0,"Disabled world nickname was rendered")
    assert(wnd.hover_mock.shown and wnd.hover_mock.text==anomaly_donation_bridge_common.game_text_cp1251_lossy("Без 3D: 63% HP"))
    level.get_target_obj=function() return nil end;wnd:Update();assert(not wnd.hover_mock.shown)
    -- F9 happens before load-state callback. It must detach and prevent a
    -- new raw CDialogHolder pointer even while the old actor still exists.
    callbacks.on_before_load_input();assert(ui.labels==0 and not wnd.shown and wnd.enabled==false)
    for i=1,8 do ui.frame=ui.frame+1;real_tick(now+i);wnd:Update();assert(ui.labels==0) end
    d.on_game_load();d.update(now+20);assert(ui.labels==0)
    level.get_target_obj=function() return objects[id] end;d.update(now+21);assert(ui.labels==1)
    local latest=next(ui.windows)
    -- Console/menu load without the key callback must retire the passive
    -- Lua-owned window from the CURRENT native holder before losing refs.
    d.on_game_load();assert(not latest.shown and latest.enabled==false and ui.labels==0)
    ui.paused=true;d.update(now+22);assert(ui.labels==0);ui.paused=false
    local function save_and_drop()
        d.update(now+23)
        local passive=next(ui.windows);assert(passive)
        local weak=setmetatable({passive},{__mode="v"})
        callbacks.on_before_save_input()
        assert(ui.labels==0 and passive.enabled==false and not passive.shown)
        return weak
    end
    local weak=save_and_drop();collectgarbage("collect")
    assert(weak[1],"Lua freed a passive window before CDialogHolder's deferred sweep")
    ui.frame=ui.frame+5;real_tick(now+24);collectgarbage("collect")
    assert(weak[1]==nil,"Retired HUD window leaked beyond its safe frame boundary")
    d.on_game_load()
end
-- Each preset's saved key operates only its own nickname group/key, and
-- runtime visibility survives loading without changing initial spawn defaults.
do
    data={};d.on_game_load();ui.frame=ui.frame+10
    local first=spawn({donor_name="End",nickname_visibility="show",nickname_key="END"})
    local second=spawn({donor_name="F6",nickname_visibility="hide",nickname_key="F6"})
    local third=spawn({donor_name="PageDown"})
    objects[first]._pos=pos(19,0,0);objects[second]._pos=pos(21,0,0);objects[third]._pos=pos(23,0,0)
    level.get_target_obj=function() return objects[first] end
    now=255000;d.update(now)
    local function shown_nicks()
        local result={}
        for wnd in pairs(ui.windows) do
            for i=9,#(wnd.children or {}),9 do local c=wnd.children[i];if c.shown then result[c.text]=true end end
        end
        return result
    end
    local untouched={ret_value=true};callbacks.on_before_key_press(DIK_keys.DIK_PRIOR,nil,false,untouched)
    assert(untouched.ret_value and not shown_nicks().PageDown,"NPC default PageUp must not enable hidden mutant labels")
    local down={ret_value=true};callbacks.on_before_key_press(DIK_keys.DIK_NEXT,nil,false,down);d.update(now)
    assert(not down.ret_value and shown_nicks().PageDown and shown_nicks().End,"PageDown must enable only default-bound mutant labels")
    local flags={ret_value=true};callbacks.on_before_key_press(DIK_keys.DIK_END,nil,false,flags)
    assert(not flags.ret_value);d.update(now+1)
    assert(not shown_nicks().End and not shown_nicks().F6 and next(ui.windows).hover_mock.shown)
    local hidden_save=clone(data)
    callbacks.on_before_key_press(DIK_keys.DIK_END,nil,false,{ret_value=true});d.update(now+2);assert(shown_nicks().End)
    data=hidden_save;d.on_game_load();d.update(now+3);assert(not shown_nicks().End)
    assert(shown_nicks().PageDown and data.adb_mutant_companions_v2[tostring(third)].show_3d_nick==false,"Saved in-game visibility was reset to the new hidden spawn default")
    callbacks.on_before_key_press(DIK_keys.DIK_F6,nil,false,{ret_value=true});d.update(now+4)
    assert(shown_nicks().F6 and not shown_nicks().End,"One remapped key toggled other preset's nicknames")
    local prefs=data.adb_companion_nickname_hud_v1.mutant_v2
    assert(prefs.END==false and prefs.F6==true)
    callbacks.on_before_key_press(DIK_keys.DIK_END,nil,true,{ret_value=true})
    callbacks.on_before_key_press(DIK_keys.DIK_END,nil,false,{ret_value=false})
    assert(prefs.END==false,"Disabled/consumed input changed nickname settings")
    assert(not d.can_execute({actor=a},{mutant_sections="gigant_strong",donor_name="x",nickname_key="F9"}))
    assert(not d.can_execute({actor=a},{mutant_sections="gigant_strong",donor_name="x",nickname_key="HOME"}))
    assert(not d.can_execute({actor=a},{mutant_sections="gigant_strong",donor_name="x",nickname_visibility="unknown"}))
    d.on_game_load()
end
-- Human companion uses its OWN preset key, defaults, persistence and HUD lease;
-- loading only this optional template must not require the V2 module.
do
    data={};d.on_game_load();a.health=1;ui.frame=ui.frame+10
    level.get_target_obj=function() return nil end
    function a:character_community() return "actor_stalker" end
    local vars={}
    function se_save_var(id,name,key,value)
        vars[id..":"..name..":"..key]=value
    end
    function se_load_var(id,name,key) return vars[id..":"..name..":"..key] end
    local next_human=9000
    function sim:create(section)
        assert(section=="online_offline_group")
        local squad={id=next_human+1000}
        function squad:add_squad_member(section,p)
            next_human=next_human+1
            local id=next_human;local npc=object(id,section,p.x);npc._pos=pos(p.x,p.y,p.z);npc.stalker=true
            objects[id]=npc
            local server={id=id}
            function server:name() return npc:name() end
            function server:set_character_name(name) npc.donor_name=name end
            function server:force_set_goodwill() end
            servers[id]=server;return id
        end
        function squad:set_squad_relation(relation) assert(relation=="friend") end
        function squad:refresh() end
        return squad
    end
    SIMBOARD={assign_squad_to_smart=function() end,setup_squad_and_group=function() end}
    axr_companions={companion_squads={},non_task_companions={}}
    anomaly_donation_bridge_catalog.capable=function() return true end
    anomaly_donation_bridge_catalog.validate_section=function() return {section_id="test_human"} end
    anomaly_donation_bridge_catalog.random_available=function() return {section_id="test_human"} end
    anomaly_donation_bridge_named_companion=load_module(core,"anomaly_donation_bridge_named_companion")
    local human=load_module(optional,"anomaly_donation_bridge_effect_spawn_named_companion")
    human.on_game_start()
    local handler=anomaly_donation_bridge_registry.get("spawn_named_companion")
    local function human_spawn(show,key)
        local context={actor=a}
        local params={section_id="test_human",donor_name="Человек",use_player_faction="false",nickname_visibility=show and (show=="true" and "show" or "hide"),nickname_key=key}
        assert(handler.can_execute(context,params))
        if show==nil then assert(context.options.show_3d_nick==false and context.options.nickname_key=="PRIOR") end
        assert(handler.execute(context)=="executed")
        return next_human
    end
    local hidden=human_spawn()
    handler.update(260000);assert(ui.labels==0,"Default-off human nickname created a HUD")
    local visible=human_spawn("true","F6")
    handler.update(260001)
    local wnd=next(ui.windows);assert(wnd and #wnd.children==9 and wnd.children[9].shown)
    assert(wnd.children[9].text==anomaly_donation_bridge_common.game_text_cp1251_lossy("Человек"))
    assert(not wnd.children[9].text:find("HP",1,true))
    assert(vars[visible..":"..objects[visible]:name()..":adb_companion_3d_nick_v1"]=="Человек")
    assert(vars[hidden..":"..objects[hidden]:name()..":adb_companion_3d_nick_v1_default"]==false)
    objects[visible]._pos.x=21;wnd:Update()
    callbacks.on_before_load_input();assert(ui.labels==0 and wnd.enabled==false and not wnd.shown)
    for i=1,5 do ui.frame=ui.frame+1;handler.update(260002+i);assert(ui.labels==0) end
    handler.on_game_load();ui.frame=ui.frame+3;handler.update(260100)
    handler.update(260101);local restored=next(ui.windows)
    assert(restored and restored~=wnd and #restored.children==9,"Human nickname flag lost across load")
    data.adb_companion_nickname_hud_v1={mutant_v2={END=false}}
    ui.frame=ui.frame+1;callbacks.on_before_key_press(DIK_keys.DIK_F6,nil,false,{ret_value=true});restored:Update()
    assert(not restored.children[9].shown and data.adb_companion_nickname_hud_v1.human.F6==false)
    local hidden_save=clone(data)
    ui.frame=ui.frame+1;callbacks.on_before_key_press(DIK_keys.DIK_F6,nil,false,{ret_value=true});restored:Update();assert(restored.children[9].shown)
    data=hidden_save;handler.on_game_load();ui.frame=ui.frame+3;handler.update(260102);handler.update(260103)
    assert(ui.labels==0,"Human saved hidden state was replaced by preset defaults")
    local untouched={ret_value=true};callbacks.on_before_key_press(DIK_keys.DIK_NEXT,nil,false,untouched)
    assert(untouched.ret_value and ui.labels==0,"Mutant default PageDown must not toggle human labels")
    ui.frame=ui.frame+1;callbacks.on_before_key_press(DIK_keys.DIK_PRIOR,nil,false,{ret_value=true});handler.update(260104)
    assert(ui.labels==1 and data.adb_companion_nickname_hud_v1.human.F6==false,"PageUp should affect only the default-bound human")
    assert(data.adb_companion_nickname_hud_v1.human.PRIOR==true)
    ui.frame=ui.frame+1;callbacks.on_before_key_press(DIK_keys.DIK_PRIOR,nil,false,{ret_value=true});handler.update(260105)
    ui.frame=ui.frame+1;callbacks.on_before_key_press(DIK_keys.DIK_F6,nil,false,{ret_value=true});handler.update(260106)
    restored=next(ui.windows);assert(restored and data.adb_companion_nickname_hud_v1.mutant_v2.END==false,"Human key changed mutant visibility")
    objects[visible].health=0;restored:Update();assert(not restored.children[9].shown)
    objects[visible].health=1;objects[visible]._name="recycled";restored:Update();assert(not restored.children[9].shown)
    handler.on_level_changing();assert(ui.labels==0)
    local invalid={actor=a}
    assert(not handler.can_execute(invalid,{section_id="test_human",donor_name="x",show_3d_nick="wrong",use_player_faction="false"}))
end
print("Companion nicknames smoke passed: default-hidden V2, PageDown enables without changing spawn defaults, saved runtime visibility persists; independent PageUp/PageDown and explicit End/rebinding, default dropdowns, always-on V2 hover HP, safe load/GC retirement/F9 guards, native combat/menus/save/V1 isolation (mock API)")
