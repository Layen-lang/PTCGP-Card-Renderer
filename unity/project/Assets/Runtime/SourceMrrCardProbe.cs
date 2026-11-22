using System;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Security.Cryptography;

// Tests source control flow and records deterministic MRT poses. This does not
// assert that a static image proves temporal equivalence with the Android game.
public static class SourceMrrCardProbe
{
    [Serializable] sealed class Trajectory {public string cardId,rarity,size,humanConfirmation; public Sample[] samples;}
    [Serializable] sealed class Sample {public float elapsed,sourceTime,sourceDelta; public bool playing; public float[] startFront,parameters;}
    static void Require(bool value,string message) {
        if(!value) throw new InvalidOperationException("MRR source probe: "+message);
    }
    static Vector3 Front(float angle) {
        float radians=angle*Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(radians),0,-Mathf.Cos(radians));
    }
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceMrrCardState>();
        Require(states.Length==1,"exactly one exported MRR controller required");
        var state=states[0]; state.Manual=true;
        var args=Environment.GetCommandLineArgs();
        int sourceIndex=Array.IndexOf(args,"--source-mrr-json");
        if(sourceIndex>=0) VerifySamples(state,args[sourceIndex+1],log);
        Require(state.SurfaceCount>0 && state.EffectCount>0 && state.FlashCount>0,"all three original tag groups must resolve");
        state.ResetState();
        state.Step(Front(state.Threshold-1),.02f,.02f);
        Require(!state.Playing,"below threshold remains idle");
        state.Step(Front(state.Threshold+1),.02f,.04f);
        Require(state.Playing && Mathf.Abs(state.Elapsed-.02f)<.000001f,"crossing advances before evaluating curves");
        state.Step(Front(state.Threshold+1),state.Duration+.1f,state.Duration+.14f);
        Require(!state.Playing,"duration ends playback");
        float completed=state.Elapsed;
        state.Step(Front(state.Threshold+1),.02f,state.Duration+.16f);
        Require(!state.Playing && state.Elapsed==completed,"holding above threshold does not retrigger");
        state.Step(Front(state.Threshold-1),.02f,state.Duration+.18f);
        state.Step(Front(state.Threshold+1),.02f,state.Duration+.20f);
        Require(state.Playing && Mathf.Abs(state.Elapsed-.02f)<.000001f,"return below threshold rearms");
        log.Add("MRRControlFlow=PASS strict-crossing,advance-before-evaluate,duration,no-retrigger,rearm; fidelityValidated=False");
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float sign in new[]{-1f,1f}) foreach(float time in new[]{0f,.1f,.25f,.5f,.6f}) {
            float side=sign,elapsed=time;
            poses.Add(new SourceCardTouchProbe.Pose {name="mrr-"+(side<0 ? "left" : "right")+"-t"+elapsed.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                root.localRotation=Quaternion.identity; state.ResetState();
                root.localRotation=Quaternion.Euler(0,-side*(state.Threshold-1),0);
                state.Step(state.transform.TransformDirection(Vector3.back),1f/60,1f/60);
                root.localRotation=Quaternion.Euler(0,-side*(state.Threshold+1),0);
                state.Step(state.transform.TransformDirection(Vector3.back),0,2f/60);
                float clock=2f/60,remaining=elapsed;
                while(remaining>0) {
                    float delta=Mathf.Min(1f/60,remaining); clock+=delta;
                    state.Step(state.transform.TransformDirection(Vector3.back),delta,clock); remaining-=delta;
                }
                log.Add("MRRPose="+(side<0 ? "left" : "right")+" time="+elapsed+" elapsed="+state.Elapsed+
                    " playing="+state.Playing+" speed="+state.AdjustedSpeed+" layerX="+state.LayerTranslation+
                    " radialScaling="+state.RadialScaling+" radialAnimation="+state.RadialAnimation);
            }});
        }
        return poses;
    }
    static void VerifySamples(SourceMrrCardState state,string path,List<string> log) {
        byte[] payload=File.ReadAllBytes(path);
        var trajectory=JsonUtility.FromJson<Trajectory>(System.Text.Encoding.UTF8.GetString(payload));
        Require(trajectory.size=="L" && !string.IsNullOrEmpty(trajectory.humanConfirmation),"confirmed original L source required");
        Require(trajectory.samples.Length>0,"nonempty source trajectory");
        int playing=0,failed=0; float maxDelta=0;
        foreach(var sample in trajectory.samples) {
            Require(sample.parameters.Length==17 && sample.startFront.Length==3,"original MRR state widths");
            var actual=state.EvaluateRecorded(sample.elapsed,new Vector3(sample.startFront[0],sample.startFront[1],sample.startFront[2]));
            for(int i=0;i<actual.Length;i++) {
                float difference=Mathf.Abs(actual[i]-sample.parameters[i]); maxDelta=Mathf.Max(maxDelta,difference);
                if(difference>.0001f) {
                    failed++;
                    log.Add("MRRNativeCurveMismatch parameter="+i+" elapsed="+sample.elapsed+" delta="+sample.sourceDelta+
                        " expected="+sample.parameters[i]+" actual="+actual[i]+" difference="+difference);
                }
            }
            if(sample.playing) playing++;
        }
        Require(playing>0,"source playback window must be sampled");
        string hash=BitConverter.ToString(SHA256.Create().ComputeHash(payload)).Replace("-","").ToLowerInvariant();
        log.Add("MRRNativeCurveSamples="+(failed==0 ? "PASS" : "FAIL")+" card="+trajectory.cardId+" samples="+trajectory.samples.Length+" playing="+playing+
            " parameters=17 maxAbsoluteDelta="+maxDelta+" sha256="+hash+" input="+path+" frameLocked=False fidelityValidated=False");
        Require(failed==0,"native parameter mismatches="+failed+"; see complete diagnostic report");
    }
}
