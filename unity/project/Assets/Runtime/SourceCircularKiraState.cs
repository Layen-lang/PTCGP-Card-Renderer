using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CircularKiraObject, including Primitive. Exact methods/ARM64
// in port-L/native/circular-20261008; original meshes retain vertices/indices.
public sealed class SourceCircularKiraState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    SourceCardEffects.CircularPrimitive[] prims;
    Renderer movingA,movingB,trailA,trailB;
    Mesh mesh;
    Vector2[] uvs;
    MaterialPropertyBlock block;
    readonly float[][] properties=new float[9][];
    static readonly string[] names={"_PrimAngles","_PrimBaseScales","_PrimBaseIntensities","_PrimMinIntensities",
        "_PrimMaxIntensities","_PrimFlickerScaling","_PrimFlickerAnimOffsets","_PrimTypes","_PrimMorphing"};
    float[] brightnesses,tempBrightnesses;
    float startTime,tilt,changeElapsed,defaultAngle,moveAngle,morphOffset,sourceTime;
    bool isTilt,hasApplied;
    int speedState,lastIndex;
    public bool Manual {get;set;}
    public bool IsAnimationStopped {get;set;}
    public float Tilt => tilt;
    public float MoveAngle => moveAngle;
    public float DefaultAngle => defaultAngle;
    public int SpeedState => speedState;
    public string SourceId => settings.sourceId;
    T ResolveComponent<T>(Transform face,SourceCardEffects.ParticlePath path) where T:Component {
        if(path==null || path.indices==null) return null;
        foreach(int index in path.indices) face=face.GetChild(index);
        var value=face.GetComponent<T>();
        if(!value) throw new InvalidOperationException("Original CircularKira component absent: "+typeof(T).Name);
        return value;
    }
    public void Initialize(SourceCardEffects.Effect source,Transform face,List<string> log) {
        settings=source;prims=source.primitives;block=new MaterialPropertyBlock();startTime=Time.time;
        if(prims==null || prims.Length==0 || settings.meshDivideCount<1)
            throw new InvalidOperationException("Original CircularKira arrays invalid");
        movingA=ResolveComponent<Renderer>(face,source.movingKiraARendererPath);
        movingB=ResolveComponent<Renderer>(face,source.movingKiraBRendererPath);
        trailA=ResolveComponent<Renderer>(face,source.trailKiraARendererPath);
        trailB=ResolveComponent<Renderer>(face,source.trailKiraBMeshFilterPath);
        var a=ResolveComponent<MeshFilter>(face,source.trailKiraAMeshFilterPath);
        var b=ResolveComponent<MeshFilter>(face,source.trailKiraBMeshFilterPath);
        if(!a || !b) throw new InvalidOperationException("Original CircularKira trail mesh filters required");
        // Original playing path uses B.mesh then assigns A.mesh to that instance.
        mesh=b.mesh;a.mesh=mesh;uvs=mesh.uv;
        if(uvs.Length!=settings.meshDivideCount*2+2)
            throw new InvalidOperationException("Original CircularKira UV/division count differs");
        for(int i=0;i<properties.Length;i++) properties[i]=new float[prims.Length];
        brightnesses=new float[settings.meshDivideCount];tempBrightnesses=new float[settings.meshDivideCount*2];
        morphOffset=UnityEngine.Random.Range(0f,10000f); // Original global PRNG; no invented fixed seed.
        for(int i=0;i<prims.Length;i++) {
            var p=prims[i];p.Speed=0;p.Angle=p.StartAngle;
            properties[1][i]=p.BaseScale;properties[2][i]=p.BaseIntensity;
            properties[3][i]=p.MinIntensity;properties[4][i]=p.MaxIntensity;properties[5][i]=p.FlickerScaling;
            properties[7][i]=p.PrimType;
        }
        defaultAngle=settings.defaultCircularAnglePattern==1 ? 50 : settings.defaultCircularAnglePattern==2 ? 132 :
            settings.defaultCircularAnglePattern==3 ? 35 : settings.defaultCircularAngleManual;
        var front=transform.TransformDirection(Vector3.back);
        UpdateTilt(front,0);moveAngle=EvaluateMoveAngle(front);lastIndex=Index(moveAngle);
        IsAnimationStopped=source.isAnimationStopped;
        if(!IsAnimationStopped) Step(front,0,0);
        log.Add("OriginalCardEffect=CircularKiraObject sourceId="+SourceId+" primitives="+prims.Length+
            " trailDivisions="+settings.meshDivideCount+" sourceGlobalRandomStateKnown=False native=1.7.5 fidelityValidated=False");
    }
    void LateUpdate() {
        if(settings!=null && !Manual && !IsAnimationStopped)
            Step(transform.TransformDirection(Vector3.back),Time.time>startTime ? Time.deltaTime : 0,Time.time-startTime);
    }
    public float EvaluateTilt(Vector3 front) {
        return Clamp01Native(Mathf.Pow(Mathf.Sin(Mathf.Atan2(new Vector2(front.x,front.y).magnitude,front.z)*3),settings.tiltPower));
    }
    public float EvaluateMoveAngle(Vector3 front) {
        // Constructor stores LightDir=(.5,.5,-.5); this method uses only XY.
        return -(((front.x*.5f+.5f)*.5f+(front.y*.5f+.5f)*.5f-.5f)*settings.moveAngleScale);
    }
    // ARM64 FCMP/FCSEL clamps NaN to zero; Unity Clamp01 propagates it.
    static float Clamp01Native(float value) => value>=0 ? (value<=1 ? value : 1) : 0;
    public float EvaluateBrakeSpeed(float elapsed,float initialSpeed) {
        float value=Mathf.Sin(Mathf.Acos(-Clamp01Native(elapsed/settings.brakeDuration)))*initialSpeed;
        return value<=.05f ? .05f : value;
    }
    public float EvaluateMorph(int index,float offset,float speed) {
        var p=prims[index];float phase=offset*p.MorphingSpeed;
        float noise=p.UseMorphingNoise ? Mathf.PerlinNoise1D(phase) : Mathf.Sin(phase);
        return Clamp01Native((noise-.5f)*p.MorphingClearly+.5f)*Clamp01Native(speed/p.MaxRotateSpeed);
    }
    void UpdateTilt(Vector3 front,float delta) {
        tilt=EvaluateTilt(front);bool next=tilt>settings.tiltThreshold;
        if(isTilt!=next) {
            changeElapsed+=delta;
            if(changeElapsed<=settings.tiltStateChangeDelay) return;
            isTilt=next;
        }
        changeElapsed=0;
    }
    int Index(float angle) => Mathf.RoundToInt(((angle+360)*settings.meshDivideCount)/360);
    public void Step(Vector3 front,float delta,float elapsed) {
        if(IsAnimationStopped) return;
        sourceTime=elapsed;UpdateTilt(front,delta);moveAngle=EvaluateMoveAngle(front);
        UpdatePrimitives(delta);UpdateTrail(delta);ApplyVertices();Apply();hasApplied=true;
    }
    void ResetBrake() {
        foreach(var p in prims) {p.BreakingTime=0;p.SpeedAtBrakeStart=0;p.BreakGoalAngle=0;p.BreakStartAngle=0;}
    }
    int Symmetry(int type) => type==0 ? settings.primTypeASymmetryCount : type==1 ? settings.primTypeBSymmetryCount :
        type==2 ? settings.primTypeCSymmetryCount : 1;
    void CalculateBrake() {
        foreach(var p in prims) {
            p.SpeedAtBrakeStart=p.Speed;
            float period=360f/Symmetry(p.PrimType);
            float goal=p.Angle+(period-(p.Angle-p.StartAngle)%period);
            float distance=p.Speed*settings.brakeDuration*Mathf.PI*.25f;
            while(goal-p.Angle<distance) goal+=period;
            p.BreakGoalAngle=goal;p.BreakStartAngle=goal-distance;
        }
    }
    void UpdatePrimitives(float delta) {
        if(speedState==0) {
            if(isTilt) {speedState=1;ResetBrake();}
        } else if(speedState==1) {
            foreach(var p in prims) p.Speed=Mathf.Clamp(p.Speed+p.MaxRotateSpeed*settings.rotateAccel*delta,0,p.MaxRotateSpeed);
            if(!isTilt) {speedState=2;CalculateBrake();}
        } else if(speedState==2) {
            foreach(var p in prims) {
                if(p.Angle>=p.BreakGoalAngle) {p.Speed=0;p.Angle=p.BreakGoalAngle;}
                else if(p.Angle>=p.BreakStartAngle) {
                    // Original ELF imports prove acosf then sinf; retain the .05 floor.
                    p.Speed=EvaluateBrakeSpeed(p.BreakingTime,p.SpeedAtBrakeStart);
                    p.BreakingTime+=delta;
                }
            }
            if(isTilt) {speedState=1;ResetBrake();}
        }
        for(int i=0;i<prims.Length;i++) {
            var p=prims[i];properties[6][i]+=delta*p.FlickerSpeed;
            p.Angle+=p.Speed*delta;properties[0][i]=p.ReverseRotation ? -p.Angle : p.Angle;
        }
        morphOffset+=delta;
        for(int i=0;i<prims.Length;i++) {
            var p=prims[i];if(!p.UseMorphing) continue;
            properties[8][i]=EvaluateMorph(i,morphOffset,p.Speed);
        }
    }
    void UpdateTrail(float delta) {
        int current=Index(moveAngle),edgeCount=0;var edges=new int[2];float previous=0;
        for(int i=0;i<tempBrightnesses.Length;i++) {
            int distance=Mathf.Abs(i-current);
            if(!((i<current || lastIndex<i) && (current<i || i<lastIndex))) tempBrightnesses[i]=settings.centerIntensity;
            else if(distance<=settings.expandLength) {
                float center=settings.centerIntensity*tilt;
                float target=Mathf.Pow(center+Mathf.Clamp01((float)distance/settings.expandLength)*(0-center),settings.expandPower);
                tempBrightnesses[i]=Mathf.Max(tempBrightnesses[i],target);
            }
            if(settings.useLengthLimit) {
                if(edgeCount>1) break; // Source stops before fading later elements this update.
                float value=tempBrightnesses[i];
                if(i==0 || i==tempBrightnesses.Length-1) {
                    if(value>0) edges[edgeCount++]=i;
                } else if(value!=0 && previous==0) edges[edgeCount++]=i;
                else if(value==0 && previous!=0) edges[edgeCount++]=i-1;
                previous=value;
            }
            float fade=settings.fadeOut;
            if(settings.useDistanceFadeOut) {
                float length=settings.limitLengthRatio*brightnesses.Length;
                fade*=settings.distanceFadeOutSpeed*Mathf.Pow(distance/(length+length),settings.distanceFadeOutCurvePower)+1;
            }
            tempBrightnesses[i]=tempBrightnesses[i]<settings.fadeOutEnd ? 0 : tempBrightnesses[i]-fade*delta;
        }
        if(settings.useLengthLimit) {
            int length=edges[1]-edges[0]+1,limit=(int)(settings.limitLengthRatio*brightnesses.Length);
            while(length>limit) {
                int side=tempBrightnesses[edges[1]]<=tempBrightnesses[edges[0]] ? 1 : 0;
                int index=edges[side];float value=tempBrightnesses[index];
                edges[side]+=side==0 ? 1 : -1;
                tempBrightnesses[index]=value*(1-settings.LimitAdjustSpeed)+settings.LimitAdjustSpeed*Mathf.Min(value,0);
                length--;
            }
            for(int i=0;i<length;i++) {
                int index=edges[0]+i;float distance=Mathf.Abs(current-index);
                float range=index<current ? current-edges[0] : edges[1]-current;
                float cap=settings.centerIntensity*(1-Mathf.Pow(distance/range,settings.LimitAdjustCurvePower));
                float value=tempBrightnesses[index];
                tempBrightnesses[index]=value*(1-settings.LimitAdjustSpeed)+settings.LimitAdjustSpeed*(cap<=value ? cap : value);
            }
        }
        for(int i=0;i<brightnesses.Length;i++) brightnesses[i]=Mathf.Max(tempBrightnesses[i],tempBrightnesses[i+brightnesses.Length]);
        lastIndex=current;
    }
    void ApplyVertices() {
        if(!trailA || !trailB) return;
        for(int i=0;i<brightnesses.Length;i++) {
            int index=uvs.Length-2-i*2;uvs[index].y=brightnesses[i];uvs[index+1].y=brightnesses[i];
        }
        uvs[0].y=uvs[3].y;uvs[1].y=uvs[uvs.Length-1].y;uvs[2].y=uvs[uvs.Length-1].y;
        mesh.SetUVs(0,uvs);
    }
    public void Apply() {
        if(!movingA || !movingB) return;
        block.Clear();movingA.GetPropertyBlock(block,0);
        block.SetFloat("_CircularDefaultAngle",defaultAngle);block.SetFloat("_MoveAngle",moveAngle);block.SetFloat("_Tilt",tilt);
        for(int i=0;i<properties.Length;i++) block.SetFloatArray(names[i],properties[i]);
        movingA.SetPropertyBlock(block,0);movingB.SetPropertyBlock(block,0);
        if(!trailA || !trailB) return;
        // Source retains the existing block contents and fetches A before sharing with B.
        trailA.GetPropertyBlock(block,0);block.SetFloat("_CircularDefaultAngle",defaultAngle);block.SetFloat("_NoiseTime",sourceTime+sourceTime);
        trailA.SetPropertyBlock(block,0);trailB.SetPropertyBlock(block,0);
    }
    public void Refresh() {if(hasApplied) Apply();}
    // Diagnostics import the sampled original shader/UV state only. They do
    // not establish random-state parity or reproduce an interaction history.
    public void ApplyRecordedSnapshot(float sampledTilt,float sampledAngle,float sampledDefault,float sampledTime,
        float[][] arrays,float[] sampledUvs) {
        if(arrays.Length!=properties.Length || sampledUvs.Length!=uvs.Length*2)
            throw new InvalidOperationException("Original CircularKira snapshot dimensions differ");
        for(int i=0;i<properties.Length;i++) {
            if(arrays[i]==null || arrays[i].Length!=prims.Length) throw new InvalidOperationException("Original CircularKira primitive array differs");
            Array.Copy(arrays[i],properties[i],prims.Length);
        }
        Manual=true;tilt=sampledTilt;moveAngle=sampledAngle;defaultAngle=sampledDefault;sourceTime=sampledTime;
        for(int i=0;i<uvs.Length;i++) uvs[i]=new Vector2(sampledUvs[i*2],sampledUvs[i*2+1]);
        mesh.SetUVs(0,uvs);Apply();hasApplied=true;
    }
    void OnDestroy() {if(mesh) Destroy(mesh);}
}
