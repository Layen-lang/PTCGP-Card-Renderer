using UnityEngine;

// Asset3DRenderer.CreateRenderTexture1.7.5 RVA47553f8, used by CardRenderer.
// RR Detail passes useMipMap=false to both mip setters before Create.
public static class SourceCardRenderTarget
{
    public static RenderTexture Create(SourceRenderQuality.Level quality)
    {
        int size=SourceRenderQuality.CardTargetSize(quality);
        var target=new RenderTexture(size,size,24,RenderTextureFormat.Default);
        target.hideFlags=HideFlags.DontSave;
        target.useMipMap=false;
        target.autoGenerateMips=false;
        target.Create();
        return target;
    }
}
