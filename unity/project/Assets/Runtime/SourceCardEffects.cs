using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Original 1.7.5 CardUVShadow and CardMReliefSRObject; exported prefab values
// and ReliefMSRAnimationSettings curves, not a rarity-wide material override.
public sealed class SourceCardEffects : MonoBehaviour
{
    [Serializable] public class Plan { public Effect[] effects; public bool emissive; }
    // EmissiveObject.UseEmissiveStatic: the bloom only runs for cards carrying one.
    public static bool CardEmissive { get; private set; }
    static readonly HashSet<Material> OutlineRampFitted=new HashSet<Material>();
    [Serializable] public class Effect {
        public string kind,sourceId; public int[] indices; public Vector2 translateOffset;
        public Vector3 lightDir; public float animStartDegree,animTimeScale,animDuration;
        public Curve[] curves;
        public float IllustAlpha,TextAlpha,EmitSwitch,EmitIntensity,Darkness;
        public float flashRadialStartOffset,recordingTime,minTiltSpeed,maxTiltSpeed,minAnimSpeed,maxAnimSpeed;
        public float transitionAngle; public Vector2 transitionBoundaryOffset;
        public float transitionBane,transitionNensei,flashAnimTimeScale,flashAnimDuration,outlineAnimTimeScale,outlineAnimDuration;
        public int useSpeedAdjust; public bool enabled;
        public float endAnimDuration,stopAnimTiming,timeOffset,intensityNoiseSpeed,reflectFlipBookMaxSpeed;
        public float reflectStartBane,reflectStartNensei,reflectEndBane,reflectEndNensei;
        public float checkRotatingTime,rotatingEndThreshold,rotatingStartThreshold;
        public float disappearBane,disappearNensei,appearBane,appearNensei;
        public bool isAnimationStopped;
        public float tiltPower,delayTime2,pointAccel,shearAccel,dorodoroDistance,resistancePower;
        public float minDorodoroCoef,maxPointSpeed,minPointSpeed,goalThreshold,pointMoveByTilt,pointForceChangeByTilt;
        public bool useMarbleDelay; public MarblePoint[] points; public string remapLabel; public int remapResolution;
        public int[] rendererIndices;
        public int animationTexFrameCount,animationFrameCount,animFrameOffset,skipAnimThreshold;
        public float animSwitchSpeed,accellRatio,animCurveScale,animStartDelayRangeA,animStartDelayRangeB;
        public float changeRangeStart,changeRangeEnd,zuzuGoalAnimThreshold,scrollLength,shapeChangeSpeed,dot2Multiply;
        public float diffOffset,shakeSpeed,noiseScale,frictionScale,maxFriction;
        public float startSandBaseEmissionRate,middleSandBaseEmissionRate,endSandBaseEmissionRate;
        public float[] scrolls; public Vector2 shakeAIntensity,shakeAFrequency,shakeBIntensity,shakeBFrequency;
        public ParticlePath[] sandParticlePaths,sand2ParticlePaths;
        public float accelIntensity,maxFlowSpeed,minFlowSpeed,initFlowSpeed,resistance,minTiltPower,lightSpeed;
        public float flowAMinRotateSpeed,flowAMaxRotateSpeed,flowBMinRotateSpeed,flowBMaxRotateSpeed,flowAInitialRotation,flowBInitialRotation;
        public float rampRepeat,scrollScale,scrollOffset,vertScaleSpeed,scaleAnimationOffset,settingsMin,settingsMax;
        public int defaultCircularAnglePattern,primTypeASymmetryCount,primTypeBSymmetryCount,primTypeCSymmetryCount,meshDivideCount,expandLength;
        public float defaultCircularAngleManual,tiltThreshold,tiltStateChangeDelay,rotateAccel,brakeDuration;
        public float moveAngleScale,centerIntensity,fadeOut,fadeOutEnd,expandPower,limitLengthRatio;
        public float LimitAdjustCurvePower,LimitAdjustSpeed,distanceFadeOutSpeed,distanceFadeOutCurvePower;
        public bool useLengthLimit,useDistanceFadeOut;
        public CircularPrimitive[] primitives;
        public ParticlePath movingKiraARendererPath,movingKiraBRendererPath,trailKiraARendererPath,trailKiraBRendererPath;
        public ParticlePath trailKiraAMeshFilterPath,trailKiraBMeshFilterPath;
    }
    [Serializable] public class Curve { public string property,nativeJson; public Key[] keys; public int preWrap,postWrap; }
    [Serializable] public class NativeCurve { public AnimationCurve curve; }
    [Serializable] public class Key { public float time,value,inSlope,outSlope,inWeight,outWeight; public int weightedMode; }
    [Serializable] public class MarblePoint {
        public Vector2 DefaultPosition,TiltMovePosition; public float RotationWithTilt,DefaultForce,TiltForce;
    }
    [Serializable] public class ParticlePath {public int[] indices;}
    [Serializable] public class CircularPrimitive {
        public int PrimType; public float BaseScale,BaseIntensity,MinIntensity,MaxIntensity,FlickerSpeed,FlickerScaling,StartAngle;
        public bool UseMorphing,UseMorphingNoise,ReverseRotation; public float MorphingSpeed,MorphingClearly,MaxRotateSpeed;
        public float Speed,Angle,BreakGoalAngle,BreakStartAngle,BreakingTime,SpeedAtBrakeStart;
    }
    Effect settings;
    Renderer[] shadowRenderers, reliefRenderers, effectRenderers;
    AnimationCurve[] curves;
    int[] properties;
    MaterialPropertyBlock block;
    static readonly int ShadowUV = Shader.PropertyToID("_ShadowUVTranslate");
    static readonly ShaderTagId SearchTag = new ShaderTagId("SearchTag");
    float lastTilt,elapsed;
    bool playing;
    readonly float[] values = new float[9];
    void Awake() { block=new MaterialPropertyBlock(); }

