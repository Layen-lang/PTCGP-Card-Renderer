using System;
using System.Collections.Generic;
using UnityEngine;

// CardRGBObject 1.8.1, OnEnable/ApplyParams at 0x4a32ef0/0x4a3303c.
// Exact coefficients and property/tag literals: evidence/v1.8.1/native-rgb/.
public sealed class SourceRgbCardState : MonoBehaviour
{
    static readonly string[] Colors = { "_AreaAColor", "_AreaBColor", "_UIColorR", "_UIColorG" };
    static readonly string[] Vectors = { "_AreaAColorOklab", "_AreaBColorOklab", "_UIColorROklab", "_UIColorGOklab" };
    readonly List<Material> materials = new List<Material>();
    MaterialPropertyBlock block;
    Renderer[] targets;
    bool initialized;

    [Serializable] sealed class Sample {
        public string renderer, material, property;
        public Color source;
        public Vector4 value;
    }

    public void Initialize(List<string> log)
    {
        initialized = true;
        if (isActiveAndEnabled) { Validate(); Apply(log); }
        log.Add("OriginalCardEffect=CardRGBObject renderers=" + (targets == null ? 0 : targets.Length)
            + " enabled=" + enabled + " native=1.8.1 lifecycle=OnEnable");
    }

    void OnEnable() { if (initialized) { Validate(); Apply(null); } }

    void Validate()
    {
        if (block == null) block = new MaterialPropertyBlock();
        if (targets == null) targets = SourceCardEffects.Collect(transform, "Card-RGB");
    }

    void Apply(List<string> log)
    {
        foreach (var renderer in targets) {
            if (!renderer) continue;
            // The original uses one renderer-wide MPB, preserving UI textures
            // and allowing later materials to overwrite earlier colour values.
            renderer.GetPropertyBlock(block);
            var ui = block.GetTexture("_DynamicUITex");
            renderer.GetSharedMaterials(materials);
            foreach (var material in materials) {
                if (!material) continue;
                for (int i = 0; i < Colors.Length; i++) {
                    if (!material.HasProperty(Colors[i])) continue;
                    var source = material.GetColor(Colors[i]);
                    var converted = Srgb2Oklab(source);
                    var value = new Vector4(converted.x, converted.y, converted.z, 0);
                    block.SetVector(Vectors[i], value);
                    if (log != null) log.Add("OriginalRgbColor=" + JsonUtility.ToJson(new Sample {
                        renderer=renderer.name, material=material.name,
                        property=Vectors[i], source=source, value=value
                    }));
                }
            }
            renderer.SetPropertyBlock(block);
            renderer.GetPropertyBlock(block);
            if (block.GetTexture("_DynamicUITex") != ui)
                throw new InvalidOperationException("RGB setup replaced original dynamic UI texture");
        }
    }

    static float SrgbToLinear(float v) { return v < 0.04045f
        ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f); }
    static float CubeRoot(float v) { return v < 0f
        ? -Mathf.Pow(-v, 0.3333333432674408f) : Mathf.Pow(v, 0.3333333432674408f); }
    public static Vector3 Srgb2Oklab(Color c)
    {
        float r=SrgbToLinear(c.r), g=SrgbToLinear(c.g), b=SrgbToLinear(c.b);
        float l=CubeRoot((r*0.4121656119823456f + g*0.5362752079963684f) + b*0.051457565277814865f);
        float m=CubeRoot((r*0.2118591070175171f + g*0.6807189583778381f) + b*0.10740657895803452f);
        float s=CubeRoot((r*0.08830979466438293f + g*0.28184741735458374f) + b*0.6302613615989685f);
        // The source has a positive final coefficient in L; preserve it.
        return new Vector3(
            (l*0.21045425534248352f + m*0.7936177849769592f) + s*0.004072046838700771f,
            (l*1.9779984951019287f + m*-2.4285922050476074f) + s*0.4505937099456787f,
            (l*0.025904037058353424f + m*0.7827717661857605f) + s*-0.8086757659912109f);
    }
}
