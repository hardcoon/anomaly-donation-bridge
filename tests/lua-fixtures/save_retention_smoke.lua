-- Virtual FS only: never opens/deletes any real save or user file.
local root=assert(arg[1]);local checks=0
local function check(v,m) checks=checks+1;assert(v,m)end
local function load(name)
    local env=setmetatable({},{__index=_G});local f=assert(loadfile(root.."/"..name..".script"));setfenv(f,env);f();return env
end
local common=load("anomaly_donation_bridge_common")
local files,deletes,denied,bad_path
FS={FS_ListFiles=1,FS_RootOnly=2,FS_sort_by_modif_down=8}
local function add(stem,stamp)
    for _,ext in ipairs({".scop",".scoc",".dds"}) do files[stem..ext]={stamp=stamp,size=100} end
end
local current="Tester - ADB - after_9999_200"
local function reset(n)
    files,deletes,denied,bad_path={},{},{},nil
    for i=1,n do add("Tester - ADB - "..(i%2==0 and "before"or"after").."_"..i.."_"..i,i)end
    add(current,0) -- deliberately wrong/equal modification time; must be pinned
    for _,s in ipairs({"quicksave1","player - autosave","manual save","ADB","x - ADB - note_12_2",
        "x - ADB - before_12_no","x - ADB - before_12_2-copy","x - ADB - before_12_2.scop.bak",
        "../x - ADB - before_1_2","x/evil - ADB - before_1_2","x:evil - ADB - before_1_2"})do add(s,100000)end
    files["Tester - ADB - after_1_1.txt"]={stamp=1}
end
function getFS()
    return {
        exist=function(_,alias,name)check(alias=="$game_saves$","only engine save alias");return files[name]end,
        update_path=function(_,alias,name)
            check(alias=="$game_saves$","MO2/profile path comes from engine, not hardcoded appdata")
            return (bad_path==name and "C:\\Other\\"or"C:\\VirtualGame\\profile saves\\")..name
        end,
        file_length=function(_,path)local name=path:match("profile saves\\(.+)$");return files[name]and files[name].size or 0 end,
        file_list_open_ex=function(_,alias,flags,pattern)
            check(alias=="$game_saves$"and flags==3 and pattern=="*.scop","root-only main-save listing")
            local list={};for name,f in pairs(files)do if name:lower():match("%.scop$")then list[#list+1]={name=name,stamp=f.stamp}end end
            return {Sort=function(_,flag)check(flag==8,"newest-first engine sorting");table.sort(list,function(a,b)return a.stamp>b.stamp end)end,
                Size=function()return #list end,GetAt=function(_,i)return {NameShort=function()return list[i+1].name end,
                    Modif=function()return "time_"..list[i+1].stamp end}end}
        end,
        file_delete=function(_,alias,name)
            check(alias=="$game_saves$","no absolute/glob/recursive delete")
            deletes[#deletes+1]=name;if not denied[name]then files[name]=nil end
        end
    }
end
local function owned_count()
    local n=0;for name in pairs(files)do if name:match("^Tester %- ADB %- [a-z]+_%d+_%d+%.scop$")then n=n+1 end end;return n
end
local function outsiders_intact()
    for _,s in ipairs({"quicksave1","player - autosave","manual save","ADB","x - ADB - note_12_2",
        "x - ADB - before_12_no","x - ADB - before_12_2-copy","x - ADB - before_12_2.scop.bak",
        "../x - ADB - before_1_2","x/evil - ADB - before_1_2","x:evil - ADB - before_1_2"})do
        for _,ext in ipairs({".scop",".scoc",".dds"})do check(files[s..ext],"unrelated/malformed save touched: "..s..ext)end
    end
    check(files["Tester - ADB - after_1_1.txt"],"non-whitelisted extension touched")
end
reset(80)
local ok,removed,failed=common.prune_effect_saves(nil,current)
check(ok and removed==31 and failed==0 and owned_count()==50,"default retains 50 total before+after entries, not 50 pairs")
check(files[current..".scop"]and files[current..".scoc"]and files[current..".dds"],"new save must survive bad timestamp ordering")
for i=1,31 do check(not files["Tester - ADB - "..(i%2==0 and "before"or"after").."_"..i.."_"..i..".scoc"],"old sidecar leaked")end
outsiders_intact()
reset(4)
for name,value in pairs(files)do if name:match("^Tester %- ADB %-")then value.stamp=3 end end
ok,removed,failed=common.prune_effect_saves(2,current)
check(ok and removed==3 and files["Tester - ADB - before_4_4.scop"]and owned_count()==2,
    "same-second saves sorted by generated tick/sequence, not arbitrary FS order")
reset(4);ok,removed,failed=common.prune_effect_saves(1,current)
check(ok and removed==4 and failed==0 and owned_count()==1,"limit one retains only newest complete save")
outsiders_intact()
reset(4);ok,removed,failed=common.prune_effect_saves(0,current)
check(ok and removed==0 and failed==0 and #deletes==0 and owned_count()==5,"zero disables cleanup")
reset(4);files[current..".scop"]=nil
ok=common.prune_effect_saves(1,current)
check(not ok and #deletes==0,"failed/refused new save cannot cause any cleanup")
reset(4);files[current..".scop"].size=0
check(not common.prune_effect_saves(1,current)and #deletes==0,"empty new save cannot trigger destructive cleanup")
reset(4);denied["Tester - ADB - after_1_1.scop"]=true
ok,removed,failed=common.prune_effect_saves(1,current)
check(ok and failed==1 and removed==3 and files["Tester - ADB - after_1_1.scoc"]and files["Tester - ADB - after_1_1.dds"],
    "denied primary delete cannot destroy sidecars of a still-loadable save")
reset(4);bad_path=current..".scop"
ok=common.prune_effect_saves(1,current)
check(not ok and #deletes==0,"resolved path escaping the exact save alias fails closed")
reset(4);bad_path="Tester - ADB - after_1_1.scop"
ok=common.prune_effect_saves(1,current)
check(not ok and #deletes==0,"all candidate main paths checked before first deletion")
reset(4);bad_path="Tester - ADB - after_1_1.scoc"
check(not common.prune_effect_saves(1,current)and #deletes==0,"all sidecar paths checked before first deletion")
for _,bad in ipairs({-1,0.5,10001,"nan","invalid"})do reset(4);check(not common.prune_effect_saves(bad,current)and #deletes==0,"bad limit cannot delete")end
reset(4);check(not common.prune_effect_saves(1,"../escape - ADB - before_1_2")and #deletes==0,"malicious current name cannot delete")

local protocol=load("anomaly_donation_bridge_protocol")
local command={"COMMAND","c","d","test.action","1","RUB",""}
check(protocol.parse_command(command).effect_save_retention_count==50,"legacy seven-field default")
for _,v in ipairs({"Donor","Effect","1","1","3"})do command[#command+1]=v end
check(protocol.parse_command(command).effect_save_retention_count==50,"legacy twelve-field default")
command[13]="7";check(protocol.parse_command(command).effect_save_retention_count==7,"new limit decoded")
command[13]="0";check(protocol.parse_command(command).effect_save_retention_count==0,"unlimited decoded")
for _,bad in ipairs({"-1","1.2","10001","nan","","1e2"})do command[13]=bad;check(not protocol.parse_command(command),"malformed wire limit rejected")end
print("ADB save retention passed: "..checks.." checks (virtual FS only; no real save deletions).")
