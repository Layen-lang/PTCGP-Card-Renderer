using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CardFutureObject: RVAs 47e5ed0..47e6970.
// Exact methods/ARM64 and serialized L fields are archived in port-L/native.
public sealed class SourceFutureCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    Renderer[] renderers;
    MaterialPropertyBlock block;
    float startTime,frame,tiltDot;
    int goal,lastGoal;
    public bool Manual {get;set;}
    // Original setter stores this flag, but Awake/LateUpdate do not read it.
    public bool IsAnimationStopped {get;set;}
    public float Frame => frame;
    public float TiltDot => tiltDot;
    public int Goal => goal;
    public void ApplyRecordedSnapshot(float sourceFrame,float sourceTiltDot) {Manual=true; frame=sourceFrame; tiltDot=sourceTiltDot; Apply();}
    public void ResetState(Vector3 front) {UpdateGoal(front); frame=goal; Apply();}
    public void Initialize(SourceCardEffects.Effect source,List<string> log) {
        settings=source; block=new MaterialPropertyBlock(); startTime=Time.time;
        IsAnimationStopped=source.isAnimationStopped;
        renderers=SourceCardEffects.Collect(transform,"Parallax-Led");
        UpdateGoal(transform.TransformDirection(Vector3.back)); frame=goal; Apply();
        log.Add("OriginalCardEffect=CardFutureObject sourceId="+source.sourceId+" ledRenderers="+renderers.Length+
            " frames="+source.animationFrameCount+" native=1.7.5 fidelityValidated=False");
    }
    void LateUpdate() {if(settings!=null && !Manual) Step(transform.TransformDirection(Vector3.back),Time.time>startTime ? Time.deltaTime : 0);}
    public void UpdateGoal(Vector3 front) {
        var normalized=front.normalized;
        tiltDot=Vector3.Dot(normalized,new Vector3(1,1,0).normalized)+.5f*Vector3.Dot(normalized,new Vector3(1,-1,0).normalized);
        goal=Mathf.Clamp(Mathf.RoundToInt(tiltDot*10+5),0,10)*(settings.animationFrameCount-1)/10;
        lastGoal=goal;
    }
    public void Step(Vector3 front,float delta) {
        UpdateGoal(front); frame=Advance(frame,goal,settings.skipAnimThreshold,settings.accellRatio,delta); Apply();
    }
    public static float Advance(float current,int destination,int skip,float acceleration,float delta) {
        float distance=Mathf.Abs(destination-current);
        if(distance<.00009999999747378752f) return destination;
        int direction=destination-current>=0 ? 1 : -1;
        if(distance>skip) {current+=(distance-skip)*direction; distance=Mathf.Abs(destination-current);}
        current+=acceleration*distance*direction*delta;
        return direction>0 ? Mathf.Min(current,destination) : Mathf.Max(current,destination);
    }
    public void Apply() {
        if(renderers==null) return;
        foreach(var renderer in renderers) for(int index=0;index<renderer.sharedMaterials.Length;index++) {
            block.Clear(); renderer.GetPropertyBlock(block,index);
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            block.SetFloat("_AnimFrame",frame+settings.animFrameOffset); block.SetFloat("_TiltDot",tiltDot);
            renderer.SetPropertyBlock(block,index);
        }
    }
}
