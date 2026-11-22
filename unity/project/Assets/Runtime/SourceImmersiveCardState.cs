using System.Collections.Generic;
using UnityEngine;

// CardImmersiveObject, original native 1.7.5 RVA 0x47e6ca0.
// Owns the card shader state; immersive movie playback is a separate controller.
public sealed class SourceImmersiveCardState : MonoBehaviour
{
    public float IllustAlpha,TextAlpha,EmitSwitch,EmitIntensity,Darkness;
    Renderer[] illustration,text,emission;
    MaterialPropertyBlock block;
    static readonly int IllustAlphaId=Shader.PropertyToID("_IllustAlpha");
    static readonly int TextAlphaId=Shader.PropertyToID("_Alpha");
    static readonly int SwitchId=Shader.PropertyToID("_Switch");
    static readonly int IntensityId=Shader.PropertyToID("_OnIntensity");
    static readonly int DarknessId=Shader.PropertyToID("_Darkness");
    void Awake() { block=new MaterialPropertyBlock(); }
    public void Initialize(SourceCardEffects.Effect source,List<string> log)
    {
        IllustAlpha=source.IllustAlpha; TextAlpha=source.TextAlpha;
        EmitSwitch=source.EmitSwitch; EmitIntensity=source.EmitIntensity; Darkness=source.Darkness;
        illustration=SourceCardEffects.Collect(transform,"Card_Parallax_Immersive");
        text=SourceCardEffects.Collect(transform,"Text_Alpha");
        emission=SourceCardEffects.Collect(transform,"Frame-Holo-Immersive");
        Apply();
        log.Add("OriginalCardEffect=CardImmersiveObject sourceId="+source.sourceId
            +" illustration="+illustration.Length+" text="+text.Length+" emission="+emission.Length
            +" state="+IllustAlpha+","+TextAlpha+","+EmitSwitch+","+EmitIntensity+","+Darkness+" native=1.7.5");
    }
    void LateUpdate() { if(illustration!=null) Apply(); }
    void Apply()
    {
        foreach(var renderer in illustration) {
            block.Clear(); renderer.GetPropertyBlock(block);
            block.SetFloat(IllustAlphaId,IllustAlpha); renderer.SetPropertyBlock(block);
        }
        foreach(var renderer in text) {
            block.Clear(); renderer.GetPropertyBlock(block);
            block.SetFloat(TextAlphaId,TextAlpha); renderer.SetPropertyBlock(block);
        }
        foreach(var renderer in emission) for(int index=0;index<renderer.sharedMaterials.Length;index++) {
            block.Clear(); renderer.GetPropertyBlock(block,index);
            if(block.isEmpty) renderer.GetPropertyBlock(block);
            block.SetFloat(SwitchId,EmitSwitch); block.SetFloat(IntensityId,EmitIntensity);
            block.SetFloat(DarknessId,Darkness); renderer.SetPropertyBlock(block,index);
        }
    }
}
