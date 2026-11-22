using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CardMRRObject RVAs 47e7530..47e865c. See port-L/native.
// The source settings retain complete weighted curves, not sampled substitutes.
public sealed class SourceMrrCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    readonly Dictionary<string,AnimationCurve> curves=new Dictionary<string,AnimationCurve>();
    readonly Queue<Vector3> distances=new Queue<Vector3>();
    MaterialPropertyBlock block;
    Renderer[] surfaces,effects,flashes;
    Vector3 lastFront,startFront;
    float lastTilt,elapsed,adjustedSpeed=1,minRadial,maxRadial,startTime;
    bool playing;
    public bool Manual {get;set;}
    public bool Playing => playing;
    public float Elapsed => elapsed;
    public float AdjustedSpeed => adjustedSpeed;
    public float LayerTranslation {get;private set;}
    public float RadialScaling {get;private set;}
    public float RadialAnimation {get;private set;}
    public float Threshold => settings.animStartDegree;
    public float Duration => settings.animDuration*settings.animTimeScale;
    public int SurfaceCount => surfaces.Length;
    public int EffectCount => effects.Length;
    public int FlashCount => flashes.Length;

    public void Initialize(SourceCardEffects.Effect source,List<string> log)
    {
        block=new MaterialPropertyBlock();
        settings=source;
        foreach(var curve in source.curves) {
            var value=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(curve.nativeJson).curve;
            if(value==null || value.length!=curve.keys.Length)
                throw new InvalidOperationException("Original MRR curve failed to deserialize: "+curve.property);
            curves.Add(curve.property,value);
        }
        surfaces=Collect("MRR-ChangeColor-Lighting","Frame-Holo-2Layer");
        effects=Collect("Card-Effect-Emit"); flashes=Collect("MRR-Parallax-Flash");
        var radial=curves["FlashRadialScaling"].keys;
        minRadial=radial[0].value; maxRadial=radial[radial.Length-1].value;
        ResetState(); Evaluate(0); Apply();
        log.Add("OriginalCardEffect=CardMRRObject sourceId="+source.sourceId+" surfaces="+surfaces.Length+
            " effects="+effects.Length+" flashes="+flashes.Length+" curves="+curves.Count+" native=1.7.5 fidelityValidated=False");
    }
    Renderer[] Collect(params string[] tags) {
        var found=new List<Renderer>();
        foreach(var tag in tags) foreach(var renderer in SourceCardEffects.Collect(transform,tag))
            if(!found.Contains(renderer)) found.Add(renderer);
        return found.ToArray();
    }
    public void ResetState() {
        distances.Clear(); lastFront=transform.TransformDirection(Vector3.back);
        startFront=Vector3.zero; lastTilt=0; elapsed=0; playing=false; adjustedSpeed=1;
        startTime=Time.time;
        if(settings!=null) Evaluate(0);
    }
    void LateUpdate() {
        if(settings!=null && !Manual)
            Step(transform.TransformDirection(Vector3.back),Time.time>startTime ? Time.deltaTime : 0,Time.realtimeSinceStartup);
    }
    public void Step(Vector3 front,float delta,float realtime) {
        float tilt=Mathf.Atan2(new Vector2(front.x,front.y).magnitude,-front.z)*Mathf.Rad2Deg;
        distances.Enqueue(new Vector3((front-lastFront).magnitude,delta,realtime));
        while(distances.Count>0 && realtime-distances.Peek().z>settings.recordingTime) distances.Dequeue();
        lastFront=front;
        if(!playing && lastTilt<settings.animStartDegree && tilt>settings.animStartDegree) {
            playing=true; elapsed=0; startFront=front;
            float distance=0,interval=0;
            foreach(var sample in distances) {distance+=sample.x; interval+=sample.y;}
            float speed=Mathf.Clamp(distance/interval,settings.minTiltSpeed,settings.maxTiltSpeed);
            float ratio=Mathf.Clamp01((speed-settings.minTiltSpeed)/(settings.maxTiltSpeed-settings.minTiltSpeed));
            adjustedSpeed=settings.minAnimSpeed+(settings.maxAnimSpeed-settings.minAnimSpeed)*ratio;
        }
        if(playing) {
            elapsed+=delta*(settings.useSpeedAdjust!=0 ? adjustedSpeed : 1);
            Evaluate(elapsed/settings.animTimeScale);
            playing=elapsed<=settings.animDuration*settings.animTimeScale;
        }
        lastTilt=tilt; Apply();
    }
    float progress;
    float Value(string name) => curves[name].Evaluate(progress);
    void Evaluate(float value) {
        progress=value;
        LayerTranslation=Value(startFront.x>0 ? "Layer2UVXTranslateByTiltingRight" : "Layer2UVXTranslateByTiltingLeft");
        float offset=settings.flashRadialStartOffset;
        // Preserve original operation order, including the source range division.
        float range=maxRadial-minRadial;
        RadialScaling=minRadial+offset*range+(range*(1-offset)*(Value("FlashRadialScaling")-minRadial))/range;
        RadialAnimation=offset+(1-offset)*Value("FlashRadialAnim");
    }
    // Diagnostic seam for comparing native sampled parameters at their actual
    // evaluated time/direction. It is never used by normal playback.
    public float[] EvaluateRecorded(float sourceElapsed,Vector3 sourceStartFront) {
        startFront=sourceStartFront; Evaluate(sourceElapsed/settings.animTimeScale);
        return new[]{Value("ChangeColorCurve"),Value("LightColorIntensityCurve"),Value("LightEmitIntensityCurve"),Value("LightPower"),
            LayerTranslation,0f,0f,0f,Value("Layer2ColorPower"),Value("Layer2EmissiveIntensity"),
            Value("EffSwitchColor"),Value("EffAdditiveIntensity"),Value("EffColor3Blend"),Value("EffEmissiveIntensity"),
            Value("FlashIntensity"),RadialScaling,RadialAnimation};
    }
    public void Apply() {
        if(settings==null) return;
        ApplyGroup(surfaces,0); ApplyGroup(effects,1); ApplyGroup(flashes,2);
    }
    void Float(string property,string curve) {block.SetFloat(property,Value(curve));}
    void ApplyGroup(Renderer[] renderers,int group) {
        foreach(var renderer in renderers) for(int index=0;index<renderer.sharedMaterials.Length;index++) {
            block.Clear(); renderer.GetPropertyBlock(block,index);
            // Host DynamicUI uses a renderer-level MPB; retain its original textures.
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            if(group==0) {
                Float("_ChangeColor","ChangeColorCurve"); Float("_LightColorIntensity","LightColorIntensityCurve");
                Float("_LightEmitIntensity","LightEmitIntensityCurve"); Float("_LightPower","LightPower");
                block.SetVector("_Layer2UVTranslate",new Vector4(LayerTranslation,0,0,0));
                Float("_Layer2EmissiveIntensity","Layer2EmissiveIntensity"); Float("_Layer2ColorPower","Layer2ColorPower");
            } else if(group==1) {
                Float("_Switch","EffSwitchColor"); Float("_AdditiveIntensity","EffAdditiveIntensity");
                Float("_Color3Blend","EffColor3Blend"); Float("_EmissiveIntensity","EffEmissiveIntensity");
            } else {
                Float("_FlashIntensity","FlashIntensity"); block.SetFloat("_RadialScaling",RadialScaling);
                block.SetFloat("_RadialAnim",RadialAnimation);
            }
            renderer.SetPropertyBlock(block,index);
        }
    }
}
