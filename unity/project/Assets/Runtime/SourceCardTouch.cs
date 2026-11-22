using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// Recovered from TouchStateRotation, FlipSettings and Asset3DRenderer.CreateNode.
// Native sessions bind the source UICardView rectangle and root Canvas camera.
// Legacy diagnostic hosts retain their old bounds; gyro remains unported.
public sealed class SourceCardTouch : MonoBehaviour
{
    public sealed class Motion
    {
        public Quaternion Rotation = Quaternion.identity;
        public bool Dragging { get; private set; }
        public Vector2 Movement { get; private set; }
        Vector2 previous;
        Quaternion? pending;
        float started;
        public void Begin(Vector2 localPoint, float realtime)
        { previous=localPoint; Movement=Vector2.zero; started=realtime; Dragging=true; }
        public void Drag(Vector2 localPoint, Vector2 rectSize)
        {
            if (!Dragging) return;
            float yaw = (Mathf.Acos(Mathf.Clamp(localPoint.x/(rectSize.x*.5f),-1,1))
                -Mathf.Acos(Mathf.Clamp(previous.x/(rectSize.x*.5f),-1,1)))*Mathf.Rad2Deg;
            float pitch = (Mathf.Acos(Mathf.Clamp(localPoint.y/(rectSize.y*.5f),-1,1))
                -Mathf.Acos(Mathf.Clamp(previous.y/(rectSize.y*.5f),-1,1)))*Mathf.Rad2Deg;
            pending=Quaternion.AngleAxis(yaw,Vector3.up)*Quaternion.AngleAxis(pitch,Vector3.left);
            Movement+=localPoint-previous; previous=localPoint;
        }
        public void Tick(float deltaTime)
        {
            if (!Dragging) Rotation=Quaternion.Slerp(Rotation,Quaternion.identity,deltaTime*10);
            else if (pending.HasValue) {
                Vector3 euler=(Rotation*pending.Value).eulerAngles;
                var target=Quaternion.Euler(euler.x,euler.y,0);
                float distance=Quaternion.Angle(Quaternion.identity,target);
                Rotation=distance>0 ? Quaternion.SlerpUnclamped(Quaternion.identity,target,Mathf.Min(30/distance,1)) : target;
                pending=null;
            }
        }
        public bool End(float realtime)
        {
            if (!Dragging) return false;
            Dragging=false;
            float angle=Vector2.Angle(Movement,Vector2.right);
            if (angle>90) angle=180-angle;
            // The caller halves the angle, then IsFlip halves FlipAngle again.
            // Both factors are present in ARM64; effective folded angle <=60.
            return realtime-started<=.2f && angle*.5f<=60*.5f && Movement.sqrMagnitude>=90*90;
        }
        public void Reset() { Rotation=Quaternion.identity; Dragging=false; pending=null; }
    }
    public readonly Motion State = new Motion();
    public Transform Root { get; private set; }
    public Transform AnimatedRotation { get; private set; }
    public Transform Parent { get; private set; }
    public bool Flipped { get; private set; }
    public bool InputEnabled;
    // An immersive player consumes a stationary hold before ordinary rotation.
    // Returning true captures this frame; a moved pointer remains a normal drag.
    public Func<Vector2,bool,bool,bool,bool> ImmersiveInput;
    bool immersiveCaptured;
    RectTransform inputView;
    Camera inputCamera;
    public bool HasSourceView => inputView;
    public void BindSourceView(RectTransform view,Camera camera)
    {
        if(!view || !camera) throw new ArgumentException("Source touch view and Canvas camera required");
        inputView=view; inputCamera=camera;
    }
    public bool SampleSourceView(Vector2 screenPoint,out Vector2 point,out Vector2 size)
    {
        if(!inputView) throw new InvalidOperationException("Source touch view not bound");
        size=inputView.rect.size;
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(inputView,screenPoint,inputCamera,out point);
    }
    public bool ContainsSourceView(Vector2 screenPoint)
    {
        if(!inputView) throw new InvalidOperationException("Source touch view not bound");
        return RectTransformUtility.RectangleContainsScreenPoint(inputView,screenPoint,inputCamera);
    }
    AssetBundle bundle;
    PlayableGraph graph;
    AnimationMixerPlayable mixer;
    AnimationClipPlayable[] playables;
    AnimationClip[] clips;
    public string CurrentClip { get; private set; }

