using System;
using System.Collections.Generic;
using UnityEngine;

// Original 1.7.5 KiraPuyoObject, RVAs 47f2238..47f25a4. No source clock.
public sealed class SourceKiraPuyoState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    Renderer surface;
    AnimationCurve curve;
    MaterialPropertyBlock block;
    public bool Manual {get;set;}
    public float Animation {get;private set;}
    public float KiraScale {get;private set;}
    public string SourceId => settings.sourceId;
    public void Initialize(SourceCardEffects.Effect source,Renderer renderer,List<string> log) {
        settings=source; surface=renderer; block=new MaterialPropertyBlock();
        var original=Array.Find(source.curves,c=>c.property=="Curve");
        if(original==null) throw new InvalidOperationException("Original KiraPuyo curve absent");
        curve=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(original.nativeJson).curve;
        if(curve==null || curve.length!=original.keys.Length)
            throw new InvalidOperationException("Original KiraPuyo curve failed to deserialize");
        Step(transform.TransformDirection(Vector3.back));
        log.Add("OriginalCardEffect=KiraPuyoObject sourceId="+SourceId+" materialIndex=0 curves=1 native=1.7.5 fidelityValidated=False");
    }
    void LateUpdate() {if(settings!=null && !Manual) Step(transform.TransformDirection(Vector3.back));}
    public void Step(Vector3 worldFront) {
        // ARM64 uses fixed LightDir=(.5,.5), without direction normalization.
        Animation=.5f*(worldFront.x*.5f+.5f)+.5f*(worldFront.y*.5f+.5f);
        KiraScale=EvaluateRecorded(Animation); Apply();
    }
    public float EvaluateRecorded(float originalAnimation) {
        float phase=settings.scaleAnimationOffset+settings.vertScaleSpeed*originalAnimation;
        // frintm at RVA 47f22d8: Floor, not the decompiler's integer truncation.
        return curve.Evaluate(Mathf.Clamp01(phase-Mathf.Floor(phase)));
    }
    public void ApplyRecordedSnapshot(float originalAnimation,float originalScale) {
        if(float.IsNaN(originalAnimation)||float.IsInfinity(originalAnimation)||float.IsNaN(originalScale)||float.IsInfinity(originalScale))
            throw new InvalidOperationException("KiraPuyo diagnostic values invalid");
        Manual=true; Animation=originalAnimation; KiraScale=originalScale; Apply();
    }
    public void Apply() {
        if(!surface) return; // Original UpdateMPB explicitly permits a null renderer.
        block.Clear(); surface.GetPropertyBlock(block,0);
        if(block.isEmpty) surface.GetPropertyBlock(block);
        block.SetFloat("_RampRepeat",settings.rampRepeat); block.SetFloat("_ScrollScale",settings.scrollScale);
        block.SetFloat("_ScrollOffset",settings.scrollOffset); block.SetFloat("_KiraScale",KiraScale); block.SetFloat("_Anim",Animation);
        surface.SetPropertyBlock(block,0);
    }
}
