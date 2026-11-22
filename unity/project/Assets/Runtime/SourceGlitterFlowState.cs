using System;
using System.Collections.Generic;
using UnityEngine;

// GlitterFlowMaps, original 1.7.5 Update (47f26b0 through 47f30d4).
public sealed class SourceGlitterFlowState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    Renderer target;
    Vector2 speed,offset;
    float light,rotationA,rotationB,startTime;
    readonly Vector4[] flow=new Vector4[2];
    MaterialPropertyBlock block;
    void Awake() { block=new MaterialPropertyBlock(); }
    static readonly int FlowParams=Shader.PropertyToID("_FlowParams");
    public void Initialize(SourceCardEffects.Effect source,Renderer renderer,List<string> log)
    {
        if(!renderer) throw new InvalidOperationException("Original glitter renderer missing");
        settings=source; target=renderer;
        speed=new Vector2(source.initFlowSpeed,source.initFlowSpeed);
        rotationA=source.flowAInitialRotation; rotationB=source.flowBInitialRotation;
        startTime=Time.time; Tick(0);
        log.Add("OriginalCardEffect=GlitterFlowMaps sourceId="+source.sourceId+" renderer="+renderer.name+" native=1.7.5");
    }
    void Update() { if(settings!=null) Tick(Time.time>startTime ? Time.deltaTime : 0); }
    static float Frac(float value) { return value-Mathf.Floor(value); }
    static float Shape(float value) { float x=2*value-value*value; return x*x; }
    void Tick(float delta)
    {
        var front=transform.forward.normalized;
        // Original acceleration and resistance are per Update, not per second.
        speed-=new Vector2(front.x,front.y)*settings.accelIntensity;
        speed=Vector2.ClampMagnitude(speed,settings.maxFlowSpeed);
        speed*=1-settings.resistance;
        float magnitude=speed.magnitude;
        // A perfectly axial host pose may have zero XY; avoid 0 * infinity.
        if(magnitude>0 && magnitude<settings.minFlowSpeed) speed*=settings.minFlowSpeed/magnitude;
        magnitude=speed.magnitude;
        var direction=speed.normalized;
        float t=(Vector2.Dot(direction,new Vector2(0.5821118950843811f,0.8131087422370911f))+1)*.5f;
        offset.x=Frac(offset.x+(settings.minTiltPower+(1-settings.minTiltPower)*Shape(t))*delta*magnitude);
        offset.y=Frac(offset.y+(settings.minTiltPower+(1-settings.minTiltPower)*Shape(1-t))*delta*magnitude);
        light=Frac(light+delta*settings.lightSpeed);
        float rotationBlend=Mathf.InverseLerp(settings.minFlowSpeed,settings.maxFlowSpeed,magnitude);
        rotationA=(rotationA+delta*Mathf.Lerp(settings.flowAMinRotateSpeed,settings.flowAMaxRotateSpeed,rotationBlend)/20)%(2*Mathf.PI);
        rotationB=(rotationB+delta*Mathf.Lerp(settings.flowBMinRotateSpeed,settings.flowBMaxRotateSpeed,rotationBlend)/20)%(2*Mathf.PI);
        // 1.8.1 array writes: offset +0x20, light +0x28, rotations +0x30 (RVA 4a3b310, 4a3b3d4, 4a3b4f4).
        flow[0]=new Vector4(offset.x,-offset.y,light,0);
        flow[1]=new Vector4(rotationA,rotationB,0,0);
        block.Clear(); target.GetPropertyBlock(block,0);
        if(block.isEmpty) target.GetPropertyBlock(block);
        block.SetVectorArray(FlowParams,flow); target.SetPropertyBlock(block,0);
    }
}