    public static SourceCardTouch Attach(Transform face, string projectRoot, List<string> report, bool input)
    {
        var root=new GameObject("Root").transform;
        var component=root.gameObject.AddComponent<SourceCardTouch>();
        component.Root=root;
        component.AnimatedRotation=new GameObject("Rotation").transform;
        component.AnimatedRotation.SetParent(root,false);
        component.Parent=new GameObject("Parent").transform;
        component.Parent.SetParent(component.AnimatedRotation,false);
        component.Parent.localEulerAngles=new Vector3(0,180,0);
        var asset=new GameObject("CardBehaviour").transform;
        asset.SetParent(component.Parent,false);
        face.SetParent(asset,false);
        // LoadFace explicitly replaces the prefab root rotation with Euler(0,180,0).
        face.localRotation=Quaternion.Euler(0,180,0);
        component.bundle=AssetBundle.LoadFromFile(Path.Combine(projectRoot,"runtime-assets/bundles/source-flip-clips.bundle"));
        if (!component.bundle) throw new InvalidOperationException("Original flip clips bundle missing");
        var loaded=component.bundle.LoadAllAssets<AnimationClip>();
        component.clips=new[] {"CardFlip_Idle","CardFlip_LeftReverb","CardFlip_RightReverb"}
            .Select(name=>loaded.Single(c=>c.name==name)).ToArray();
        if (component.clips[1].length!=.75f || component.clips[2].length!=.75f)
            throw new InvalidOperationException("Original flip animation duration mismatch");
        var animator=root.gameObject.AddComponent<Animator>();
        animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        component.graph=PlayableGraph.Create("OriginalCardFlip");
        component.mixer=AnimationMixerPlayable.Create(component.graph,3);
        component.playables=new AnimationClipPlayable[3];
        for (int i=0;i<3;i++) {
            component.playables[i]=AnimationClipPlayable.Create(component.graph,component.clips[i]);
            component.graph.Connect(component.playables[i],0,component.mixer,i);
        }
        var output=AnimationPlayableOutput.Create(component.graph,"OriginalCardFlip",animator);
        output.SetSourcePlayable(component.mixer);
        component.graph.Play(); component.Play(0);
        component.InputEnabled=input;
        report.Add("OriginalTouch nodes=Root/Rotation/Parent/CardBehaviour/Face maxTilt=30 clips=Idle,Left,Right payloadsUnchanged flipDuration=0.75 gyroUnported hostViewBoundsUnmatched");
        return component;
    }
    void Play(int index)
    {
        for (int i=0;i<3;i++) componentWeight(i,i==index ? 1 : 0);
        playables[index].SetTime(0); playables[index].SetDone(false);
        CurrentClip=clips[index].name;
        graph.Evaluate(0);
    }
    void componentWeight(int index, float weight) { mixer.SetInputWeight(index,weight); }
    public void SetFlipped(bool flipped)
    {
        if (Flipped==flipped) return;
        Parent.localEulerAngles=new Vector3(0,flipped ? 0 : 180,0);
        Flipped=flipped;
    }
    public bool Release(float realtime)
    {
        if (!State.End(realtime)) return false;
        Play(0);
        // Native <=0 branch selects FlipSettings.Right (static offset 8).
        Play(State.Movement.x<=0 ? 2 : 1);
        SetFlipped(!Flipped);
        return true;
    }
    public void Tick(float deltaTime) { State.Tick(deltaTime); Root.localRotation=State.Rotation; }
    public void PoseClip(int index, double time)
    {
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        Play(index); playables[index].SetTime(time); graph.Evaluate(0);
    }
    public void ResetFront()
    { State.Reset(); Root.localRotation=Quaternion.identity; SetFlipped(false); Play(0); }
    void Update()
    {
        if (!InputEnabled) return;
        if(inputView) {
            var screenPoint=new Vector2(Input.mousePosition.x,Input.mousePosition.y);
            if(ImmersiveInput!=null && ImmersiveInput(screenPoint,Input.GetMouseButtonDown(0),Input.GetMouseButton(0),Input.GetMouseButtonUp(0))) {
                immersiveCaptured=true;State.Reset(); Tick(Time.deltaTime); return;
            }
            bool projected=SampleSourceView(screenPoint,out var localPoint,out var rectSize);
            if((Input.GetMouseButtonDown(0) || (immersiveCaptured && Input.GetMouseButton(0))) && projected && ContainsSourceView(screenPoint))
                State.Begin(localPoint,Time.realtimeSinceStartup);
            immersiveCaptured=false;
            // A captured drag continues outside the rectangle, as PointerEventData does.
            if(Input.GetMouseButton(0) && projected) State.Drag(localPoint,rectSize);
            if(Input.GetMouseButtonUp(0)) Release(Time.realtimeSinceStartup);
            Tick(Time.deltaTime);
            if(Input.GetKeyDown(KeyCode.R)) ResetFront();
            return;
        }
        float side=Mathf.Min(Screen.width,Screen.height);
        float referenceScale=1024f/Screen.height;
        var point=(new Vector2(Input.mousePosition.x,Input.mousePosition.y)-new Vector2(Screen.width,Screen.height)*.5f)*referenceScale;
        var size=Vector2.one*side*referenceScale;
        if (Input.GetMouseButtonDown(0) && Mathf.Abs(point.x)<=size.x*.5f && Mathf.Abs(point.y)<=size.y*.5f)
            State.Begin(point,Time.realtimeSinceStartup);
        if (Input.GetMouseButton(0)) State.Drag(point,size);
        if (Input.GetMouseButtonUp(0)) Release(Time.realtimeSinceStartup);
        Tick(Time.deltaTime);
        if (Input.GetKeyDown(KeyCode.R)) ResetFront();
    }
    void OnDestroy() { if (graph.IsValid()) graph.Destroy(); if (bundle) bundle.Unload(false); }
}
