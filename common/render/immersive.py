"""Original IM movie, shader animation coefficients and particle timing."""
import hashlib
import math
import struct
import subprocess
import zlib
import UnityPy
from .tools import ffmpeg

SOURCE_ROOTS = [
    'Common/Feature/Immersive/Prefabs/ImmersiveUITimelines.prefab_bundles',
    'Common/Feature/Immersive/Prefabs/ImmersiveModelTimelines.prefab_bundles',
    'Common/Feature/Immersive/Settings/ImmersiveSettings.asset_bundles',
    'Common/Shader/Common/Vfx/BattleEffect.shader_bundles',
    'Common/Shader/Common/Vfx/UIEffect.shader_bundles',
    'Common/CardNew/System/Prefabs/UIImmersiveStencil.prefab_bundles',
]

def movie_dimensions(payload):
    """CRI VIDEO_HDRINFO distinguishes display dimensions from H.264 padding."""
    offset=0
    while True:
        offset=payload.find(b'@UTF',offset)
        if offset<0:raise ValueError('Original CRI video header absent')
        base=offset+8
        rows,strings,_,name,count,row_size,row_count=struct.unpack_from('>IIIIHHI',payload,base)
        string_base=base+strings
        def string(index):return payload[string_base+index:payload.index(b'\0',string_base+index)].decode()
        if string(name)!='VIDEO_HDRINFO':offset+=4;continue
        position=offset+32;row=base+rows;result={}
        for _ in range(count):
            flag=payload[position];key=string(struct.unpack_from('>I',payload,position+1)[0]);position+=5
            kind=flag&15;size={0:1,1:1,2:2,3:2,4:4,5:4,6:8,7:8,8:4,10:4,11:8}[kind]
            if flag&0xF0==0x50:
                result[key]=int.from_bytes(payload[row:row+size],'big');row+=size
            elif flag&0xF0==0x30:
                result[key]=int.from_bytes(payload[position:position+size],'big');position+=size
            elif flag&0xF0==0x10:result[key]=0
            else:raise ValueError('Unsupported original UTF storage')
        return {key:result[key] for key in ('width','height','disp_width','disp_height')}

