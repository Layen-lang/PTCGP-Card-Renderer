using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

public static class SourceBloomAtlasProbe
{
    public static void Run(Shader shader, string output, List<string> report)
    {
        using (var atlas = new SourceBloomAtlas(new Vector2Int(455, 455), 5, .5f, 1)) {
            var material = new Material(shader);
            var inputs = new Texture2D[5];
            var target = new RenderTexture(atlas.size.x, atlas.size.y, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(atlas.size.x, atlas.size.y, TextureFormat.RGBAFloat, false, true);
            var reconstruction = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var reconstructed = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
            try {
                using (var command = new CommandBuffer { name = "Source bloom atlas diagnostic" }) {
                    for (int i = 0; i < inputs.Length; i++) {
                        inputs[i] = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                        inputs[i].SetPixel(0, 0, new Color((i+1) * .125f, .25f, .5f, 1));
                        inputs[i].Apply();
                        material.SetTexture("_DownSampling" + (i+1) + "Tex", inputs[i]);
                    }
                    command.SetRenderTarget(target);
                    command.ClearRenderTarget(false, true, Color.magenta);
                    command.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    command.DrawMesh(atlas.imageToSheet, Matrix4x4.identity, material, 0, 2);
                    Graphics.ExecuteCommandBuffer(command);
                }
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0,0,target.width,target.height),0,0);
                readback.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(output, "bloom-atlas-diagnostic.png"), readback.EncodeToPNG());
                var counts = new int[5];
                foreach (Color pixel in readback.GetPixels()) {
                    if (pixel.r == 0 && pixel.g == 0 && pixel.b == 0) continue;
                    int level = Mathf.RoundToInt(pixel.r / .125f) - 1;
                    if (level < 0 || level >= 5 || Mathf.Abs(pixel.g-.25f) > .00001f || Mathf.Abs(pixel.b-.5f) > .00001f)
                        throw new InvalidOperationException("Unexpected original atlas shader output: " + pixel);
                    counts[level]++;
                }
                for (int i = 0; i < 5; i++) {
                    int expected = atlas.levels[i].width * atlas.levels[i].height;
                    if (counts[i] != expected) throw new InvalidOperationException("Atlas level " + (i+1) + " pixels=" + counts[i] + " expected=" + expected);
                    report.Add("SourceBloomAtlas level=" + (i+1) + " pixels=" + counts[i] + " rect=" + atlas.levels[i] + " weight=" + atlas.weights[i].ToString("R"));
                }
                material.SetTexture("_MainTex", target);
                using (var command = new CommandBuffer { name = "Source bloom reconstruction diagnostic" }) {
                    command.SetRenderTarget(reconstruction);
                    command.ClearRenderTarget(false, true, Color.magenta);
                    command.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    command.DrawMesh(atlas.sheetToImage, Matrix4x4.identity, material, 0, 5);
                    Graphics.ExecuteCommandBuffer(command);
                }
                RenderTexture.active = reconstruction;
                reconstructed.ReadPixels(new Rect(0,0,64,64),0,0);
                reconstructed.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(output, "bloom-reconstruction-diagnostic.png"), reconstructed.EncodeToPNG());
                float expectedRed = 0;
                for (int i = 0; i < 5; i++) expectedRed += .125f * (i+1) * atlas.weights[i];
                Color actual = reconstructed.GetPixel(32,32);
                if (Mathf.Abs(actual.r-expectedRed) > .00001f || Mathf.Abs(actual.g-.25f) > .00001f || Mathf.Abs(actual.b-.5f) > .00001f || Mathf.Abs(actual.a-5) > .00001f)
                    throw new InvalidOperationException("Source bloom reconstruction center=" + actual + " expectedRed=" + expectedRed);
                report.Add("SourceBloomReconstruction center=" + actual + " expectedRed=" + expectedRed.ToString("R") + " floatAlpha=5 (unclamped diagnostic target)");
            } finally {
                RenderTexture.active = null;
                foreach (var texture in inputs) if (texture) UnityEngine.Object.Destroy(texture);
                target.Release(); reconstruction.Release();
                UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(reconstruction);
                UnityEngine.Object.Destroy(material); UnityEngine.Object.Destroy(readback); UnityEngine.Object.Destroy(reconstructed);
            }
        }
    }
}
