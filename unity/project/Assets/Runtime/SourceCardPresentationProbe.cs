using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Native URP experiment: isolates the final original material/quad, without claiming the main UI
// renderer or dialog/safe-area/animation orchestration has been ported.
public static class SourceCardPresentationProbe
{
    [Serializable] class Scaler { public int uiScaleMode,screenMatchMode; public float[] referenceResolution; public float referencePixelsPerUnit,dynamicPixelsPerUnit; }
    [Serializable] class Plan { public Scaler scaler; public float cardWidth,cardHeight; public int fit,probeWidth,probeHeight; public bool useMipMap; public string probePresentationCaveat; }
    [Serializable] class Bundle { public string path,blob; }
    [Serializable] class Manifest { public Bundle[] bundles; }
    public static void Run(RenderTexture cardTexture,string root,string output,List<string> log)
    {
        var plan=JsonUtility.FromJson<Plan>(File.ReadAllText(Path.Combine(root,"runtime-assets/plans/presentation.json")));
        if (plan.scaler.uiScaleMode!=1 || plan.scaler.screenMatchMode!=1 || plan.fit!=0 || plan.useMipMap)
            throw new InvalidOperationException("Unported presentation branch");
        var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(root,"runtime-assets/bundles/presentation-player/manifest.json")));
        var loaded=new List<AssetBundle>();
        GameObject canvasObject=null,cameraObject=null;
        RenderTexture target=null;
        var previousPipeline=GraphicsSettings.defaultRenderPipeline;
        var previousQuality=QualitySettings.renderPipeline;
        var global=GraphicsSettings.GetSettingsForRenderPipeline<UnityEngine.Rendering.Universal.UniversalRenderPipeline>();
        var previousActive=RenderTexture.active;
        try {
            // Preserve dependency identities and unchanged shader/material payloads.
            foreach (var b in manifest.bundles.Reverse()) {
                var bundle=AssetBundle.LoadFromFile(Path.Combine(root,b.path));
                if (!bundle) throw new InvalidOperationException("Presentation bundle failed: "+b.blob);
                loaded.Add(bundle);
            }
            var material=loaded.SelectMany(b=>b.LoadAllAssets<Material>()).Single(m=>m.name=="UI_Default_From_RT");
            if (material.shader.name!="Lettuce/Common/CardNew/UI/Default(from RT)" || !material.shader.isSupported)
                throw new InvalidOperationException("Original UI presentation shader unavailable");
            if (material.GetFloat("_UseFXAA")!=0) throw new InvalidOperationException("Unexpected original FXAA material default");
            GraphicsSettings.defaultRenderPipeline=null; QualitySettings.renderPipeline=null;
            target=new RenderTexture(plan.probeWidth,plan.probeHeight,24,RenderTextureFormat.ARGB32);
            target.Create();
            cameraObject=new GameObject("Original final UI material probe",typeof(Camera));
            var camera=cameraObject.GetComponent<Camera>();
            camera.enabled=false; camera.targetTexture=target;
            camera.orthographic=true; camera.orthographicSize=5;
            camera.nearClipPlane=.3f; camera.farClipPlane=1000; camera.fieldOfView=60;
            camera.allowHDR=false; camera.allowMSAA=false; camera.cullingMask=(1<<5)|(1<<26);
            // The original UI camera clears depth. This isolated probe supplies
            // transparent color instead of the unported main scene/background.
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.clear;
            canvasObject=new GameObject("Original card presentation canvas",typeof(RectTransform),typeof(Canvas));
            canvasObject.layer=5;
            var canvas=canvasObject.GetComponent<Canvas>();
            canvas.renderMode=RenderMode.ScreenSpaceCamera; canvas.worldCamera=camera;
            canvas.planeDistance=100; canvas.pixelPerfect=false; canvas.additionalShaderChannels=0;
            // CanvasScaler Expand, applied to target pixels rather than the
            // hidden batch window's unrelated dimensions.
            canvas.scaleFactor=Mathf.Min(plan.probeWidth/plan.scaler.referenceResolution[0],plan.probeHeight/plan.scaler.referenceResolution[1]);
            canvas.referencePixelsPerUnit=plan.scaler.referencePixelsPerUnit;
            var cardObject=new GameObject("Card image",typeof(RectTransform),typeof(RawImage));
            cardObject.layer=5;
            var rect=cardObject.GetComponent<RectTransform>(); rect.SetParent(canvasObject.transform,false);
            rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);
            float vertical=(.88f*.5f)/.6026955842971802f;
            // Established detail-view size after UICardView.UpdateCardSize,
            // original1.7.5 RVA47ffc48: height / VerticalPercentage.
            float side=plan.cardHeight/vertical;
            rect.sizeDelta=new Vector2(side,side);
            var image=cardObject.GetComponent<RawImage>();
            image.material=material; image.texture=cardTexture; image.raycastTarget=false; image.color=Color.white;
            Canvas.ForceUpdateCanvases(); camera.Render();
            RenderTexture.active=target;
            var png=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            try {
                png.ReadPixels(new Rect(0,0,target.width,target.height),0,0); png.Apply();
                int visible=png.GetPixels32().Count(p=>p.a>0 && (p.r>0 || p.g>0 || p.b>0));
                if (visible<1000) throw new InvalidOperationException("Original UI presentation output empty");
                File.WriteAllBytes(Path.Combine(output,"angle-0-presentation.png"),png.EncodeToPNG());
                log.Add("OriginalPresentation shader="+material.shader.name+" scale="+canvas.scaleFactor.ToString("R")+" logicalSide="+side.ToString("R")+" frame="+target.width+"x"+target.height+" visibleRGB="+visible+" sourceRT="+cardTexture.width+" filter="+cardTexture.filterMode+" alpha=originalShaderInverse fxaa=materialDefault0");
                log.Add("OriginalPresentationCaveat="+plan.probePresentationCaveat);
            } finally { UnityEngine.Object.DestroyImmediate(png); }
        } finally {
            RenderTexture.active=previousActive;
            if (canvasObject) UnityEngine.Object.DestroyImmediate(canvasObject);
            if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
            if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            foreach (var b in loaded.AsEnumerable().Reverse()) b.Unload(true);
            if (global) GraphicsSettings.TrySetCurrentRenderPipelineGlobalSettings(global);
            GraphicsSettings.defaultRenderPipeline=previousPipeline; QualitySettings.renderPipeline=previousQuality;
        }
    }
}
