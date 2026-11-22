using System.Collections.Generic;
using UnityEngine;

// CardURObject, original 1.7.5 LateUpdate / UpdateParams (47ebb88 / 47ebcec).
public sealed class SourceUrCardState : MonoBehaviour
{
    Renderer[] targets;
    MaterialPropertyBlock block;
    void Awake() { block=new MaterialPropertyBlock(); }
    static readonly int Front=Shader.PropertyToID("_Front"), Tilt=Shader.PropertyToID("_Tilt");
    public void Initialize(List<string> log)
    {
        var collected=new List<Renderer>(SourceCardEffects.Collect(transform,"Opaque-UR-Oklab"));
        foreach(var r in SourceCardEffects.Collect(transform,"Frame-2Layer-UR"))
            if(!collected.Contains(r)) collected.Add(r);
        targets=collected.ToArray(); Refresh();
        log.Add("OriginalCardEffect=CardURObject renderers="+targets.Length+" native=1.7.5");
    }
    void LateUpdate() { Refresh(); }
    public void Refresh()
    {
        if(targets==null) return;
        var front=transform.TransformDirection(Vector3.back);
        float sine=Mathf.Sin(3*Mathf.Atan2(new Vector2(front.x,front.y).magnitude,front.z));
        foreach(var r in targets) for(int index=0;index<r.sharedMaterials.Length;index++) {
            block.Clear(); r.GetPropertyBlock(block,index);
            if(block.isEmpty) r.GetPropertyBlock(block);
            block.SetVector(Front,new Vector4(front.x,front.y,front.z,0));
            block.SetFloat(Tilt,sine*sine);
            r.SetPropertyBlock(block,index);
        }
    }
}
