local root=assert(arg[1])
local env=setmetatable({},{__index=_G});local f=assert(loadfile(root..'/anomaly_donation_bridge_visual_safety.script'));setfenv(f,env);f()
local function u16(n)return string.char(n%256,math.floor(n/256)%256)end
local function u32(n)return u16(n%65536)..u16(math.floor(n/65536))end
local function chunk(id,s)return u32(id)..u32(#s)..s end
local bones={'root_stalker','bip01','bip01_head','bip01_spine','bip01_spine1','bip01_spine2','bip01_l_hand','bip01_r_hand',
    'bip01_l_foot','bip01_r_foot','bip01_tail','bip01_r_finger1','bip01_l_finger1','bip01_r_finger11','jaw_1'}
for i=16,47 do bones[i]='bone'..i end
local cycles={'head_idle_0','head_talk_0','norm_idle_0','norm_walk_fwd_0','norm_run_fwd_0','cr_idle_0',
    'norm_torso_0_idle_0','norm_torso_1_aim_0','norm_torso_2_aim_0'}
local function skeleton(list)
    local s=u32(#list);for i,name in ipairs(list)do s=s..name..'\0'..(i==1 and '' or 'root_stalker')..'\0'..string.rep('\0',60)end;return s
end
local function omf(list,anim)
    local p=u16(4)..u16(1)..'all\0'..u16(#list)
    for i,name in ipairs(list)do p=p..name..'\0'..u32(i-1)end
    p=p..u16(#anim);local keys=chunk(0,u32(#anim))
    for i,name in ipairs(anim)do
        p=p..name..'\0'..u32(0)..u16(0)..u16(i-1)..string.rep('\0',16)..u32(0)
        keys=keys..chunk(i,name..'\0'..u32(1)..string.rep(string.char(2)..string.rep('\0',6)..u16(32767)..string.rep('\0',12),#list))
    end
    return chunk(14,keys)..chunk(15,p)
end
local function ogf(list,ref,body)
    return chunk(1,string.char(4,3)..string.rep('\0',42))..chunk(13,body or skeleton(list))..chunk(16,'IK')..
        chunk(9,'children')..chunk(24,u32(1)..(ref or 'actors\\motions')..'\0')
end
local files={['actors\\safe.ogf']=ogf(bones),['actors\\motions.omf']=omf(bones,cycles)}
local temp,deleted={},0;local packed=false
env.io={open=function(path)
    local data=temp[path] or (not packed and files[path]);if not data then return end;local p=0
    return {seek=function(_,how,n)if how=='end'then p=#data elseif how=='set'then p=n end;return p end,
        read=function(_,n)local s=data:sub(p+1,p+n);p=p+#s;return s end,close=function()end}
end}
local fs={exist=function(_,alias,p)return files[p or alias]~=nil or temp[p or alias]~=nil end,
    update_path=function(_,alias,p)return p end,file_copy=function(_,src,dst)temp[dst]=assert(files[src])end,
    file_delete=function(_,p)temp[p]=nil;deleted=deleted+1 end}
function getFS()return fs end;function time_global()return 100 end
local cfg={stalker={damage='damage',bone_head='bip01_head',bone_shoulder='bip01_spine2',bone_spin='bip01_spine1',
    bone_model='bip01',weapon_bone0='bip01_r_finger1',weapon_bone1='bip01_l_finger1',weapon_bone2='bip01_r_finger11'},
    damage={default='1,1,1',bip01_spine2='1,1,1',jaw_1='1,1,1'}}
local ini={section_exist=function(_,s)return cfg[s]~=nil end,line_exist=function(_,s,k)return cfg[s] and cfg[s][k]~=nil end,
    r_string=function(_,s,k)return cfg[s][k]end,line_count=function(_,s)local n=0;for _ in pairs(cfg[s])do n=n+1 end;return n end,
    r_line=function(_,s,i)local keys={};for k in pairs(cfg[s])do keys[#keys+1]=k end;table.sort(keys);return true,keys[i+1],cfg[s][keys[i+1]] end}
assert(env.check('actors\\safe.ogf',ini,'stalker'))
assert(not env.check('actors\\safe.ogf',ini,nil),'unknown NPC config must not silently fall back to stock stalker')
packed=true;env.clear_cache();assert(env.check('actors\\safe.ogf',ini,'stalker') and deleted==2 and not next(temp),'archive checks must release scratch files')
local function denied(path,code)local ok,reason=env.check(path,ini,'stalker',true);assert(not ok and reason:find(code,1,true),tostring(reason));assert(not next(temp))end
denied('..\\unsafe.ogf','path_invalid')
local missing={};for _,b in ipairs(bones)do if b~='bip01_spine2' then missing[#missing+1]=b end end
files['actors\\missing_spine.ogf']=ogf(missing,'actors\\missing_spine');files['actors\\missing_spine.omf']=omf(missing,cycles)
denied('actors\\missing_spine.ogf','missing_bone_bip01_spine2') -- reproduces the reported CDamageManager failure before native spawn
files['actors\\mismatch.ogf']=ogf(missing)
denied('actors\\mismatch.ogf','animation_bone_count_mismatch')
files['actors\\trader.ogf']=ogf(bones,'actors\\trader');files['actors\\trader.omf']=omf(bones,{'sit_idle_0'})
denied('actors\\trader.ogf','missing_animation_')
files['actors\\cycle.ogf']=ogf(bones,nil,skeleton(bones):gsub('bip01\0root_stalker\0','bip01\0bip01\0'))
denied('actors\\cycle.ogf','bone_hierarchy_invalid')
files['actors\\wildcard.ogf']=ogf(bones,'actors\\extra_*');denied('actors\\wildcard.ogf','animation_wildcard_unverified')
files['actors\\truncated.ogf']=ogf(bones):sub(1,-2);denied('actors\\truncated.ogf','invalid_chunk')
files['actors\\no_motion.ogf']=ogf(bones,'actors\\absent');denied('actors\\no_motion.ogf','resource_missing')
cfg.damage.modpack_extra_bone='1,1,1';env.clear_cache();denied('actors\\safe.ogf','missing_bone_modpack_extra_bone')
cfg.damage.modpack_extra_bone=nil;cfg.stalker.bone_shoulder='custom_shoulder';env.clear_cache();denied('actors\\safe.ogf','missing_bone_custom_shoulder')
cfg.stalker.bone_shoulder='bip01_spine2';env.clear_cache();assert(env.check('actors\\safe.ogf',ini,'stalker'))
-- A later file replacement cannot bypass the final pre-spawn guard's forced read.
files['actors\\safe.ogf']=files['actors\\missing_spine.ogf'];denied('actors\\safe.ogf','missing_bone_bip01_spine2')
assert(not next(temp))
print('PASS special visual preflight: missing spine regression, installed damage/weapon/animation bones, OMF partition mismatch, trader-only motions, corruption, cached scan and forced final guard, archive cleanup')
