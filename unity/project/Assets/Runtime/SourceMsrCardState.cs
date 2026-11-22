using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CardMSRObject RVAs 47e994c..47ea974; port-L/native.
// Retain source update order, spring denominators and partial stop semantics.
public sealed class SourceMsrCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    readonly Dictionary<string,AnimationCurve> curves=new Dictionary<string,AnimationCurve>();
    readonly Queue<Vector3> distances=new Queue<Vector3>();
    Renderer[] auras,parallaxes,reflections;
    MaterialPropertyBlock block;
    Vector3 lastFront;
    float lastDegree,previousDelta,elapsedTime,startTime;
    float parallaxTilt,parallaxSpeed,parallaxTransparency,parallaxTranslate;
    float aura,center,intensity,noise,noiseOffset,flipSpeed,flipAcceleration,flipBlend,flipAnim;
    float animElapsed,timeParam;
    int animState,reflectionState;
    public bool Manual {get;set;}
    public bool IsAnimationStopped {get;set;}
    public int AnimationState => animState;
    public int ReflectionState => reflectionState;
    public float Elapsed => animElapsed;
    public float Threshold => settings.animStartDegree;
    public float Duration => settings.animDuration*settings.animTimeScale;
    public int AuraCount => auras.Length;
    public int ParallaxCount => parallaxes.Length;
    public int ReflectionCount => reflections.Length;
    public float[] Parameters => new[]{aura,settings.timeOffset+timeParam,parallaxTransparency,
        parallaxTranslate,center,intensity+noiseOffset,flipAnim,flipBlend};

    public void Initialize(SourceCardEffects.Effect source,List<string> log) {
        settings=source; block=new MaterialPropertyBlock();
        foreach(var curve in source.curves) {
            var value=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(curve.nativeJson).curve;
            if(value==null || value.length!=curve.keys.Length)
                throw new InvalidOperationException("Original MSR curve failed to deserialize: "+curve.property);
            curves.Add(curve.property,value);
        }
        auras=SourceCardEffects.Collect(transform,"Card-Aura");
        parallaxes=SourceCardEffects.Collect(transform,"Card_Parallax_Transparent_Translate");
        reflections=SourceCardEffects.Collect(transform,"ShadowBox_MSR");
        IsAnimationStopped=source.isAnimationStopped;
        ResetState();
        // Awake: Validate, Initialize, LateUpdate, then set spring to current tilt.
        Step(transform.TransformDirection(Vector3.back),0);
        parallaxSpeed=0;
        var front=transform.TransformDirection(Vector3.back);
        parallaxTilt=Mathf.Sin(Mathf.Atan2(new Vector2(front.x,front.y).magnitude,-front.z)*3);
        // The game's card enters rotating, so its first resting view is past this animation (measured on PK_20_011180_00).
        Evaluate(settings.animDuration); Apply();
        log.Add("OriginalCardEffect=CardMSRObject sourceId="+source.sourceId+" auras="+auras.Length+
            " parallaxes="+parallaxes.Length+" reflections="+reflections.Length+" curves="+curves.Count+" native=1.7.5 fidelityValidated=False");
    }
    public void ResetState() {
        distances.Clear(); lastFront=Vector3.zero; lastDegree=0; previousDelta=0; elapsedTime=0;
        parallaxTilt=parallaxSpeed=parallaxTransparency=parallaxTranslate=0;
        noiseOffset=flipSpeed=flipAcceleration=flipBlend=flipAnim=animElapsed=timeParam=0;
        animState=reflectionState=0; startTime=Time.time; Evaluate(0);
    }
    void LateUpdate() {
        if(settings!=null && !Manual) Step(transform.TransformDirection(Vector3.back),Time.time>startTime ? Time.deltaTime : 0);
    }
    float Value(string name,float time) => curves[name].Evaluate(time);
    void Evaluate(float time) {
        aura=Value("AuraTransparency",time); center=Value("OutlineReflectCenterX",time);
        noise=Value("ColorIntensityNoiseStrength",time); intensity=Value("OutlineReflectColorIntensity",time);
    }
    public void Step(Vector3 front,float delta) {
        elapsedTime+=previousDelta; previousDelta=delta;
        float angle=Mathf.Atan2(new Vector2(front.x,front.y).magnitude,-front.z);
        float degree=angle*Mathf.Rad2Deg,tilt=Mathf.Sin(angle*3);
        distances.Enqueue(new Vector3((front-lastFront).magnitude,delta,elapsedTime));
        while(distances.Count>0 && elapsedTime-distances.Peek().z>settings.checkRotatingTime) distances.Dequeue();
        lastFront=front;
        if(!IsAnimationStopped) {
            float difference=tilt-parallaxTilt;
            bool equal=difference==0 || float.IsNaN(difference);
            float stiffness=equal ? settings.appearBane : settings.disappearBane;
            float damping=equal ? settings.appearNensei : settings.disappearNensei;
            parallaxSpeed+=delta*(stiffness*(difference-delta*parallaxSpeed)-parallaxSpeed*damping)/
                (delta*settings.disappearNensei+1);
            parallaxTilt=Mathf.Clamp01(parallaxTilt+delta*parallaxSpeed);
            parallaxTransparency=Value("ParallaxTransparency",parallaxTilt);
            parallaxTranslate=Value("ParallaxTranslate",parallaxTilt);
        }
        if(animState==2) {
            timeParam+=delta;
            if(degree<settings.animStartDegree) {animState=1; reflectionState=1;}
        } else if(animState==0 && lastDegree<settings.animStartDegree && degree>settings.animStartDegree) {
            animState=1; reflectionState=1;
        }
        if(animState==1) {
            animElapsed+=delta; timeParam+=delta; Evaluate(animElapsed/settings.animTimeScale);
            if(animElapsed>Duration) {animState=0; animElapsed=0; timeParam=0; reflectionState=2;}
        }
        lastDegree=degree;
        noiseOffset=animState==0 ? 0 : Mathf.PerlinNoise(elapsedTime*settings.intensityNoiseSpeed,0)*noise;
        float distance=0,interval=0;
        foreach(var sample in distances) {distance+=sample.x; interval+=sample.y;}
        float averageSpeed=distance/interval;
        if(reflectionState==0 && averageSpeed>settings.rotatingStartThreshold && tilt>0.004999999888241291f)
            reflectionState=1;
        if((reflectionState==1 && animState!=1 || reflectionState==2) && averageSpeed<settings.rotatingEndThreshold)
            reflectionState=0;
        if(reflectionState==0 || reflectionState==1) {
            float target=reflectionState==1 ? 1 : 0;
            float stiffness=reflectionState==1 ? settings.reflectStartBane : settings.reflectEndBane;
            float damping=reflectionState==1 ? settings.reflectStartNensei : settings.reflectEndNensei;
            flipAcceleration+=delta*(stiffness*((target-flipSpeed)-delta*flipAcceleration)-flipAcceleration*damping)/(delta*damping+1);
            flipSpeed+=delta*flipAcceleration;
        }
        flipBlend=flipSpeed; flipAnim+=flipSpeed*settings.reflectFlipBookMaxSpeed*delta;
        Apply();
    }
    // Diagnostic evaluation at recorded source states, not normal playback.
    public float[] EvaluateRecorded(float sourceElapsed,float sourceTimeParam,float sourceParallaxTilt,
                                    float sourceElapsedTime,int sourceAnimState,float sourceFlipAnim,float sourceFlipBlend) {
        Evaluate(sourceElapsed/settings.animTimeScale);
        float offset=sourceAnimState==0 ? 0 : Mathf.PerlinNoise(sourceElapsedTime*settings.intensityNoiseSpeed,0)*noise;
        return new[]{aura,settings.timeOffset+sourceTimeParam,Value("ParallaxTransparency",sourceParallaxTilt),
            Value("ParallaxTranslate",sourceParallaxTilt),center,intensity+offset,sourceFlipAnim,sourceFlipBlend};
    }
    public void Apply() {
        if(settings==null) return;
        ApplyGroup(auras,0); ApplyGroup(parallaxes,1); ApplyGroup(reflections,2);
    }
    void ApplyGroup(Renderer[] renderers,int group) {
        foreach(var renderer in renderers) for(int i=0;i<renderer.sharedMaterials.Length;i++) {
            block.Clear(); renderer.GetPropertyBlock(block,i);
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            if(group==0) {block.SetFloat("_Transparency",aura); block.SetFloat("_TimeParam",settings.timeOffset+timeParam);}
            else if(group==1) {block.SetFloat("_Transparency",parallaxTransparency); block.SetFloat("_Translate",parallaxTranslate);}
            else {block.SetFloat("_ReflectCenterX",center); block.SetFloat("_ReflectIntensity",intensity+noiseOffset);
                block.SetFloat("_FlipAnim",flipAnim); block.SetFloat("_FlipBlend",flipBlend);}
            renderer.SetPropertyBlock(block,i);
        }
    }
}
