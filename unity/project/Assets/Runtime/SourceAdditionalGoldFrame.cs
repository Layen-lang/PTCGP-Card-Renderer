using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Original AdditionalFrameLoader.LoadDetailGoldFrame / DynamicCardParts.
// UI capture comes from the selected original design in the prepared config.
// Switching frames and restoring UI within a session remain unvalidated.
public sealed class SourceAdditionalGoldFrame : MonoBehaviour
{
    [Serializable] public class Plan {
        public string scope, frameId, originalDesign;
        public SourceUiPlan.Asset template, panelMaterial;
        public int[] panelIndices;
        public CardPreview.Binding[] bindings;
    }
    readonly List<Transform> swapped = new List<Transform>();
    GameObject gold;

    static T Resolve<T>(SourceUiPlan.Asset asset, IDictionary<string,AssetBundle> bundles) where T:UnityEngine.Object {
        var found=bundles[asset.bundle].LoadAllAssets<T>().Where(a=>a.name==asset.name).ToArray();
        if(found.Length!=1) throw new InvalidOperationException("Ambiguous gold asset "+asset.sourceId+" "+asset.name);
        return found[0];
    }
    public static void Attach(Transform face, string json, IDictionary<string,AssetBundle> bundles,
        RenderTexture[] textures, List<string> log) {
        var plan=JsonUtility.FromJson<Plan>(json);
        if(plan.scope!="L" || plan.frameId!="GOLD_FRAME_100010") throw new InvalidOperationException("Unsupported frame source");
        var port=face.gameObject.AddComponent<SourceAdditionalGoldFrame>();
        port.gold=Instantiate(Resolve<GameObject>(plan.template,bundles),face);
        var originalChildren=new List<Transform>();
        foreach(Transform child in face) if(child!=port.gold.transform) originalChildren.Add(child);
        // Apply uses direct children and the first equal GameObject name.
        foreach(Transform child in port.gold.transform) {
            var original=originalChildren.FirstOrDefault(t=>t.name==child.name);
            if(original) { original.gameObject.SetActive(false);port.swapped.Add(original); }
        }
        var panel=port.gold.transform;
        foreach(int index in plan.panelIndices) panel=panel.GetChild(index);
        var renderer=panel.GetComponent<Renderer>();
        if(!renderer) throw new InvalidOperationException("Original gold panel renderer absent");
        // Apply leaves the original serialized panel untouched when the
        // design has no enabled override (the Trainer gold branch).
        if(plan.panelMaterial!=null && !string.IsNullOrEmpty(plan.panelMaterial.bundle))
            renderer.material=Resolve<Material>(plan.panelMaterial,bundles);
        CardPreview.ApplyDynamicTextures(port.gold.transform,plan.bindings,textures);
        log.Add("AdditionalGoldFrame design="+plan.originalDesign+" swapped="+port.swapped.Count+" panel="+renderer.sharedMaterial.name+" bindings="+plan.bindings.Length+" fidelityValidated=False");
    }
    public void RestoreOriginalParts() {
        // Original Revert reactivates stored transforms. This is the geometry
        // step; it does not recapture the original design's UI.
        foreach(var child in swapped) if(child) child.gameObject.SetActive(true);
        swapped.Clear();
        if(gold) Destroy(gold);
    }
}
