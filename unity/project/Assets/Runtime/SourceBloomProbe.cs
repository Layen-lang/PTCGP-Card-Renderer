using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// Diagnostic only. The game's RenderGraph and atlas meshes are not ported here.
public static class SourceBloomProbe
{
    public static void Run(Shader shader, string output, List<string> report)
    {
        var material = new Material(shader);
        var input = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
        var pixels = new Color[64];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(.5f, .25f, .75f, 1);
        input.SetPixels(pixels);
        input.Apply();
        var target = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var readback = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
        try {
            foreach (int pass in new[] { 0, 6 }) {
                var clear = new Color(.125f, .125f, .125f, .6f);
                using (var command = new CommandBuffer { name = "Original bloom shader diagnostic" }) {
                    command.SetRenderTarget(target);
                    command.ClearRenderTarget(false, true, clear);
                    command.SetGlobalTexture(pass == 0 ? "_BlitTexture" : "_BloomTex", input);
                    command.SetGlobalVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
                    command.SetGlobalVector("_GlobalMipBias", Vector4.zero);
                    command.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, 3);
                    Graphics.ExecuteCommandBuffer(command);
                }
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                readback.Apply();
                RenderTexture.active = null;
                var expected = pass == 0 ? new Color(Linear(.5f), Linear(.25f), Linear(.75f), 1)
                    : new Color(.625f, .375f, .875f, .6f);
                float error = 0;
                foreach (var actual in readback.GetPixels()) for (int channel = 0; channel < 4; channel++)
                    error = Mathf.Max(error, Mathf.Abs(actual[channel] - expected[channel]));
                if (error > .00001f) throw new InvalidOperationException("Original bloom pass " + pass + " error=" + error);
                report.Add("SourceBloomPass=" + pass + " pixels=64 maxFloatError=" + error.ToString("R"));
                File.WriteAllBytes(Path.Combine(output, "bloom-pass-" + pass + "-diagnostic.png"), readback.EncodeToPNG());
            }
        } finally {
            RenderTexture.active = null;
            target.Release();
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(readback);
            UnityEngine.Object.Destroy(input);
            UnityEngine.Object.Destroy(material);
        }
    }

    // Constants read from original pass0 SPIR-V, not a replacement bloom effect.
    static float Linear(float x) { return x * (x * (x * .3053060173988342285f + .6821711063385009766f) + .012522878125309944f); }
}
