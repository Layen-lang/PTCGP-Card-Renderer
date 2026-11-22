using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

// Diagnostic pose imported from bounded observations bracketing a live game
// screenshot. No pixel fitting or change to the interactive input behavior.
public static class SourceCardMatchedPose
{
    [Serializable] class Node { public float[] position,rotation,scale; }
    [Serializable] class Nodes { public Node root,rotation,parent; }
    [Serializable] class Point {public float[] current,goal,speed;}
    [Serializable] class Controller {
        public string @class,sourceId;public float tilt,frame,tiltDot,animation,kiraScale,moveAngle,defaultCircularAngle,sourceTime;
        public bool playing,controllerBracketStable;public float[] currentTilt,goalTilt,worldFront,remapped,shake,sandVolumes,parameters;
        public float[] primAngles,primBaseScales,primBaseIntensities,primMinIntensities,primMaxIntensities;
        public float[] primFlickerScaling,primFlickerAnimOffsets,primTypes,primMorphing,vertexUVs;
        public Point[] points;
    }
    [Serializable] class Observation { public Nodes nodes;public Controller[] controllers; }
    [Serializable] class Capture { public bool heldTransformsStable,transformsStable; public Observation heldBeforeScreenshot,heldAfterScreenshot,before,after;public string cardId,size,humanConfirmation; }
    [Serializable] class EffectPlan {public EffectEntry[] effects;}
    [Serializable] class EffectEntry {public string kind;}
    static Vector3 V(float[] values) {
        if(values==null || values.Length!=3 || values.Any(v=>float.IsNaN(v)||float.IsInfinity(v))) throw new InvalidDataException("Source pose vector invalid");
        return new Vector3(values[0],values[1],values[2]);
    }
    static Quaternion Q(float[] values) {
        if(values==null || values.Length!=4 || values.Any(v=>float.IsNaN(v)||float.IsInfinity(v))) throw new InvalidDataException("Source pose quaternion invalid");
        if(Mathf.Abs(values.Sum(v=>v*v)-1)>.00001f) throw new InvalidDataException("Source pose quaternion not unit length");
        return new Quaternion(values[0],values[1],values[2],values[3]);
    }
    static void Apply(Transform target,Node node) {target.localPosition=V(node.position);target.localRotation=Q(node.rotation);target.localScale=V(node.scale);}
    public static SourceCardTouchProbe.Pose Read(Transform root,List<string> report)
    {
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--source-pose-json");
        if(index<0) return null;
        if(index+1>=args.Length) throw new InvalidDataException("Source pose path missing");
        var path=Path.GetFullPath(args[index+1]);var bytes=File.ReadAllBytes(path);
        var source=JsonUtility.FromJson<Capture>(System.Text.Encoding.UTF8.GetString(bytes));
        bool confirmed=source.before!=null && source.before.nodes!=null && source.before.nodes.root!=null && source.before.nodes.root.rotation!=null;
        if(confirmed) {
            if(source.size!="L" || string.IsNullOrEmpty(source.humanConfirmation) || CardSelection.Config.cardId!=source.cardId)
                throw new InvalidDataException("Confirmed original L card identity must match loaded config");
            source.heldBeforeScreenshot=source.before;source.heldAfterScreenshot=source.after;
            source.heldTransformsStable=source.transformsStable;
            bool poseOnly=source.before.controllers!=null && source.before.controllers.Length==0 &&
                source.after.controllers!=null && source.after.controllers.Length==0;
            if(poseOnly) {
                var plan=JsonUtility.FromJson<EffectPlan>(CardSelection.Text("effects.json.gz"));
                if(plan.effects==null || plan.effects.Length!=0)
                    throw new InvalidDataException("A controller-free pose requires an empty original L effect plan");
                report.Add("MatchedPoseOnly originalEffectPlanEmpty=True shaderStateObserved=False shaderTimeFrameLocked=False fidelityValidated=False");
            }
            bool kiraGroup=source.before.controllers!=null && source.before.controllers.Length>0 &&
                source.before.controllers.All(c=>c.@class.EndsWith(".KiraPuyoObject") || c.@class.EndsWith(".CircularKiraObject"));
            if(source.before.controllers==null || (!poseOnly && !kiraGroup && (source.before.controllers.Length!=1 ||
                !(source.before.controllers[0].@class.EndsWith(".CardMarbleLayer") ||
                  source.before.controllers[0].@class.EndsWith(".CardAncientObject") ||
                  source.before.controllers[0].@class.EndsWith(".CardMSSRObject") ||
                  source.before.controllers[0].@class.EndsWith(".CardURObject") ||
                  source.before.controllers[0].@class.EndsWith(".CardFutureObject")))) ||
                (!kiraGroup && JsonUtility.ToJson(source.before)!=JsonUtility.ToJson(source.after)))
                throw new InvalidDataException("Original controller presentation parameters must be stable around the screenshot");
            if(kiraGroup) {
                if(source.after.controllers==null || source.before.controllers.Length!=source.after.controllers.Length)
                    throw new InvalidDataException("Original Kira controller bracket width differs");
                for(int i=0;i<source.before.controllers.Length;i++) {
                    var first=source.before.controllers[i];var last=source.after.controllers[i];
                    if(first.@class!=last.@class || first.sourceId!=last.sourceId)
                        throw new InvalidDataException("Original Kira controller bracket identity differs");
                    if(first.@class.EndsWith(".KiraPuyoObject")) {
                        if(JsonUtility.ToJson(first)!=JsonUtility.ToJson(last)) throw new InvalidDataException("Original KiraPuyo parameters moved during screenshot");
                    } else {
                        if(first.tilt!=last.tilt || first.moveAngle!=last.moveAngle || first.defaultCircularAngle!=last.defaultCircularAngle ||
                            first.vertexUVs==null || first.vertexUVs.Any(v=>float.IsNaN(v)||float.IsInfinity(v)))
                            throw new InvalidDataException("CircularKira diagnostic requires stable orientation and finite sampled UVs");
                        report.Add("CircularKiraSnapshot sourcePhaseBracketStable="+first.controllerBracketStable+
                            " sourceTimeBefore="+first.sourceTime+" sourceTimeAfter="+last.sourceTime+
                            " sampledBeforeScreenshot=True shaderTimeFrameLocked=False uvFrameLocked=False fidelityValidated=False");
                    }
                }
            }
            var c=poseOnly ? null : source.before.controllers[0];
            if(c!=null && c.@class.EndsWith(".CardURObject")) {
                if(!c.controllerBracketStable) throw new InvalidDataException("Original UR orientation packet changed during read");
                report.Add("UrSourceOrientationBracketStable=True shaderTimeFrameLocked=False glitterPhaseFrameLocked=False fidelityValidated=False");
            }
            if(c!=null && c.@class.EndsWith(".CardMSSRObject") && (c.playing || c.parameters==null || c.parameters.Length!=4))
                throw new InvalidDataException("MSSR diagnostic snapshot requires stable nonplaying source parameters");
            if(c!=null && c.@class.EndsWith(".CardAncientObject") && (c.sandVolumes==null || c.sandVolumes.Length!=4 || c.sandVolumes.Any(v=>v!=0)))
                throw new InvalidDataException("Ancient diagnostic snapshot requires zero sand volumes; particle replay is not implemented");
        }
        if(!source.heldTransformsStable || JsonUtility.ToJson(source.heldBeforeScreenshot.nodes)!=JsonUtility.ToJson(source.heldAfterScreenshot.nodes)) throw new InvalidDataException("Original transforms not stable around source screenshot");
        var touch=root.GetComponent<SourceCardTouch>();
        if(!touch || root!=touch.Root) throw new InvalidOperationException("Source pose root does not match original hierarchy");
        var nodes=source.heldBeforeScreenshot.nodes;
        var rotation=Q(nodes.root.rotation);
        if(!confirmed && Quaternion.Angle(rotation,Quaternion.identity)<1) throw new InvalidDataException("Matched inclination must not be another face capture");
        using(var hash=SHA256.Create()) report.Add("MatchedSourcePose path="+path+" sha256="+BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","")+" root="+rotation.ToString("R")+" screenshotBracketStable=True gestureParityUnvalidated=True");
        return new SourceCardTouchProbe.Pose {name="matched-source",apply=()=>{
            Apply(touch.Root,nodes.root);Apply(touch.AnimatedRotation,nodes.rotation);Apply(touch.Parent,nodes.parent);
            if(confirmed) {
                if(source.before.controllers.Length==0) return;
                if(source.before.controllers.All(value=>value.@class.EndsWith(".KiraPuyoObject") || value.@class.EndsWith(".CircularKiraObject"))) {
                    var states=root.GetComponentsInChildren<SourceKiraPuyoState>().ToDictionary(s=>s.SourceId);
                    var circular=root.GetComponentsInChildren<SourceCircularKiraState>().ToDictionary(s=>s.SourceId);
                    if(states.Count!=source.before.controllers.Count(c=>c.@class.EndsWith(".KiraPuyoObject")) ||
                        (source.before.controllers.Any(c=>c.@class.EndsWith(".CircularKiraObject")) && circular.Count!=source.before.controllers.Count(c=>c.@class.EndsWith(".CircularKiraObject"))))
                        throw new InvalidDataException("Matched Kira controller count differs");
                    foreach(var original in source.before.controllers.Where(c=>c.@class.EndsWith(".KiraPuyoObject"))) {
                        if(!states.TryGetValue(original.sourceId,out var state)) throw new InvalidDataException("Matched KiraPuyo source identity absent");
                        state.ApplyRecordedSnapshot(original.animation,original.kiraScale);
                    }
                    foreach(var original in source.before.controllers.Where(c=>c.@class.EndsWith(".CircularKiraObject"))) {
                        if(!circular.TryGetValue(original.sourceId,out var state)) throw new InvalidDataException("Matched CircularKira source identity absent");
                        state.ApplyRecordedSnapshot(original.tilt,original.moveAngle,original.defaultCircularAngle,original.sourceTime,
                            new[]{original.primAngles,original.primBaseScales,original.primBaseIntensities,original.primMinIntensities,
                                original.primMaxIntensities,original.primFlickerScaling,original.primFlickerAnimOffsets,original.primTypes,original.primMorphing},original.vertexUVs);
                    }
                    report.Add("MatchedKiraPuyoState originalSampledParameters=True tiltFormulaVerified=False motionReplay=False fidelityValidated=False");
                    return;
                }
                var c=source.before.controllers[0];
                if(c.@class.EndsWith(".CardMarbleLayer")) {
                    var state=root.GetComponentInChildren<SourceMarbleCardState>();
                    if(!state) throw new InvalidDataException("Matched source Marble controller absent");
                    state.ApplyRecordedSnapshot(c.tilt,V(c.currentTilt),V(c.worldFront),c.points.Select(p=>V(p.current)).ToArray());
                    report.Add("MatchedMarbleState originalSampledPoints=True sourceCurveTexture=True texturePixelsCompared=False inertiaReplay=False fidelityValidated=False");
                } else if(c.@class.EndsWith(".CardMSSRObject")) {
                    var state=root.GetComponentInChildren<SourceMssrCardState>();
                    if(!state) throw new InvalidDataException("Matched source MSSR controller absent");
                    state.ApplyRecordedSnapshot(c.parameters);
                    report.Add("MatchedMSSRState originalSampledParameters=True curveHistoryReplay=False fidelityValidated=False");
                } else if(c.@class.EndsWith(".CardFutureObject")) {
                    var state=root.GetComponentInChildren<SourceFutureCardState>();
                    if(!state) throw new InvalidDataException("Matched source Future controller absent");
                    state.ApplyRecordedSnapshot(c.frame,c.tiltDot);
                    report.Add("MatchedFutureState originalSampledFrame=True frameTrajectoryReplay=False fidelityValidated=False");
                } else if(c.@class.EndsWith(".CardURObject")) {
                    var state=root.GetComponentInChildren<SourceUrCardState>();
                    if(!state) throw new InvalidDataException("Matched source UR controller absent");
                    state.Refresh();
                    report.Add("MatchedURPose originalOrientationBracket=True sampledShaderTime=False glitterReplay=False fidelityValidated=False");
                } else {
                    var state=root.GetComponentInChildren<SourceAncientCardState>();
                    if(!state || c.shake==null || c.shake.Length!=2) throw new InvalidDataException("Matched source Ancient controller/shake absent");
                    state.ApplyRecordedSnapshot(c.remapped,new Vector2(c.shake[0],c.shake[1]));
                    report.Add("MatchedAncientState originalSampledStrata=True zeroSourceSandVolumes=True particleReplay=False strataTrajectoryReplay=False fidelityValidated=False");
                }
            }
        }};
    }
}
