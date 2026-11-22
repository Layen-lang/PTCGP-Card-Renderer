using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lettuce.Graphics.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Original material and serialized Base/App/Over cameras. Dialog routing,
// scene background, safe area, dynamic postprocessing still need validation.
public sealed class SourceCardPresentationStack : IDisposable
{
    [Serializable] class Scaler { public int uiScaleMode,screenMatchMode; public float[] referenceResolution; public float referencePixelsPerUnit; }
    [Serializable] class Settings { public bool useDepthTexture; public int postEffectGroup; }
    [Serializable] class Bits { public int m_Bits; }
    [Serializable] class Extra { public bool m_RenderShadows,m_RenderPostProcessing,m_ClearDepth,m_AllowXRRendering,m_AllowHDROutput; public int m_RequiresDepthTextureOption,m_RequiresOpaqueTextureOption,m_Antialiasing,m_AntialiasingQuality,m_VolumeFrameworkUpdateModeOption,m_RendererIndex; public Bits m_VolumeLayerMask; }
    [Serializable] class CameraPlan { public string name; public bool orthographic,hdr,msaa,occlusion,hasRendererSettings; public float orthographicSize,nearClip,farClip,fieldOfView,depth; public int cullingMask,clearFlags; public Color color; public Extra additional; public Settings rendererSettings; }
    [Serializable] class CanvasPlan { public float m_PlaneDistance; public bool m_PixelPerfect; public int m_AdditionalShaderChannelsFlag,m_SortingOrder; }
    [Serializable] class Plan { public Scaler scaler; public float cardWidth,cardHeight; public CameraPlan baseCamera,appCamera,overCamera; public CanvasPlan canvas; public string caveat; }
    [Serializable] class Bundle { public string path; }
    [Serializable] class Manifest { public Bundle[] bundles; }
    readonly List<AssetBundle> bundles=new();
    readonly List<GameObject> objects=new();
    public Camera BaseCamera { get; private set; }
    public Canvas Canvas { get; private set; }
    public RectTransform CardRect { get; private set; }
    public RectTransform ViewRect { get; private set; }
    public Camera ViewCamera { get; private set; }
    public float LogicalSide { get; private set; }
    public float InitialLogicalSide { get; private set; }
    public string Caveat { get; private set; }
    SourceDetailBackground background;
    SourceScreenVignette vignette;
    public string BackgroundBranch => background?.Branch;
    public SourceCardPresentationStack(RenderTexture card,string root,RenderTexture output=null)
    {
        try {
            var plan=JsonUtility.FromJson<Plan>(File.ReadAllText(Path.Combine(root,"runtime-assets/plans/presentation-stack.json")));
            if (plan.scaler.uiScaleMode!=1 || plan.scaler.screenMatchMode!=1)
                throw new InvalidOperationException("Unsupported main CanvasScaler branch");
            Caveat=plan.caveat;
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(root,"runtime-assets/bundles/presentation-player/manifest.json")));
            foreach(var b in manifest.bundles.Reverse()) {
                var loaded=AssetBundle.LoadFromFile(Path.Combine(root,b.path));
                if (!loaded) throw new InvalidOperationException("Presentation bundle unavailable: "+b.path);
                bundles.Add(loaded);
            }
            var material=bundles.SelectMany(b=>b.LoadAllAssets<Material>()).Single(m=>m.name=="UI_Default_From_RT");
            if (material.shader.name!="Lettuce/Common/CardNew/UI/Default(from RT)" || !material.shader.isSupported)
                throw new InvalidOperationException("Original presentation shader unsupported");
            BaseCamera=CreateCamera(plan.baseCamera,CameraRenderType.Base);
            BaseCamera.targetTexture=output;
            var app=CreateCamera(plan.appCamera,CameraRenderType.Overlay);
            ViewCamera=app;
            var over=CreateCamera(plan.overCamera,CameraRenderType.Overlay);
            // A batch window has unrelated dimensions. These probe-only targets
            // make Unity's Canvas geometry use the same viewport as the Base.
            // URP overlays inherit the Base attachments when the stack renders.
            if(output) { app.targetTexture=output; over.targetTexture=output; }
            var extra=BaseCamera.GetUniversalAdditionalCameraData();
            // Native DefaultSortOrderComparer.GetOrder maps App to4, Over to6.
            extra.cameraStack.Add(app); extra.cameraStack.Add(over);
            var canvasObject=CreateObject("CommonApp1Canvas",typeof(RectTransform),typeof(Canvas));
            canvasObject.layer=5; Canvas=canvasObject.GetComponent<Canvas>();
            Canvas.renderMode=RenderMode.ScreenSpaceCamera; Canvas.worldCamera=app;
            Canvas.planeDistance=plan.canvas.m_PlaneDistance; Canvas.pixelPerfect=plan.canvas.m_PixelPerfect;
            Canvas.additionalShaderChannels=(AdditionalCanvasShaderChannels)plan.canvas.m_AdditionalShaderChannelsFlag;
            Canvas.sortingOrder=plan.canvas.m_SortingOrder;
            Canvas.referencePixelsPerUnit=plan.scaler.referencePixelsPerUnit;
            if (output) {
                Canvas.scaleFactor=Mathf.Min(output.width/plan.scaler.referenceResolution[0],output.height/plan.scaler.referenceResolution[1]);
            } else {
                var scaler=canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution=new Vector2(plan.scaler.referenceResolution[0],plan.scaler.referenceResolution[1]);
                scaler.screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
                scaler.referencePixelsPerUnit=plan.scaler.referencePixelsPerUnit;
            }
            background=new SourceDetailBackground(Canvas,root);
            var viewObject=CreateObject("Original UICardView",typeof(RectTransform));
            viewObject.layer=5; ViewRect=viewObject.GetComponent<RectTransform>();
            ViewRect.SetParent(canvasObject.transform,false);
            ViewRect.anchorMin=ViewRect.anchorMax=ViewRect.pivot=new Vector2(.5f,.5f);
            ViewRect.sizeDelta=new Vector2(plan.cardWidth,plan.cardHeight);
            var child=CreateObject("Original UICardView child",typeof(RectTransform),typeof(RawImage));
            child.layer=5; CardRect=child.GetComponent<RectTransform>();
            CardRect.SetParent(ViewRect,false);
            CardRect.anchorMin=CardRect.anchorMax=CardRect.pivot=new Vector2(.5f,.5f);
            LogicalSide=plan.cardWidth/(((.88f*.5f)/.6026955842971802f)*(734f/1024f));
            InitialLogicalSide=LogicalSide;
            CardRect.sizeDelta=new Vector2(LogicalSide,LogicalSide);
            var image=child.GetComponent<RawImage>(); image.material=material;
            image.texture=card; image.color=Color.white; image.raycastTarget=false;
            UnityEngine.Canvas.ForceUpdateCanvases();
            // Detail presenter UpdateCardSize calls UICardView.UpdateCardSize
            // (1.7.5 RVA47ffc48) after changing its view rectangle. This uses
            // GetPixelAdjustedRect.height / renderer.VerticalPercentage,
            // independently of the initial horizontal SetChildRectSize fit.
            // Canvas.pixelPerfect=false makes that height ViewRect.rect.height.
            UpdateCardSize((.88f*.5f)/.6026955842971802f);
            UnityEngine.Canvas.ForceUpdateCanvases();
            vignette=new SourceScreenVignette(over,root,output);
        } catch { Dispose(); throw; }
    }
    void UpdateCardSize(float verticalPercentage)
    {
        if(Canvas.pixelPerfect || verticalPercentage<=0)
            throw new InvalidOperationException("Unported pixel-adjusted card sizing branch");
        LogicalSide=ViewRect.rect.height/verticalPercentage;
        CardRect.sizeDelta=new Vector2(LogicalSide,LogicalSide);
    }
    GameObject CreateObject(string name,params Type[] types)
    { var go=new GameObject(name,types); objects.Add(go); return go; }
    Camera CreateCamera(CameraPlan p,CameraRenderType type)
    {
        var camera=CreateObject(p.name,typeof(Camera)).GetComponent<Camera>();
        camera.enabled=false;
        camera.orthographic=p.orthographic; camera.orthographicSize=p.orthographicSize;
        camera.nearClipPlane=p.nearClip; camera.farClipPlane=p.farClip;
        camera.fieldOfView=p.fieldOfView; camera.depth=p.depth; camera.cullingMask=p.cullingMask;
        camera.backgroundColor=p.color; camera.clearFlags=(CameraClearFlags)p.clearFlags;
        camera.allowHDR=p.hdr; camera.allowMSAA=p.msaa; camera.useOcclusionCulling=p.occlusion;
        var extra=camera.GetUniversalAdditionalCameraData(); extra.renderType=type;
        extra.renderShadows=p.additional.m_RenderShadows;
        extra.requiresDepthOption=(CameraOverrideOption)p.additional.m_RequiresDepthTextureOption;
        extra.requiresColorOption=(CameraOverrideOption)p.additional.m_RequiresOpaqueTextureOption;
        extra.renderPostProcessing=p.additional.m_RenderPostProcessing;
        extra.antialiasing=(AntialiasingMode)p.additional.m_Antialiasing;
        extra.antialiasingQuality=(AntialiasingQuality)p.additional.m_AntialiasingQuality;
        extra.volumeLayerMask=p.additional.m_VolumeLayerMask.m_Bits;
        camera.SetVolumeFrameworkUpdateMode((VolumeFrameworkUpdateMode)p.additional.m_VolumeFrameworkUpdateModeOption);
        if(extra.clearDepth!=p.additional.m_ClearDepth) throw new InvalidOperationException("Unported clearDepth branch");
        extra.allowXRRendering=p.additional.m_AllowXRRendering;
        extra.allowHDROutput=p.additional.m_AllowHDROutput; extra.SetRenderer(p.additional.m_RendererIndex);
        if (p.hasRendererSettings) camera.gameObject.AddComponent<CameraRendererSettings>().Initialize(
            (CameraStackPostEffectGroup)p.rendererSettings.postEffectGroup,p.rendererSettings.useDepthTexture);
        return camera;
    }
    public void Enable()
    {
        BaseCamera.enabled=true;
        foreach(var camera in BaseCamera.GetUniversalAdditionalCameraData().cameraStack) camera.enabled=true;
    }
    public void EnableOverlaysForRequest()
    { foreach(var camera in BaseCamera.GetUniversalAdditionalCameraData().cameraStack) camera.enabled=true; }
    public void Dispose()
    {
        vignette?.Dispose();vignette=null;
        background?.Dispose();background=null;
        foreach(var go in objects.AsEnumerable().Reverse()) if(go) UnityEngine.Object.DestroyImmediate(go);
        objects.Clear();
        foreach(var b in bundles.AsEnumerable().Reverse()) if(b) b.Unload(true);
        bundles.Clear();
    }
}
