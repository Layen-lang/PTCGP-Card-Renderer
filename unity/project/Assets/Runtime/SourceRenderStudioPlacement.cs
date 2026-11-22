using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Original RenderStudio.Awake: position=(RecyclableIndex.Get(this)*10,-100,0).
// Indices are observed in the reference context, not fitted to reference pixels.
public static class SourceRenderStudioPlacement
{
    [Serializable] class Plan { public string sourceVersion, nativeAwakeRva; public float spacing, isolationY; public int uiIndex, modelIndex; }
    static Plan Read(string root) {
        var p=JsonUtility.FromJson<Plan>(File.ReadAllText(Path.Combine(root,"runtime-assets/plans/studio-placement.json")));
        if(p.sourceVersion!="1.7.5" || p.nativeAwakeRva!="0x4758370" || p.spacing!=10 || p.isolationY!=-100 || p.uiIndex<0 || p.modelIndex<0 || p.uiIndex==p.modelIndex)
            throw new InvalidOperationException("Original studio placement plan mismatch");
        return p;
    }
    static Transform Create(string name,Plan plan,int index,List<string> report) {
        var studio=new GameObject(name).transform;
        studio.position=new Vector3(index*plan.spacing,plan.isolationY,0);
        report.Add("OriginalStudioPlacement name="+name+" index="+index+" position="+studio.position.ToString("R")+" allocationContext=ArticunoReference");
        return studio;
    }
    public static void AttachUi(Canvas canvas,Camera camera,GameObject ui,string root,List<string> report) {
        var p=Read(root);
        var studio=Create("UIRenderStudio",p,p.uiIndex,report);
        camera.transform.SetParent(studio,false);
        camera.name="Camera";
        camera.gameObject.layer=17;
        canvas.transform.SetParent(studio,false);
        canvas.name="Canvas";
        var parent=new GameObject("Root",typeof(RectTransform)).GetComponent<RectTransform>();
        parent.gameObject.layer=17;
        parent.SetParent(canvas.transform,false);
        parent.anchorMin=Vector2.zero;parent.anchorMax=Vector2.one;
        parent.anchoredPosition=Vector2.zero;parent.sizeDelta=Vector2.zero;
        parent.pivot=new Vector2(.5f,.5f);
        ui.transform.SetParent(parent,false);
    }
    public static void AttachModel(Camera camera,Transform assetRoot,string root,List<string> report) {
        var p=Read(root);
        var studio=Create("ModelRenderStudio",p,p.modelIndex,report);
        camera.transform.SetParent(studio,false);
        var parent=new GameObject("Root").transform;
        parent.SetParent(studio,false);
        assetRoot.SetParent(parent,false);
        report.Add("OriginalModelStudio cameraWorld="+camera.transform.position.ToString("R")+" assetWorld="+assetRoot.position.ToString("R")+" prefabRoot=Root");
    }
}
