"""Export original shadow, relief and immersive components by prefab path."""
import json


def build_effects(s,prefab_blob):
    objects={row['id']:row for row in s.rows_of(prefab_blob) if row['type'] in ('Transform','MonoBehaviour')}
    trees={i:s.tree({'m_PathID':i}) for i in objects}
    transforms={i:t for i,t in trees.items() if objects[i]['type']=='Transform'}
    locations={}
    def walk(i,path):
        t=transforms[i]
        locations[t['m_GameObject']['m_PathID']]=path
        for n,c in enumerate(t['m_Children']):walk(c['m_PathID'],path+[n])
    roots=[i for i,t in transforms.items() if not t['m_Father']['m_PathID']]
    assert len(roots)==1
    walk(roots[0],[])
    effects = []
    emissive = False
    curve_properties = dict(ChangeColorCurve='_ChangeColor',LightColorIntensityCurve='_LightColorIntensity',
        LightEmitIntensityCurve='_LightEmitIntensity',LightPower='_LightPower',ShadowDecayCurve='_ShadowDecay',
        EffSwitchColor='_Switch',EffAdditiveIntensity='_AdditiveIntensity',EffColor3Blend='_FlashBlend',
        EffEmissiveIntensity='_EmissiveIntensity')
    for i,t in trees.items():
        if objects[i]['type']!='MonoBehaviour': continue
        script = s.tree(t['m_Script'])
        kind = script.get('m_ClassName')
        # EmissiveObject.UseEmissiveStatic: the bloom only runs for cards carrying one.
        emissive = emissive or kind=='EmissiveObject'
        if kind not in ('CardUVShadow','CardMReliefSRObject','CardImmersiveObject','CardURObject','GlitterFlowMaps','CardMRRObject','CardDualMRRObject','CardMSRObject','CardDualMSRObject','CardDualNoParallaxObject','CardMSSRObject','CardMarbleLayer','CardAncientObject','CardFutureObject','KiraPuyoObject','CircularKiraObject','CardRGBObject'): continue
        if kind=='CardRGBObject':
            # 1.8.1 OnEnable discovers renderers by the original shader SearchTag;
            # it derives Oklab vectors from each material's own base colours.
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardDualNoParallaxObject':
            settings=s.tree(t['_settings'])
            fields=('TypeIconHighlightTranslateByTiltingLeft','TypeIconHighlightTranslateByTiltingRight')
            # In 1.8.1 this auto-property backing field is not serialized. The
            # native constructor (RVA 0x4A2D560) leaves offset 0xB8 at CLR false;
            # only the accessor pair reads/writes it, not the animation methods.
            stopped_field='<IsAnimationStopped>k__BackingField'
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in ('_animStartDegree','_animTimeScale','_animDuration')},
                isAnimationStopped=bool(t.get(stopped_field,False)),
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_settings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CircularKiraObject':
            scalars=('_defaultCircularAnglePattern','_defaultCircularAngleManual','_tiltPower','_tiltThreshold',
                '_tiltStateChangeDelay','_rotateAccel','_brakeDuration','_primTypeASymmetryCount','_primTypeBSymmetryCount',
                '_primTypeCSymmetryCount','_meshDivideCount','_moveAngleScale','_centerIntensity','_fadeOut','_fadeOutEnd',
                '_expandLength','_expandPower','_limitLengthRatio','_LimitAdjustCurvePower','_LimitAdjustSpeed',
                '_distanceFadeOutSpeed','_distanceFadeOutCurvePower')
            def component_path(key):
                ref=t[key]
                return dict(indices=locations[s.tree(ref)['m_GameObject']['m_PathID']]) if ref['m_PathID'] else None
            prims=[]
            for original in t['_prims']:
                prim=dict(original)
                for flag in ('UseMorphing','UseMorphingNoise','ReverseRotation'):prim[flag]=bool(prim[flag])
                prims.append(prim)
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in scalars},primitives=prims,useLengthLimit=bool(t['_useLengthLimit']),
                useDistanceFadeOut=bool(t['_useDistanceFadeOut']),isAnimationStopped=bool(t['<IsAnimationStopped>k__BackingField']),
                **{k[1:]+'Path':component_path(k) for k in ('_movingKiraARenderer','_movingKiraBRenderer',
                    '_trailKiraARenderer','_trailKiraBRenderer','_trailKiraAMeshFilter','_trailKiraBMeshFilter')},
                enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='KiraPuyoObject':
            settings=s.tree(t['_settings']); curve=settings['Curve']
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                rendererIndices=locations[s.tree(t['_renderer'])['m_GameObject']['m_PathID']],
                **{k[1:]:t[k] for k in ('_rampRepeat','_scrollScale','_scrollOffset','_vertScaleSpeed','_scaleAnimationOffset')},
                curves=[dict(property='Curve',keys=curve['m_Curve'],nativeJson=json.dumps(dict(curve=curve)),
                             preWrap=curve['m_PreInfinity'],postWrap=curve['m_PostInfinity'])],
                settingsMin=settings['Min'],settingsMax=settings['Max'],settingsSource=s.asset(t['_settings']),
                enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardMSSRObject':
            settings=s.tree(t['_settings'])
            fields=('EffSwitchColor','EffAdditiveIntensity','EffColor3Blend','EffEmissiveIntensity')
            scalars=('_animStartDegree','_animTimeScale','_animDuration','_useSpeedAdjust',
                     '_recordingTime','_minTiltSpeed','_maxTiltSpeed','_minAnimSpeed','_maxAnimSpeed')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in scalars},
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_settings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardFutureObject':
            fields=('_animationTexFrameCount','_animationFrameCount','_animSwitchSpeed','_animFrameOffset','_skipAnimThreshold','_accellRatio')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in fields},isAnimationStopped=bool(t['<IsAnimationStopped>k__BackingField']),
                enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardAncientObject':
            settings=s.tree(t['_animCurveSettings'])
            fields=('ZuzuA','ZuzuB','ZuzuC','Zzzzz','ZuzuGoal','ShakeIntensity')
            scalars=('_animCurveScale','_animStartDelayRangeA','_animStartDelayRangeB','_changeRangeStart','_changeRangeEnd',
                '_zuzuGoalAnimThreshold','_goalThreshold','_scrolls','_scrollLength','_shapeChangeSpeed','_dot2Multiply',
                '_accellRatio','_diffOffset','_shakeAIntensity','_shakeAFrequency','_shakeBIntensity','_shakeBFrequency',
                '_shakeSpeed','_noiseScale','_frictionScale','_maxFriction','_startSandBaseEmissionRate',
                '_middleSandBaseEmissionRate','_endSandBaseEmissionRate')
            def particle_paths(key):
                return [dict(indices=locations[s.tree(ref)['m_GameObject']['m_PathID']],source=s.asset(ref))
                        for ref in t[key] if ref['m_PathID']]
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in scalars},isAnimationStopped=bool(t.get('<IsAnimationStopped>k__BackingField',False)),
                sandParticlePaths=particle_paths('_sandParticleSystems'),sand2ParticlePaths=particle_paths('_sand2ParticleSystems'),
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_animCurveSettings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardMarbleLayer':
            remap=t['_defaultNoiseRemapSettings']
            fields=('DefaultRemapCurve','TiltRemapCurve','RemapRemapCurve')
            scalars=('_tiltPower','_delayTime2','_pointAccel','_shearAccel','_dorodoroDistance','_resistancePower',
                     '_minDorodoroCoef','_maxPointSpeed','_minPointSpeed','_goalThreshold','_pointMoveByTilt','_pointForceChangeByTilt')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                rendererIndices=locations[s.tree(t['_renderer'])['m_GameObject']['m_PathID']],
                **{k[1:]:t[k] for k in scalars},useMarbleDelay=bool(t['_useMarbleDelay']),points=t['_points'],
                remapLabel=remap['CurveLabel'],remapResolution=remap['Resolution'],
                curves=[dict(property=field,keys=remap[field]['m_Curve'],nativeJson=json.dumps(dict(curve=remap[field])),
                             preWrap=remap[field]['m_PreInfinity'],postWrap=remap[field]['m_PostInfinity']) for field in fields],
                enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardDualMSRObject':
            # Original 1.8.1 DualMSR settings have transition, hologram and
            # type-icon curves, with separate flash/outline clocks. The serialized
            # component has no legacy MSR parallax or aura parameters.
            settings=s.tree(t['_settings'])
            fields=('OutlineReflectCenterX','OutlineReflectColorIntensity','ColorIntensityNoiseStrength',
                    'BGTransitionSwitchCurve','FrameTransitionSwitchCurve','FrameBandWidthCurve','HoloPowerCurve',
                    'TypeIconHighlightTranslateByTiltingLeft','TypeIconHighlightTranslateByTiltingRight',
                    'EffSwitchColor','EffAdditiveIntensity','EffColor3Blend','EffEmissiveIntensity')
            scalars=('_transitionAngle','_transitionBane','_transitionNensei','_animStartDegree',
                     '_flashAnimTimeScale','_flashAnimDuration','_outlineAnimTimeScale','_outlineAnimDuration',
                     '_timeOffset','_minTiltSpeed','_maxTiltSpeed','_minAnimSpeed','_maxAnimSpeed',
                     '_intensityNoiseSpeed','_reflectFlipBookMaxSpeed','_reflectStartBane','_reflectStartNensei',
                     '_reflectEndBane','_reflectEndNensei','_checkRotatingTime','_rotatingEndThreshold','_rotatingStartThreshold')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in scalars},isAnimationStopped=bool(t['_isAnimationStopped']),
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_settings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardMSRObject':
            settings=s.tree(t['_settings'])
            fields=('OutlineReflectCenterX','OutlineReflectColorIntensity','ColorIntensityNoiseStrength',
                    'OutlineReflectFlipBookAnim','OutlineReflectStartSpeed','OutlineReflectEndSpeed',
                    'AuraTransparency','ParallaxTransparency','ParallaxTranslate','ParallaxAppearTransparency',
                    'ParallaxAppearTranslate','ParallaxDisappearTransparency','ParallaxDisappearTranslate')
            scalars=('_animStartDegree','_animTimeScale','_animDuration','_endAnimDuration','_stopAnimTiming',
                     '_timeOffset','_intensityNoiseSpeed','_reflectFlipBookMaxSpeed','_reflectStartBane',
                     '_reflectStartNensei','_reflectEndBane','_reflectEndNensei','_checkRotatingTime',
                     '_rotatingEndThreshold','_rotatingStartThreshold','_disappearBane','_disappearNensei',
                     '_appearBane','_appearNensei')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in scalars},isAnimationStopped=bool(t['_isAnimationStopped']),
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_settings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardMRRObject':
            settings = s.tree(t['_settings'])
            fields = ('ChangeColorCurve','LightColorIntensityCurve','LightEmitIntensityCurve','LightPower',
                      'Layer2UVXTranslateByTiltingLeft','Layer2UVXTranslateByTiltingRight',
                      'Layer2ColorPower','Layer2EmissiveIntensity','EffSwitchColor','EffAdditiveIntensity',
                      'EffColor3Blend','EffEmissiveIntensity','FlashIntensity','FlashRadialScaling','FlashRadialAnim')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in ('_animStartDegree','_animTimeScale','_animDuration','_flashRadialStartOffset',
                                      '_useSpeedAdjust','_recordingTime','_minTiltSpeed','_maxTiltSpeed','_minAnimSpeed','_maxAnimSpeed')},
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_settings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind=='CardDualMRRObject':
            # Original 1.8.1 DualMRR has its own transition/icon curves and no
            # MRR speed-adjust history. Renderer groups are discovered by the
            # native controller's SearchTags, not serialized target-name guesses.
            settings=s.tree(t['_settings'])
            fields=('ChangeColorCurve','LightColorIntensityCurve','LightEmitIntensityCurve','LightPower',
                    'Layer2UVXTranslateByTiltingLeft','Layer2UVXTranslateByTiltingRight',
                    'Layer2ColorPower','Layer2EmissiveIntensity','EffSwitchColor','EffAdditiveIntensity',
                    'EffColor3Blend','EffEmissiveIntensity','FlashIntensity','FlashRadialScaling','FlashRadialAnim',
                    'TransitionBoundsCurve','TransitionRangeCurve','TypeEffectTransitionSwitchCurve',
                    'TypeIconHighlightTranslateByTiltingLeft','TypeIconHighlightTranslateByTiltingRight')
            scalars=('_animStartDegree','_animTimeScale','_animDuration','_flashRadialStartOffset',
                     '_transitionAngle','_transitionBoundaryOffset')
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k[1:]:t[k] for k in scalars},isAnimationStopped=bool(t['_isAnimationStopped']),
                curves=[dict(property=field,keys=settings[field]['m_Curve'],nativeJson=json.dumps(dict(curve=settings[field])),
                             preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity']) for field in fields],
                settingsSource=s.asset(t['_settings']),enabled=bool(t['m_Enabled']),fidelityValidated=False))
            continue
        if kind in ('CardURObject','GlitterFlowMaps'):
            effect = dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i))
            if kind=='GlitterFlowMaps':
                effect.update({k[1:]:v for k,v in t.items() if k.startswith('_') and k!='_renderer'})
                effect['rendererIndices']=locations[s.tree(t['_renderer'])['m_GameObject']['m_PathID']]
            effects.append(effect)
            continue
        if kind=='CardImmersiveObject':
            effects.append(dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                **{k:t[k] for k in ('IllustAlpha','TextAlpha','EmitSwitch','EmitIntensity','Darkness')}))
            continue
        effect = dict(kind=kind,indices=locations[t['m_GameObject']['m_PathID']],sourceId=str(i),
                      translateOffset=t['_translateOffset'],lightDir=t['_lightDir'])
        if kind=='CardMReliefSRObject':
            effect.update(animStartDegree=t['_animStartDegree'],animTimeScale=t['_animTimeScale'],animDuration=t['_animDuration'])
            settings = s.tree(t['_settings'])
            effect['curves'] = [dict(property=prop,keys=settings[field]['m_Curve'],
                nativeJson=json.dumps(dict(curve=settings[field])),
                preWrap=settings[field]['m_PreInfinity'],postWrap=settings[field]['m_PostInfinity'])
                for field,prop in curve_properties.items()]
            effect['settingsSourceId'] = str(t['_settings']['m_PathID'])
        effects.append(effect)

    return dict(effects=effects,emissive=emissive)
