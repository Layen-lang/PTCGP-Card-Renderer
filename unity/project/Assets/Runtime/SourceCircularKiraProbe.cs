using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

public static class SourceCircularKiraProbe
{
    [Serializable] class Capture {public string cardId,size,humanConfirmation,controllerKind;public Observation before,after;public Controller[] samples;}
    [Serializable] class Observation {public Controller[] controllers;}
    [Serializable] class Controller {
        public string @class,sourceId;public float[] worldFront,primAngles,primMorphing,primFlickerAnimOffsets;
        public float tilt,moveAngle,defaultCircularAngle,sourceDelta,morphingAnimOffset;
        public int speedState;public Primitive[] primitives;public bool inputBracketStable,clockBracketStable;
    }
    [Serializable] class Primitive {public float speed,angle,goal,start,breakingTime,initialSpeed;public bool bracketStable;}
    static void Require(bool value,string message) {if(!value) throw new InvalidOperationException("CircularKira probe: "+message);}
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> log) {
        var states=root.GetComponentsInChildren<SourceCircularKiraState>();Require(states.Length==1,"one original L controller required");
        var state=states[0];state.Manual=true;
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--source-circular-kira-json");
        if(index>=0) {
            byte[] bytes=File.ReadAllBytes(args[index+1]);var capture=JsonUtility.FromJson<Capture>(System.Text.Encoding.UTF8.GetString(bytes));
            Require(capture.size=="L" && !string.IsNullOrEmpty(capture.humanConfirmation),"confirmed L reference required");
            var samples=new List<Controller>();
            if(capture.controllerKind=="CircularKiraObject") samples.AddRange(capture.samples);
            else {
                var a=capture.before.controllers.Single(c=>c.@class.EndsWith(".CircularKiraObject"));
                var b=capture.after.controllers.Single(c=>c.@class.EndsWith(".CircularKiraObject"));
                Require(a.sourceId==state.SourceId && b.sourceId==state.SourceId,"original component identity");
                Require(a.worldFront.SequenceEqual(b.worldFront) && a.tilt==b.tilt && a.moveAngle==b.moveAngle,"stable rest inputs");
                a.inputBracketStable=b.inputBracketStable=true;samples.Add(a);samples.Add(b);
            }
            int inputs=0,excluded=0,brakes=0,morphs=0;float maximumTilt=0,maximumAngle=0,maximumBrake=0,maximumMorph=0;
            foreach(var sample in samples) {
                Require(sample.sourceId==state.SourceId,"matching original controller identity");
                if(sample.inputBracketStable) {
                    var front=new Vector3(sample.worldFront[0],sample.worldFront[1],sample.worldFront[2]);
                    float tilt=Mathf.Abs(state.EvaluateTilt(front)-sample.tilt),angle=Mathf.Abs(state.EvaluateMoveAngle(front)-sample.moveAngle);
                    maximumTilt=Mathf.Max(maximumTilt,tilt);maximumAngle=Mathf.Max(maximumAngle,angle);inputs++;
                    Require(tilt<.000002f && angle<.0001f,"original tilt/move functions differ");
                } else excluded++;
                if(!sample.clockBracketStable || sample.primitives==null) continue;
                for(int i=0;i<sample.primitives.Length;i++) {
                    var p=sample.primitives[i];if(!p.bracketStable) continue;
                    // Speed is evaluated before angle and braking-time integration.
                    float previousAngle=p.angle-p.speed*sample.sourceDelta;
                    if(sample.speedState==2 && p.speed>0 && previousAngle>=p.start && previousAngle<p.goal && p.breakingTime>=sample.sourceDelta) {
                        float difference=Mathf.Abs(state.EvaluateBrakeSpeed(p.breakingTime-sample.sourceDelta,p.initialSpeed)-p.speed);
                        maximumBrake=Mathf.Max(maximumBrake,difference);brakes++;Require(difference<.00002f,"native brake speed differs");
                    }
                    if(sample.primMorphing!=null) {
                        float difference=Mathf.Abs(state.EvaluateMorph(i,sample.morphingAnimOffset,p.speed)-sample.primMorphing[i]);
                        maximumMorph=Mathf.Max(maximumMorph,difference);morphs++;Require(difference<.00002f,"native morph function differs");
                    }
                }
            }
            Require(inputs>0,"stable original input samples required");
            string hash=BitConverter.ToString(SHA256.Create().ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            log.Add("CircularKiraNativeFunctions=PASS card="+capture.cardId+" inputSamples="+inputs+" excludedInputs="+excluded+
                " maxTiltDelta="+maximumTilt+" maxMoveAngleDelta="+maximumAngle+" brakeSamples="+brakes+" maxBrakeDelta="+maximumBrake+
                " morphSamples="+morphs+" maxMorphDelta="+maximumMorph+" sha256="+hash+
                " randomStateKnown=False completeHistoryVerified=False uvTrajectoryVerified=False fidelityValidated=False");
        }
        Require(Mathf.RoundToInt(4.5f)==4 && Mathf.RoundToInt(5.5f)==6,"original tie-to-even rounding");
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float angle in new[]{0f,-12f,12f}) {
            float y=angle;poses.Add(new SourceCardTouchProbe.Pose {name="circular-kira-y"+y.ToString("F0"),apply=()=>{
                root.localRotation=Quaternion.Euler(0,y,0);state.Step(state.transform.TransformDirection(Vector3.back),0,0);
                log.Add("CircularKiraPose y="+y+" tilt="+state.Tilt+" moveAngle="+state.MoveAngle+" state="+state.SpeedState+" historyReplay=False fidelityValidated=False");
            }});
        }
        return poses;
    }
}
