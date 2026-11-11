local root=assert(arg[1])
local env=setmetatable({},{__index=_G});local f=assert(loadfile(root..'/anomaly_donation_bridge_npc_visuals.script'));setfenv(f,env);f()
local function u32(n)local out={};for i=1,4 do out[i]=string.char(n%256);n=math.floor(n/256)end;return table.concat(out)end
local function chunk(n,s)return u32(n)..u32(#s)..s end
local skeleton='validated by visual_safety_smoke.lua'
local contents={['actors\\external_motion.ogf']='safe'}
local temporary={}
env.io={open=function(path)
    local data=temporary[path];if not data then return end;local offset=0
    return {seek=function(_,how,n)if how=='end'then offset=#data elseif how=='set'then offset=n end;return offset end,
        read=function(_,n)if n=='*a'then n=#data-offset end;local s=data:sub(offset+1,offset+n);offset=offset+#s;return s end,close=function()end}
end}
local fs={update_path=function(_,alias,path)return path end,exist=function(_,alias,path)return contents[path or alias]~=nil end,
    file_copy=function(_,from,to)temporary[to]=assert(contents[from])end,file_delete=function(_,path)temporary[path]=nil end}
function getFS()return fs end;function time_global()return 20 end
local sha=setmetatable({},{__index=_G});local sf=assert(loadfile(root..'/anomaly_donation_bridge_sha256.script'));setfenv(sf,sha);sf()
anomaly_donation_bridge_sha256=sha
anomaly_donation_bridge_visual_safety={clear_cache=function()end,check=function(path,ini,base)
    assert(base=='normal','validate against the SAME normal spawn section')
    return contents[path]~=nil and contents[path]~='unsafe','special_visual_missing_bone_bip01_spine2'
end}
local rows
-- A second build contributes unique profiles outside actors\; only the normal
-- catalogue's actually reachable visual pool is subtracted, not whole folders.
contents['actors\\person.ogf']=chunk(1,string.char(4,3)..string.rep('\0',42))..chunk(13,skeleton)..chunk(9,'children')..chunk(14,'animation')
contents['pack\\custom_unique.ogf']=contents['actors\\person.ogf']
contents['actors\\invisible.ogf']=contents['actors\\person.ogf']
contents['actors\\orphan.ogf']=contents['actors\\person.ogf']
contents['gameplay\\profiles.xml']=[[<xml>
<character id="normal"><class>normal_class</class></character>
<character id="story"><specific_character>hero</specific_character></character>
<character id="duplicate"><specific_character>hero_copy</specific_character></character>
<character id="custom"><specific_character>custom_hero</specific_character></character>
<character id="shared"><specific_character>shared_hero</specific_character></character>
<character id="invisible"><specific_character>invisible_hero</specific_character></character>
</xml>]]
contents['gameplay\\characters.xml']=[[<xml>
<!-- <specific_character id="ignored"><visual>actors\orphan</visual></specific_character> -->
#include "gameplay\unique.xml"
<specific_character id="ordinary"><class>normal_class</class><visual>actors\external_motion</visual></specific_character>
<specific_character id="shared_hero"><name>Shared</name><visual>actors\external_motion</visual></specific_character>
<specific_character id="invisible_hero"><name>Invisible</name><visual>actors\invisible</visual></specific_character>
</xml>]]
contents['gameplay\\unique.xml']=[[<specific_character id='hero' no_random="1"><class>normal_class</class><name>Hero</name><icon>ui_Hero_Portrait</icon><visual>actors\person</visual></specific_character>
<specific_character id="hero_copy"><name>HeroCopy</name><icon>ui_other_portrait</icon><visual>ACTORS/person.ogf</visual></specific_character>
<specific_character id="custom_hero"><name>Custom</name><visual>pack\custom_unique</visual></specific_character>]]
game={translate_string=function(token)return 'Name '..token end}
anomaly_donation_bridge_common={game_text_utf8=function(s)return s end,truncate_utf8=function(s,n)return s:sub(1,n)end}
local values={profiles={files='profiles',specific_characters_files='characters'}}
local initial={}
for _,name in ipairs({'normal','story','duplicate','custom','shared','invisible'})do
    values[name]={character_profile=name}
    initial[#initial+1]={section_id=name,kind='npc',available=name=='normal',quarantine_reason=name=='normal' and 'none' or 'story_unique'}
end
local source={line_exist=function(_,s,k)return values[s] and values[s][k]~=nil end,r_string=function(_,s,k)return values[s][k] end}
local function fresh()local out={};for _,e in ipairs(initial)do out[#out+1]=e end;return out end
rows=fresh();local ok,count=env.append_special(rows,source)
assert(ok and count==2 and #rows==8 and not next(temporary),'special packed profiles must deduplicate and subtract ordinary visuals')
local ids={};for i=7,#rows do ids[env.resolve(rows[i].section_id)]=rows[i].section_id;assert(rows[i].display_name:find('Name ',1,true)) end
assert(ids['actors\\person.ogf'] and ids['pack\\custom_unique.ogf'],'do not hardcode actors folder or vanilla characters')
assert(not ids['actors\\external_motion.ogf'] and not ids['actors\\invisible.ogf'] and not ids['actors\\orphan.ogf'])
local old_id=ids['actors\\person.ogf']
assert(env.resolve_icon(old_id)=='ui_Hero_Portrait','portrait must belong to the profile selected for the label/model, preserve case')
assert(env.resolve_visual_icon('ACTORS/person')=='ui_Hero_Portrait','saved visuals without OGF suffix must resolve the same portrait')
assert(not env.resolve_icon(ids['pack\\custom_unique.ogf']),'missing profile portrait should keep base icon, not invent one')
local unique=contents['gameplay\\unique.xml'];contents['gameplay\\unique.xml']='#include "gameplay\\characters.xml"'
rows=fresh();ok=env.append_special(rows,source);assert(not ok and #rows==6 and env.failure_reason() and not env.resolve(old_id),'failed rebuild must not leave stale model IDs')
assert(not env.resolve_icon(old_id),'failed rebuild must also invalidate portrait metadata')
contents['gameplay\\unique.xml']=unique
values.normal.character_profile='missing_normal_profile'
rows=fresh();assert(not env.append_special(rows,source) and #rows==6,'unresolved normal pools cannot become special appearances')
values.normal.character_profile='normal'
rows=fresh();assert(env.append_special(rows,source) and #rows==8 and env.resolve(old_id),'refresh must recover deterministically')
assert(env.resolve_icon(old_id)=='ui_Hero_Portrait')
assert(next(temporary)==nil,'packed profile scratch files leaked')
print('PASS special appearance catalogue: installed profiles/includes, modded models, normal-pool subtraction, shared OGF dedup, no_random, technical/orphan rejection, atomic failure/rebuild')

-- Incompatible installed profiles are filtered, with no replacement model or
-- synthetic catalogue entry, even if an unrelated private OGF exists on disk.
local orphan='anomaly_donation_bridge\\sidorovich_companion.ogf'
local original='actors\\stalker_trader\\stalker_trader_1.ogf'
contents[orphan]='safe';contents[original]='unsafe'
contents['gameplay\\profiles.xml']=contents['gameplay\\profiles.xml']..'<character id="sidor"><specific_character>sidor</specific_character></character>'
contents['gameplay\\characters.xml']=contents['gameplay\\characters.xml']..'<specific_character id="sidor"><name>Sidor</name><icon>ui_Sidor</icon><visual>actors\\stalker_trader\\stalker_trader_1</visual></specific_character>'
values.sidor={character_profile='sidor'};initial[#initial+1]={section_id='sidor',kind='npc',available=false,quarantine_reason='story_unique'}
rows=fresh();ok,count=env.append_special(rows,source);assert(ok and count==2 and #rows==9)
for _,row in ipairs(rows)do if row.kind=='npc_visual' then
    local path=env.resolve(row.section_id);assert(path~=original and path~=orphan)
end end
assert(env.spawn_section()=='normal' and env.excluded_models()[original])
function system_ini()return source end
local prepared,reason=env.prepare_visual('actors/stalker_trader/stalker_trader_1','normal')
assert(not prepared and reason=='special_visual_missing_bone_bip01_spine2','unsafe saved model must be rejected without remapping')
assert(env.prepare_visual('ACTORS/person','normal')=='actors\\person.ogf','compatible installed models retain their own path')
print('PASS incompatible appearances: filtered installed profile, no bundled replacement or injected entry, final validation without remapping')