    public static void Attach(Transform face,string json,List<string> log)
    {
        var plan=JsonUtility.FromJson<Plan>(json);
        CardEmissive=plan.emissive;
        // Fitted on the real game (SR cards without effect controller, 1.8.1; cards with one do not shift): the outline ramp reads 0.025 further
        // than the saved _RampOffset. No game code writes this value; the cause is still unknown.
        if(plan.effects.Length==0) foreach(var r in face.GetComponentsInChildren<Renderer>(true)) foreach(var m in r.sharedMaterials)
            if(m && m.shader.name.EndsWith("ShadowBox/Opaque-Hologram_Tuning") && OutlineRampFitted.Add(m)) m.SetFloat("_RampOffset",m.GetFloat("_RampOffset")+.025f);
        foreach(var effect in plan.effects) {
            var node=face;
            foreach(int index in effect.indices) node=node.GetChild(index);
            if(effect.kind=="CardRGBObject") {
                var rgb=node.gameObject.AddComponent<SourceRgbCardState>();
                rgb.enabled=effect.enabled; rgb.Initialize(log);
                continue;
            }
            if(effect.kind=="CircularKiraObject") {
                var circular=node.gameObject.AddComponent<SourceCircularKiraState>();
                circular.Initialize(effect,face,log); circular.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="KiraPuyoObject") {
                var target=face;
                foreach(int index in effect.rendererIndices) target=target.GetChild(index);
                var kira=node.gameObject.AddComponent<SourceKiraPuyoState>();
                kira.Initialize(effect,target.GetComponent<Renderer>(),log); kira.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardAncientObject") {
                var ancient=node.gameObject.AddComponent<SourceAncientCardState>();
                ancient.Initialize(effect,face,log); ancient.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardFutureObject") {
                var future=node.gameObject.AddComponent<SourceFutureCardState>();
                future.Initialize(effect,log); future.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardMRRObject") {
                var mrr=node.gameObject.AddComponent<SourceMrrCardState>();
                mrr.Initialize(effect,log); mrr.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardDualMRRObject") {
                var dual=node.gameObject.AddComponent<SourceDualMrrCardState>();
                dual.enabled=effect.enabled; dual.Initialize(effect,log);
                continue;
            }
            if(effect.kind=="CardDualMSRObject") {
                var dual=node.gameObject.AddComponent<SourceDualMsrCardState>();
                dual.enabled=effect.enabled; dual.Initialize(effect,log);
                continue;
            }
            if(effect.kind=="CardDualNoParallaxObject") {
                var dual=node.gameObject.AddComponent<SourceDualNoParallaxCardState>();
                dual.enabled=effect.enabled; dual.Initialize(effect,log);
                continue;
            }
            if(effect.kind=="CardMSSRObject") {
                var mssr=node.gameObject.AddComponent<SourceMssrCardState>();
                mssr.Initialize(effect,log); mssr.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardMSRObject") {
                var msr=node.gameObject.AddComponent<SourceMsrCardState>();
                msr.Initialize(effect,log); msr.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardMarbleLayer") {
                var target=face;
                foreach(int index in effect.rendererIndices) target=target.GetChild(index);
                var marble=node.gameObject.AddComponent<SourceMarbleCardState>();
                marble.Initialize(effect,target.GetComponent<Renderer>(),log); marble.enabled=effect.enabled;
                continue;
            }
            if(effect.kind=="CardURObject") {
                node.gameObject.AddComponent<SourceUrCardState>().Initialize(log);
                continue;
            }
            if(effect.kind=="GlitterFlowMaps") {
                var target=face;
                foreach(int index in effect.rendererIndices) target=target.GetChild(index);
                node.gameObject.AddComponent<SourceGlitterFlowState>().Initialize(effect,target.GetComponent<Renderer>(),log);
                continue;
            }
            if(effect.kind=="CardImmersiveObject") {
                node.gameObject.AddComponent<SourceImmersiveCardState>().Initialize(effect,log);
                continue;
            }
            var state=node.gameObject.AddComponent<SourceCardEffects>();
            state.settings=effect;
            state.shadowRenderers=Collect(node,"Card-UVShadow");
            state.reliefRenderers=effect.kind=="CardMReliefSRObject" ? Collect(node,"MRR-ChangeColor-Lighting") : new Renderer[0];
            state.effectRenderers=effect.kind=="CardMReliefSRObject" ? Collect(node,"Card-Effect-Emit") : new Renderer[0];
            if(effect.curves!=null) {
                state.curves=new AnimationCurve[effect.curves.Length];
                state.properties=new int[effect.curves.Length];
                for(int n=0;n<effect.curves.Length;n++) {
                    var source=effect.curves[n];
                    // Unity's serialized infinity enum differs from WrapMode.
                    // Let its own serializer import the complete original curve.
                    state.curves[n]=JsonUtility.FromJson<NativeCurve>(source.nativeJson).curve;
                    if(state.curves[n]==null || state.curves[n].length!=source.keys.Length)
                        throw new InvalidOperationException("Original relief curve did not deserialize: "+source.property);
                    state.properties[n]=Shader.PropertyToID(source.property);
                }
                state.Evaluate(0);
            }
            state.Tick(0);
            log.Add("OriginalCardEffect="+effect.kind+" sourceId="+effect.sourceId+" shadows="+state.shadowRenderers.Length+" relief="+state.reliefRenderers.Length+" effects="+state.effectRenderers.Length+" native=1.7.5");
        }
    }
    internal static Renderer[] Collect(Transform node,string tag)
    {
        var wanted=new ShaderTagId(tag); var result=new List<Renderer>();
        foreach(var renderer in node.GetComponentsInChildren<Renderer>()) {
            foreach(var material in renderer.sharedMaterials) {
                if(!material) continue;
                var shader=material.shader;
                for(int pass=0;pass<shader.passCount;pass++)
                    if(shader.FindPassTagValue(pass,SearchTag)==wanted && !result.Contains(renderer)) result.Add(renderer);
            }
        }
        return result.ToArray();
    }
    void Evaluate(float progress) { for(int i=0;i<curves.Length;i++) values[i]=curves[i].Evaluate(progress); }
    void LateUpdate() { if(settings!=null) Tick(Time.deltaTime); }
    public static void Refresh(Transform card) {
        foreach(var state in card.GetComponentsInChildren<SourceCardEffects>()) state.Tick(0);
        foreach(var state in card.GetComponentsInChildren<SourceUrCardState>()) state.Refresh();
        foreach(var state in card.GetComponentsInChildren<SourceMrrCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceDualMrrCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceDualMsrCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceDualNoParallaxCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceMssrCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceKiraPuyoState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceCircularKiraState>()) state.Refresh();
        foreach(var state in card.GetComponentsInChildren<SourceMsrCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceMarbleCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceAncientCardState>()) state.Apply();
        foreach(var state in card.GetComponentsInChildren<SourceFutureCardState>()) state.Apply();
    }
    void Tick(float delta)
    {
        // UpdateShadowTranslate RVAs 47ee618/47e8fe8: project negative
        // normalized light onto the plane defined by transformed Vector3.back.
        var front=transform.TransformDirection(Vector3.back);
        var projected=Vector3.ProjectOnPlane(-settings.lightDir.normalized,front.normalized);
        var local=transform.InverseTransformDirection(projected);
        var translation=new Vector4(local.x+settings.translateOffset.x,local.y+settings.translateOffset.y,0,0);
        if(curves!=null) {
            // UpdateTilt 47e8e9c and UpdateAnimation 47e8f5c.
            float tilt=Mathf.Atan2(new Vector2(front.x,front.y).magnitude,-front.z)*Mathf.Rad2Deg;
            if(!playing && lastTilt<settings.animStartDegree && tilt>settings.animStartDegree) { playing=true; elapsed=0; }
            if(playing) {
                elapsed+=delta; Evaluate(elapsed/settings.animTimeScale);
                playing=elapsed<=settings.animDuration*settings.animTimeScale;
            }
            lastTilt=tilt;
        }
        Apply(reliefRenderers,translation,0,4,false);
        Apply(shadowRenderers,translation,4,2,true);
        Apply(effectRenderers,translation,5,4,false);
    }
    void Apply(Renderer[] renderers,Vector4 translation,int start,int count,bool shadow)
    {
        foreach(var renderer in renderers) for(int index=0;index<renderer.sharedMaterials.Length;index++) {
            block.Clear(); renderer.GetPropertyBlock(block,index);
            // DynamicUI uses renderer-level blocks in this host. Carry those
            // textures into indexed blocks before adding original effect state.
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            if(shadow) block.SetVector(ShadowUV,translation);
            if(curves!=null) for(int n=start;n<start+count;n++) block.SetFloat(properties[n],values[n]);
            renderer.SetPropertyBlock(block,index);
        }
    }
}
