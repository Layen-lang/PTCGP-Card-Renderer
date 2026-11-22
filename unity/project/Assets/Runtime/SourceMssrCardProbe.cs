using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

public static class SourceMssrCardProbe
{
    [Serializable] sealed class Trajectory {public string cardId,size,humanConfirmation; public Sample[] samples;}
    [Serializable] sealed class Sample {
        public float elapsed,sourceDelta,adjustedSpeed; public bool playing,clockBracketStable; public float[] parameters;
    }
    static void Require(bool value,string message) {
        if(!value) throw new InvalidOperationException("MSSR source probe: "+message);
    }
    static Vector3 Front(float angle) {
        float radians=angle*Mathf.Deg2Rad; return new Vector3(Mathf.Sin(radians),0,-Mathf.Cos(radians));
    }
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceMssrCardState>();
        Require(states.Length==1,"exactly one original L MSSR controller");
        var state=states[0]; state.Manual=true;
        var args=Environment.GetCommandLineArgs(); int source=Array.IndexOf(args,"--source-mssr-json");
        if(source>=0) VerifySamples(state,args[source+1],log);
        Require(state.SurfaceCount>0,"original Card-Effect-Emit tag resolves");
        state.ResetState(); state.Step(Front(state.Threshold-1),.02f,.02f);
        Require(!state.Playing,"below threshold remains idle");
        state.Step(Front(state.Threshold+1),.02f,.04f);
        float increment=.02f*(state.UsesSpeedAdjustment ? state.AdjustedSpeed : 1);
        Require(state.Playing && state.EvaluatedElapsed==0 && Mathf.Abs(state.Elapsed-increment)<.000001f,
            "crossing evaluates zero BEFORE advancing");
        state.Step(Front(state.Threshold+1),.02f,.06f);
        Require(Mathf.Abs(state.EvaluatedElapsed-increment)<.000001f,"next frame evaluates the previous elapsed");
        state.Step(Front(state.Threshold+1),state.Duration*10+1,state.Duration*10+1.06f);
        Require(!state.Playing,"duration ends playback");
        float finalTime=state.EvaluatedElapsed;
        state.Step(Front(state.Threshold+1),.02f,state.Duration*10+1.08f);
        Require(!state.Playing && state.EvaluatedElapsed==finalTime,"holding retains last curve evaluation");
        state.Step(Front(state.Threshold-1),.02f,state.Duration*10+1.10f);
        state.Step(Front(state.Threshold+1),.02f,state.Duration*10+1.12f);
        Require(state.Playing && state.EvaluatedElapsed==0,"return below threshold rearms");
        log.Add("MSSRControlFlow=PASS crossing,evaluate-before-advance,retained-final-sample,rearm; fidelityValidated=False");
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float sign in new[]{-1f,1f}) foreach(float time in new[]{0f,.1f,.25f,.4f,.55f}) {
            float side=sign,elapsed=time;
            poses.Add(new SourceCardTouchProbe.Pose {name="mssr-"+(side<0 ? "left" : "right")+"-t"+
                elapsed.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                root.localRotation=Quaternion.identity; state.ResetState();
                state.Step(Front(state.Threshold-1),1f/60,1f/60);
                root.localRotation=Quaternion.Euler(0,-side*(state.Threshold+1),0);
                state.Step(state.transform.TransformDirection(Vector3.back),0,2f/60);
                float clock=2f/60,remaining=elapsed;
                while(remaining>0) {
                    float delta=Mathf.Min(1f/60,remaining); clock+=delta;
                    state.Step(state.transform.TransformDirection(Vector3.back),delta,clock); remaining-=delta;
                }
                log.Add("MSSRPose side="+side+" requestedTime="+elapsed+" elapsed="+state.Elapsed+
                    " evaluatedElapsed="+state.EvaluatedElapsed+" playing="+state.Playing+" adjustedSpeed="+state.AdjustedSpeed);
            }});
        }
        return poses;
    }
    static void VerifySamples(SourceMssrCardState state,string path,List<string> log) {
        byte[] payload=File.ReadAllBytes(path);
        var trajectory=JsonUtility.FromJson<Trajectory>(System.Text.Encoding.UTF8.GetString(payload));
        Require(trajectory.size=="L" && !string.IsNullOrEmpty(trajectory.humanConfirmation),"confirmed L source");
        int compared=0,excluded=0,failed=0; float maxDelta=0;
        foreach(var sample in trajectory.samples) {
            if(!sample.clockBracketStable || !sample.playing) {excluded++; continue;}
            Require(sample.parameters.Length==4,"four native values");
            // Native fields store elapsed after increment but curve values before it.
            float evaluated=sample.elapsed-sample.sourceDelta*(state.UsesSpeedAdjustment ? sample.adjustedSpeed : 1);
            Require(evaluated>=-.00001f,"native elapsed cannot precede zero");
            var actual=state.EvaluateRecorded(evaluated);
            for(int i=0;i<4;i++) {
                float difference=Mathf.Abs(actual[i]-sample.parameters[i]); maxDelta=Mathf.Max(maxDelta,difference);
                if(difference>.0001f) {failed++;log.Add("MSSRNativeMismatch parameter="+i+" evaluated="+evaluated+
                    " expected="+sample.parameters[i]+" actual="+actual[i]+" delta="+difference);}
            }
            compared++;
        }
        string hash=BitConverter.ToString(SHA256.Create().ComputeHash(payload)).Replace("-","").ToLowerInvariant();
        log.Add("MSSRNativeCurveSamples="+(failed==0 ? "PASS" : "FAIL")+" card="+trajectory.cardId+" compared="+compared+
            " excluded="+excluded+" parameters=4 maxAbsoluteDelta="+maxDelta+" sha256="+hash+" input="+path+
            " fullHistoryCompared=False fidelityValidated=False");
        Require(compared>0 && failed==0,"native stable playing samples required; mismatches="+failed);
    }
}
