using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lettuce.Graphics.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

[DefaultExecutionOrder(-100)]
public sealed class NativeRenderGraphSession : MonoBehaviour
{
    string captureLabel, output;
    int pending;
    Exception captureError;
    List<string> report;
    RenderPipelineGlobalSettings globalSettings;
    Camera interactiveCamera;
    RenderTexture interactiveTarget;
    bool interactiveReady;
    SourceCardPresentationStack presentation;
    RenderTexture presentationTarget;
    SourceImmersivePlayer immersive;
    SourceImmersiveBloom urBloom;
    int interactiveSubmittedFrames;
    class ReadbackData { public TextureHandle color, emission; public string label; }
    void Awake()
    {
        // Original globalgamemanagers selects Android Medium (index 2),
        // anisotropicTextures=1. The desktop template selects Fantastic (5)
        // and forces anisotropy even on textures whose own setting is 0/1.
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
        // The existing UI capture still uses its builtin Camera.Render path.
        // Switch only after those textures have been produced.
        globalSettings = GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>();
        GraphicsSettings.defaultRenderPipeline = null;
        QualitySettings.renderPipeline = null;
    }
    public IEnumerator Run(Camera camera, Transform card, string root, List<string> log)
    {
        report = log;
        output = Path.Combine(root,"logs/runtime/rendergraph-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var steps = Capture(camera, card, root);
        while (true) {
            bool more = false; Exception failure = null;
            try { more = steps.MoveNext(); } catch (Exception error) { failure = error; }
            if (failure != null) {
                Debug.LogException(failure);
                report.Add("FAIL="+failure);
                File.WriteAllLines(Path.Combine(output,"rendergraph-report.txt"),report);
                Application.Quit(1); yield break;
            }
            if (!more) break;
            yield return steps.Current;
        }
        Application.Quit(0);
    }
    IEnumerator Capture(Camera camera, Transform card, string root)
    {
        Configure(camera);
        var pipeline = GraphicsSettings.currentRenderPipeline;
        CustomRendererRenderGraph.Diagnostic = RecordReadback;
        int size = SourceRenderQuality.CardTargetSize(SourceRenderQuality.SelectedLevel());
        var target = SourceCardRenderTarget.Create(SourceRenderQuality.SelectedLevel());
        camera.targetTexture = target;
        SourceCardPipelineState.Record(camera,target,root,report,"ConfiguredCapture");
        for (int i=0;i<3;i++) yield return null;
        report.Add("Probe pipeline="+pipeline+" target="+size+" customMRT=True originalScalarSettings=True featureGroupsIncomplete");
        var poses=new List<SourceCardTouchProbe.Pose>();
        if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-touch")>=0)
            poses=SourceCardTouchProbe.Run(card.GetComponent<SourceCardTouch>(),report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-mrr")>=0)
            poses=SourceMrrCardProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-msr")>=0)
            poses=SourceMsrCardProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-marble")>=0)
            poses=SourceMarbleCardProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-ancient")>=0)
            poses=SourceAncientCardProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-mssr")>=0)
            poses=SourceMssrCardProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-kira-puyo")>=0)
            poses=SourceKiraPuyoProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-circular-kira")>=0)
            poses=SourceCircularKiraProbe.Run(card,report);
        else if (Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-native-future")>=0)
            poses=SourceFutureCardProbe.Run(card,report);
        else foreach (int degrees in new[] {0,20,180}) {
            int angle=degrees;
            poses.Add(new SourceCardTouchProbe.Pose {name="angle-"+angle,apply=()=>card.localRotation=Quaternion.Euler(0,angle,0)});
        }
        var matched=SourceCardMatchedPose.Read(card,report);
        if(matched!=null) poses.Add(matched);
        // Fidelity bench: only the poses compared with the game, each given time to settle as in the game.
        var arguments=Environment.GetCommandLineArgs();
        int settleOption=Array.IndexOf(arguments,"--settle-seconds");
        float settleSeconds=settleOption>=0 ? float.Parse(arguments[settleOption+1],System.Globalization.CultureInfo.InvariantCulture) : 0;
        if(settleSeconds>0) poses.RemoveAll(pose=>pose.name!="touch-front" && pose.name!="touch-yaw-limit");
        // Fidelity bench: finger offsets as fractions of the half-width, as dragged in the game.
        int sweepOption=Array.IndexOf(arguments,"--tilt-sweep");
        if(sweepOption>=0) foreach(var text in arguments[sweepOption+1].Split(',')) {
            float fraction=float.Parse(text,System.Globalization.CultureInfo.InvariantCulture);
            var touch=card.GetComponent<SourceCardTouch>();
            poses.Add(new SourceCardTouchProbe.Pose {name="touch-sweep-"+text,apply=()=>{
                touch.ResetFront(); touch.State.Begin(Vector2.zero,0); touch.State.Drag(new Vector2(500*fraction,0),Vector2.one*1000); touch.Tick(0);
            }});
        }
        foreach (var pose in poses) {
            pose.apply();
            SourceCardEffects.Refresh(card);
            // Source ADB matched poses are captured after the short relief flash.
            if (pose.name.StartsWith("matched")) for (int i=0;i<36;i++) yield return null;
            // Simulated time: long animations settle without waiting for them in real time.
            Time.captureDeltaTime=settleSeconds>0 ? 1f/30 : 0;
            for(int frame=0;frame<settleSeconds*30;frame++) yield return null;
            captureLabel = pose.name;
            pending = 2; captureError = null;
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(camera,request)) throw new InvalidOperationException("URP SingleCameraRequest unsupported");
            int before = CustomRendererRenderGraph.RecordedFrames;
            RenderPipeline.SubmitRenderRequest(camera,request);
            for (int i=0;pending>0 && i<300;i++) yield return null;
            if (captureError != null) throw captureError;
            if (pending>0) throw new TimeoutException("Native RenderGraph GPU readback timed out");
            if (CustomRendererRenderGraph.RecordedFrames != before+1) throw new InvalidOperationException("Native renderer did not record exactly one requested frame");
            report.Add("NativeFrame pose="+pose.name+" "+CustomRendererRenderGraph.LastColorDescriptor);
            RenderTexture.active = target;
            var image = new Texture2D(size,size,TextureFormat.RGBA32,false);
            image.ReadPixels(new Rect(0,0,size,size),0,0); image.Apply();
            File.WriteAllBytes(Path.Combine(output,pose.name+"-final.png"),image.EncodeToPNG());
            Destroy(image); RenderTexture.active = null;
            if (pose.name=="angle-0") {
                SourceCardPresentationProbe.Run(target,root,output,report);
                var stackSteps=CapturePresentationStack(target,root);
                while(stackSteps.MoveNext()) yield return stackSteps.Current;
            }
            else if(pose.name=="matched-source" || settleSeconds>0) {
                var stackSteps=CapturePresentationStack(target,root,pose.name);
                while(stackSteps.MoveNext()) yield return stackSteps.Current;
            }
        }
        CustomRendererRenderGraph.Diagnostic = null;
        camera.targetTexture = null; target.Release(); Destroy(target);
        File.WriteAllLines(Path.Combine(output,"rendergraph-report.txt"),report);
        File.WriteAllText(Path.Combine(root,"logs/runtime/latest-rendergraph-capture.txt"),output);
    }
    IEnumerator CapturePresentationStack(RenderTexture card,string root,string label="angle-0")
    {
        var target=new RenderTexture(1080,1920,24,RenderTextureFormat.ARGB32);
        target.Create();
        try {
            using(var stack=new SourceCardPresentationStack(card,root,target)) {
                CustomRendererRenderGraph.Diagnostic=(graph,frame,color,emission)=> {
                    pending+=2;
                    captureLabel=(label=="angle-0" ? "stack-" : "stack-"+label+"-")+frame.Get<UniversalCameraData>().camera.name;
                    RecordReadback(graph,frame,color,emission);
                };
                stack.Enable();
                // Unity must rebuild CanvasRenderer batches for the enabled
                // camera before a render request can submit their geometry.
                yield return null; yield return null;
                UnityEngine.Canvas.ForceUpdateCanvases();
                int frames=CustomRendererRenderGraph.RecordedFrames;
                int clears=CustomRendererRenderGraph.GroupClearCount;
                var request=new RenderPipeline.StandardRequest {destination=target};
                if (!RenderPipeline.SupportsRenderRequest(stack.BaseCamera,request)) throw new InvalidOperationException("Standard stack request unsupported");
                RenderPipeline.SubmitRenderRequest(stack.BaseCamera,request);
                int recorded=CustomRendererRenderGraph.RecordedFrames-frames;
                if(recorded!=3) throw new InvalidOperationException("Expected Base/App/Over render stack, got "+recorded);
                CustomRendererRenderGraph.Diagnostic=null;
                for(int i=0;pending>0&&i<300;i++) yield return null;
                if(pending>0) throw new TimeoutException("Stack diagnostic readback timeout");
                if(captureError!=null) throw captureError;
                RenderTexture.active=target;
                var image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
                try {
                    image.ReadPixels(new Rect(0,0,image.width,image.height),0,0); image.Apply();
                    var pixels=image.GetPixels32();
                    SourceDetailBackground.VerifyPresentationPixels(pixels);
                    int positive=pixels.Count(p=>p.a>0 && (p.r>0 || p.g>0 || p.b>0));
                    File.WriteAllBytes(Path.Combine(output,label+"-presentation-stack.png"),image.EncodeToPNG());
                    report.Add("StackProbe RGB="+pixels.Count(p=>p.r>0||p.g>0||p.b>0)+" alpha="+pixels.Count(p=>p.a>0)+" mainPixelRect="+stack.BaseCamera.pixelRect+" appPixelRect="+stack.Canvas.worldCamera.pixelRect+" canvasRect="+((RectTransform)stack.Canvas.transform).rect+" cardPosition="+stack.CardRect.position+" cardScale="+stack.CardRect.lossyScale);
                    var graphic=stack.CardRect.GetComponent<UnityEngine.UI.RawImage>();
                    report.Add("StackCanvas meshVertices="+graphic.canvasRenderer.GetMesh().vertexCount+" culled="+graphic.canvasRenderer.cull+" material="+graphic.materialForRendering.name+" alpha="+graphic.canvasRenderer.GetAlpha()+" color="+graphic.color+" rt="+graphic.texture.width);
                    if(positive<1000) throw new InvalidOperationException("URP presentation stack output empty");
                    report.Add("OriginalPresentationStack frames="+recorded+" groupClears="+(CustomRendererRenderGraph.GroupClearCount-clears)+" positiveRGB="+positive+" scale="+stack.Canvas.scaleFactor.ToString("R")+" logicalSide="+stack.LogicalSide.ToString("R")+" initialFitSide="+stack.InitialLogicalSide.ToString("R")+" updateCardSize=RVA47ffc48 sourceCanvasCamera=UIApp30 shader=originalUnchanged");
                    report.Add("OriginalPresentationStackCaveat="+stack.Caveat);
                } finally { Destroy(image); RenderTexture.active=null; }
            }
        } finally { CustomRendererRenderGraph.Diagnostic=RecordReadback; target.Release(); Destroy(target); }
    }
    void Configure(Camera camera)
    {
        // Cards with an EmissiveObject (UR glitter, Mega SR aura, ...) go through the
        // recovered card-studio sheet bloom before presentation, as for IM.
        if(SourceCardEffects.CardEmissive && urBloom==null)
            urBloom=new SourceImmersiveBloom();
        var pipeline = Resources.Load<UniversalRenderPipelineAsset>("SourceCardPipeline");
        if (!pipeline) throw new InvalidOperationException("SourceCardPipeline asset missing");
        camera.enabled = false;
        camera.nearClipPlane = .3f; camera.farClipPlane = 1000;
        camera.backgroundColor = Color.black; camera.allowHDR = true;
        camera.allowMSAA = true; camera.useOcclusionCulling = false;
        // Asset3DRenderer selects Detail layer21; live1.7.5 confirms this mask.
        camera.cullingMask = 1 << 21;
        var extra = camera.GetUniversalAdditionalCameraData();
        extra.renderPostProcessing = true;
        extra.volumeLayerMask = 1 << 27;
        extra.antialiasing = AntialiasingMode.None;
        extra.renderShadows = false;
        extra.requiresDepthOption = CameraOverrideOption.UsePipelineSettings;
        extra.requiresColorOption = CameraOverrideOption.UsePipelineSettings;
        if (!globalSettings || !GraphicsSettings.TrySetCurrentRenderPipelineGlobalSettings(globalSettings))
            throw new InvalidOperationException("Cannot restore native pipeline global settings after UI capture");
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
    }
    public IEnumerator RunInteractive(Camera camera)
    {
        Configure(camera);
        interactiveCamera = camera;
        int size = SourceRenderQuality.CardTargetSize(SourceRenderQuality.SelectedLevel());
        interactiveTarget = SourceCardRenderTarget.Create(SourceRenderQuality.SelectedLevel());
        camera.targetTexture = interactiveTarget;
        yield return null;
        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = interactiveTarget };
        if (!RenderPipeline.SupportsRenderRequest(camera,request)) throw new InvalidOperationException("Native interactive render request unsupported");
        string root=Environment.GetCommandLineArgs().SkipWhile(a=>a!="--project-root").Skip(1).FirstOrDefault();
        if(string.IsNullOrEmpty(root)) root=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.."));
        SourceCardPipelineState.Record(camera,interactiveTarget,root,report,"ConfiguredInteractive");
        if(Application.isBatchMode) {
            presentationTarget=new RenderTexture(1080,1920,24,RenderTextureFormat.ARGB32);
            presentationTarget.Create();
        }
        presentation=new SourceCardPresentationStack(interactiveTarget,root,presentationTarget);
        var touch=FindFirstObjectByType<SourceCardTouch>();
        if(!touch) throw new InvalidOperationException("Native touch renderer unavailable");
        touch.BindSourceView(presentation.ViewRect,presentation.ViewCamera);
        immersive=SourceImmersivePlayer.Attach(touch,presentation);
        if(Application.isBatchMode) presentation.EnableOverlaysForRequest(); else presentation.Enable();
        interactiveReady = true;
    }
    public IEnumerator VerifyImmersive(Camera camera,string root,List<string> log)
    {
        report=log;
        yield return RunInteractive(camera);
        if(!immersive) throw new InvalidOperationException("IM verification requires an immersive card");
        string destination=Path.Combine(root,"logs/runtime/immersive-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(destination);
        float deadline=Time.realtimeSinceStartup+15;
        while(!immersive.Ready && Time.realtimeSinceStartup<deadline && immersive.Error==null) yield return null;
        if(!immersive.Ready) throw new InvalidOperationException("Movie preparation failed: "+immersive.Error);
        var point=RectTransformUtility.WorldToScreenPoint(presentation.ViewCamera,presentation.ViewRect.position);
        CaptureImmersive(destination,"before");
        immersive.Pointer(point,true,true,false);
        float held=Time.realtimeSinceStartup;
        while(Time.realtimeSinceStartup-held<.7f) { immersive.Pointer(point,false,true,false);yield return null; }
        if(immersive.CurrentPhase!=SourceImmersivePlayer.Phase.Preparation) throw new InvalidOperationException("Stationary hold failed to prepare IM");
        CaptureImmersive(destination,"preparation");
        immersive.Pointer(point,false,false,true);
        while(immersive.Busy) yield return null;
        if(immersive.CanceledHolds!=1 || immersive.CardScale!=1) throw new InvalidOperationException("IM canceled hold did not restore card");
        CaptureImmersive(destination,"cancel-restored");
        immersive.Pointer(point,true,true,false);held=Time.realtimeSinceStartup;
        while(Time.realtimeSinceStartup-held<.45f) { yield return null;immersive.Pointer(point,false,true,false); }
        if(immersive.Pointer(point+new Vector2(100,0),false,true,false) || immersive.Pointer(point+new Vector2(110,0),false,true,false)) throw new InvalidOperationException("Canceled IM hold blocked rotation input");
        while(immersive.Busy) yield return null;
        if(immersive.CanceledHolds!=2) throw new InvalidOperationException("Moved IM hold did not cancel");
        immersive.Pointer(point,true,true,false);held=Time.realtimeSinceStartup;
        while(Time.realtimeSinceStartup-held<2) { immersive.Pointer(point,false,true,false);yield return null; }
        immersive.Pointer(point,false,false,true);
        if(immersive.CurrentPhase!=SourceImmersivePlayer.Phase.Starting) throw new InvalidOperationException("Long hold did not start IM");
        deadline=Time.realtimeSinceStartup+8;
        var transitionTimes=new[]{.15f,.5f,1f,2.5f,2.8f,3.1f};int transition=0;
        float transitionStart=Time.realtimeSinceStartup-.2f;
        while(immersive.CurrentPhase==SourceImmersivePlayer.Phase.Starting && Time.realtimeSinceStartup<deadline) {
            if(transition<transitionTimes.Length && Time.realtimeSinceStartup-transitionStart>=transitionTimes[transition]) {CaptureImmersive(destination,"start-"+transitionTimes[transition].ToString("F2",System.Globalization.CultureInfo.InvariantCulture));transition++;}
            yield return null;
        }
        if(immersive.CurrentPhase!=SourceImmersivePlayer.Phase.Movie || immersive.VideoFrame<0) throw new InvalidOperationException("Original IM movie did not produce frames");
        immersive.Seek(5);
        for(int i=0;i<45;i++) yield return null;
        immersive.TogglePause();
        for(int i=0;i<12;i++) yield return null;
        long pausedFrame=immersive.VideoFrame;
        for(int i=0;i<12;i++) yield return null;
        if(!immersive.Paused || immersive.VideoFrame!=pausedFrame) throw new InvalidOperationException("IM pause did not hold decoder frame");
        CaptureImmersive(destination,"movie-paused");
        log.Add("ImmersivePausedFrame="+pausedFrame);
        immersive.TogglePause();
        for(int i=0;i<12;i++) yield return null;
        if(immersive.VideoFrame<=pausedFrame) throw new InvalidOperationException("IM resume did not advance decoder");
        immersive.Seek(28.2);
        deadline=Time.realtimeSinceStartup+8;
        float endingStart=-1;transition=0;
        while(immersive.Busy && Time.realtimeSinceStartup<deadline) {
            if(immersive.CurrentPhase==SourceImmersivePlayer.Phase.Ending) {
                if(endingStart<0) endingStart=Time.realtimeSinceStartup;
                if(transition<3 && Time.realtimeSinceStartup-endingStart>new[]{.7f,1.8f,2.7f}[transition]) {CaptureImmersive(destination,"end-"+transition);transition++;}
            }
            yield return null;
        }
        if(immersive.Busy || immersive.CompletedPlays!=1 || immersive.CardScale!=1) throw new InvalidOperationException("IM natural end did not restore card");
        CaptureImmersive(destination,"natural-end-restored");
        while(!immersive.Ready && Time.realtimeSinceStartup<deadline) yield return null;
        immersive.BeginPlayback();
        for(int i=0;i<24;i++) yield return null;
        immersive.Skip();
        deadline=Time.realtimeSinceStartup+6;
        while(immersive.Busy && Time.realtimeSinceStartup<deadline) yield return null;
        if(immersive.Busy || immersive.CompletedPlays!=2 || immersive.CardScale!=1) throw new InvalidOperationException("IM skip or replay failed");
        CaptureImmersive(destination,"skip-restored");
        log.AddRange(immersive.Events);
        log.Add("ImmersiveLifecycle=PASS canceledHold=2 movedHoldReleasesInput=true movieFramesAdvanced=true pauseResume=true naturalEnd=true replaySkip=true originalParticles="+immersive.ParticleSystems);
        log.Add("ImmersiveVisualParity=false sourceVideoStreamUnchanged=true originalRoundedStencil=true audioDisabledInBatch=true");
        File.WriteAllLines(Path.Combine(destination,"immersive-report.txt"),log);
        File.WriteAllText(Path.Combine(root,"logs/runtime/latest-immersive.txt"),destination);
        Application.Quit(0);
    }
    void CaptureImmersive(string destination,string name)
    {
        RenderPipeline.SubmitRenderRequest(interactiveCamera,new UniversalRenderPipeline.SingleCameraRequest {destination=interactiveTarget});
        RenderPipeline.SubmitRenderRequest(presentation.BaseCamera,new RenderPipeline.StandardRequest {destination=presentationTarget});
        var previous=RenderTexture.active;RenderTexture.active=presentationTarget;
        var image=new Texture2D(presentationTarget.width,presentationTarget.height,TextureFormat.RGBA32,false);
        image.ReadPixels(new Rect(0,0,image.width,image.height),0,0);image.Apply();
        File.WriteAllBytes(Path.Combine(destination,name+".png"),image.EncodeToPNG());
        Destroy(image);RenderTexture.active=previous;
    }
    public IEnumerator VerifyInteractive(Camera camera, string root, List<string> log)
    {
        report=log;
        yield return RunInteractive(camera);
        Canvas.ForceUpdateCanvases();
        SourceCardTouchProbe.VerifyView(FindFirstObjectByType<SourceCardTouch>(),presentation.Canvas,log);
        log.Add("BackgroundBranch="+presentation.BackgroundBranch+" routingValidated=False transitionsUnported=True");
        int before = CustomRendererRenderGraph.RecordedFrames;
        int beforeSubmitted=interactiveSubmittedFrames;
        for (int i=0;i<60;i++) yield return null;
        int frames = CustomRendererRenderGraph.RecordedFrames-before;
        string destination = Path.Combine(root,"logs/runtime/interactive-graph-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(destination);
        RenderTexture.active = interactiveTarget;
        var image = new Texture2D(interactiveTarget.width,interactiveTarget.height,TextureFormat.RGBA32,false);
        image.ReadPixels(new Rect(0,0,image.width,image.height),0,0); image.Apply();
        int positive = 0;
        foreach (var pixel in image.GetPixels32()) if (pixel.r>0 || pixel.g>0 || pixel.b>0) positive++;
        File.WriteAllBytes(Path.Combine(destination,"continuous-target.png"),image.EncodeToPNG());
        Destroy(image); RenderTexture.active = null;
        int submitted=interactiveSubmittedFrames-beforeSubmitted;
        int presentationPositive=0;
        if(presentationTarget) {
            RenderTexture.active=presentationTarget;
            var frameImage=new Texture2D(presentationTarget.width,presentationTarget.height,TextureFormat.RGBA32,false);
            frameImage.ReadPixels(new Rect(0,0,frameImage.width,frameImage.height),0,0); frameImage.Apply();
            var presentationPixels=frameImage.GetPixels32();
            SourceDetailBackground.VerifyPresentationPixels(presentationPixels);
            presentationPositive=presentationPixels.Count(p=>p.a>0&&(p.r>0||p.g>0||p.b>0));
            File.WriteAllBytes(Path.Combine(destination,"continuous-presentation.png"),frameImage.EncodeToPNG());
            Destroy(frameImage); RenderTexture.active=null;
        }
        log.Add("ContinuousGraphFrames="+frames+" cardRequests="+submitted+" positiveRGB="+positive+" presentationPositiveRGB="+presentationPositive+" visibleWindowNotVerified="+Application.isBatchMode);
        bool passed = submitted>=59 && frames>=submitted && positive>1000 && (!presentationTarget || presentationPositive>1000);
        log.Add("ContinuousGraph="+(passed ? "PASS" : "FAIL"));
        File.WriteAllLines(Path.Combine(destination,"interactive-report.txt"),log);
        File.WriteAllText(Path.Combine(root,"logs/runtime/latest-interactive-graph.txt"),destination);
        Application.Quit(passed ? 0 : 1);
    }
    void LateUpdate()
    {
        if (!interactiveReady) return;
        RenderPipeline.SubmitRenderRequest(interactiveCamera, new UniversalRenderPipeline.SingleCameraRequest { destination = interactiveTarget });
        interactiveSubmittedFrames++;
        if(presentationTarget) RenderPipeline.SubmitRenderRequest(presentation.BaseCamera,new RenderPipeline.StandardRequest {destination=presentationTarget});
    }
    void OnDestroy()
    {
        urBloom?.Dispose();
        presentation?.Dispose();
        if(immersive) Destroy(immersive.gameObject);
        if(presentationTarget) { presentationTarget.Release(); Destroy(presentationTarget); }
        if (interactiveTarget) { interactiveTarget.Release(); Destroy(interactiveTarget); }
    }
    void RecordReadback(RenderGraph graph, ContextContainer frame, TextureHandle color, TextureHandle emission)
    {
        if (captureLabel == null) return;
        using (var builder = graph.AddUnsafePass<ReadbackData>("Native card diagnostic readback",out var data)) {
            data.color=color; data.emission=emission; data.label=captureLabel;
            captureLabel=null;
            builder.UseTexture(color,AccessFlags.Read); builder.UseTexture(emission,AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((ReadbackData pass, UnsafeGraphContext context) => {
                var command = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                // RenderGraph returns pass data to its pool before asynchronous
                // callbacks finish. Copy the label before the next camera reuses it.
                string label=pass.label;
                command.RequestAsyncReadback((Texture)pass.color,0,TextureFormat.RGBAFloat,request=>Save(request,label+"-color"));
                command.RequestAsyncReadback((Texture)pass.emission,0,TextureFormat.RGBAFloat,request=>Save(request,label+"-emission"));
            });
        }
    }
    void Save(AsyncGPUReadbackRequest request,string label)
    {
        try {
            if (request.hasError) throw new InvalidOperationException("Native GPU readback failed: "+label);
            var image = new Texture2D(request.width,request.height,TextureFormat.RGBAFloat,false,true);
            try {
                image.LoadRawTextureData(request.GetData<byte>()); image.Apply();
                var pixels=image.GetPixels(); int positive=0,positiveAlpha=0; float max=0;
                foreach (var pixel in pixels) {
                    float value=Mathf.Max(pixel.r,Mathf.Max(pixel.g,pixel.b));
                    if (float.IsNaN(value) || float.IsInfinity(value) || float.IsNaN(pixel.a) || float.IsInfinity(pixel.a)) throw new InvalidOperationException("Nonfinite native MRT output");
                    if (value>.00001f) positive++; if (pixel.a>.00001f) positiveAlpha++;
                    max=Mathf.Max(max,value);
                }
                if (label.EndsWith("-color") && !label.StartsWith("stack-") && positive<1000) throw new InvalidOperationException("Native card color target is empty");
                File.WriteAllBytes(Path.Combine(output,label+".png"),image.EncodeToPNG());
                report.Add("GPU "+label+" positiveRGB="+positive+" maxRGB="+max+" positiveAlpha="+positiveAlpha);
                for (int i=0;i<pixels.Length;i++) pixels[i]=new Color(pixels[i].a,pixels[i].a,pixels[i].a,1);
                image.SetPixels(pixels); image.Apply();
                File.WriteAllBytes(Path.Combine(output,label+"-alpha.png"),image.EncodeToPNG());
            } finally { Destroy(image); }
        } catch (Exception error) { captureError=error; }
        pending--;
    }
}
