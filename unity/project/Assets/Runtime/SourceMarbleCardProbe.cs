using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

// Controller rule checks and original MRT output, not complete frame parity.
public static class SourceMarbleCardProbe
{
    [Serializable] sealed class Trajectory {public string size,humanConfirmation;public Sample[] samples;}
    [Serializable] sealed class Point {public float[] goal;}
    [Serializable] sealed class Sample {public float tiltPower,tiltRotation;public float[] worldFront,goalTilt;public Point[] points;public bool goalsBracketStable;}
    static void Require(bool value,string message) {
        if(!value) throw new InvalidOperationException("Marble source probe: "+message);
    }
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceMarbleCardState>();
        Require(states.Length==1,"one original Marble controller"); var state=states[0]; state.Manual=true;
        var args=Environment.GetCommandLineArgs(); int index=Array.IndexOf(args,"--source-marble-json");
        if(index>=0) VerifySamples(state,args[index+1],log);
        Require(state.RemapTexture.format==TextureFormat.R16 && state.RemapTexture.mipmapCount==1 &&
            state.RemapTexture.wrapMode==TextureWrapMode.Clamp,"original R16 linear clamp texture without mipmaps");
        state.ResetState(); var original=state.Attributes;
        var front=new Vector3(.2f,0,-Mathf.Sqrt(.96f));
        state.Step(front,.02f,0);
        Require(state.GoalTilt==Vector3.zero && state.Attributes[0]==original[0],"delay retains initial points");
        state.Step(front,.02f,1);
        Require(state.GoalTilt.x==front.x && state.CurrentTilt.x>0 && state.Attributes[0]!=original[0],"expired history sets goals and starts inertia");
        state.Step(Vector3.back,.02f,1.02f);
        Require(state.GoalTilt.x==front.x,"return respects original delay");
        state.Step(Vector3.back,.02f,2);
        Require(state.GoalTilt.x==0,"delayed return reaches new goal");
        log.Add("MarbleControlFlow=PASS delayed-goals,point-inertia,delayed-return; texture=R16/no-mips/clamp; texturePixelsCompared=False fidelityValidated=False");
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float side in new[]{-1f,1f}) foreach(float instant in new[]{0f,.1f,.5f,2f}) {
            float sign=side,time=instant;
            poses.Add(new SourceCardTouchProbe.Pose {name="marble-"+(sign<0 ? "left" : "right")+"-t"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                root.localRotation=Quaternion.identity; state.ResetState();
                root.localRotation=Quaternion.Euler(0,-sign*12,0);
                state.Step(state.transform.TransformDirection(Vector3.back),0,0);
                float remaining=time,clock=0;
                while(remaining>0) {float delta=Mathf.Min(.02f,remaining); clock+=delta;
                    state.Step(state.transform.TransformDirection(Vector3.back),delta,clock); remaining-=delta;}
                log.Add("MarblePose side="+sign+" time="+time+" tilt="+state.Tilt+" front="+state.CurrentTilt+
                    " attributes="+string.Join(",",state.Attributes));
            }});
        }
        return poses;
    }
    static void VerifySamples(SourceMarbleCardState state,string path,List<string> log) {
        byte[] payload=File.ReadAllBytes(path);
        var input=JsonUtility.FromJson<Trajectory>(System.Text.Encoding.UTF8.GetString(payload));
        Require(input.size=="L" && !string.IsNullOrEmpty(input.humanConfirmation) && input.samples.Length>0,"confirmed nonempty L reference");
        int failed=0,stable=0; float maximum=0;
        foreach(var s in input.samples) {
            var actual=state.EvaluateRecorded(new Vector3(s.worldFront[0],s.worldFront[1],s.worldFront[2]),new Vector3(s.goalTilt[0],s.goalTilt[1],s.goalTilt[2]));
            var expected=new List<float>{s.tiltPower,s.tiltRotation};
            foreach(var p in s.points) expected.AddRange(p.goal);
            Require(expected.Count==actual.Length,"original Marble point count");
            if(s.goalsBracketStable) stable++;
            for(int i=0;i<(s.goalsBracketStable ? actual.Length : 2);i++) {
                float difference=Mathf.Abs(actual[i]-expected[i]); maximum=Mathf.Max(maximum,difference);
                if(difference>.0001f) {failed++; log.Add("MarbleNativeFunctionMismatch parameter="+i+" actual="+actual[i]+" expected="+expected[i]+" delta="+difference);}
            }
        }
        string hash=BitConverter.ToString(SHA256.Create().ComputeHash(payload)).Replace("-","").ToLowerInvariant();
        Require(stable>0,"stable bracketed point goals required");
        log.Add("MarbleNativeFunctions="+(failed==0 ? "PASS" : "FAIL")+" samples="+input.samples.Length+" stableGoalSamples="+stable+
            " parameters="+(2+state.Attributes.Length*3)+" maxAbsoluteDelta="+maximum+" sha256="+hash+
            " frameLocked=False inertiaTrajectoryVerified=False texturePixelsCompared=False fidelityValidated=False");
        Require(failed==0,"native function mismatches="+failed);
    }
}
