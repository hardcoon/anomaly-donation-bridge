-- Engine-contract regression: run the actual standalone helpers with native
-- rq_result/OpenAL semantics, not only optimistic scene mocks.
local optional=assert(arg[3])
local names={"burer","pseudogiant","tushkano","poltergeist","bloodsucker","controller"}
local function vector()
    return {set=function(self,x,y,z) self.x,self.y,self.z=x,y,z;return self end}
end
for _,name in ipairs(names) do
    local file=assert(io.open(optional.."/anomaly_donation_bridge_effect_hardcoon_adb_"..name.."_form.script","rb"))
    local source=file:read("*a");file:close()
    assert(source:find('RegisterScriptCallback("actor_on_hit_callback",hurt)',1,true),name.." native hurt callback missing")
    assert(not source:find('RegisterScriptCallback("actor_on_hit",',1,true),name.." uses a nonexistent callback")
    local body=assert(source:match("(local function ray%b().-)\nlocal function"),name.." ray missing")
    local layers={}
    local player={position=function() return {x=-500,y=2,z=800} end}
    local env=setmetatable({vector=vector,actor=function() return player end,
        finite=function(n) return type(n)=="number" and n==n and math.abs(n)<1e8 end,
        plus=function(p,d,n) return {x=p.x+d.x*n,y=p.y+d.y*n,z=p.z+d.z*n} end,
        vec=function(p) return vector():set(p.x,p.y,p.z) end,
        rq_result=function() return {} end
    },{__index=_G})
    local calls=0
    env.level={ray_pick=function(from,dir,range,mode,result,ignore)
        assert(dir.x==0 and dir.y==0 and dir.z==1 and ignore==player)
        assert(range<=20 and from.z+range<=20.00001,"Traversal extended original range")
        calls=calls+1
        for _,layer in ipairs(layers) do
            if layer.z>=from.z and layer.z<=from.z+range then
                result.range=layer.z-from.z;result.object=layer.object;result.element=7
                if layer.object then
                    setmetatable(result,{__index=function(_,key)
                        if key=="material_flags" or key=="material_name" then error("Read invalid static material for a dynamic bone") end
                    end})
                else result.material_flags=layer.flags;result.material_name=layer.material end
                return true
            end
        end
        return false
    end}
    local chunk=assert(loadstring(body.."\nreturn ray",name.."_actual_ray"));setfenv(chunk,env)
    local ray=chunk();local from,dir={x=0,y=0,z=0},{x=0,y=0,z=1}
    local npc={}
    layers={{z=10,object=npc}}
    assert(ray(from,dir,20,2).object==npc,"Dynamic target lost")
    for _,material in ipairs({"materials\\grass","materials\\bush","materials\\bush_sux","materials\\leaves","materials/foliage"}) do
        layers={{z=1,flags=0,material=material},{z=10,object=npc}}
        assert(ray(from,dir,20,2).object==npc,"Vegetation without Passable blocked "..name..": "..material)
    end
    for _,material in ipairs({"materials\\tree_trunk","materials\\wood","materials\\glass","materials\\metal_fence","unknown"}) do
        layers={{z=1,flags=0,material=material},{z=10,object=npc}}
        assert(not ray(from,dir,20,2).object,"Solid material became transparent")
    end
    layers={{z=1,flags=128+4096,material="materials\\grass"},{z=10,object=npc}}
    assert(ray(from,dir,20,2).range==1,"ActorObstacle ignored")
    layers={}
    for i=1,40 do layers[#layers+1]={z=i*0.05,flags=128} end
    layers[#layers+1]={z=10,object=npc}
    assert(ray(from,dir,20,2).object==npc,"Dense foliage still blocked")
    layers={}
    for i=1,70 do layers[#layers+1]={z=i*0.05,flags=128} end
    calls=0;assert(not ray(from,dir,20,2).object and calls==64,"Unbounded traversal")
    layers={{z=1,flags=128},{z=21,object=npc}}
    assert(not ray(from,dir,20,2),"Out-of-range target hit")
    if name~="poltergeist" then
        local soundbody=assert(source:match("(local function play_sound%b().-)\nlocal function"))
        local form={sound_assets={step={"native_step"},melee={"native_attack"}},sounds={}}
        local played={}
        env.form=form;env.stop_sound=function() end
        env.sound_object=setmetatable({s2d=2,looped=1},{__call=function(_,resource) return {
            play_at_pos=function(self,owner,pos,delay,flags)
                played={resource=resource,pos=pos,flags=flags,owner=owner}
            end} end})
        local soundchunk=assert(loadstring(soundbody.."\nreturn play_sound"));setfenv(soundchunk,env)
        local play=soundchunk()
        assert(play("step",player:position(),0.7))
        assert(played.flags==2 and played.pos.x==0 and played.pos.y==0 and played.pos.z==0,"Mono 2D footstep pans with world position")
        assert(play("melee",player:position(),0.8))
        assert(played.flags==0 and played.pos.x==-500 and played.pos.z==800,"Attack lost spatial positioning")
    end
end
print("Metamorphosis native-contract regression passed: six actual ray helpers (dynamic bone metadata, named grass, dense foliage, solid cover, bounded range); five centred mono footstep helpers, spatial attacks preserved.")
