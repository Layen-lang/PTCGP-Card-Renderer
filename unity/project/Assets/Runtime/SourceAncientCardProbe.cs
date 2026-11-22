using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

public static class SourceAncientCardProbe
{
    [Serializable] sealed class Trajectory {public string size,humanConfirmation; public Sample[] samples;}
    [Serializable] sealed class Sample {
        public float[] worldFront,shake,remapped,sandVolumes; public int[] meshIndices;
        public float elapsedTime,goal; public bool clockBracketStable,isAnimationStopped;
        public SourceAncientCardState.Stratum[] strata;
    }
    static void Require(bool value,string message) {if(!value) throw new InvalidOperationException("Ancient source probe: "+message);}
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceAncientCardState>(); Require(states.Length==1,"one original Ancient controller");
        var state=states[0]; state.Manual=true;
        var args=Environment.GetCommandLineArgs(); int index=Array.IndexOf(args,"--source-ancient-json");
        if(index>=0) VerifySamples(state,args[index+1],log);
        var saved=UnityEngine.Random.state;
        try {
            UnityEngine.Random.InitState(175); // Diagnostic reproducibility, not the game's PRNG state.
            var before=state.Strata;
            state.IsAnimationStopped=true;
            state.Step(new Vector3(.2f,0,-Mathf.Sqrt(.96f)),.02f);
            foreach(float rate in state.Rates) Require(rate==0,"stop sets all emission rates to zero");
            Require(state.Strata[0].state!=0 && state.Strata[0].elapsed>=before[0].elapsed,"stop leaves strata animation active");
            state.IsAnimationStopped=false;
            state.Step(new Vector3(.2f,0,-Mathf.Sqrt(.96f)),.02f);
            Require(state.Strata[0].lastDir!=0,"original direction and random delay retained");
            log.Add("AncientControlFlow=PASS partial-stop,original-particles; diagnosticRandomSeed=175 sourceRandomStateKnown=False fidelityValidated=False");
        } finally {UnityEngine.Random.state=saved;}
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float side in new[]{-1f,1f}) foreach(float instant in new[]{0f,.1f,.5f,2f}) {
            float sign=side,time=instant;
            poses.Add(new SourceCardTouchProbe.Pose {name="ancient-"+(sign<0 ? "left" : "right")+"-t"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                state.ResetState(); UnityEngine.Random.InitState(175);
                root.localRotation=Quaternion.Euler(0,-sign*12,0);
                state.Step(state.transform.TransformDirection(Vector3.back),0);
                float remaining=time;
                while(remaining>0) {float delta=Mathf.Min(.016666667f,remaining); state.Step(state.transform.TransformDirection(Vector3.back),delta); remaining-=delta;}
                log.Add("AncientPose side="+sign+" time="+time+" goal="+state.Goal+" remapped="+string.Join(",",state.Remapped)+" rates="+string.Join(",",state.Rates));
            }});
        }
        return poses;
    }
    static void VerifySamples(SourceAncientCardState state,string path,List<string> log) {
        byte[] payload=File.ReadAllBytes(path); var input=JsonUtility.FromJson<Trajectory>(System.Text.Encoding.UTF8.GetString(payload));
        Require(input.size=="L" && !string.IsNullOrEmpty(input.humanConfirmation) && input.samples.Length>0,"confirmed L reference");
        int stable=0,failed=0,checkedValues=0; float maximum=0;
        foreach(var s in input.samples) {
            if(!s.clockBracketStable) continue; stable++;
            var actual=state.EvaluateRecorded(new Vector3(s.worldFront[0],s.worldFront[1],s.worldFront[2]),s.elapsedTime,s.goal,s.strata);
            var expected=new List<float>{s.goal,s.shake[0],s.shake[1]}; expected.AddRange(s.remapped); expected.AddRange(s.sandVolumes);
            foreach(int value in s.meshIndices) expected.Add(value);
            for(int i=0;i<17;i++) {
                if(i>=3 && i<9 && s.strata[i-3].state==0) continue; // Native early return retains remap.
                if(i>=9 && s.isAnimationStopped) continue; // Native stop retains volumes/indices.
                checkedValues++; float difference=Mathf.Abs(actual[i]-expected[i]); maximum=Mathf.Max(maximum,difference);
                if(difference>.0001f) {failed++; log.Add("AncientNativeFunctionMismatch parameter="+i+" actual="+actual[i]+" expected="+expected[i]+" delta="+difference+" elapsed="+s.elapsedTime);}
            }
        }
        Require(stable>0,"stable bracketed samples");
        string hash=BitConverter.ToString(SHA256.Create().ComputeHash(payload)).Replace("-","").ToLowerInvariant();
        log.Add("AncientNativeFunctions="+(failed==0 ? "PASS" : "FAIL")+" samples="+input.samples.Length+" stableClockSamples="+stable+" values="+checkedValues+
            " parameters=17 maxAbsoluteDelta="+maximum+" sha256="+hash+" frameLocked=False strataTrajectoryVerified=False sourceRandomStateKnown=False fidelityValidated=False");
        Require(failed==0,"native function mismatches="+failed);
    }
}
