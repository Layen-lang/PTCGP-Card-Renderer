using System;
using System.Collections.Generic;
using UnityEngine;

// Independent expectations: centered half-radius drag => acos(0.5)-acos(0)
// = -30deg; streamed clip knot values are read from original-flip-findings.json.
public static class SourceCardTouchProbe
{
    public sealed class Pose { public string name; public Action apply; }
    static void Near(float actual,float expected,string name,float tolerance=.03f)
    { if (Mathf.Abs(actual-expected)>tolerance) throw new InvalidOperationException(name+" expected="+expected+" actual="+actual); }
    static void Require(bool result,string name)
    { if (!result) throw new InvalidOperationException("Original touch probe: "+name); }
    public static void VerifyView(SourceCardTouch touch,Canvas canvas,List<string> report)
    {
        Require(touch && touch.HasSourceView,"source UICardView must be bound");
        var center=new Vector2(540,960);
        // Source Canvas Expand750x1334, view630x880, test viewport1080x1920.
        float scale=Mathf.Min(1080f/750f,1920f/1334f);
        Require(touch.SampleSourceView(center,out var point,out var size),"center projection");
        Near(point.magnitude,0,"source view centered",.01f);
        Near(size.x,630,"source view width"); Near(size.y,880,"source view height");
        Near(canvas.scaleFactor,scale,"source Expand scale",.00001f);
        Require(touch.ContainsSourceView(center+Vector2.right*(314*scale)),"inside source hit rectangle");
        Require(!touch.ContainsSourceView(center+Vector2.right*(316*scale)),"outside source hit rectangle");
        foreach(var axis in new[] {Vector2.right,Vector2.up}) {
            var endpoint=center+new Vector2(axis.x*157.5f,axis.y*220f)*scale;
            Require(touch.SampleSourceView(endpoint,out point,out size),"quarter-view projection");
            Near(point.x,axis.x*157.5f,"quarter x",.01f);
            Near(point.y,axis.y*220f,"quarter y",.01f);
            var motion=new SourceCardTouch.Motion(); motion.Begin(Vector2.zero,0);motion.Drag(point,size);motion.Tick(0);
            var expected=axis.x>0 ? Quaternion.Euler(0,-30,0) : Quaternion.Euler(30,0,0);
            Near(Quaternion.Angle(motion.Rotation,expected),0,"source projected quarter drag",.05f);
        }
        foreach(float distance in new[] {89f,91f}) {
            Require(touch.SampleSourceView(center+Vector2.right*(distance*scale),out point,out size),"flip projection");
            var motion=new SourceCardTouch.Motion();motion.Begin(Vector2.zero,0);motion.Drag(point,size);
            Require(motion.End(.1f)==(distance>90),"flip threshold uses Canvas local units");
        }
        report.Add("PASS SourceTouchView: Canvas camera projection,630x880 parent rectangle,center/quarter-axis points,hit bounds,30deg axes,90 local-unit flip threshold; active game dialog routing remains unvalidated");
    }
    public static List<Pose> Run(SourceCardTouch touch,List<string> report)
    {
        var motion=new SourceCardTouch.Motion();
        motion.Begin(Vector2.zero,0); motion.Drag(new Vector2(250,0),Vector2.one*1000); motion.Tick(0);
        Near(Quaternion.Angle(motion.Rotation,Quaternion.Euler(0,-30,0)),0,"horizontal direction");
        Require(!motion.End(.3f),"slow release must not flip");
        motion.Tick(.05f);
        Near(Quaternion.Angle(motion.Rotation,Quaternion.identity),15,"idle half-way after 50ms");
        motion.Reset(); motion.Begin(Vector2.zero,0); motion.Drag(new Vector2(0,250),Vector2.one*1000); motion.Tick(0);
        Near(Quaternion.Angle(motion.Rotation,Quaternion.Euler(30,0,0)),0,"vertical direction");
        motion.Reset(); motion.Begin(Vector2.zero,0); motion.Drag(new Vector2(10000,10000),Vector2.one*1000); motion.Tick(0);
        Near(Quaternion.Angle(motion.Rotation,Quaternion.identity),30,"diagonal sphere limit");
        motion.Reset(); motion.Begin(Vector2.zero,0);
        motion.Drag(new Vector2(250,0),Vector2.one*1000); motion.Drag(Vector2.zero,Vector2.one*1000); motion.Tick(0);
        Near(Quaternion.Angle(motion.Rotation,Quaternion.Euler(0,30,0)),0,"latest pending delta overwrites");
        foreach (var point in new[] {new Vector2(89,0),new Vector2(100,200),new Vector2(100,150)}) {
            motion.Reset(); motion.Begin(Vector2.zero,0); motion.Drag(point,Vector2.one*1000);
            bool expected=point.y==150;
            Require(motion.End(.1f)==expected,"flip distance/doubled angle factors "+point);
        }
        touch.ResetFront(); touch.State.Begin(Vector2.zero,0); touch.State.Drag(new Vector2(-100,0),Vector2.one*1000); touch.Tick(0);
        Require(touch.Release(.1f) && touch.Flipped && touch.CurrentClip=="CardFlip_RightReverb","negative drag selects original Right");
        touch.State.Begin(Vector2.zero,1); touch.State.Drag(new Vector2(100,0),Vector2.one*1000); touch.Tick(0);
        Require(touch.Release(1.1f) && !touch.Flipped && touch.CurrentClip=="CardFlip_LeftReverb","positive drag selects original Left");
        float[] times={0,1f/3,7f/12,.75f};
        float[] right={-30,20,-10,0};
        for (int i=0;i<times.Length;i++) foreach (int clip in new[] {1,2}) {
            touch.PoseClip(clip,times[i]);
            float expected=clip==2 ? right[i] : -right[i];
            Near(Mathf.DeltaAngle(0,touch.AnimatedRotation.localEulerAngles.y),expected,"compiled clip="+clip+" time="+times[i]);
        }
        report.Add("PASS OriginalTouch: axes,30deg spherical limit,50ms idle,latest-delta overwrite,flip timing/distance/folded angle,direction,8 original compiled curve knot samples");
        var poses=new List<Pose>();
        poses.Add(new Pose {name="touch-front",apply=()=>touch.ResetFront()});
        poses.Add(new Pose {name="touch-yaw-limit",apply=()=>{
            touch.ResetFront(); touch.State.Begin(Vector2.zero,0); touch.State.Drag(new Vector2(250,0),Vector2.one*1000); touch.Tick(0);
        }});
        poses.Add(new Pose {name="touch-release-50ms",apply=()=>{touch.State.End(.3f); touch.Tick(.05f);}});
        foreach (float time in times) {
            float sample=time;
            poses.Add(new Pose {name="touch-flip-right-"+time.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),apply=()=>{
                touch.ResetFront(); touch.SetFlipped(true); touch.PoseClip(2,sample);
            }});
        }
        touch.ResetFront();
        return poses;
    }
}
