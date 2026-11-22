using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CardMSSRObject RVAs 47eab48..47eb658, archived in port-L/native.
public sealed class SourceMssrCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    readonly AnimationCurve[] curves=new AnimationCurve[4];
    readonly float[] values=new float[4];
    static readonly string[] names={"EffSwitchColor","EffAdditiveIntensity","EffColor3Blend","EffEmissiveIntensity"};
    static readonly string[] properties={"_Switch","_AdditiveIntensity","_Color3Blend","_EmissiveIntensity"};
    readonly Queue<Vector3> distances=new Queue<Vector3>();
    Renderer[] renderers;
    MaterialPropertyBlock block;
    Vector3 lastFront;
    float lastTilt,elapsed,adjustedSpeed=1,startTime,evaluatedElapsed;
    bool playing;
    public bool Manual {get;set;}
    public bool Playing => playing;
    public float Elapsed => elapsed;
    public float EvaluatedElapsed => evaluatedElapsed;
    public float AdjustedSpeed => adjustedSpeed;
    public float Threshold => settings.animStartDegree;
    public float Duration => settings.animDuration*settings.animTimeScale;
    public bool UsesSpeedAdjustment => settings.useSpeedAdjust!=0;
    public int SurfaceCount => renderers.Length;

    public void Initialize(SourceCardEffects.Effect source,List<string> log) {
        settings=source; block=new MaterialPropertyBlock();
        for(int i=0;i<names.Length;i++) {
            var curve=Array.Find(source.curves,c=>c.property==names[i]);
            if(curve==null) throw new InvalidOperationException("Missing original MSSR curve: "+names[i]);
            curves[i]=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(curve.nativeJson).curve;
            if(curves[i]==null || curves[i].length!=curve.keys.Length)
                throw new InvalidOperationException("Original MSSR curve failed to deserialize: "+names[i]);
        }
        renderers=SourceCardEffects.Collect(transform,"Card-Effect-Emit");
        ResetState();
        // Original Awake calls Initialize then LateUpdate in the same frame.
        Step(transform.TransformDirection(Vector3.back),0,Time.realtimeSinceStartup);
        log.Add("OriginalCardEffect=CardMSSRObject sourceId="+source.sourceId+" surfaces="+renderers.Length+
            " curves=4 native=1.7.5 fidelityValidated=False");
    }
    public void ResetState() {
        distances.Clear(); lastFront=transform.TransformDirection(Vector3.back);
        lastTilt=0; elapsed=0; adjustedSpeed=1; playing=false; startTime=Time.time; Evaluate(0);
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
            playing=true; elapsed=0;
            float distance=0,interval=0;
            foreach(var sample in distances) {distance+=sample.x; interval+=sample.y;}
            float speed=Mathf.Clamp(distance/interval,settings.minTiltSpeed,settings.maxTiltSpeed);
            float ratio=Mathf.Clamp01((speed-settings.minTiltSpeed)/(settings.maxTiltSpeed-settings.minTiltSpeed));
            adjustedSpeed=settings.minAnimSpeed+(settings.maxAnimSpeed-settings.minAnimSpeed)*ratio;
        }
        if(playing) {
            // Source evaluates BEFORE increment; even the terminating update retains this sample.
            Evaluate(elapsed);
            elapsed+=delta*(UsesSpeedAdjustment ? adjustedSpeed : 1);
            playing=elapsed<=Duration;
        }
        lastTilt=tilt; Apply();
    }
    void Evaluate(float sourceElapsed) {
        evaluatedElapsed=sourceElapsed;
        for(int i=0;i<4;i++) values[i]=curves[i].Evaluate(sourceElapsed/settings.animTimeScale);
    }
    // Diagnostic seam only: evaluated native time, not a reconstructed gesture history.
    public float[] EvaluateRecorded(float sourceEvaluatedElapsed) {
        Evaluate(sourceEvaluatedElapsed); return (float[])values.Clone();
    }
    public void ApplyRecordedSnapshot(float[] sourceValues) {
        if(sourceValues==null || sourceValues.Length!=4)
            throw new InvalidOperationException("MSSR diagnostic snapshot requires four native values");
        for(int i=0;i<4;i++) {
            if(float.IsNaN(sourceValues[i]) || float.IsInfinity(sourceValues[i]))
                throw new InvalidOperationException("MSSR diagnostic value invalid");
            values[i]=sourceValues[i];
        }
        Manual=true; Apply();
    }
    public void Apply() {
        if(settings==null) return;
        foreach(var renderer in renderers) for(int index=0;index<renderer.sharedMaterials.Length;index++) {
            block.Clear(); renderer.GetPropertyBlock(block,index);
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            for(int i=0;i<4;i++) block.SetFloat(properties[i],values[i]);
            renderer.SetPropertyBlock(block,index);
        }
    }
}
