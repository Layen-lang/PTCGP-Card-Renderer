using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 CardMarbleLayer and nested Point/RemapCurveSettings.
// RVAs 47e2a34..47e3fa0; original methods and ARM64 in port-L/native.
public sealed class SourceMarbleCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    Renderer target;
    Texture2D remap;
    MaterialPropertyBlock block;
    readonly Queue<Vector4> fronts=new Queue<Vector4>();
    Vector3[] current,goal,speeds;
    Vector4[] attributes;
    Vector3 currentTilt,goalTilt,tiltSpeed,worldFront;
    float tilt,rotation,startTime;
    public bool Manual {get;set;}
    public float Tilt => tilt;
    public Vector3 CurrentTilt => currentTilt;
    public Vector3 GoalTilt => goalTilt;
    public Vector4[] Attributes => (Vector4[])attributes.Clone();
    public Texture2D RemapTexture => remap;

    public void Initialize(SourceCardEffects.Effect source,Renderer renderer,List<string> log) {
        settings=source; target=renderer; block=new MaterialPropertyBlock();
        if(!target) throw new InvalidOperationException("Original Marble renderer unresolved");
        current=new Vector3[source.points.Length]; goal=new Vector3[current.Length];
        speeds=new Vector3[current.Length]; attributes=new Vector4[current.Length];
        ResetState(); GenerateRemap(); Step(transform.TransformDirection(Vector3.back),0,Time.realtimeSinceStartup);
        log.Add("OriginalCardEffect=CardMarbleLayer sourceId="+source.sourceId+" points="+current.Length+
            " remap="+remap.width+"x"+remap.height+" format="+remap.format+" mipmaps="+remap.mipmapCount+" native=1.7.5 fidelityValidated=False");
    }
    public void ResetState() {
        fronts.Clear(); currentTilt=goalTilt=tiltSpeed=Vector3.zero; tilt=rotation=0; startTime=Time.time;
        for(int i=0;i<current.Length;i++) {
            var p=settings.points[i]; current[i]=goal[i]=new Vector3(p.DefaultPosition.x,p.DefaultPosition.y,p.DefaultForce);
            speeds[i]=Vector3.zero; attributes[i]=new Vector4(current[i].x,current[i].y,current[i].z,0);
        }
    }
    void GenerateRemap() {
        var curves=new Dictionary<string,AnimationCurve>();
        foreach(var c in settings.curves) {
            var value=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(c.nativeJson).curve;
            if(value==null || value.length!=c.keys.Length) throw new InvalidOperationException("Original Marble curve failed: "+c.property);
            curves.Add(c.property,value);
        }
        // ldexpf(1, Resolution), TextureFormat.R16=9, no mips, linear=true,
        // wrap Clamp=1. Default Texture2D filtering is retained.
        int size=(int)Mathf.Pow(2,settings.remapResolution);
        remap=new Texture2D(size,size,TextureFormat.R16,false,true) {wrapMode=TextureWrapMode.Clamp};
        var colors=new Color[size*size];
        for(int y=0;y<size;y++) {
            float blend=Mathf.Clamp01(curves["RemapRemapCurve"].Evaluate((float)y/size));
            for(int x=0;x<size;x++) {
                float normal=curves["DefaultRemapCurve"].Evaluate((float)x/size);
                float tilted=curves["TiltRemapCurve"].Evaluate((float)x/size);
                float value=normal+blend*(tilted-normal);
                colors[x+y*size]=new Color(value,value,value,1);
            }
        }
        remap.SetPixels(colors); remap.Apply(false,true);
    }
    void FixedUpdate() {
        if(settings!=null && !Manual)
            // unscaledTime matches real time in play and follows the bench's simulated time.
            Step(transform.TransformDirection(Vector3.back),Time.time>startTime ? Time.deltaTime : 0,Time.unscaledTime);
    }
    public void Step(Vector3 front,float delta,float realtime) {
        worldFront=front;
        float angle=Mathf.Atan2(new Vector2(front.x,front.y).magnitude,front.z);
        tilt=Mathf.Clamp01(Mathf.Pow(Mathf.Sin(angle*3),settings.tiltPower));
        if(settings.useMarbleDelay) {
            fronts.Enqueue(new Vector4(front.x,front.y,tilt,realtime));
            bool removed=false;
            while(fronts.Count>0 && realtime-fronts.Peek().w>settings.delayTime2) {
                var old=fronts.Dequeue(); goalTilt=new Vector3(old.x,old.y,old.z); removed=true;
            }
            if(removed) UpdateGoals(goalTilt);
        }
        rotation=(Mathf.Atan2(front.y,front.x)/Mathf.PI+1)*.5f;
        if(settings.useMarbleDelay) {
            for(int i=0;i<current.Length;i++) {
                Advance(settings.pointAccel,ref speeds[i],ref current[i],goal[i],delta);
                attributes[i]=new Vector4(current[i].x,current[i].y,current[i].z,0);
            }
            Advance(settings.shearAccel,ref tiltSpeed,ref currentTilt,goalTilt,delta);
        }
        Apply();
    }
    void UpdateGoals(Vector3 front) {
        float degree=Mathf.Atan2(front.y,front.x)*Mathf.Rad2Deg;
        for(int i=0;i<goal.Length;i++) {
            var p=settings.points[i];
            // Internal_FromEulerRad receives degree*RotationWithTilt*Deg2Rad.
            var moved=Quaternion.Euler(0,0,degree*p.RotationWithTilt)*(Vector3)p.TiltMovePosition;
            goal[i]=new Vector3(p.DefaultPosition.x+moved.x*front.z*settings.pointMoveByTilt,
                p.DefaultPosition.y+moved.y*front.z*settings.pointMoveByTilt,
                p.DefaultForce+front.z*p.TiltForce*settings.pointForceChangeByTilt);
        }
    }
    void Advance(float acceleration,ref Vector3 speed,ref Vector3 position,Vector3 destination,float delta) {
        var difference=destination-position; float distance=difference.magnitude;
        if(distance<settings.goalThreshold) {speed=Vector3.zero; return;}
        var direction=distance>0.000009999999747378752f ? difference/distance : Vector3.zero;
        speed+=direction*acceleration*delta;
        if(distance<=settings.dorodoroDistance) {
            float resistance=Mathf.Pow(1-distance/settings.dorodoroDistance,settings.resistancePower);
            speed*=settings.minDorodoroCoef+(1-resistance)*(1-settings.minDorodoroCoef);
        }
        float magnitude=speed.magnitude;
        if(magnitude>settings.maxPointSpeed) speed*=settings.maxPointSpeed/magnitude;
        else if(magnitude<settings.minPointSpeed) speed=direction*settings.minPointSpeed;
        position+=speed*delta;
    }
    // Diagnostic reconstruction of algebraic values at their source inputs.
    // It does not replay the queue or certify frame-by-frame inertia.
    public float[] EvaluateRecorded(Vector3 sourceFront,Vector3 sourceGoalTilt) {
        float angle=Mathf.Atan2(new Vector2(sourceFront.x,sourceFront.y).magnitude,sourceFront.z);
        var result=new float[2+goal.Length*3];
        result[0]=Mathf.Clamp01(Mathf.Pow(Mathf.Sin(angle*3),settings.tiltPower));
        result[1]=(Mathf.Atan2(sourceFront.y,sourceFront.x)/Mathf.PI+1)*.5f;
        UpdateGoals(sourceGoalTilt);
        for(int i=0;i<goal.Length;i++) {result[2+i*3]=goal[i].x; result[3+i*3]=goal[i].y; result[4+i*3]=goal[i].z;}
        return result;
    }
    public void ApplyRecordedSnapshot(float sourceTilt,Vector3 sourceCurrentTilt,Vector3 sourceFront,Vector3[] positions) {
        if(positions.Length!=attributes.Length) throw new InvalidOperationException("Source Marble point count mismatch");
        Manual=true; tilt=sourceTilt; currentTilt=sourceCurrentTilt; worldFront=sourceFront;
        rotation=(Mathf.Atan2(sourceFront.y,sourceFront.x)/Mathf.PI+1)*.5f;
        for(int i=0;i<positions.Length;i++) attributes[i]=new Vector4(positions[i].x,positions[i].y,positions[i].z,0);
        Apply();
    }
    public void Apply() {
        if(!target || !remap) return;
        block.Clear(); target.GetPropertyBlock(block,0);
        if(block.isEmpty) target.GetPropertyBlock(block);
        block.SetInt("_PointCount",attributes.Length); block.SetVectorArray("_Attributes",attributes);
        block.SetFloat("_Tilt",tilt); block.SetVector("_Front",new Vector4(currentTilt.x,currentTilt.y,0,0));
        block.SetVector("_WorldFront",new Vector4(worldFront.x,worldFront.y,worldFront.z,0));
        block.SetFloat("_TiltRotation",rotation); block.SetTexture(settings.remapLabel,remap);
        target.SetPropertyBlock(block,0);
    }
    void OnDestroy() {if(remap) Destroy(remap);}
}