def customizer_materials(assets,script,location):
    """PropertyCustomizer builds particle materials at runtime; this is its recipe per renderer."""
    material_plans=[]
    for obj in assets.rows():
        if obj['type']!='MonoBehaviour' or assets.bundle_name(obj['bundle'])=='Common/Font_bundles':continue
        tree=assets.tree({'m_PathID':obj['id']})
        if script(tree)!='PropertyCustomizer':continue
        loc=location(tree['m_GameObject'])
        if not loc:continue
        refs={r['rid']:r for r in tree['references']['RefIds']}
        effect=refs[tree['_effectType']['rid']]['data']
        core=refs[effect['_coreEffectSettings']['rid']]['data']
        if not core['_enabled']:continue
        mods=[refs[p['rid']] for p in core['_modifiers']]
        floats=[];vectors=[];textures=[];gradients=[];keywords=[];streams=[];ui_scale=1;clipped=False
        def num(n,v):floats.append(dict(name=n,value=v))
        def vec(n,v):vectors.append(dict(name=n,value=v))
        def custom(n,c):
            num(n+'Scale',c['_scale']);num(n+'Offset',c['_offset']);num(n+'CustomData',c['_usage'])
        def texture(n,c):
            ptr=c['_texture'];enabled=bool(ptr['m_PathID']);num('_Use'+n,int(enabled))
            if enabled:
                tex=assets.object(ptr)
                textures.append(dict(property=n,name=tex['name'],bundleName=assets.bundle_name(tex['bundle'])))
                keywords.append(n.upper()+'_ON')
            vec(n+'_ST',[c['_textureTiling']['x'],c['_textureTiling']['y'],c['_textureOffset']['x'],c['_textureOffset']['y']])
            radians=c['_textureRotation']*math.pi/180
            vec(n+'RotationVector',[math.cos(radians),-math.sin(radians),math.sin(radians),math.cos(radians)])
            vec(n+'SamplerState',[c['_textureWrapModeU'],c['_textureWrapModeV'],0,0]);num(n+'UVMode',c['_uvMode'])
            custom(n+'ScrollU',c['_textureScrollU']);custom(n+'ScrollV',c['_textureScrollV'])
        for mod in mods:
            kind=mod['type']['class'];d=mod['data']
            if kind=='AlbedoModifier':texture(d['_textureName'],d['_albedoComponent'])
            elif kind=='GeneralSettingsModifier':
                num('_CameraOffset',d['_emitterCameraOffset']);num('_CameraOffsetVector',d['_emitterCameraOffset']);num('_EmissivePower',d.get('_emissivePower',0))
            elif kind in ('BattleBlendSettingsModifier','BlendSettingModifier','CardPartsEffectStencilModifier'):
                clipped=clipped or kind=='CardPartsEffectStencilModifier' and not d.get('_visibleOutsideCard',True)
                blend=d['_blendControlType'];transparent=bool(d['_opacityType']);queue=3000 if transparent else 2000
                num('_ZWrite',0 if transparent else 1);num('_Cull',d.get('_cullMode',d.get('_renderFace',0)));num('_BlendMode',blend)
                num('_BlendSrcColor',1);num('_BlendDstColor',(10 if blend==0 else 1 if blend==1 else 3) if transparent else 0)
                num('_BlendSrcAlpha',1);num('_BlendDstAlpha',10 if transparent else 0)
                num('_BlendOpColor',[0,3,4,1,2][d['_blendOpColor']]);num('_BlendOpAlpha',[0,3,4,1,2][d['_blendOpAlpha']])
                num('_AlphaClipThreshold',d.get('_alphaClipThreshold',0));num('_Opacity',int(transparent));queue+=d['_queueOffset']
                # The Battle shader reads _IsTransparent: left at 0, the emission of every transparent particle was weighted by its alpha twice.
                num('_IsTransparent',int(transparent))
                num('_IsAlphaBlend',int(blend==0));num('_UseBlendBuffer',1)
                if 'MinFadeDistanceParams' in d:vec('_DistanceFadeParams',[d['MinFadeDistanceParams']['x'],d['MinFadeDistanceParams']['y'],0,0])
                keywords.append('_OPACITY_TRANSPARENT' if transparent else '_OPACITY_OPAQUE')
            elif kind in ('GradationMapModifier','RimGradationModifier') and d['_inUse']:
                n=d['_textureName'];g=d['_gradationMapComponent'];num('_Use'+n,1);custom(n+'Index',g['_gradationIndex']);keywords.append(d.get('_keyword',d.get('_shaderKeyword','')))
                keys=[]
                for gradient in g['_gradationList']:
                    keys.append(dict(mode=gradient['m_Mode'],colors=[dict(time=gradient[f'ctime{i}']/65535,color=gradient[f'key{i}']) for i in range(gradient['m_NumColorKeys'])],alphas=[dict(time=gradient[f'atime{i}']/65535,alpha=gradient[f'key{i}']['a']) for i in range(gradient['m_NumAlphaKeys'])]))
                gradients.append(dict(property=n,resolution=g['_resolution'],filter=g['_filterMode'],keys=keys))
            elif kind=='SwitchMaskModifier' and d['_inUse']:
                n=d['_textureName'];texture(n,d['_maskComponent']);custom(n+'Threshold',d['_maskThreshold']);num(n+'MaskType',d['_maskType'])
            elif kind in ('ShurikenModifier','CardPartsShurikenModifier'):streams=d['_vertexStreams']
            elif kind=='UIModifier':ui_scale=d['_scale']
            elif kind=='FlowModifier' and d['_inUse']:
                n=d['_textureName'];texture(n,d['_flowComponent']);custom('_FlowPower',d['_flowPower']);num('_FlowChangeMasks',d['_flowChangeMasks'])
            elif kind=='TextureSheetAnimationModifier' and d['_inUse']:raise ValueError('An active texture sheet modifier needs porting')
        material_plans.append(dict(**loc,shader={'UIAlbedo':'Lettuce/Effect/Standard/UI','CardPartsEffect':'Lettuce/Effect/Standard/CardParts'}.get(core['_shaderName'],'Lettuce/Effect/Standard/Battle'),floats=floats,vectors=vectors,textures=textures,gradients=gradients,keywords=keywords,clipped=clipped,streams=streams,uiScale=ui_scale,queue=queue))
    return material_plans

