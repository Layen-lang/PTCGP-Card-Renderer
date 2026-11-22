using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

public static class SourceMsrCardProbe
{
    [Serializable] sealed class Trajectory {public string cardId,size,humanConfirmation; public Sample[] samples;}
    [Serializable] sealed class Sample {
        public int animationState; public float elapsed,timeParam,parallaxTilt,elapsedTime,flipAnim,flipBlend;
        public float[] parameters;
    }
    static void Require(bool value,string message) {
        if(!value) throw new InvalidOperationException("MSR source probe: "+message);
    }
    static Vector3 Front(float angle) {
        float radians=angle*Mathf.Deg2Rad; return new Vector3(Mathf.Sin(radians),0,-Mathf.Cos(radians));
    }
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceMsrCardState>();
        Require(states.Length==1,"exactly one exported MSR controller");
        var state=states[0]; state.Manual=true;
        Require(state.AuraCount>0 && state.ParallaxCount>0 && state.ReflectionCount>0,"original three tag groups resolve");
        var args=Environment.GetCommandLineArgs(); int source=Array.IndexOf(args,"--source-msr-json");
        if(source>=0) VerifySamples(state,args[source+1],log);
        state.ResetState(); state.Step(Front(state.Threshold-1),.02f);
        Require(state.AnimationState==0,"below threshold idle");
        state.Step(Front(state.Threshold+1),.02f);
        Require(state.AnimationState==1 && Mathf.Abs(state.Elapsed-.02f)<1e-6f,"crossing advances before evaluation");
        state.IsAnimationStopped=true; float parallax=state.Parameters[2];
        state.Step(Front(state.Threshold+2),.02f);
        Require(state.AnimationState==1 && state.Elapsed>.02f && state.Parameters[2]==parallax,"stop freezes only parallax");
        state.IsAnimationStopped=false; state.Step(Front(state.Threshold+1),state.Duration+.1f);
        Require(state.AnimationState==0 && state.Elapsed==0,"duration resets elapsed and animation state");
        state.Step(Front(state.Threshold+1),.02f);
        Require(state.AnimationState==0,"held above does not retrigger");
        state.Step(Front(state.Threshold-1),.02f); state.Step(Front(state.Threshold+1),.02f);
        Require(state.AnimationState==1,"below then crossing rearms");
        log.Add("MSRControlFlow=PASS strict-crossing,advance-before-evaluate,partial-stop,duration-reset,no-retrigger,rearm; fidelityValidated=False");
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float side in new[]{-1f,1f}) foreach(float instant in new[]{0f,.5f,1f,2.5f}) {
            float sign=side,time=instant;
            poses.Add(new SourceCardTouchProbe.Pose {name="msr-"+(sign<0 ? "left" : "right")+"-t"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                root.localRotation=Quaternion.identity; state.ResetState();
                state.Step(Front(0),0); root.localRotation=Quaternion.Euler(0,-sign*(state.Threshold+1),0);
                state.Step(state.transform.TransformDirection(Vector3.back),0);
                float remaining=time;
                while(remaining>0) {float delta=Mathf.Min(1f/60,remaining); state.Step(state.transform.TransformDirection(Vector3.back),delta); remaining-=delta;}
                log.Add("MSRPose side="+sign+" time="+time+" elapsed="+state.Elapsed+" animState="+state.AnimationState+
                    " reflectionState="+state.ReflectionState+" parameters="+string.Join(",",state.Parameters));
            }});
        }
        return poses;
    }
    static void VerifySamples(SourceMsrCardState state,string path,List<string> log) {
        var payload=File.ReadAllBytes(path);
        var input=JsonUtility.FromJson<Trajectory>(System.Text.Encoding.UTF8.GetString(payload));
        Require(input.size=="L" && !string.IsNullOrEmpty(input.humanConfirmation),"confirmed L reference");
        int count=0,failed=0; float maximum=0;
        foreach(var s in input.samples) {
            // Source resets elapsed at the end but preserves evaluated curves.
            // Idle samples cannot reconstruct that evaluation from elapsed alone.
            if(s.animationState!=1) continue;
            Require(s.parameters.Length==8,"native parameter width");
            var actual=state.EvaluateRecorded(s.elapsed,s.timeParam,s.parallaxTilt,s.elapsedTime,s.animationState,s.flipAnim,s.flipBlend);
            for(int i=0;i<6;i++) {
                float difference=Mathf.Abs(actual[i]-s.parameters[i]); maximum=Mathf.Max(maximum,difference);
                if(difference>.0001f) {failed++; log.Add("MSRNativeFunctionMismatch parameter="+i+" elapsed="+s.elapsed+" actual="+actual[i]+" expected="+s.parameters[i]+" delta="+difference);}
            }
            count++;
        }
        Require(count>0,"must sample playing window");
        string hash=BitConverter.ToString(SHA256.Create().ComputeHash(payload)).Replace("-","").ToLowerInvariant();
        log.Add("MSRNativeFunctions="+(failed==0 ? "PASS" : "FAIL")+" samples="+count+" parameters=6 maxAbsoluteDelta="+maximum+
            " sha256="+hash+" frameLocked=False springTrajectoryVerified=False fidelityValidated=False");
        Require(failed==0,"sample mismatch count="+failed);
    }
}
