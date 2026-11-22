using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// LtUIVignette inherits Graphic's quad generation. Original Awake creates the
// LtUI/Vignette material; SetupVignette passes RGB/aspect and center/3I/5S.
public sealed class SourceVignetteGraphic : Graphic { }

public sealed class SourceScreenVignette : IDisposable
{
    [Serializable] class Scaler { public int uiScaleMode,screenMatchMode; public float[] referenceResolution; public float referencePixelsPerUnit; }
    [Serializable] class CanvasPlan { public float m_PlaneDistance; public bool m_PixelPerfect; public int m_AdditionalShaderChannelsFlag,m_SortingOrder; }
    [Serializable] class Plan { public string shaderName; public Vector2 center; public float intensity,smoothness; public bool rounded; public Color color; public Scaler scaler; public CanvasPlan canvas; }
    [Serializable] class Manifest { public string path,runtimeName,shaderName,sourceShaderSha256; public bool originalShaderPayloadUnchanged; }
    GameObject canvasObject,graphicObject;
    Material material;
    AssetBundle bundle;
    readonly Vector3[] corners=new Vector3[4];
    readonly Plan plan;
    readonly SourceVignetteGraphic graphic;
    public Vector4 Param1 { get; private set; }
    public Vector4 Param2 { get; private set; }
    public SourceScreenVignette(Camera camera,string root,RenderTexture output)
    {
        try {
            plan=JsonUtility.FromJson<Plan>(File.ReadAllText(Path.Combine(root,"runtime-assets/plans/vignette.json")));
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(root,"runtime-assets/bundles/vignette-player/manifest.json")));
            if(plan.shaderName!="LtUI/Vignette" || !manifest.originalShaderPayloadUnchanged || manifest.shaderName!=plan.shaderName)
                throw new InvalidOperationException("Unexpected original screen vignette shader provenance");
            if(plan.scaler.uiScaleMode!=1 || plan.scaler.screenMatchMode!=1)
                throw new InvalidOperationException("Unsupported screen vignette scaler");
            bundle=AssetBundle.LoadFromFile(Path.Combine(root,manifest.path));
            if(!bundle || bundle.name!=manifest.runtimeName) throw new InvalidOperationException("Original vignette bundle unavailable");
            var shader=bundle.LoadAllAssets<Shader>().Single();
            if(shader.name!=plan.shaderName || !shader.isSupported) throw new InvalidOperationException("Original vignette shader unsupported");
            material=new Material(shader) { name="Original ScreenVignette175" };
            canvasObject=new GameObject("CommonEffectCanvas",typeof(RectTransform),typeof(Canvas));
            canvasObject.layer=5;
            var canvas=canvasObject.GetComponent<Canvas>();
            canvas.renderMode=RenderMode.ScreenSpaceCamera; canvas.worldCamera=camera;
            canvas.planeDistance=plan.canvas.m_PlaneDistance; canvas.pixelPerfect=plan.canvas.m_PixelPerfect;
            canvas.additionalShaderChannels=(AdditionalCanvasShaderChannels)plan.canvas.m_AdditionalShaderChannelsFlag;
            canvas.sortingOrder=plan.canvas.m_SortingOrder; canvas.referencePixelsPerUnit=plan.scaler.referencePixelsPerUnit;
            if(output) canvas.scaleFactor=Mathf.Min(output.width/plan.scaler.referenceResolution[0],output.height/plan.scaler.referenceResolution[1]);
            else {
                var scaler=canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution=new Vector2(plan.scaler.referenceResolution[0],plan.scaler.referenceResolution[1]);
                scaler.screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
                scaler.referencePixelsPerUnit=plan.scaler.referencePixelsPerUnit;
            }
            graphicObject=new GameObject("ScreenVignette",typeof(RectTransform),typeof(CanvasRenderer),typeof(SourceVignetteGraphic));
            graphicObject.layer=5;
            var rect=graphicObject.GetComponent<RectTransform>(); rect.SetParent(canvasObject.transform,false);
            rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one; rect.pivot=new Vector2(.5f,.5f);
            rect.anchoredPosition=Vector2.zero; rect.sizeDelta=Vector2.zero;
            graphic=graphicObject.GetComponent<SourceVignetteGraphic>();
            graphic.color=plan.color; graphic.material=material; graphic.raycastTarget=false;
            UnityEngine.Canvas.ForceUpdateCanvases();
            RefreshSourceUniforms();
        } catch { Dispose(); throw; }
    }
    public void RefreshSourceUniforms()
    {
        graphic.rectTransform.GetWorldCorners(corners);
        Vector2 min=corners[0],max=corners[0];
        for(int i=1;i<4;i++) { min=Vector2.Min(min,corners[i]); max=Vector2.Max(max,corners[i]); }
        if(max.y<=min.y) throw new InvalidOperationException("Screen vignette world rectangle is empty");
        float aspect=plan.rounded ? (max.x-min.x)/(max.y-min.y) : 1;
        Param1=new Vector4(graphic.color.r,graphic.color.g,graphic.color.b,aspect);
        Param2=new Vector4(plan.center.x,plan.center.y,plan.intensity*3,plan.smoothness*5);
        material.SetVector("_VignetteParam1",Param1); material.SetVector("_VignetteParam2",Param2);
    }
    public void Dispose()
    {
        if(graphicObject) UnityEngine.Object.DestroyImmediate(graphicObject);
        if(canvasObject) UnityEngine.Object.DestroyImmediate(canvasObject);
        if(material) UnityEngine.Object.DestroyImmediate(material);
        if(bundle) bundle.Unload(true);
    }
}
