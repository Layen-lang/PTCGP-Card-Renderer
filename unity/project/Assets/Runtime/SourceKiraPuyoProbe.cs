using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

public static class SourceKiraPuyoProbe
{
    [Serializable] class Capture {public string cardId,size,humanConfirmation,controllerKind;public Observation before,after;public MotionSample[] samples;}
    [Serializable] class MotionSample {public MotionController[] kiraStates;}
    [Serializable] class MotionController {public string sourceId;public float animation,kiraScale;public bool bracketStable;}
    [Serializable] class Observation {public Controller[] controllers;}
    [Serializable] class Controller {public string @class,sourceId;public float animation,kiraScale;}
    static void Require(bool value,string message) {if(!value) throw new InvalidOperationException("KiraPuyo probe: "+message);}
    public static List<SourceCardTouchProbe.Pose> Run(Transform root,List<string> report) {
        var states=root.GetComponentsInChildren<SourceKiraPuyoState>();
        Require(states.Length>0,"original L KiraPuyo controllers required");
        foreach(var state in states) state.Manual=true;
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--source-kira-puyo-json");
        if(index>=0) VerifySamples(states,args[index+1],report);
        var poses=new List<SourceCardTouchProbe.Pose>();
        foreach(float angle in new[]{0f,-12f,12f}) {
            float tilt=angle;
            poses.Add(new SourceCardTouchProbe.Pose {name="kira-puyo-y"+tilt.ToString("F0",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                root.localRotation=Quaternion.Euler(0,tilt,0);
                foreach(var state in states) state.Step(state.transform.TransformDirection(Vector3.back));
                report.Add("KiraPuyoPose y="+tilt+" controllers="+states.Length+" nativeMotionReplay=False fidelityValidated=False");
            }});
        }
        return poses;
    }
    static void VerifySamples(SourceKiraPuyoState[] states,string path,List<string> report) {
        var bytes=File.ReadAllBytes(path);
        var source=JsonUtility.FromJson<Capture>(System.Text.Encoding.UTF8.GetString(bytes));
        Require(source.size=="L" && !string.IsNullOrEmpty(source.humanConfirmation),"confirmed L reference required");
        var byId=states.ToDictionary(s=>s.SourceId);int compared=0,failed=0,excluded=0;float maximum=0;
        var checkedIds=new HashSet<string>();
        var observations=new List<MotionController>();
        if(source.controllerKind!="KiraPuyoObject") {
            Require(source.before!=null && source.before.controllers!=null && source.after!=null,"original capture schema required");
            Require(JsonUtility.ToJson(source.before)==JsonUtility.ToJson(source.after),"stable native presentation fields");
            foreach(var c in source.before.controllers) if(c.@class.EndsWith(".KiraPuyoObject"))
                observations.Add(new MotionController {sourceId=c.sourceId,animation=c.animation,kiraScale=c.kiraScale,bracketStable=true});
        } else {
            Require(source.controllerKind=="KiraPuyoObject" && source.samples!=null && source.samples.Length>0,"original KiraPuyo motion trace required");
            foreach(var sample in source.samples) {
                Require(sample.kiraStates!=null && sample.kiraStates.Length==states.Length,"complete original controller sample width");
                observations.AddRange(sample.kiraStates);
            }
        }
        foreach(var sample in observations) {
            if(!sample.bracketStable) {excluded++;continue;}
            Require(byId.TryGetValue(sample.sourceId,out var state),"matching original controller identity");
            float actual=state.EvaluateRecorded(sample.animation),difference=Mathf.Abs(actual-sample.kiraScale);
            maximum=Mathf.Max(maximum,difference);compared++;checkedIds.Add(sample.sourceId);
            if(difference>.000001f) {failed++;report.Add("KiraPuyoMismatch sourceId="+sample.sourceId+" animation="+sample.animation+
                " expected="+sample.kiraScale+" actual="+actual+" difference="+difference);}
        }
        string hash=BitConverter.ToString(SHA256.Create().ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        report.Add("KiraPuyoNativeCurves="+(failed==0 ? "PASS" : "FAIL")+" card="+source.cardId+" controllers="+checkedIds.Count+
            " comparedValues="+compared+" excluded="+excluded+" maxAbsoluteDelta="+maximum+" sha256="+hash+" input="+path+
            " tiltFormulaVerified=False motionVerified=False fidelityValidated=False");
        Require(checkedIds.Count==states.Length && failed==0,"native curve mismatches="+failed);
    }
}