def build_immersive(game,assets,illustration,movie_name,out):
    def script(t):
        return assets.tree(t['m_Script']).get('m_ClassName','') if t.get('m_Script',{}).get('m_PathID') else ''
    def component(name):
        return assets.tree({'m_PathID':assets.find('MonoBehaviour',lambda t:script(t)==name)['id']})
    settings = component('ImmersiveSettings')
    ui, model = component('ImmersiveUITimelines'), component('ImmersiveModelTimelines')
    fields = ['CardIllustAlpha','CardScale','CardTextAlpha','CardFrameEmitSwitch','CardFrameEmitIntensity','CardFrameDarkness']
    hashes = {zlib.crc32(n.encode()):n for n in fields}
    def animation(director):
        timeline = assets.tree(assets.tree(director)['m_PlayableAsset'])
        for pointer in timeline['m_Tracks']:
            track = assets.tree(pointer)
            if script(track) != 'AnimationTrack': continue
            clip = track.get('m_InfiniteClip')
            if not clip or not clip['m_PathID']:
                clip = assets.tree(track['m_Clips'][0]['m_Asset'])['m_Clip']
            t = assets.tree(clip)
            data = t['m_MuscleClip']['m_Clip']['data']
            stream = data['m_StreamedClip']
            raw = struct.pack('<'+'I'*len(stream['data']), *stream['data'])
            channels = [[] for _ in range(stream['curveCount'])]
            offset = 0
            while offset < len(raw):
                time, count = struct.unpack_from('<fI', raw, offset); offset += 8
                for _ in range(count):
                    index,a,b,c,d = struct.unpack_from('<I4f',raw,offset); offset += 20
                    # The pre-frame supplies initial values; infinity is a sentinel.
                    if math.isfinite(time):channels[index].append(dict(time=max(0,time),a=a,b=b,c=c,d=d))
            bindings = t['m_ClipBindingConstant']['genericBindings']
            curves = [dict(field=hashes[b['attribute']], keys=channels[i]) for i,b in enumerate(bindings[:len(channels)])]
            constants = [dict(field=hashes[b['attribute']],value=value) for b,value in zip(bindings[len(channels):],data['m_ConstantClip']['data'])]
            return dict(sourceId=str(clip['m_PathID']),name=t['m_Name'],duration=t['m_MuscleClip']['m_StopTime'],curves=curves,constants=constants)
        raise ValueError('Original controller animation absent')
    def game_name(pointer):
        t=assets.tree(pointer)
        if 'm_GameObject' in t: t=assets.tree(t['m_GameObject'])
        return t['m_Name']
    effect_animations=[]
    def particles(director, start=0):
        d=assets.tree(director);timeline=assets.tree(d['m_PlayableAsset'])
        refs=dict(d['m_ExposedReferences']['m_References']);result=[]
        bindings={p['key']['m_PathID']:p['value'] for p in d.get('m_SceneBindings',[])}
        for pointer in timeline['m_Tracks']:
            track=assets.tree(pointer)
            for clip in track.get('m_Clips',[]):
                play=assets.tree(clip['m_Asset']);kind=script(play)
                if kind=='AnimationPlayableAsset' and start==0 and bindings.get(pointer['m_PathID'],{}).get('m_PathID'):
                    target=assets.tree(bindings[pointer['m_PathID']]);anim=assets.tree(play['m_Clip'])
                    if anim['m_Name']=='efp_Out_IMS_Start00_anim':
                        loc=location(target['m_GameObject'])
                        raw_data=anim['m_MuscleClip']['m_Clip']['data']['m_StreamedClip']
                        if anim['m_ClipBindingConstant']['genericBindings']!=[dict(path=0,attribute=1,script={'m_FileID':0,'m_PathID':0},typeID=4,customType=0,isPPtrCurve=0,isIntCurve=0,isSerializeReferenceCurve=0)]:
                            raise ValueError('Unsupported original effect transform animation')
                        raw=struct.pack('<'+'I'*len(raw_data['data']),*raw_data['data']);offset=0;channels=[[],[],[]]
                        while offset<len(raw):
                            tm,count=struct.unpack_from('<fI',raw,offset);offset+=8
                            for _ in range(count):
                                index,aa,bb,cc,dd=struct.unpack_from('<I4f',raw,offset);offset+=20
                                if math.isfinite(tm):channels[index].append(dict(time=max(0,tm),a=aa,b=bb,c=cc,d=dd))
                        effect_animations.append(dict(**loc,start=clip['m_Start'],duration=clip['m_Duration'],length=anim['m_MuscleClip']['m_StopTime'],curves=[dict(field=n,keys=k) for n,k in zip(('x','y','z'),channels)]))
                source_go=play.get('sourceGameObject',{})
                target=refs.get(source_go.get('exposedName'),source_go.get('defaultValue',{}))
                if not target or not target.get('m_PathID'): continue
                tm=start+clip['m_Start']
                if kind=='LtParticlePlayableAsset':
                    result.append(dict(**location(target),name=game_name(target),start=tm,duration=clip['m_Duration'],seed=play['particleRandomSeed']))
                elif kind=='ControlPlayableAsset':
                    go=assets.tree(target)
                    for comp in go.get('m_Component',[]):
                        row=assets.object(comp['component'])
                        if row['type']=='PlayableDirector': result += particles(comp['component'],tm)
        return result
    marker = next(assets.tree({'m_PathID':o['id']}) for o in assets.rows() if o['type']=='MonoBehaviour' and assets.bundle_name(o['bundle'])!='Common/Font_bundles' and script(assets.tree({'m_PathID':o['id']}))=='PlayImmersiveVideoSignalMarker')
    def location(go):
        indices=[]
        if 'm_GameObject' in assets.tree(go):go=assets.tree(go)['m_GameObject']
        while True:
            t=assets.tree(go);tr=assets.tree(t['m_Component'][0]['component'])
            if t['m_Name'] in ('ImmersiveUITimelines','ImmersiveModelTimelines'):
                return dict(root='ui' if t['m_Name']=='ImmersiveUITimelines' else 'model',indices=list(reversed(indices)))
            father=tr['m_Father']
            if not father['m_PathID']:return None
            parent=assets.tree(father)
            indices.append(next(i for i,p in enumerate(parent['m_Children']) if p['m_PathID']==t['m_Component'][0]['component']['m_PathID']))
            go=parent['m_GameObject']
    starts = particles(ui['StartImmersiveDirector']) + particles(model['StartImmersiveDirector'])
    ends = particles(ui['EndImmersiveDirector']) + particles(model['EndImmersiveDirector'])
    material_plans=customizer_materials(assets,script,location)
    movie=game.by_name[movie_name]
    data=next(o.read() for o in UnityPy.load(str(game.path(movie))).objects if o.type.name=='TextAsset').m_Script
    payload=data.encode('utf-8','surrogateescape') if isinstance(data,str) else bytes(data)
    dimensions=movie_dimensions(payload)
    source_sha=hashlib.sha256(payload).hexdigest()
    usm=out/'original.usm';mp4=out/'movie.mp4'
    usm.write_bytes(payload)
    try:
        subprocess.run([ffmpeg(),'-v','warning','-y','-i',str(usm),'-map','0:v','-map','0:a','-c:v','copy','-c:a','aac','-b:a','192k','-movflags','+faststart',str(mp4)],check=True)
    finally:
        usm.unlink(missing_ok=True)
    w,h,dw,dh=(dimensions[k] for k in ('width','height','disp_width','disp_height'))
    return dict(movieRectSize=[dw,dh],movieDimensions=dimensions,
              movieUv=[0,(h-dh+1)/h,(dw-1)/w,(dh-1)/h],
              holdStart=settings['YotyoStartLongTapTime'],holdDuration=settings['YotyoTime'],cancelDuration=settings['CancelTime'],
              movieStart=marker['m_Time'],endFramesRemaining=ui['EndNumberOfFramesRemaining'],
              start=animation(ui['StartImmersiveDirector']),end=animation(ui['EndImmersiveDirector']),
              startParticles=starts,endParticles=ends,effectAnimations=effect_animations,
              materials=material_plans,
              sourceMovie=movie['name'],sourceMovieSha256=source_sha,
              movieSha256=hashlib.sha256(mp4.read_bytes()).hexdigest(),videoReencoded=False,
              caveat='Original card stencil, polynomial animation and particle tracks; Unity video decoder replaces CRI. Exact transition parity unverified.')