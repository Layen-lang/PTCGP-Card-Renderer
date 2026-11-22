using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// Movie payload: original H.264 (stream copy), original HCA decoded to AAC.
// Controller values use the original streamed animation polynomials, without
// rebuilding their tangents. The original rounded stencil is retained; decoded
// RGBA replaces CRI's YUV planes on the Windows host.
public sealed class SourceImmersivePlayer : MonoBehaviour
{
    [Serializable] public class Key { public float time,a,b,c,d; }
    [Serializable] public class Curve { public string field; public Key[] keys; }
    [Serializable] public class Constant { public string field; public float value; }
    [Serializable] public class Clip { public float duration; public Curve[] curves; public Constant[] constants; }
    [Serializable] public class Particle { public string name,root; public int[] indices; public float start,duration; public uint seed; }
    [Serializable] public class EffectAnimation { public string root; public int[] indices; public float start,duration,length; public Curve[] curves; }
    [Serializable] public class Plan {
        public string caveat;
        public float[] movieRectSize;
        public float[] movieUv;
        public float holdStart,holdDuration,cancelDuration,movieStart;
        public int endFramesRemaining;
        public Clip start,end;
        public Particle[] startParticles,endParticles;
        public EffectAnimation[] effectAnimations;
        public SourceImmersiveMaterials.Entry[] materials;
    }
    [Serializable] class Bundle { public string path; }
    [Serializable] class Manifest { public Bundle[] bundles; }
    public enum Phase { Idle, Holding, Preparation, Starting, Movie, Ending, Canceling, Error }
    public Phase CurrentPhase { get; private set; }
    public string Error { get; private set; }
    public bool Paused { get; private set; }
    public bool Ready => video && video.isPrepared;
    public bool Busy => CurrentPhase!=Phase.Idle && CurrentPhase!=Phase.Error;
    public float CardScale { get; private set; }=1;
    public long VideoFrame => video ? video.frame : -1;
    public double VideoTime => video ? video.time : 0;
    public int CompletedPlays { get; private set; }
    public int CanceledHolds { get; private set; }
    public int ParticleSystems => uiRoot.GetComponentsInChildren<ParticleSystem>(true).Length+modelRoot.GetComponentsInChildren<ParticleSystem>(true).Length;
    public readonly List<string> Events=new();
    readonly List<AssetBundle> bundles=new();
    SourceCardTouch touch;
    SourceImmersiveCardState state;
    SourceCardPresentationStack stack;
    Plan plan;
    VideoPlayer video;
    RenderTexture movieTarget;
    RawImage movieImage;
    RectTransform mask,movieRect;
    GameObject uiRoot,modelRoot;
    ParticleSystem[] preparation;
    Vector3 originalScale;
    Vector2 pointerStart;
    float holdTime,pressedAt,startTime,endTime,cancelTime;
    bool pointerCaptured,movieStarted,movieFirstFrame;
    float movieTimelineOrigin;
    bool canceledByMovement;
    HashSet<string> scheduled=new();
    readonly Dictionary<string,ParticleSystem[]> scheduledParticles=new();
    bool audioEnabled;
    SourceImmersiveBloom bloom;
    Material movieMaterial;
    public static SourceImmersivePlayer Attach(SourceCardTouch touch,SourceCardPresentationStack stack)
    {
        if(!CardSelection.Config.hasImmersive) return null;
        var host=new GameObject("Original immersive sequence");
        var player=host.AddComponent<SourceImmersivePlayer>();
        try { player.Initialize(touch,stack); }
        catch { Destroy(host);throw; }
        return player;
    }
    void Initialize(SourceCardTouch sourceTouch,SourceCardPresentationStack sourceStack)
    {
        touch=sourceTouch;stack=sourceStack;
        state=touch.GetComponentInChildren<SourceImmersiveCardState>();
        if(!state) throw new InvalidOperationException("IM shader controller missing");
        plan=JsonUtility.FromJson<Plan>(CardSelection.Text("immersive.json.gz"));
        string movie=CardSelection.File("movie.mp4");
        if(!File.Exists(movie)) throw new FileNotFoundException("Original IM movie unavailable",movie);
        var manifest=JsonUtility.FromJson<Manifest>(CardSelection.Text("immersive-bundles.json.gz"));
        foreach(var b in manifest.bundles.Reverse()) {
            // The game mirror path is the bundle's own name. The presentation stack
            // already holds some of these under another file, and Unity refuses a second copy.
            string name=b.path.Substring("game/".Length);
            if(AssetBundle.GetAllLoadedAssetBundles().Any(existing=>string.Equals(existing.name,name,StringComparison.OrdinalIgnoreCase))) continue;
            var loaded=AssetBundle.LoadFromFile(Path.Combine(CardSelection.Root,b.path));
            if(!loaded) throw new InvalidOperationException("IM bundle unavailable: "+b.path);
            bundles.Add(loaded);
        }
        // Some dependencies already belong to the selected card. Reuse their
        // loaded bundles rather than opening the same CAB twice.
        var all=AssetBundle.GetAllLoadedAssetBundles().ToArray();
        GameObject Prefab(string name) => all.SelectMany(b=>b.LoadAllAssets<GameObject>()).Single(p=>p.name==name);
        uiRoot=Instantiate(Prefab("ImmersiveUITimelines"),stack.Canvas.transform,false);
        // Setup parents model effects to CardImmersiveObject, including its
        // source face rotation. Parenting to the touch node reverses the mesh.
        modelRoot=Instantiate(Prefab("ImmersiveModelTimelines"),state.transform,false);
        SourceImmersiveMaterials.Apply(plan.materials,uiRoot,modelRoot,stack.ViewCamera,Events);
        bloom=new SourceImmersiveBloom();
        // The game schedules these directors and custom tracks itself. This host
        // uses their exported schedule; it never runs partially bound directors.
        foreach(var director in uiRoot.GetComponentsInChildren<UnityEngine.Playables.PlayableDirector>(true).Concat(modelRoot.GetComponentsInChildren<UnityEngine.Playables.PlayableDirector>(true))) director.enabled=false;
        foreach(var animation in plan.effectAnimations) {var animator=Resolve(animation.root,animation.indices).GetComponent<Animator>();if(animator) animator.enabled=false;}
        StopParticles(true);
        preparation=FindGroup("PreparationOutside",uiRoot).GetComponentsInChildren<ParticleSystem>(true)
            .Concat(FindGroup("PreparationInner",modelRoot).GetComponentsInChildren<ParticleSystem>(true)).ToArray();
        originalScale=stack.CardRect.localScale;
        var maskObject=Instantiate(Prefab("UIImmersiveStencil"));
        maskObject.layer=5;mask=maskObject.GetComponent<RectTransform>();
        mask.SetParent(stack.ViewRect.parent,false);
        mask.anchorMin=mask.anchorMax=mask.pivot=Vector2.one*.5f;
        mask.sizeDelta=stack.CardRect.sizeDelta;
        mask.SetSiblingIndex(stack.ViewRect.GetSiblingIndex());
        var imageObject=new GameObject("Original movie",typeof(RectTransform),typeof(RawImage));
        imageObject.layer=5;movieRect=imageObject.GetComponent<RectTransform>();movieRect.SetParent(mask,false);
        movieRect.anchorMin=movieRect.anchorMax=movieRect.pivot=Vector2.one*.5f;
        movieImage=imageObject.GetComponent<RawImage>();movieImage.raycastTarget=false;
        // CRI samples 999x1749 pixels for the original 1000x1750 movie window.
        // H.264 exposes its padded 1008x1760 frame to the Windows decoder.
        movieImage.uvRect=new Rect(plan.movieUv[0],plan.movieUv[1],plan.movieUv[2],plan.movieUv[3]);
        movieImage.maskable=false;
        movieMaterial=new Material(Shader.Find("Source/Immersive RGBA Video"));movieImage.material=movieMaterial;
        movieTarget=new RenderTexture(1008,1760,0,RenderTextureFormat.ARGB32);movieTarget.Create();
        movieImage.texture=movieTarget;maskObject.SetActive(false);
        video=gameObject.AddComponent<VideoPlayer>();
        video.playOnAwake=false;video.isLooping=false;video.skipOnDrop=false;video.waitForFirstFrame=true;
        video.renderMode=VideoRenderMode.RenderTexture;video.targetTexture=movieTarget;
        audioEnabled=!Application.isBatchMode;
        video.audioOutputMode=audioEnabled ? VideoAudioOutputMode.Direct : VideoAudioOutputMode.None;
        if(audioEnabled && !FindFirstObjectByType<AudioListener>()) stack.ViewCamera.gameObject.AddComponent<AudioListener>();
        video.url=movie;
        video.sendFrameReadyEvents=true;
        video.frameReady+=(_,frame)=> { if(movieStarted && !movieFirstFrame) {movieFirstFrame=true;movieTimelineOrigin=Time.realtimeSinceStartup-(float)video.time;Events.Add("MovieFirstFrame="+frame);} };
        video.errorReceived+=(_,message)=>Fail(message);
        video.loopPointReached+=_=> { if(CurrentPhase==Phase.Movie) BeginEnd(); };
        touch.ImmersiveInput=Pointer;
        video.Prepare();SetPhase(Phase.Idle);RestoreCard();
        Events.Add("OriginalMovie=1008x1760/24fps originalVideoStream=true sourceParticles="+ParticleSystems+" "+plan.caveat);
    }
    static Transform FindGroup(string name,GameObject root)
    { return root.GetComponentsInChildren<Transform>(true).Single(t=>t.name==name); }
    void SetPhase(Phase next)
    { CurrentPhase=next;Events.Add("Phase="+next+" at="+Time.realtimeSinceStartup.ToString("F3")); }
    public bool Pointer(Vector2 point,bool down,bool held,bool up)
    {
        if(CurrentPhase==Phase.Starting || CurrentPhase==Phase.Movie || CurrentPhase==Phase.Ending) return true;
        if(CurrentPhase==Phase.Canceling) return !canceledByMovement;
        if(down && Ready && !touch.Flipped && touch.ContainsSourceView(point)) {
            pointerStart=point;pointerCaptured=true;holdTime=0;pressedAt=Time.realtimeSinceStartup;SetPhase(Phase.Holding);
        }
        if(!pointerCaptured) return false;
        touch.SampleSourceView(point,out var local,out var size);
        touch.SampleSourceView(pointerStart,out var first,out _);
        if((local-first).sqrMagnitude>12*12) {
            CancelHold(true);return false;
        }
        if(up || !held) { CancelHold();return CurrentPhase==Phase.Canceling; }
        holdTime=Time.realtimeSinceStartup-pressedAt;
        if(CurrentPhase==Phase.Holding && holdTime>=plan.holdStart) BeginPreparation(point);
        if(CurrentPhase==Phase.Preparation && holdTime>=plan.holdStart+plan.holdDuration) BeginPlayback();
        // Rotation gets the first short press so moved pointers keep normal
        // capture. A preparation hold consumes input and resets its rotation.
        return CurrentPhase==Phase.Preparation;
    }
    public void BeginPreparation(Vector2 point)
    {
        SetPhase(Phase.Preparation);
        var tap=uiRoot.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="efp_OUT_IMS_TapPower00");
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)stack.Canvas.transform,point,stack.ViewCamera,out var local);
        tap.localPosition=new Vector3(local.x,local.y,tap.localPosition.z);
        foreach(var particle in preparation) { particle.gameObject.SetActive(true);particle.Play(false); }
    }
    public void CancelHold(bool moved=false)
    {
        pointerCaptured=false;
        if(CurrentPhase==Phase.Preparation) {
            CanceledHolds++;cancelTime=0;canceledByMovement=moved;SetPhase(Phase.Canceling);
            foreach(var p in preparation) p.Stop(false,ParticleSystemStopBehavior.StopEmitting);
        } else if(CurrentPhase==Phase.Holding) SetPhase(Phase.Idle);
    }
    public void BeginPlayback()
    {
        if(!Ready) throw new InvalidOperationException("Movie not prepared");
        pointerCaptured=false;touch.ResetFront();startTime=0;movieStarted=movieFirstFrame=false;scheduled.Clear();
        scheduledParticles.Clear();
        foreach(var p in preparation) p.Stop(false,ParticleSystemStopBehavior.StopEmitting);
        video.time=0;video.Pause();mask.gameObject.SetActive(true);SetPhase(Phase.Starting);
        Evaluate(plan.start,0);
        EvaluateEffects(0);
    }
    public void TogglePause()
    {
        if(CurrentPhase!=Phase.Movie) return;
        Paused=!Paused;if(Paused) video.Pause();else video.Play();
        Events.Add("Paused="+Paused+" frame="+video.frame);
    }
    public void Seek(double seconds)
    {
        if(CurrentPhase!=Phase.Movie || !video.canSetTime) throw new InvalidOperationException("IM seek unavailable");
        video.time=seconds;
    }
    public void Skip()
    {
        if(CurrentPhase!=Phase.Starting && CurrentPhase!=Phase.Movie) return;
        Paused=false;video.Pause();BeginEnd(false);Events.Add("Skipped=true");
    }
    void BeginEnd(bool natural=true)
    {
        if(CurrentPhase==Phase.Ending) return;
        Paused=false;endTime=natural ? Mathf.Max(0,(float)(video.time-EndMovieTime)) : 0;
        scheduled.Clear();scheduledParticles.Clear();StopParticles(true);SetPhase(Phase.Ending);
        Evaluate(plan.end,endTime);
    }
    double EndMovieTime => video.frameRate>0 ? ((long)video.frameCount-plan.endFramesRemaining)/video.frameRate : 27.5;
    void Update()
    {
        if(CurrentPhase==Phase.Canceling) {
            cancelTime+=Time.unscaledDeltaTime;
            if(cancelTime>=plan.cancelDuration) { StopParticles(true);RestoreCard(!canceledByMovement);SetPhase(Phase.Idle); }
        }
        if(CurrentPhase==Phase.Starting) {
            if(!movieStarted) startTime=Mathf.Min(startTime+Time.unscaledDeltaTime,plan.movieStart);
            else if(movieFirstFrame) startTime=plan.movieStart+Time.realtimeSinceStartup-movieTimelineOrigin;
            Evaluate(plan.start,startTime);EvaluateEffects(startTime);Schedule(plan.startParticles,startTime);
            if(!movieStarted && startTime>=plan.movieStart) { movieStarted=true;video.Play();Events.Add("VideoStarted=true"); }
            if(startTime>=plan.start.duration) { stack.CardRect.gameObject.SetActive(false);SetPhase(Phase.Movie); }
        }
        if(CurrentPhase==Phase.Movie && !Paused) {
            startTime+=Time.unscaledDeltaTime;Schedule(plan.startParticles,startTime);
            if(video.frameCount>0 && video.frame>=(long)video.frameCount-plan.endFramesRemaining) BeginEnd();
        }
        if(CurrentPhase==Phase.Ending) {
            if(video.isPlaying) endTime=Mathf.Max(0,(float)(video.time-EndMovieTime));
            else endTime+=Time.unscaledDeltaTime;
            stack.CardRect.gameObject.SetActive(true);
            Evaluate(plan.end,endTime);Schedule(plan.endParticles,endTime);
            if(endTime>=plan.end.duration) {
                video.Stop();mask.gameObject.SetActive(false);StopParticles(true);RestoreCard();CompletedPlays++;
                // Stop resets native decoder state; prepare again for replay.
                video.Prepare();SetPhase(Phase.Idle);
            }
        }
        if(Input.GetKeyDown(KeyCode.Escape)) Skip();
        if(Input.GetKeyDown(KeyCode.Space)) TogglePause();
        UpdateMovieRect();
    }
    void UpdateMovieRect()
    {
        if(!mask) return;
        // SetupImmersive uses a fixed 1000x1750 logical rectangle. Fitting the
        // decoded 1008x1760 frame to the screen instead loses the source crop.
        movieRect.sizeDelta=new Vector2(plan.movieRectSize[0],plan.movieRectSize[1]);
        movieRect.localScale=new Vector3(1/Mathf.Max(.001f,mask.localScale.x),1/Mathf.Max(.001f,mask.localScale.y),1);
    }
    void Schedule(Particle[] entries,float time)
    {
        foreach(var entry in entries) {
            string key=entry.root+"/"+string.Join("/",entry.indices)+"/"+entry.start;
            if(time>=entry.start+entry.duration && scheduledParticles.TryGetValue(key,out var finished)) {
                foreach(var p in finished) p.Stop(false,ParticleSystemStopBehavior.StopEmitting);
                scheduledParticles.Remove(key);
            }
            if(time<entry.start || !scheduled.Add(key)) continue;
            var roots=new[]{Resolve(entry.root,entry.indices)};
            var active=new List<ParticleSystem>();
            foreach(var effect in roots) {
                effect.gameObject.SetActive(true);
                foreach(var p in effect.GetComponentsInChildren<ParticleSystem>(true)) {p.gameObject.SetActive(true);p.useAutoRandomSeed=false;p.randomSeed=entry.seed;p.Play(false);active.Add(p);}
            }
            scheduledParticles[key]=active.ToArray();
        }
    }
    public void Evaluate(Clip clip,float time)
    {
        foreach(var curve in clip.curves) {
            var key=curve.keys[0];
            foreach(var next in curve.keys) { if(next.time>time) break; key=next; }
            float dt=Mathf.Max(0,time-key.time);
            Assign(curve.field,((key.a*dt+key.b)*dt+key.c)*dt+key.d);
        }
        foreach(var constant in clip.constants) Assign(constant.field,constant.value);
        stack.CardRect.localScale=new Vector3(originalScale.x*CardScale,originalScale.y*CardScale,originalScale.z);
        mask.localScale=new Vector3(CardScale-.01f,CardScale-.01f,1);
        UpdateMovieRect();
    }
    Transform Resolve(string root,int[] indices)
    { var target=root=="ui" ? uiRoot.transform : modelRoot.transform;foreach(int index in indices) target=target.GetChild(index);return target; }
    static float Sample(Curve curve,float time)
    { var key=curve.keys[0];foreach(var next in curve.keys) {if(next.time>time) break;key=next;}float dt=Mathf.Max(0,time-key.time);return ((key.a*dt+key.b)*dt+key.c)*dt+key.d; }
    void EvaluateEffects(float time)
    {
        foreach(var animation in plan.effectAnimations) {
            if(time<animation.start) continue;
            float local=Mathf.Min(time-animation.start,animation.length);
            Resolve(animation.root,animation.indices).localPosition=new Vector3(Sample(animation.curves[0],local),Sample(animation.curves[1],local),Sample(animation.curves[2],local));
        }
    }
    void Assign(string field,float value)
    {
        switch(field) {
            case "CardIllustAlpha":state.IllustAlpha=value;break;
            case "CardTextAlpha":state.TextAlpha=value;break;
            case "CardFrameEmitSwitch":state.EmitSwitch=value;break;
            case "CardFrameEmitIntensity":state.EmitIntensity=value;break;
            case "CardFrameDarkness":state.Darkness=value;break;
            case "CardScale":CardScale=value;break;
            default:throw new InvalidOperationException("Unknown original IM channel: "+field);
        }
    }
    void RestoreCard(bool resetTouch=true)
    {
        CardScale=1;state.IllustAlpha=state.TextAlpha=state.EmitIntensity=1;state.EmitSwitch=state.Darkness=0;
        stack.CardRect.localScale=originalScale;stack.CardRect.gameObject.SetActive(true);stack.ViewRect.gameObject.SetActive(true);
        if(mask) {mask.localScale=Vector3.one;mask.gameObject.SetActive(false);}
        pointerCaptured=false;if(resetTouch) touch.ResetFront();
    }
    void StopParticles(bool clear)
    {
        if(!uiRoot || !modelRoot) return;
        foreach(var p in uiRoot.GetComponentsInChildren<ParticleSystem>(true).Concat(modelRoot.GetComponentsInChildren<ParticleSystem>(true)))
            p.Stop(false,clear ? ParticleSystemStopBehavior.StopEmittingAndClear : ParticleSystemStopBehavior.StopEmitting);
    }
    void Fail(string message)
    { Error=message;StopParticles(true);RestoreCard();SetPhase(Phase.Error);Debug.LogError("Immersive movie: "+message); }
    void OnApplicationFocus(bool focused)
    { if(!focused && CurrentPhase==Phase.Movie && !Paused) TogglePause(); }
    void OnGUI()
    {
        if(CurrentPhase!=Phase.Movie) return;
        float width=Mathf.Min(Screen.width*.3f,180),height=40;
        if(GUI.Button(new Rect(12,Screen.height-height-12,width,height),Paused ? "Reprendre" : "Pause")) TogglePause();
        if(GUI.Button(new Rect(Screen.width-width-12,Screen.height-height-12,width,height),"Skip")) Skip();
    }
    void OnDestroy()
    {
        bloom?.Dispose();
        if(touch) touch.ImmersiveInput=null;
        if(video) video.Stop();
        if(uiRoot) Destroy(uiRoot);if(modelRoot) Destroy(modelRoot);if(mask) Destroy(mask.gameObject);
        if(movieTarget) {movieTarget.Release();Destroy(movieTarget);}
        if(movieMaterial) Destroy(movieMaterial);
        foreach(var b in bundles.AsEnumerable().Reverse()) if(b) b.Unload(false);
    }
}
