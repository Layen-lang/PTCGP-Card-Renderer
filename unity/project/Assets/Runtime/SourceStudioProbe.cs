using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Exercises recovered studio camera/target settings separately from presentation.
// Custom URP draw passes and text quality are not yet implemented here.
public static class SourceStudioProbe
{
    public static void Run(Camera camera, string output, List<string> report)
    {
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        var background = camera.backgroundColor;
        float near = camera.nearClipPlane, far = camera.farClipPlane;
        bool hdr = camera.allowHDR, msaa = camera.allowMSAA, occlusion = camera.useOcclusionCulling;
        var effect = camera.GetComponent<AutomaticCameraProbe>();
        bool effectEnabled = effect && effect.enabled;
        try {
            if (effect) effect.enabled = false;
            camera.nearClipPlane = .3f;
            camera.farClipPlane = 1000f;
            camera.backgroundColor = Color.black;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.useOcclusionCulling = false;
            foreach (SourceRenderQuality.Level level in Enum.GetValues(typeof(SourceRenderQuality.Level))) {
                int size = SourceRenderQuality.CardTargetSize(level);
                var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                // Mipmap argument of the final native call remains to be traced.
                var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
                try {
                    target.Create();
                    camera.targetTexture = target;
                    camera.Render();
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(Path.Combine(output, "studio-"+level.ToString().ToLowerInvariant()+"-diagnostic.png"), image.EncodeToPNG());
                    report.Add("SourceStudio quality="+level+" cardFactor="+SourceRenderQuality.CardFactor(level)+" textFactor="+SourceRenderQuality.TextFactor(level)+" target="+size+"x"+size+" near=0.3 far=1000 HDR=True format=ARGB32 depth24 pipeline=builtin diagnostic textQualityNotApplied");
                } finally {
                    camera.targetTexture = previousTarget;
                    RenderTexture.active = previousActive;
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                    UnityEngine.Object.Destroy(image);
                }
            }
        } finally {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            camera.backgroundColor = background;
            camera.nearClipPlane = near; camera.farClipPlane = far;
            camera.allowHDR = hdr; camera.allowMSAA = msaa;
            camera.useOcclusionCulling = occlusion;
            if (effect) effect.enabled = effectEnabled;
        }
    }
}
