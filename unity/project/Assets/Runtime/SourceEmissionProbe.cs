using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// Diagnostic of the original shaders' MRT outputs, not a URP replacement.
public static class SourceEmissionProbe
{
    public static void Run(Camera camera, Transform card, string output, List<string> report)
    {
        var tag = new ShaderTagId("EmissiveType");
        var shaders = new HashSet<Shader>();
        int always = 0, power = 0;
        foreach (var renderer in card.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials) {
                if (!material || !shaders.Add(material.shader)) continue;
                for (int pass = 0; pass < material.shader.passCount; pass++) {
                    string value = material.shader.FindPassTagValue(pass, tag).name;
                    report.Add("EmissiveTag shader=" + material.shader.name + " pass=" + pass + " value=" + value);
                    if (value == "Always") always++;
                    if (value == "EmissivePower") power++;
                }
            }
        report.Add("EmissiveTags uniqueShaders=" + shaders.Count + " Always=" + always + " EmissivePower=" + power);
        var color = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var emission = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var previousTarget = camera.targetTexture;
        var previousBackground = camera.backgroundColor;
        var effect = camera.GetComponent<AutomaticCameraProbe>();
        bool effectEnabled = effect && effect.enabled;
        try {
            if (effect) effect.enabled = false;
            color.Create(); emission.Create();
            camera.backgroundColor = Color.black;
            camera.SetTargetBuffers(new[] { color.colorBuffer, emission.colorBuffer }, color.depthBuffer);
            camera.Render();
            foreach (var row in new[] { new KeyValuePair<string,RenderTexture>("color",color), new KeyValuePair<string,RenderTexture>("emission",emission) }) {
                RenderTexture.active = row.Value;
                var image = new Texture2D(1024,1024,TextureFormat.RGBAFloat,false,true);
                try {
                    image.ReadPixels(new Rect(0,0,1024,1024),0,0);
                    image.Apply();
                    int positive = 0, positiveAlpha = 0; float maximum = 0, minimumAlpha = float.PositiveInfinity, maximumAlpha = float.NegativeInfinity;
                    var pixels = image.GetPixels();
                    foreach (var pixel in pixels) {
                        float intensity = Mathf.Max(pixel.r,Mathf.Max(pixel.g,pixel.b));
                        if (float.IsNaN(intensity) || float.IsInfinity(intensity)) throw new InvalidOperationException("Nonfinite MRT output");
                        if (float.IsNaN(pixel.a) || float.IsInfinity(pixel.a)) throw new InvalidOperationException("Nonfinite MRT alpha");
                        if (intensity > .00001f) positive++;
                        maximum = Mathf.Max(maximum,intensity);
                        if (pixel.a > .00001f) positiveAlpha++;
                        minimumAlpha = Mathf.Min(minimumAlpha,pixel.a);
                        maximumAlpha = Mathf.Max(maximumAlpha,pixel.a);
                    }
                    File.WriteAllBytes(Path.Combine(output,"mrt-"+row.Key+"-diagnostic.png"),image.EncodeToPNG());
                    for (int index = 0; index < pixels.Length; index++) {
                        float alpha = pixels[index].a;
                        pixels[index] = new Color(alpha, alpha, alpha, 1);
                    }
                    image.SetPixels(pixels); image.Apply();
                    File.WriteAllBytes(Path.Combine(output,"mrt-"+row.Key+"-alpha-diagnostic.png"),image.EncodeToPNG());
                    report.Add("SourceMRT "+row.Key+" positiveRGBPixels="+positive+" maxFloatRGB="+maximum.ToString("R")+" format=ARGBFloat diagnostic");
                    report.Add("SourceMRT "+row.Key+" positiveAlphaPixels="+positiveAlpha+" minAlpha="+minimumAlpha.ToString("R")+" maxAlpha="+maximumAlpha.ToString("R"));
                } finally { UnityEngine.Object.Destroy(image); }
            }
        } finally {
            RenderTexture.active = null;
            // Assigning targetTexture restores a single target after SetTargetBuffers.
            camera.targetTexture = previousTarget;
            camera.backgroundColor = previousBackground;
            if (effect) effect.enabled = effectEnabled;
            color.Release(); emission.Release();
            UnityEngine.Object.Destroy(color); UnityEngine.Object.Destroy(emission);
        }
    }
}
