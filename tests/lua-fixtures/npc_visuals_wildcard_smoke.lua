-- Isolated regression checks: no running game, renderer, NPC spawn or user data.
local root=assert(arg[1])
local function norm(s) return s:lower():gsub('/','\\') end
local function module(name,env)
    local f=assert(loadfile(root..'/'..name..'.script'))
    setfenv(f,env);f();return env
end
local function sandbox(options)
    options=options or {}
    local env=setmetatable({},{__index=_G})
    local files,temp={},{}
    local stats={listed=0,freed=0,copied=0,closed=0,read={}}
    local function put(path,text,packed)
        files[norm(path)]={name=path:gsub('/','\\'),text=text,packed=packed==true}
    end
    local function reader(text)
        local offset=0
        return {seek=function(_,how,value)
            if how=='end' then offset=#text elseif how=='set' then offset=value end
            return offset
        end,read=function(_,count)
            if count=='*a' then count=#text-offset end
            local out=text:sub(offset+1,offset+count);offset=offset+#out;return out
        end,close=function() stats.closed=stats.closed+1 end}
    end
    local fs={}
    function fs:update_path(alias,path)
        if alias=='$app_data_root$' then return 'temp\\'..path end
        return 'config\\'..path
    end
    function fs:exist(alias,path)
        if path then return files[norm(path)]~=nil end
        return temp[alias]~=nil
    end
    function fs:file_copy(from,to)
        local record=assert(files[norm(from:sub(8))])
        temp[to]=record.text;stats.copied=stats.copied+1
    end
    function fs:file_delete(path) temp[path]=nil end
    local function listing(directory,objects)
        stats.listed=stats.listed+1
        local names={}
        directory=norm(directory)
        for key,record in pairs(files) do
            if key:sub(1,#directory)==directory then
                local rest=record.name:sub(#directory+1)
                if rest~='' and not rest:find('\\',1,true) then names[#names+1]=rest end
            end
        end
        table.sort(names,function(a,b)return norm(a)>norm(b)end) -- deliberately reversed
        if options.duplicate and names[1] then names[#names+1]=names[1]:upper() end
        local freed=false
        return {Size=function()return options.list_size or #names end,
            GetAt=function(_,index)
                assert(not freed)
                if options.list_error then error('injected listing failure') end
                local name=assert(names[index+1])
                if not objects then return name end
                if options.short_names then return {NameShort=function()return name end} end
                return {NameFull=function()return name end}
            end,
            Free=function()assert(not freed);freed=true;stats.freed=stats.freed+1 end}
    end
    function fs:file_list_open(alias,directory,flags)
        assert(alias=='$game_config$' and flags==9)
        return listing(directory,false)
    end
    function fs:file_list_open_ex(alias,flags,mask)
        assert(alias=='$game_config$' and flags==9 and mask)
        return listing('',true)
    end
    env.io={open=function(path)
        if temp[path] then return reader(temp[path]) end
        local key=norm(path:sub(8));local record=files[key]
        if record and not record.packed then
            stats.read[key]=(stats.read[key] or 0)+1
            return reader(record.text)
        end
    end}
    env.getFS=function()return fs end
    env.time_global=function()return 123 end
    env.FS={FS_ListFiles=1,FS_RootOnly=8}
    env.game={translate_string=function(s)return s end}
    env.anomaly_donation_bridge_common={game_text_utf8=function(s)return s end,truncate_utf8=function(s,n)return s:sub(1,n)end}
    env.anomaly_donation_bridge_sha256=module('anomaly_donation_bridge_sha256',setmetatable({},{__index=_G}))
    env.anomaly_donation_bridge_visual_safety={clear_cache=function()end,check=function(path,ini,base)
        assert(base=='normal')
        return files[norm(path)]~=nil,'special_visual_resource_missing'
    end}
    local cfg={profiles={files='profiles',specific_characters_files='characters'},normal={character_profile='normal'},story={character_profile='story'}}
    local ini={line_exist=function(_,s,k)return cfg[s] and cfg[s][k]~=nil end,r_string=function(_,s,k)return cfg[s][k]end}
    local profiles='<xml><character id="normal"><class>normal_class</class></character><character id="story"><specific_character>hero</specific_character></character></xml>'
    local ordinary='<specific_character id="ordinary"><class>normal_class</class><visual>actors\\normal</visual></specific_character>'
    local function hero(label,visual)
        return '<specific_character id="hero" no_random="1"><name>'..label..'</name><icon>ui_'..label..'</icon><visual>'..(visual or 'custom\\unique')..'</visual></specific_character>'
    end
    put('gameplay\\profiles.xml',profiles,true)
    put('gameplay\\characters.xml','<xml>\n'..ordinary..'\n#include "gameplay\\unique\\part-*.xml"\n</xml>',true)
    put('gameplay\\unique\\part-a.xml',hero('First','custom\\old'),true)
    put('gameplay\\unique\\part-z.xml',hero('DUX'),false)
    put('gameplay\\unique\\unmatched.txt',hero('Wrong'),false)
    put('actors\\normal.ogf','skeleton')
    put('custom\\unique.ogf','skeleton')
    put('custom\\old.ogf','skeleton')
    module('anomaly_donation_bridge_npc_visuals',env)
    local function scan()
        local entries={{section_id='normal',kind='npc',available=true,quarantine_reason='none'},
            {section_id='story',kind='npc',available=false,quarantine_reason='story_unique'}}
        local ok,n=env.append_special(entries,ini)
        assert(not next(temp),'temporary file leak')
        assert(stats.listed==stats.freed,'VFS list leak')
        return ok,n,entries
    end
    return {env=env,put=put,files=files,stats=stats,scan=scan,hero=hero,ordinary=ordinary}
end

local function expect_one(s,label)
    local ok,n,rows=s.scan()
    assert(ok and n==1 and #rows==3,tostring(s.env.failure_reason()))
    assert(rows[3].display_name:find(label,1,true),rows[3].display_name)
    assert(s.env.resolve(rows[3].section_id)=='custom\\unique.ogf')
    assert(s.env.resolve_icon(rows[3].section_id)=='ui_'..label)
    return rows[3].section_id
end

local s=sandbox({duplicate=true})
local old_id=expect_one(s,'DUX')
assert(s.stats.copied>0 and s.stats.listed==1)
print('PASS wildcard directory: merged visible files, packed fallback, sorted overrides, mixed case duplicate suppression, portrait identity')

for _,short in ipairs({false,true}) do
    s=sandbox({short_names=short})
    s.put('gameplay\\characters.xml',s.ordinary..'\n#include "root*.xml"')
    s.put('root1.xml',s.hero('Root'))
    expect_one(s,'Root')
end
print('PASS root masks: FS file objects with NameFull or NameShort')

s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "local-*.xml"')
s.put('gameplay\\local-1.xml',s.hero('Relative'),true);expect_one(s,'Relative')
s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "unique/part-*.xml"')
expect_one(s,'DUX')
print('PASS relative wildcard fallback: same directory and subdirectory; forward slash normalization')

s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "gameplay\\unique\\part-?.xml"')
s.put('gameplay\\unique\\part-zz.xml',s.hero('Wrong'));s.put('gameplay\\unique\\part-z-xml',s.hero('Wrong'))
expect_one(s,'DUX')
print('PASS masks: one-character wildcard, literal dot and hyphen, anchored matching')

s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "gameplay\\unique\\part-*.xml"\n#include "gameplay\\unique\\part-*.xml"')
expect_one(s,'DUX');assert(s.stats.listed==1)
print('PASS repeated includes: deterministic cache, no repeated enumeration')

s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "gameplay\\unique\\part-z.xml"')
expect_one(s,'DUX')
print('PASS plain includes unchanged')

s=sandbox();old_id=expect_one(s,'DUX')
s.put('gameplay\\unique\\part-z.xml','#include "gameplay\\unique\\part-*.xml"')
local ok,reason,rows=s.scan()
assert(not ok and reason=='special_visual_include_cycle_or_depth' and #rows==2 and not s.env.resolve(old_id))
print('PASS wildcard cycles: bounded failure, no partial catalogue or stale model ID')

for _,path in ipairs({'..\\part-*.xml','C:\\part-*.xml','gameplay\\*\\part.xml','part%*.xml'}) do
    s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "'..path..'"')
    ok,reason,rows=s.scan();assert(not ok and #rows==2,tostring(path))
end
print('PASS invalid include paths rejected')

s=sandbox({list_size=20001});ok,reason,rows=s.scan()
assert(not ok and reason=='special_visual_include_entry_limit' and #rows==2)
s=sandbox({list_error=true});ok,reason,rows=s.scan();assert(not ok and #rows==2)
print('PASS listing limits/errors: clean failure and released list handles')

-- The native XML preprocessor reads the quoted name without anchoring the
-- closing quote to end-of-line.
s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n#include "gameplay\\unique\\part-z.xml" // profile comment')
ok,reason,rows=s.scan()
assert(ok and reason==1,'trailing-comment include is still omitted')
print('PASS trailing-comment include')

s=sandbox();s.put('gameplay\\characters.xml',s.ordinary..'\n<!-- #include "../bad.xml" -->\n<name>text #include "../bad.xml"</name>\n#include "gameplay\\unique\\part-z.xml"')
expect_one(s,'DUX')
print('PASS include-looking text inside XML/comment is not treated as a directive')
