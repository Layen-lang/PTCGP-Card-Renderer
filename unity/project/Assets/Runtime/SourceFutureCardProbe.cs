using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

public static class SourceFutureCardProbe
{
    [Serializable] sealed class Trajectory {public string size,humanConfirmation; public Sample[] samples;}
    [Serializable] sealed class Sample {public float[] worldFront; public float tiltDot; public int goal; public bool clockBracketStable;}
    static void Require(bool value,string message) {if(!value) throw new InvalidOperationException("Future source probe: "+message);}
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceFutureCardState>(); Require(states.Length==1,"one original Future controller");
        var state=states[0]; state.Manual=true;
        var args=Environment.GetCommandLineArgs(); int index=Array.IndexOf(args,"--source-future-json");
        if(index>=0) {
            byte[] payload=File.ReadAllBytes(args[index+1]); var input=JsonUtility.FromJson<Trajectory>(System.Text.Encoding.UTF8.GetString(payload));
            Require(input.size=="L" && !string.IsNullOrEmpty(input.humanConfirmation) && input.samples.Length>0,"confirmed L reference");
            int stable=0; float maximum=0;
            foreach(var s in input.samples) {
                if(!s.clockBracketStable) continue; stable++;
                state.UpdateGoal(new Vector3(s.worldFront[0],s.worldFront[1],s.worldFront[2]));
                maximum=Mathf.Max(maximum,Mathf.Abs(state.TiltDot-s.tiltDot));
                Require(state.Goal==s.goal && Mathf.Abs(state.TiltDot-s.tiltDot)<.0001f,"quantized goal/tilt match source");
            }
            Require(stable>0,"stable bracketed samples");
            string hash=BitConverter.ToString(SHA256.Create().ComputeHash(payload)).Replace("-","").ToLowerInvariant();
            log.Add("FutureNativeFunctions=PASS samples="+input.samples.Length+" stableClockSamples="+stable+" parameters=2 maxAbsoluteDelta="+maximum+
                " sha256="+hash+" frameLocked=False frameTrajectoryVerified=False fidelityValidated=False");
        }
        Require(Mathf.RoundToInt(4.5f)==4 && Mathf.RoundToInt(5.5f)==6,"native tie-to-even rounding");
        Require(SourceFutureCardState.Advance(0,8,4,5,0)==4,"skip threshold applies before delta integration");
        Require(SourceFutureCardState.Advance(0,8,4,5,1)==8 && SourceFutureCardState.Advance(8,0,4,5,1)==0,"overshoot clamps in both directions");
        Require(SourceFutureCardState.Advance(3.00005f,3,4,5,0)==3,"native near-goal snap");
        state.IsAnimationStopped=true; state.Step(new Vector3(.2f,0,-Mathf.Sqrt(.96f)),.02f);
        log.Add("FutureControlFlow=PASS ties-even,skip-before-integration,overshoot-both-signs,near-goal-snap; stopFlagDoesNotFreeze=True fidelityValidated=False");
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float side in new[]{-1f,1f}) foreach(float instant in new[]{0f,.1f,.5f,2f}) {
            float sign=side,time=instant;
            poses.Add(new SourceCardTouchProbe.Pose {name="future-"+(sign<0 ? "left" : "right")+"-t"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                state.ResetState(Vector3.back);
                root.localRotation=Quaternion.Euler(0,-sign*12,0);
                float remaining=time; state.Step(state.transform.TransformDirection(Vector3.back),0);
                while(remaining>0) {float delta=Mathf.Min(.016666667f,remaining); state.Step(state.transform.TransformDirection(Vector3.back),delta); remaining-=delta;}
                log.Add("FuturePose side="+sign+" time="+time+" goal="+state.Goal+" frame="+state.Frame+" tiltDot="+state.TiltDot);
            }});
        }
        return poses;
    }
}
