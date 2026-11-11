local root=assert(arg[1])
local checks=0
local function check(value,why)checks=checks+1;assert(value,why)end
local function module(name)
    local env=setmetatable({},{__index=_G})
    local fn=assert(loadfile(root.."/"..name..".script"));setfenv(fn,env);fn();_G[name]=env;return env
end
local W={}
W.__index=W
function W:SetAutoDelete()end
function W:AttachChild(child)self.children=self.children or {};self.children[#self.children+1]=child end
function W:SetWndRect(r)self.rectangle=r end
function W:SetWndPos(p)self.position=p end
function W:SetWndSize(s)self.size=s end
function W:InitTexture(t)self.texture=t end
function W:SetTextureRect(r)self.uv=r end
function W:SetStretchTexture()end
function W:SetTextureColor(c)self.color=c end
function W:SetFont()end
function W:SetTextComplexMode()end
function W:SetTextAlignment()end
function W:SetVTextAlignment(value)self.valign=value end
function W:SetTextOffset(x,y)self.offset={x,y}end
function W:SetTextColor()end
function W:SetText(value)self.value=value end
function W:GetText()return self.value or ""end
function W:TextControl()return self end
function W:Show(value)self.shown=value end
function W:Enable(value)self.enabled=value end
function W:EnableHeading()end
function W:SetHeading(value)self.heading=value end
function W:Register()end
function W:AddCallback(id,event,fn)self.callbacks=self.callbacks or {};self.callbacks[id]=fn end
function W:IsCursorOverWindow()return false end
function W:Update()end
function W:OnKeyboard()return false end
function W:ShowDialog(value)self.shown=value end
function W:HideDialog()self.shown=false end
local function widget()return setmetatable({},W)end
CUIStatic=widget;CUIWindow=widget;CUITextWnd=widget;CUIButton=widget
CUIScriptWnd=W
function class(name)
    local env=getfenv(2)
    return function(base)
        local cls=setmetatable({},{__index=base})
        cls.__index=cls
        setmetatable(cls,{__index=base,__call=function(t,...)
            local object=setmetatable({},t);object:__init(...);return object
        end})
        env[name]=cls;env.super=function()end
    end
end
function vector2()return {set=function(self,x,y)self.x=x;self.y=y;return self end}end
function Frect()return {set=function(self,x1,y1,x2,y2)self.x1=x1;self.y1=y1;self.x2=x2;self.y2=y2;return self end}end
function device()return {width=1920,height=1080}end
function GetARGB(a,r,g,b)return a*16777216+r*65536+g*256+b end
function GetFontLetterica16Russian()return 1 end
local now=1000
function time_global()return now end
local modal_now
local function animation_time()return modal_now or time_global()end
ui_events={BUTTON_CLICKED=1,WINDOW_KEY_PRESSED=2,WINDOW_KEY_RELEASED=3};DIK_keys={DIK_ESCAPE=1}
key_bindings={kJUMP=4};local jump_key=57
function dik_to_bind(key)return key==jump_key and 4 or -1 end
local cat=module("anomaly_donation_bridge_roulette_catalog")
local data={}
for i=1,20 do data["item"..i]={inv_name="item"..i,class="E_HLMET",kind="o_helmet",cost=tostring(i*100),visual="test",inv_grid_width="1",inv_grid_height="1"}end
cat.rebuild({section_for_each=function(_,fn)for s in pairs(data)do fn(s)end end,
    line_exist=function(_,s,k)return data[s][k]~=nil end,r_string=function(_,s,k)return data[s][k]end},function(s)return s end)
local core=module("anomaly_donation_bridge_roulette_core")
local objects={[1]={id=1,section="item10",value=1000,condition=1,amount=1,tier=3}}
local spawn_failure=false
core.configure({snapshot=function(id)return objects[id]end,now=animation_time,random=function()return 0 end,
    exists=function(id)return objects[id]~=nil end,create=function(s)if spawn_failure then return nil,"spawn_failed"end;objects[1000]={id=1000,section=s};return 1000 end,
    release=function(id)objects[id]=nil;return true end},{})
local batch,called;local mode="both"
anomaly_donation_bridge_roulette={text=function(s,...)return s end,category_name=function(s)return s end,default_count=function()return 3 end,
    animation_time=animation_time,update=function()core.update()end,
    risk_mode=function()return mode end,
    preview=function(risk)return core.quote_category(1,risk)end,
    batch_status=function()return batch end,
    start_batch=function(count,risk)called={count=tonumber(count),risk=risk};batch={index=1,total=tonumber(count),risk=risk};return core.start_category(1,risk)end,
    claim_reward=function()local result=core.claim();batch=nil;if result then anomaly_donation_bridge_roulette.GUI=nil end;return result end,
    close=function()return false,"must_roll"end}
module("anomaly_donation_bridge_roulette_art")
local ui=module("anomaly_donation_bridge_roulette_ui");ui.ensure_class()
local wnd=ui.adb_roulette_window();anomaly_donation_bridge_roulette.GUI=wnd
check(not wnd.cells and not wnd.catalog_cells and not wnd.picker,"no manual inventory or reward picker")
check(#wnd.sectors==4 and not wnd.odds.jackpot,"fourth jackpot strip has no numeric caption")
check(math.abs(wnd.k-0.75)<1e-8,"16:9 aspect correction")
local buttons=0;for _ in pairs(wnd.buttons)do buttons=buttons+1 end
check(buttons==3 and not wnd.count_edit and not wnd.detail,"only risk buttons and action; no extra captions")
check(wnd.icon.shown,"actual stake preview before roll")
check(wnd.needle.texture=="adb_roulette_casino_pointer","authored ruby pointer is used")
check(wnd.buttons.low.texture=="adb_roulette_casino_risk_selected","selected risk uses authored glass button")
check(wnd.buttons.low.caption.valign==1 and wnd.buttons.low.caption.offset[2]==0 and
    wnd.buttons.high.caption.valign==1 and wnd.buttons.high.caption.offset[2]==0,"both risk captions use vertical centring without a manual shift")
check(wnd:OnKeyboard(1,2) and anomaly_donation_bridge_roulette.GUI==wnd and not core.busy(),"escape cannot bypass initial obligation")
wnd:SetRisk("high");wnd:OnKeyboard(57,2);check(core.busy() and called.risk=="high" and called.count==3,"jump starts selected risk and N")
wnd:OnKeyboard(57,2);check(core.current().id==1,"held jump does not repeat")
wnd:OnKeyboard(57,3)
check(wnd:OnKeyboard(1,2) and anomaly_donation_bridge_roulette.GUI==wnd and core.current().phase=="spinning","escape cannot skip animation")
check(wnd.buttons.action.texture=="adb_roulette_casino_active_rus","Russian active sprite keeps lever down during roll")
check(not wnd.buttons.low.enabled,"risk locked during sequence")
local colors={};for _,sector in ipairs(wnd.sectors)do if sector.shown then colors[sector.color]=true end end
local count=0;for _ in pairs(colors)do count=count+1 end;check(count==4,"wheel displays three tier colors and red jackpot strip")
check(wnd.sectors[4].color==0xffba101c and wnd.sectors[4].shown,"jackpot is a visible red strip")
check(wnd.odds.higher.value=="30%" and wnd.odds.lower.value=="50%","new high risk odds directly on sectors")
wnd:SetRisk("low");check(wnd.risk=="high","risk frozen after wager")
now=5000;wnd:Update();check(core.busy() and not objects[1] and wnd.buttons.action.control.value=="claim","result remains until claim")
check(wnd.buttons.action.texture=="adb_roulette_casino_claim_rus","Russian claim sprite resets lever to raised position")
check(wnd.icon.shown and wnd.icon.uv.x1==cat.get(core.current().target).icon_x*50,"awarded native icon replaces stake")
check(wnd:OnKeyboard(1,2) and anomaly_donation_bridge_roulette.GUI==wnd,"escape cannot dismiss pending result")
jump_key=34;wnd:OnKeyboard(57,2);check(anomaly_donation_bridge_roulette.GUI==wnd,"old jump key is not hardcoded")
wnd:OnKeyboard(34,2);check(not core.busy() and not anomaly_donation_bridge_roulette.GUI,"rebound jump acknowledges and closes")
objects[1]={id=1,section="item1",value=100,condition=1,amount=1,tier=1}
wnd=ui.adb_roulette_window();anomaly_donation_bridge_roulette.GUI=wnd
check(wnd.odds.lower.value=="20%" and wnd.sectors[1].color==0xff292329,"white has visible dark loss sector")
wnd:Action();now=now+4000;wnd:Update()
check(core.result().lost and wnd.loss.shown and not wnd.icon.shown and not wnd.unknown.shown,"loss shows empty outcome, not original item")
check(wnd.buttons.action.picture_state=="loss","loss has its own button state")
check(wnd.buttons.action.control.value=="sad","loss caption is independent of technical errors")
wnd:Action();check(not core.busy() and not anomaly_donation_bridge_roulette.GUI,"loss acknowledgement closes window")
objects[1]={id=1,section="item10",value=1000,condition=1,amount=1,tier=3};spawn_failure=true
wnd=ui.adb_roulette_window();anomaly_donation_bridge_roulette.GUI=wnd;wnd:Action();now=now+4000;wnd:Update()
check(objects[1] and not core.result().lost and wnd.buttons.action.picture_state=="dismiss","technical failure retains stake and original acknowledgement")
check(wnd.buttons.action.control.value=="dismiss","technical error is not labelled as a loss")
wnd:Action()
spawn_failure=false;objects[1]={id=1,section="item10",value=1000,condition=1,amount=1,tier=3}
assert(core.start_category(1,"low"));local saved={};core.save(saved);saved.anomaly_donation_bridge_roulette.version=1;core.reset()
check(core.load(saved)==false and not core.busy(),"previous development save formats are not adapted")
check(objects[1] and not core.current(),"discarding obsolete state does not consume the current inventory")
for _,fixed in ipairs({"low","high"})do
    mode=fixed;batch=nil
    objects[1]={id=1,section="item10",value=1000,condition=1,amount=1,tier=3}
    wnd=ui.adb_roulette_window();anomaly_donation_bridge_roulette.GUI=wnd
    check(wnd.risk==fixed,"fixed risk selected before preview")
    check(not wnd.buttons.low.control.shown and not wnd.buttons.high.control.shown and
        not wnd.buttons.low.caption.shown and not wnd.buttons.high.caption.shown,"both risk controls and captions hidden")
    wnd:SetRisk(fixed=="low" and "high" or "low");check(wnd.risk==fixed,"hidden risk cannot be selected via stale callback")
    wnd:Action();check(called.risk==fixed,"spin uses forced policy")
    now=now+4000;wnd:Update();wnd:Action()
end
mode="both";batch=nil
objects[1]={id=1,section="item10",value=1000,condition=1,amount=1,tier=3}
wnd=ui.adb_roulette_window();anomaly_donation_bridge_roulette.GUI=wnd
check(wnd.buttons.low.control.shown and wnd.buttons.high.control.shown and
    wnd.buttons.low.caption.shown and wnd.buttons.high.caption.shown,"both-risk default restores both buttons and labels")
wnd:SetRisk("high");check(wnd.risk=="high","both-risk mode allows player choice")
-- The actual window must drive the shared adapter, not just the core or an
-- actor callback, and use its selected clock to animate while game time stops.
modal_now=1000000
wnd:Action();local game_stamp=now
local heading=wnd.needle.heading
modal_now=modal_now+200;wnd:Update()
check(now==game_stamp and wnd.needle.heading~=heading and core.current().phase=="spinning",
    "real UI needle animates on the adapter clock with frozen simulation time")
modal_now=modal_now+4000;wnd:Update()
check(now==game_stamp and core.current().phase=="await_claim" and wnd.buttons.action.control.value=="claim",
    "real UI completes paused-mode spin through its own update")
wnd:Action();modal_now=nil
print("Roulette UI flow checks passed: "..checks)
