using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Selected source Detail/GrayGradient8. Standalone Viewer uses captured blur4;
// dynamic dialog routing and transitions are deliberately not inferred here.
public sealed class SourceDetailBackground : IDisposable
{
    [Serializable] class RectPlan {
        public Vector2 m_AnchorMin,m_AnchorMax,m_AnchoredPosition,m_SizeDelta,m_Pivot;
        public Vector3 m_LocalScale; public Quaternion m_LocalRotation;
    }
    [Serializable] class Gradient { public int direction,gradientStyle,offsetOption; public float rotation,offset1,offset2; public Color color1,color2; }
    [Serializable] class Icon { public RectPlan rect; public Color color; public string spriteName; public int type; public bool preserveAspect,useSpriteMesh,enabled,active; public float pixelsPerUnitMultiplier; }
    [Serializable] class Plan { public string branch; public int backgroundType; public bool routingValidated; public RectPlan rect; public Gradient gradient; public Color color; public Icon icon; }
    [Serializable] class Bundle { public string path,runtimeName; }
    [Serializable] class Manifest { public Bundle[] bundles; }
    [Serializable] class DefaultShaderManifest { public string path,runtimeName,shaderName,sourceShaderSha256; public bool originalShaderPayloadUnchanged; }
    readonly List<AssetBundle> owned=new();
    Material originalDefault;
    GameObject background;
    public string Branch { get; private set; }
    public SourceDetailBackground(Canvas canvas,string root)
    {
        try {
            var plan=JsonUtility.FromJson<Plan>(File.ReadAllText(Path.Combine(root,"runtime-assets/plans/background.json")));
            var g=plan.gradient;
            if(plan.backgroundType!=8 || g.direction!=2 || g.gradientStyle!=0 || g.offsetOption!=0 || g.offset1!=0 || g.offset2!=0)
                throw new InvalidOperationException("Unported source background branch");
            Branch=plan.branch;
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(root,"runtime-assets/bundles/dialog-background-player/manifest.json")));
            var available=AssetBundle.GetAllLoadedAssetBundles().ToList();
            foreach(var entry in manifest.bundles.Reverse()) {
                if(available.Any(b=>string.Equals(b.name,entry.runtimeName,StringComparison.OrdinalIgnoreCase))) continue;
                var bundle=AssetBundle.LoadFromFile(Path.Combine(root,entry.path));
                if(!bundle) throw new InvalidOperationException("Original background bundle unavailable: "+entry.runtimeName);
                owned.Add(bundle); available.Add(bundle);
            }
            var sprites=available.SelectMany(b=>b.LoadAllAssets<Sprite>()).Where(s=>s.name==plan.icon.spriteName).Distinct().ToArray();
            if(sprites.Length!=1 || !sprites[0].texture) throw new InvalidOperationException("Original background sprite not uniquely loaded");
            var shaderPlan=JsonUtility.FromJson<DefaultShaderManifest>(File.ReadAllText(Path.Combine(root,"runtime-assets/bundles/default-ui-player/manifest.json")));
            if(!shaderPlan.originalShaderPayloadUnchanged || shaderPlan.shaderName!="UI/Default") throw new InvalidOperationException("Original default UI shader provenance missing");
            var shaderBundle=AssetBundle.LoadFromFile(Path.Combine(root,shaderPlan.path));
            if(!shaderBundle) throw new InvalidOperationException("Original default UI shader bundle unavailable");
            owned.Add(shaderBundle);
            var shaders=shaderBundle.LoadAllAssets<Shader>();
            if(shaders.Length!=1 || shaders[0].name!=shaderPlan.shaderName || !shaders[0].isSupported)
                throw new InvalidOperationException("Original default UI shader unsupported");
            // The source prefab's material is null, so the game resolves Canvas'
            // default material. Use its original shader bytes and serialized
            // property defaults locally without mutating the engine-wide cache.
            originalDefault=new Material(shaders[0]){name="OriginalUI_Default175"};
            Debug.Log("OriginalDefaultUIShader="+shaders[0].name+" payloadSha256="+shaderPlan.sourceShaderSha256+" keywords="+string.Join(",",originalDefault.shaderKeywords));
            background=new GameObject("gray_gradient_bg",typeof(RectTransform),typeof(Image));
            background.layer=5;
            ApplyRect(background.GetComponent<RectTransform>(),canvas.transform,plan.rect);
            background.transform.SetAsFirstSibling();
            var image=background.GetComponent<Image>();image.material=originalDefault;image.color=plan.color;image.raycastTarget=false;
            var gradient=background.AddComponent<SourceAngleGradient>();gradient.First=g.color1;gradient.Second=g.color2;gradient.Degrees=g.rotation;
            image.SetVerticesDirty();
            var child=new GameObject("icon_img",typeof(RectTransform),typeof(Image));child.layer=5;
            ApplyRect(child.GetComponent<RectTransform>(),background.transform,plan.icon.rect);
            var icon=child.GetComponent<Image>();icon.material=originalDefault;icon.sprite=sprites[0];icon.color=plan.icon.color;
            icon.type=(Image.Type)plan.icon.type;icon.preserveAspect=plan.icon.preserveAspect;
            icon.pixelsPerUnitMultiplier=plan.icon.pixelsPerUnitMultiplier;icon.useSpriteMesh=plan.icon.useSpriteMesh;
            icon.raycastTarget=false;icon.enabled=plan.icon.enabled;
            // ShowAsync selects the parent background object only. The source
            // icon GameObject is inactive; default fade clips animate only the
            // root CanvasGroup alpha, with no child activation curves.
            child.SetActive(plan.icon.active);
            foreach(var graphic in new Graphic[]{image,icon}) {
                var shader=graphic.materialForRendering.shader;
                Debug.Log("SourceBackgroundShader="+shader.name+" supported="+shader.isSupported+" passes="+graphic.materialForRendering.passCount);
                if(shader.name!="UI/Default" || !shader.isSupported)
                    throw new InvalidOperationException("Source default UGUI shader unavailable in player");
            }
        } catch {Dispose();throw;}
    }
    static void ApplyRect(RectTransform rect,Transform parent,RectPlan plan)
    {
        rect.SetParent(parent,false);rect.anchorMin=plan.m_AnchorMin;rect.anchorMax=plan.m_AnchorMax;
        rect.pivot=plan.m_Pivot;rect.sizeDelta=plan.m_SizeDelta;rect.anchoredPosition=plan.m_AnchoredPosition;
        rect.localScale=plan.m_LocalScale;rect.localRotation=plan.m_LocalRotation;
    }
    public static void VerifyPresentationPixels(Color32[] pixels)
    {
        // Nonempty output also accepts Unity's magenta error shader. Reject a
        // large error-colored area independently of any game reference image.
        int errors=pixels.Count(p=>p.r>=250 && p.g<=5 && p.b>=250);
        if(errors>pixels.Length/10) throw new InvalidOperationException("Presentation contains a large magenta shader-error area");
        if(pixels.Any(p=>p.a!=255)) throw new InvalidOperationException("Selected opaque Detail background did not cover the presentation");
    }
    public void Dispose()
    {
        if(background) UnityEngine.Object.DestroyImmediate(background);
        if(originalDefault) UnityEngine.Object.DestroyImmediate(originalDefault);
        foreach(var bundle in owned.AsEnumerable().Reverse()) if(bundle) bundle.Unload(true);
        owned.Clear();
    }
}
