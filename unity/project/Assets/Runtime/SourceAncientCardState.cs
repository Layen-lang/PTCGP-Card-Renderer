using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CardAncientObject, RVAs 47e3ff0..47e5df0.
// Native ARM64, nested StrataData and original serialized settings are evidence.
public sealed class SourceAncientCardState : MonoBehaviour
{
    [Serializable] public struct Stratum {
        public float current,remapped; public int lastDir;
        public float elapsed,changeTime,startDelay,speed; public int state;
    }
    SourceCardEffects.Effect settings;
    readonly Dictionary<string,AnimationCurve> curves=new Dictionary<string,AnimationCurve>();
    readonly Stratum[] strata=new Stratum[6];
    readonly float[] remapped=new float[6],volumes=new float[4],rates=new float[4];
    readonly int[] meshIndices=new int[4];
    Renderer[] renderers;
    ParticleSystem[] sand,sand2;
    MaterialPropertyBlock block;
    float startTime,elapsedTime,goal;
    Vector2 shake;
    public bool Manual {get;set;}
    public bool IsAnimationStopped {get;set;}
    public float Goal => goal;
    public Vector2 Shake => shake;
    public float[] Remapped => (float[])remapped.Clone();
    public float[] Rates => (float[])rates.Clone();
    public int[] MeshIndices => (int[])meshIndices.Clone();
    public Stratum[] Strata => (Stratum[])strata.Clone();
    public void ApplyRecordedSnapshot(float[] sourceRemapped,Vector2 sourceShake) {
        if(sourceRemapped.Length!=6) throw new InvalidOperationException("Original Ancient strata count");
        Manual=true; Array.Copy(sourceRemapped,remapped,6); shake=sourceShake;
        for(int i=0;i<4;i++) SetEmissionRate(i,0); Apply();
    }
    public void ResetState() {
        Array.Clear(strata,0,strata.Length); Array.Clear(remapped,0,remapped.Length);
        Array.Clear(volumes,0,volumes.Length); Array.Clear(meshIndices,0,meshIndices.Length);
        elapsedTime=goal=0; shake=Vector2.zero;
        for(int i=0;i<4;i++) SetEmissionRate(i,0);
    }
    public void Initialize(SourceCardEffects.Effect source,Transform face,List<string> log) {
        settings=source; block=new MaterialPropertyBlock(); startTime=Time.time;
        IsAnimationStopped=source.isAnimationStopped;
        foreach(var c in source.curves) {
            var curve=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(c.nativeJson).curve;
            if(curve==null || curve.length!=c.keys.Length) throw new InvalidOperationException("Original Ancient curve failed: "+c.property);
            curves.Add(c.property,curve);
        }
        sand=Resolve(face,source.sandParticlePaths); sand2=Resolve(face,source.sand2ParticlePaths);
        renderers=SourceCardEffects.Collect(transform,"Parallax-Strata");
        Step(transform.TransformDirection(Vector3.back),0);
        log.Add("OriginalCardEffect=CardAncientObject sourceId="+source.sourceId+" strataRenderers="+renderers.Length+
            " sand="+sand.Length+" sand2="+sand2.Length+" native=1.7.5 fidelityValidated=False");
    }
    static ParticleSystem[] Resolve(Transform face,SourceCardEffects.ParticlePath[] paths) {
        var result=new List<ParticleSystem>();
        foreach(var path in paths) {
            var node=face; foreach(int index in path.indices) node=node.GetChild(index);
            var original=node.GetComponent<ParticleSystem>();
            if(!original) throw new InvalidOperationException("Original Ancient ParticleSystem missing at "+string.Join("/",path.indices));
            result.Add(original);
        }
        return result.ToArray();
    }
    void LateUpdate() {if(settings!=null && !Manual) Step(transform.TransformDirection(Vector3.back),Time.time>startTime ? Time.deltaTime : 0);}
    public void Step(Vector3 front,float delta) {
        elapsedTime+=delta; goal=EvaluateGoal(front);
        for(int i=0;i<6;i++) {
            bool written=AdvanceStratum(ref strata[i],goal,delta,settings,curves,()=>UnityEngine.Random.Range(settings.changeRangeStart,settings.changeRangeEnd),
                ()=>UnityEngine.Random.Range(settings.animStartDelayRangeA,settings.animStartDelayRangeB));
            if(written) {strata[i].remapped=strata[i].current*settings.scrolls[i]*settings.scrollLength; remapped[i]=strata[i].remapped;}
        }
        shake=EvaluateShake(elapsedTime,goal,strata[0].current,strata[0].elapsed);
        UpdateSand(); Apply();
    }
    public float EvaluateGoal(Vector3 front) {
        var n=front.normalized;
        return Vector3.Dot(n,new Vector3(1,1,0).normalized)+settings.dot2Multiply*Vector3.Dot(n,new Vector3(1,-1,0).normalized);
    }
    // Curves receive max(elapsed-startDelay,0), before incrementing elapsed.
    // Native goal return retains both remapped values and LastDir.
    public static bool AdvanceStratum(ref Stratum s,float goal,float delta,SourceCardEffects.Effect p,
        Dictionary<string,AnimationCurve> curves,Func<float> changeTime,Func<float> startDelay) {
        float distance=Mathf.Abs(goal-s.current);
        if(distance<p.goalThreshold) {s.current=goal; s.elapsed=0; s.speed=0; s.state=0; return false;}
        int direction=goal-s.current>=0 ? 1 : -1;
        if(s.state==0 || s.lastDir!=direction) {s.elapsed=0; s.speed=0; s.state=1; s.changeTime=changeTime(); s.startDelay=startDelay();}
        float t=Mathf.Max(s.elapsed-s.startDelay,0);
        s.state=t<s.changeTime ? 1 : distance>p.zuzuGoalAnimThreshold ? 2 : 3;
        s.speed=curves[s.state==1 ? "ZuzuA" : s.state==2 ? "Zzzzz" : "ZuzuGoal"].Evaluate(t)*(direction*p.animCurveScale);
        if(s.state==2) s.speed*=Mathf.Max(p.accellRatio*Mathf.Abs(goal-s.current),1);
        s.lastDir=direction; s.elapsed+=delta;
        s.current=Mathf.Clamp(s.current+s.speed*delta,-1,1); return true;
    }
    float Noise(float value) {
        float scaled=Mathf.PerlinNoise1D(value)*settings.noiseScale;
        float fraction=scaled-Mathf.Floor(scaled);
        return fraction>1 ? 1 : fraction<0 ? -1 : fraction+fraction-1;
    }
    public Vector2 EvaluateShake(float elapsed,float sourceGoal,float current,float animElapsed) {
        float clock=elapsed*settings.shakeSpeed;
        var a=new Vector2(Noise(clock*settings.shakeAFrequency.x),Noise(clock*settings.shakeAFrequency.y+.5f));
        var b=new Vector2(Noise(clock*settings.shakeBFrequency.x),Noise(clock*settings.shakeBFrequency.y+.5f));
        // Native multiplies each weighted noise vector by .1 before adding.
        var value=Vector2.Scale(a,settings.shakeAIntensity)*.1f+Vector2.Scale(b,settings.shakeBIntensity)*.1f;
        return value*(curves["ShakeIntensity"].Evaluate(animElapsed)*Mathf.Clamp01(settings.diffOffset+Mathf.Abs(sourceGoal-current)));
    }
    // Algebraic reconstruction at sampled native inputs; no random/history replay.
    public float[] EvaluateRecorded(Vector3 front,float elapsed,float sourceGoal,Stratum[] source) {
        var result=new float[17]; result[0]=EvaluateGoal(front);
        var value=EvaluateShake(elapsed,sourceGoal,source[0].current,source[0].elapsed);
        result[1]=value.x; result[2]=value.y;
        for(int i=0;i<6;i++) result[3+i]=source[i].current*settings.scrolls[i]*settings.scrollLength;
        for(int i=0;i<4;i++) {
            result[9+i]=Mathf.Clamp(settings.frictionScale*Mathf.Abs(source[4-i].speed*settings.scrolls[4-i]-source[5-i].speed*settings.scrolls[5-i]),0,settings.maxFriction);
            float noise=Mathf.PerlinNoise1D(i*10+elapsed*settings.shapeChangeSpeed)*5;
            // ARM64 s1 retains StrataNum-1 as fmodf's second argument.
            result[13+i]=Mathf.FloorToInt((noise+noise)%5);
        }
        return result;
    }
    void UpdateSand() {
        if(IsAnimationStopped) {for(int i=0;i<4;i++) SetEmissionRate(i,0); return;}
        for(int i=0;i<4;i++) {
            float noise=Mathf.PerlinNoise1D(i*10+elapsedTime*settings.shapeChangeSpeed)*5;
            meshIndices[i]=Mathf.FloorToInt((noise+noise)%5);
            volumes[i]=Mathf.Clamp(settings.frictionScale*Mathf.Abs(strata[4-i].speed*settings.scrolls[4-i]-strata[5-i].speed*settings.scrolls[5-i]),0,settings.maxFriction);
            int state=strata[i].state;
            float rate=state==1 ? settings.startSandBaseEmissionRate : state==2 ? settings.middleSandBaseEmissionRate : state==3 ? settings.endSandBaseEmissionRate : 0;
            SetEmissionRate(i,rate*volumes[i]);
        }
    }
    public void SetEmissionRate(int index,float rate) {
        if(index<0) return;
        if(index<4) rates[index]=rate;
        if(index<sand.Length) {
            var emission=sand[index].emission; emission.rateOverTime=new ParticleSystem.MinMaxCurve(rate);
            var shape=sand[index].shape; shape.meshMaterialIndex=meshIndices[index];
        }
        if(index<sand2.Length) {var emission=sand2[index].emission; emission.rateOverTime=new ParticleSystem.MinMaxCurve(rate);}
    }
    public void Apply() {
        if(renderers==null) return;
        foreach(var renderer in renderers) for(int index=0;index<renderer.sharedMaterials.Length;index++) {
            block.Clear(); renderer.GetPropertyBlock(block,index);
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            block.SetFloatArray("_StrataFaults",remapped); block.SetVector("_Shake",new Vector4(shake.x,shake.y,0,0));
            renderer.SetPropertyBlock(block,index);
        }
    }
}
