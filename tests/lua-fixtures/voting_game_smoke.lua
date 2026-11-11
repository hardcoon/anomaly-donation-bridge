local root=assert(arg[1])
local checks=0
local function check(value,why)checks=checks+1;assert(value,why)end
local function module(name)
    local env=setmetatable({},{__index=_G})
    local fn=assert(loadfile(root.."/"..name..".script"));setfenv(fn,env);fn();_G[name]=env;return env
end
local W={}
local font_height,font_width=14,7
W.__index=W
function W:SetAutoDelete(value)self.auto_delete=value end
function W:AttachChild(child)self.children=self.children or {};self.children[#self.children+1]=child end
function W:AddWindow(child,auto_delete)
    -- Native export has no adopt policy: its raw pad pointers do not keep a
    -- Lua wrapper alive. A strong universal widget tree hid the real GC bug.
    self.native_rows=self.native_rows or setmetatable({},{__mode="v"})
    self.native_rows[#self.native_rows+1]=child
    if auto_delete then child:SetAutoDelete(true)end
end
function W:SetWndRect(r)self.rectangle=r end
function W:SetWndPos(p)self.position=p end
function W:SetWndSize(s)self.size=s end
function W:InitTexture(t)self.texture=t end
function W:SetTextureRect(r)self.uv=r end
function W:SetStretchTexture()end
function W:SetTextureColor(c)self.color=c end
function W:SetFont(value)self.font=value end
function W:SetTextComplexMode(value)self.complex=value end
function W:SetEllipsis(value)self.ellipsis=value end
function W:GetWidth()return self.size.x end
function W:GetHeight()return self.size.y end
function W:AdjustHeightToText()
    self.size.y=font_height*(self.complex and math.max(1,math.ceil(#(self.value or "")*font_width/self.size.x))or 1)
end
function W:SetTextAlignment(value)self.alignment=value end
function W:SetVTextAlignment(value)self.valign=value end
function W:SetTextOffset(x,y)self.offset={x,y}end
function W:SetTextColor(value)self.text_color=value end
function W:SetText(value)self.value=value end
function W:GetText()return self.value or ""end
-- Native CUIStatic.TextControl returns CUILines, not CUITextWnd. Its Lua
-- export has no SetVTextAlignment; a universal widget mock hid this mismatch.
local Lines={SetFont=W.SetFont,SetText=W.SetText,SetTextColor=W.SetTextColor}
Lines.__index=Lines
function W:TextControl()
    self.lines=self.lines or setmetatable({},Lines);return self.lines
end
function W:Show(value)self.shown=value end
function W:Enable(value)self.enabled=value end
function W:EnableHeading(value)self.uses_heading=value~=false end
function W:SetHeading(value)self.heading=value end
function W:Register()end
function W:AddCallback(id,event,fn)self.callbacks=self.callbacks or {};self.callbacks[id]=fn end
function W:IsCursorOverWindow()return false end
function W:Update()end
function W:OnKeyboard()return false end
function W:AllowWorkInPause(value)self.works_in_pause=value end
function W:ShowDialog(value)self.shown=value end
function W:HideDialog()self.shown=false end
local function widget()return setmetatable({},W)end
CUIStatic=widget;CUIWindow=widget;CUITextWnd=widget;CUIButton=widget
function CScriptXmlInit()local xml;return {
    ParseFile=function(_,path)
        local file=assert(io.open(root.."/../configs/ui/"..path,"rb"));local xml=file:read("*a");file:close()
        assert(xml:find('<legend_',1,true),"native legend XML must ship with addon")
        _.xml=xml
    end,
    InitScrollView=function(_,path,parent)
        local node=assert(_.xml:match("<"..path.." [^>]+/>"),"selected aspect profile must exist in shipped XML")
        local child=widget();parent:AttachChild(child)
        child:SetWndSize({x=tonumber(node:match('width="([%d.]+)"')),y=tonumber(node:match('height="([%d.]+)"'))})
        return child
    end
}end
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
function time_continual()return now end
local paused=false
local width,height,precache=1920,1080,0
local frame_now=100
local function advance_frames(count)frame_now=frame_now+count;now=now+count end
device=function()return {width=width,height=height,frame=frame_now,precache_frame=precache,is_paused=function()return paused end,pause=function(_,value)paused=value end}end
local sound_plays,sound_stops=0,0
sound_object=setmetatable({s2d=7},{__call=function(_,path)
    assert(path=="ui\\anomaly_donation_bridge_roulette_tick","voting uses the existing casino tick asset")
    return {play=function(_,actor,delay,mode)assert(actor==db.actor and mode==7);sound_plays=sound_plays+1 end,
        stop=function()sound_stops=sound_stops+1 end}
end})
ui_events={BUTTON_CLICKED=1,WINDOW_KEY_PRESSED=2,WINDOW_KEY_RELEASED=3};DIK_keys={DIK_ESCAPE=1}
key_bindings={kJUMP=4};local jump_key=57
function dik_to_bind(key)return key==jump_key and 4 or -1 end
local common=module("anomaly_donation_bridge_common");local sha=module("anomaly_donation_bridge_sha256")
local registry=module("anomaly_donation_bridge_registry")
local callbacks={}
function RegisterScriptCallback(name,fn) callbacks[name]=fn end
local registered=false
function Register_UI() registered=true end
function Unregister_UI() registered=false end
local busy=false
function Check_UI() return busy end
local hud_windows={}
local native_hud={AddDialogToRender=function(_,window)hud_windows[window]=true end,RemoveDialogToRender=function(_,window)hud_windows[window]=nil end}
function get_hud()return native_hud end
function app_ready() return true end
level={present=function()return true end}
db={actor={alive=function()return true end,iterate_inventory=function()error("voting must never inspect/spend inventory")end}}
anomaly_donation_bridge_roulette=setmetatable({},{__index=function()error("item roulette must not be invoked")end})
local ui=module("anomaly_donation_bridge_voting_ui")
module("anomaly_donation_bridge_roulette_art")
local logic=module("anomaly_donation_bridge_voting");logic.on_game_start()
local replies={}
local ui_errors={}
function printf(format,...)ui_errors[#ui_errors+1]=string.format(format,...)end
local ready=true
local function send(fields) replies[#replies+1]=fields;return true end
local function can_start()return ready,ready and "ok" or "loading" end
local function message(fields)logic.handle(fields,send,can_start)end
local function status()return replies[#replies] and replies[#replies][3]end
local sequence=0
local function request(automatic,count,long_title)
    sequence=sequence+1;count=count or 2
    local s={id=string.format("%032x",sequence),title="Голосование",auto=automatic,winner=count-1,fraction=count==2 and "0.9" or "0.999",count=count,options={}}
    local parts={s.title,"\n",automatic and "1" or "0","\n",tostring(s.winner),"\n",s.fraction,"\n",tostring(count),"\n"}
    for index=1,count do
        local ending=count==2 and (index==1 and "0.8" or "1") or index==count and "1" or string.format("%.6f",index/count)
        local title=long_title or (index==1 and "Отряд Долга" or index==2 and "Отряд Свободы" or "Шаблон "..index)
        s.options[index]={finish=ending,title=title}
        parts[#parts+1]=tostring(index-1).."\t"..ending.."\t"..title.."\n"
    end
    s.hash=sha.digest(table.concat(parts));return s
end
local function begin(s)message({"VOTING_BEGIN",s.id,s.title,s.auto and "1" or "0",tostring(s.winner),s.fraction,tostring(s.count),s.hash})end
local function option(s,index) local o=s.options[index];message({"VOTING_OPTION",s.id,tostring(index-1),o.finish,o.title})end
local function commit(s)message({"VOTING_END",s.id,s.hash})end
local function transfer(s)begin(s);for i=1,s.count do option(s,i)end;commit(s)end
local function query(s)message({"VOTING_QUERY",s.id})end
local function clone(value)
    if type(value)~="table" then return value end
    local copy={};for k,v in pairs(value)do copy[k]=clone(v)end;return copy
end
check(logic.supported() and not registry.busy_reason(),"idle private component is supported but nonblocking")
local hud_id=string.rep("f",32)
for _,position in ipairs({"left","right","top","bottom"})do
    message({"VOTING_HUD",hud_id,position,"Тестим\n[||||................] 20%\n!долг 80%\n!свобода 20%"})
    local window=ui.HUD;local controls=window and window.rows[1].controls
    check(window and hud_windows[window] and #window.rows==4 and #controls==9 and not registered and not paused,
        "progress is a nonmodal in-game HUD, not OBS or a dialog that blocks play")
    check(controls[9].control.alignment==0 and controls[9].control.font==1 and controls[9].control.text_color==0xffbebebe and
        controls[1].control.text_color==0xff000000,"progress uses grey Letterica16/black outline with left alignment")
    local point=controls[9].control.position
    check(position~="left" or point.x<30,"left position")
    check(position~="right" or point.x>500,"right position")
    check(position~="top" or point.y==40,"top position")
    check(position~="bottom" or point.y==640,"bottom position")
end
message({"VOTING_HUD",hud_id,"xy,0.25,0.4","Тестим\n[||||................] 20%"})
check(math.abs(ui.HUD.rows[1].controls[9].control.position.x-256)<1e-6 and
    math.abs(ui.HUD.rows[1].controls[9].control.position.y-307.2)<1e-6,"saved desktop coordinates map to native viewport fractions")
message({"VOTING_HUD",hud_id,"xy,1,1","Тестим\n[||||................]\n200 / 1000 руб."})
check(ui.HUD.rows[1].controls[9].control.position.y==708 and
    ui.HUD.rows[3].controls[9].control.position.y==748,"bottom placement clamps whole HUD inside screen; amount is below bar")
message({"VOTING_HUD",hud_id,"xy,1,1","Тестим\n[||||................] 20%\nда 0%\nВыдать аптечку\n\nнет 0%\nНичего не делать"})
check(ui.HUD.rows[4].text=="Выдать аптечку" and ui.HUD.rows[7].text=="Ничего не делать" and
    ui.HUD.rows[4].controls[9].control.position.y-ui.HUD.rows[3].controls[9].control.position.y==20 and
    ui.HUD.rows[6].controls[9].control.position.y-ui.HUD.rows[4].controls[9].control.position.y==28,
    "named HUD puts the template directly below its command and adds only 8px between options")
check(ui.HUD.rows[1].controls[9].control.position.y==640 and ui.HUD.rows[7].controls[9].control.position.y==748,
    "bottom clamp includes names and the smaller spacer row")
local named_payload={"Тестим","[||||................] 20%"}
for i=1,6 do
    if i>1 then named_payload[#named_payload+1]="" end
    named_payload[#named_payload+1]="!команда"..i.." 0%";named_payload[#named_payload+1]="Название шаблона "..i
end
message({"VOTING_HUD",hud_id,"xy,1,1",table.concat(named_payload,"\n")})
check(#named_payload==19 and ui.HUD.rows[19].text=="Название шаблона 6" and
    ui.HUD.rows[19].controls[9].control.position.y==748,"six named options survive the expanded bounded HUD payload")
message({"VOTING_HUD",hud_id,"left",table.concat(named_payload,"\n")..string.rep("\nлишняя",6)})
check(ui.HUD.rows[1].controls[9].control.position.y==448,"more than 24 rows cannot replace the last valid HUD")
message({"VOTING_HUD",hud_id,"xy,0.1,0.1","Тестим\n[||||................] 20%\nда 0%\nнет 0%"})
check(ui.HUD.rows[4].text=="нет 0%" and
    ui.HUD.rows[4].controls[9].control.position.y-ui.HUD.rows[3].controls[9].control.position.y==20 and
    not ui.HUD.rows[7].controls[9].control.shown and not ui.HUD.rows[19].controls[9].control.shown,
    "turning names off restores compact spacing and hides every retired name row")
local old_text=ui.HUD.rows[1].text
message({"VOTING_HUD",hud_id,"left",string.rep("x",1601)})
check(ui.HUD.rows[1].text==old_text,"malformed display update cannot replace the last valid HUD or affect voting")
busy=true;logic.update();check(not ui.HUD and not registered,"foreign menu hides nonmodal HUD")
busy=false;logic.update();check(ui.HUD,"progress returns after leaving menu without restarting collection")
now=now+8001;logic.update();check(not ui.HUD,"disconnected application leaves no stale progress display")
message({"VOTING_HUD",hud_id,"left","Тестим\n[||||................] 200 / 1000 руб."})
check(ui.HUD and ui.HUD.rows[2].text:find("200 / 1000",1,true),"amount mode renders actual funds, not percent text")
precache=5;logic.update();check(not ui.HUD,"loading/precache never creates a native HUD window")
precache=0;logic.update();check(ui.HUD,"rendered game resumes compact HUD after precache")
callbacks.load_state({});message({"VOTING_HUD",hud_id,"left","Тестим\n[||||................] 20%"})
check(not ui.HUD,"F9 discards old HUD and waits eight frames before rebuilding native labels")
now=now+1000;logic.update();check(not ui.HUD,"elapsed milliseconds alone cannot bypass the rendered-frame F9 fence")
advance_frames(9);logic.update();check(ui.HUD,"fresh IPC display returns after safe F9 retirement")
local flags={};callbacks.on_before_save_input(flags)
check(not ui.HUD and not flags.ret,"F5 retires cosmetic HUD without blocking normal quicksave")
local hud_save={};callbacks.save_state(hud_save);advance_frames(9);logic.update()
check(ui.HUD and not hud_save.adb_voting_game_hud,"F5 restores display after barrier without serialising native widgets")
callbacks.on_before_load_input(0,0,{})
message({"VOTING_HUD",hud_id,"xy,0.2,0.3","Old\n[||||................] 20%"})
now=now+10;logic.update();check(not ui.HUD,"F9 loading fence ignores arriving HUD IPC until new level load state")
callbacks.actor_on_net_destroy();callbacks.load_state(hud_save)
message({"VOTING_HUD",hud_id,"xy,0.2,0.3","New\n[||||................] 20%"})
advance_frames(9);logic.update();check(ui.HUD and ui.HUD.rows[1].text=="New","F9 rebuilds only fresh level display, not retired windows")
for cycle=1,30 do
    callbacks.on_before_save_input({});callbacks.save_state({});advance_frames(9);logic.update()
    check(ui.HUD,"repeated F5 restores cosmetic HUD without a native modal")
    callbacks.on_before_load_input(0,0,{});callbacks.actor_on_net_destroy();callbacks.load_state({})
    message({"VOTING_HUD",hud_id,"xy,0.2,0.3","Reload\n[||||................] 20%"})
    collectgarbage("collect");advance_frames(9);logic.update()
    local count=0;for _ in pairs(hud_windows)do count=count+1 end
    check(ui.HUD and count==1 and not registered and not paused,"F9 recreation leaves exactly one live render entry after forced GC")
end
callbacks.on_before_load_input(0,0,{});callbacks.load_state({});frame_now=0
message({"VOTING_HUD",hud_id,"xy,0.2,0.3","Frame reset\n[||||................] 20%"})
check(not ui.HUD,"frame rollback restarts the safe loading fence")
advance_frames(9);logic.update();check(ui.HUD,"reset frame counter does not leave HUD permanently suppressed")
callbacks.actor_on_net_destroy();callbacks.actor_on_first_update()
message({"VOTING_HUD",hud_id,"xy,0.2,0.3","New game\n[||||................] 20%"})
advance_frames(9);logic.update();check(ui.HUD and ui.HUD.rows[1].text=="New game","new game without saved load_state releases loading fence safely")
message({"VOTING_HUD",hud_id,"hidden",""});check(not ui.HUD,"disabled progress detaches from real HUD render list")
local s=request(true);begin(s);option(s,1)
check(not logic.is_active() and not logic.GUI,"partial snapshot cannot open roulette")
commit(s);check(status()=="rejected" and not logic.is_active(),"truncated snapshot rejected atomically")
s=request(true);begin(s);option(s,2);check(status()=="rejected" and not logic.is_active(),"out-of-order option rejected")
s=request(true);s.hash=string.rep("a",64);transfer(s);check(status()=="rejected" and not logic.is_active(),"tampered content hash rejected")
s=request(true);ready=false;transfer(s)
check(status()=="busy" and not logic.GUI,"loading preflight leaves pending snapshot unopened")
ready=true;busy=true;query(s);check(status()=="busy" and not logic.GUI,"foreign modal blocks presentation")
busy=false;query(s);check(status()=="active" and logic.GUI and registered and paused and logic.GUI.works_in_pause,"pending snapshot opens, owns pause and native dialog keeps updating")
check(registry.busy_reason()=="effect_in_progress","ordinary effects blocked throughout native modal")
local wnd=logic.GUI
collectgarbage("collect");collectgarbage("collect")
check(#wnd.legend_windows==2 and wnd.legend.native_rows[1]==wnd.legend_windows[1] and
    wnd.legend_windows[1].auto_delete==false,"scroll rows survive forced GC and have exactly one owner (Lua), not native auto-delete plus Lua")
check(#wnd.segments==360 and not wnd.buttons and not wnd.icon and wnd.frame.texture=="adb_roulette_casino_frame" and
    wnd.marker.texture=="adb_roulette_casino_pointer" and wnd.button.texture=="adb_roulette_casino_risk_normal",
    "voting reuses original casino art without invoking inventory roulette or risk controls")
check(wnd.segments[1].texture=="adb_voting_material" and wnd.segments[1].uv.x1==2 and
    wnd.segments[360].uv.x1==359*8+2 and wnd.divisions.texture=="adb_roulette_divisions",
    "dynamic voting strips use original casino relief and the original visible tick layer")
check(not wnd.name and not wnd.name_background and wnd.legend_heading.controls[2].value==common.game_text_cp1251_lossy("Эффекты:"),
    "redundant lower caption is absent and the right list is called Effects")
check(#wnd.legend_rows==2 and wnd.legend and math.abs(wnd.x+110*wnd.scale*wnd.k-512)<1e-6 and
    math.abs(wnd.y+80*wnd.scale-384)<1e-6,"wheel centre is exactly the screen centre and right-hand legend has every paid preset")
local marker_x,marker_y,marker_heading=wnd.marker.position.x,wnd.marker.position.y,wnd.marker.heading
local segment_x=wnd.segments[1].position.x
check(not wnd.button.shown and not wnd.button.enabled,"automatic mode has no clickable button")
check(logic.at_fraction(s,.1)==1 and logic.at_fraction(s,.79999)==1 and logic.at_fraction(s,.8)==2,"80/20 proportions and exact boundary")
local titles={}
local started=now
for t=0,4280,20 do now=started+t;frame_now=frame_now+1;logic.update();titles[logic.view().title]=true end
check(titles[s.options[1].title] and titles[s.options[2].title],"caption changes rapidly with rotating marker")
check(wnd.marker.position.x==marker_x and wnd.marker.position.y==marker_y and wnd.marker.heading~=marker_heading and
    wnd.segments[1].position.x==segment_x,"original casino pointer rotates while coloured sectors remain fixed")
check(sound_plays>0 and paused,"original casino rotation clicks play while the native window owns game pause")
now=started+4300;logic.update()
check(logic.view().finished and logic.view().title==s.options[2].title and not wnd.button.shown,"stops on persisted winner without buttons")
check(logic.at_fraction(logic.snapshot(),(logic.view().angle%360)/360)==s.winner+1 and sound_stops>0,
    "sector actually under rotating pointer is the saved winner and rotation sound stops at the result")
check(not logic.apply() and logic.GUI,"automatic presentation cannot be prematurely acknowledged")
now=started+6299;logic.update();check(logic.GUI,"winner remains readable for two seconds")
now=started+6300;logic.update();query(s)
check(status()=="done" and not logic.GUI and not logic.is_active() and not registered and not paused and not registry.busy_reason(),"auto closes/unpauses before final acknowledgement/unblocks queue")
transfer(s);check(status()=="done" and not logic.GUI,"replayed snapshot cannot reroll/reopen a completed presentation")
s=request(false);transfer(s);wnd=logic.GUI;started=now
check(not logic.apply() and not wnd.button.shown,"manual mode cannot apply during spin")
now=started+4300;logic.update();check(wnd.button.shown and wnd.button.enabled,"manual mode shows casino-styled Apply after stopping")
now=now+60000;logic.update();check(logic.GUI==wnd,"manual result stays open until Apply")
local saved={};callbacks.save_state(saved);saved=clone(saved);callbacks.actor_on_net_destroy();callbacks.load_state(saved);advance_frames(9);logic.update()
check(logic.GUI and logic.view().finished and logic.view().title==s.options[2].title and logic.GUI.button.shown,"saved manual result restores without reroll")
check(logic.apply() and not logic.GUI and not registered and not paused,"manual Apply closes native input/unpauses before effect can run")
query(s);check(status()=="done","manual completion uses same immutable result ID")
callbacks.load_state(saved);advance_frames(9);logic.update();query(s)
check(status()=="done" and not logic.GUI,"old F9 save cannot reopen an already acknowledged result in the same process")
s=request(false);transfer(s);now=now+1000;logic.update();local before=logic.view().angle
saved={};callbacks.save_state(saved);saved=clone(saved);callbacks.actor_on_net_destroy();callbacks.load_state(saved);advance_frames(9);logic.update()
check(math.abs(logic.view().angle-before)<1e-6,"active spin resumes saved progress, not a new winner")
message({"VOTING_CANCEL",s.id});check(status()=="done" and not logic.GUI and not registry.busy_reason(),"reset/delete cancellation closes modal without applying")
message({"VOTING_HUD",hud_id,"xy,0.1,0.2","Before roulette\n[||||................] 20%"})
s=request(true);transfer(s)
check(status()=="busy" and replies[#replies][4]=="ui_retiring_hud" and not ui.HUD and not logic.GUI,
    "nonmodal HUD is detached before constructing a modal, never overlapping two native render entries")
advance_frames(3);logic.update();query(s)
check(status()=="active" and not ui.HUD and logic.GUI,"same frozen presentation opens after holder has swept retired HUD")
local save_flags,load_flags={},{};callbacks.on_before_save_input(save_flags);callbacks.on_before_load_input(0,0,load_flags)
check(save_flags.ret and load_flags.ret,"F5 and F9 are blocked while roulette owns native modal input")
message({"VOTING_CANCEL",s.id});message({"VOTING_HUD",hud_id,"hidden",""})
s=request(true,200);transfer(s);check(status()=="active" and #logic.snapshot().options==200 and #logic.GUI.segments==360 and
    #logic.GUI.legend_rows==200,"200 options remain bounded and are available in the native scroll legend")
message({"VOTING_CANCEL",s.id})
-- Project the ACTUAL native renderer's S2DVert::rotate_pt formula, not just
-- widget rectangles: heading textures multiply X by kx around their pivot.
local function project(control,px,py)
    local x,y=px,py
    if control.uses_heading then
        local cx,cy=control.size.x/2,control.size.y/2
        local a=control.heading or 0;local dx,dy=x-cx,y-cy
        local k=(height/width)/(768/1024)
        x=cx+(dx*math.cos(a)+dy*math.sin(a))*k
        y=cy+dy*math.cos(a)-dx*math.sin(a)
    end
    return (control.position.x+x)*width/1024,(control.position.y+y)*height/768
end
local function geometry_checks(window)
    local factor=window.scale*height/768
    local centre_x,centre_y=width/2,height/2
    local min_radius,max_radius=math.huge,0
    local centers_ok=true
    for _,segment in ipairs(window.segments)do
        local sx,sy=project(segment,segment.size.x/2,segment.size.y/2)
        if math.abs(math.sqrt((sx-centre_x)^2+(sy-centre_y)^2)-54.92*factor)>1e-5 then centers_ok=false end
        for _,p in ipairs({{0,0},{segment.size.x,0},{0,segment.size.y},{segment.size.x,segment.size.y}})do
            local x,y=project(segment,p[1],p[2]);local r=math.sqrt((x-centre_x)^2+(y-centre_y)^2)
            min_radius=math.min(min_radius,r);max_radius=math.max(max_radius,r)
        end
    end
    check(centers_ok and min_radius>=46*factor-.1 and max_radius<=63.84*factor+.1,
        "every native-rotated coloured strip stays in the original concentric 46..63.84 casino annulus, at every aspect")
    local frame=window.frame;local cx=(frame.position.x+frame.size.x*625/1254)*width/1024
    local cy=(frame.position.y+frame.size.y*612/1254)*height/768
    local radius_x=frame.size.x*593/1254*width/1024;local radius_y=frame.size.y*580/1254*height/768
    check(math.abs(cx-centre_x)<.01 and math.abs(cy-centre_y)<.01 and math.abs(radius_x-radius_y)<.01,
        "casino frame's calibrated visual centre matches sectors and remains physically circular, including 5:4")
    local px,py=project(window.marker,window.marker.size.x/2,window.marker.size.y/2)
    check(math.abs(px-centre_x)<.001 and math.abs(py-centre_y)<.001,"rotating pointer uses exactly the same physical centre")
    local labels_ok=true
    for _,odds in ipairs(window.odds)do
        local c=odds.label.controls[2];local x=(c.position.x+c.size.x/2)*width/1024
        local y=(c.position.y+c.size.y/2)*height/768
        if math.abs(math.sqrt((x-centre_x)^2+(y-centre_y)^2)-54.92*factor)>.01 then labels_ok=false end
    end
    check(labels_ok,"percentage labels are centred in the colour band, not on the outer gold grid")
    local order={};for index,child in ipairs(window.children)do order[child]=index end
    check(#window.odds==0 or order[window.frame]<order[window.odds[1].label.controls[1]],
        "casino frame renders BEFORE percentage labels, never masking their glyphs")
    check(window.legend.position.x+window.legend:GetWidth()<=1024-8 and window.legend.position.y+window.legend:GetHeight()<768-24,
        "entire initialized native scroll viewport fits 4:3/16:9/21:9/32:9 without resizing its private scrollbar")
    local rows_ok=true
    for _,item in ipairs(window.legend_items)do
        local title,odds=item.title.controls[2],item.odds.controls[2]
        if title.position.x<0 or title.position.x+title.size.x>item.row.size.x or
            odds.position.y<title.position.y+title.size.y+5.9 or odds.position.y+odds.size.y>item.height-12 or
            item.background.size.x~=item.row.size.x or item.background.size.y~=item.height then rows_ok=false end
    end
    check(rows_ok,"native-measured long titles and odds fit the SAME skin rectangle with separate padded lines")
    check(not window.name and not window.name_background and order[window.divisions]<order[window.frame] and
        order[window.divisions]>order[window.segments[360]],
        "no redundant lower caption; original ticks draw above material and below original frame")
end
for _,size in ipairs({{1024,768},{1280,1024},{1280,720},{1920,1080},{1920,1200},{2560,1080},{3440,1440},{5120,1440},{3840,2160}})do
    for _,metrics in ipairs({{14,7},{26,11}})do
        width,height=size[1],size[2];font_height,font_width=metrics[1],metrics[2]
        s=request(true,3,string.rep("Длинное название выбранного шаблона ",4));transfer(s);wnd=logic.GUI
        check(status()=="active","widescreen/large-font presentation opens without changing winner")
        geometry_checks(wnd);collectgarbage("collect")
        check(wnd.legend.native_rows[3]==wnd.legend_windows[3] and not wnd.legend_windows[3].auto_delete,
            "resized legend keeps its safe single-owner rows through GC")
        message({"VOTING_CANCEL",s.id})
    end
end
font_height,font_width=14,7
width,height=1920,1080
s=request(true,1);transfer(s)
check(status()=="active" and logic.view().title==s.options[1].title and #logic.GUI.segments==360,
    "one paid option renders a full circle even when all other commands received zero")
message({"VOTING_CANCEL",s.id})
s=request(true);begin(s);now=now+15001;logic.update();query(s);check(status()=="unknown","abandoned staging transfer expires")
local original=ui.adb_voting_window
ui.adb_voting_window=function()return {ShowDialog=function()error("simulated native failure")end,HideDialog=function()end}end
s=request(true);transfer(s);check(status()=="busy" and not logic.GUI and not logic.is_active() and not registered,"partial native construction rolls back cleanly")
check(replies[#replies][4]=="ui_unavailable_show" and ui_errors[#ui_errors]:find("simulated native failure",1,true),
    "native UI errors retain the exact failing stage and diagnostic instead of silently hiding it")
local error_count=#ui_errors;query(s)
check(#ui_errors==error_count,"unchanged native opening error is not logged on every retry")
ui.adb_voting_window=original;query(s);check(status()=="active" and logic.GUI,"failed modal remains retryable without changing winner")
message({"VOTING_CANCEL",s.id})
local encoded=common.game_text_cp1251_lossy("Отряд Свободы")
check(encoded and encoded~="", "game-native caption conversion available")
paused=true;s=request(true);transfer(s)
check(status()=="busy" and paused and not logic.GUI,"foreign pause is never acquired or released by a failed open")
paused=false;query(s);message({"VOTING_CANCEL",s.id})
-- Real runtime paused-UI pump: the actor clock is frozen, native UI/realtime
-- keeps servicing heartbeat/query/cancel, but cannot dispatch ordinary effects.
s=request(true);transfer(s);local frozen=now
function time_global()return paused and frozen or now end
local events,sent={},{}
local connected=true;local connects=0
anomaly_donation_bridge_transport={is_connected=function()return connected end,
    connect=function()connects=connects+1;connected=connects>=2;return connected end,
    process_id=function()return 1234 end,disconnect=function()connected=false end,
    poll=function()local result=events;events={};return result end,
    send=function(fields)sent[#sent+1]=fields;return true end}
module("anomaly_donation_bridge_protocol")
local runtime=module("anomaly_donation_bridge_runtime")
events={{"WELCOME","1","test"},{"PING","paused-probe"},{"VOTING_QUERY",s.id},
    {"COMMAND","paused-command","donation","change_money","100","RUB",""}}
now=now+200;logic.GUI:Update()
local pong,blocked,queried=false,false,false
for _,fields in ipairs(sent)do
    if fields[1]=="PONG" and fields[2]=="paused-probe" then pong=true end
    if fields[1]=="RESULT" and fields[2]=="paused-command" and fields[3]=="deferred" then blocked=true end
    if fields[1]=="VOTING_STATUS" and fields[2]==s.id and fields[3]=="active" then queried=true end
end
check(paused and time_global()==frozen and pong and blocked and queried,"paused native UI maintains IPC liveness without running ordinary commands")
connected=false;now=now+200;logic.GUI:Update()
check(connects==1 and not connected,"paused UI reconnect begins without actor clock advancing")
now=now+1001;logic.GUI:Update()
check(connects==2 and connected,"paused reconnect retry uses realtime, not frozen game timer")
events={{"WELCOME","1","test"},{"VOTING_CANCEL",s.id}};now=now+200;logic.GUI:Update()
check(not paused and not logic.GUI and not logic.is_active(),"application reset can cancel/unpause a native UI through paused IPC pump")
print("Voting game presentation checks passed: "..checks)
