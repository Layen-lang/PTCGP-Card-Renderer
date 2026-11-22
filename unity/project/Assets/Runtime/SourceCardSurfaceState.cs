using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// Observe resolved textures after bundle loading, including null references.
public static class SourceCardSurfaceState
{
    [Serializable] class TextureState {
        public string property, name, format;
        public int width, height, filter, wrapU, wrapV, wrapW, anisoLevel;
        public float mipMapBias;
        public bool missing;
    }
    [Serializable] class Surface {
        public string path, material, shader;
        public int slot;
        public TextureState[] textures;
    }
    [Serializable] class Report { public Surface[] surfaces; public int anisotropicFiltering; }
    public static void Record(Transform face, string root, List<string> log)
    {
        var rows = new List<Surface>();
        foreach (var renderer in face.GetComponentsInChildren<Renderer>(true)) {
            var path = renderer.name;
            for (var t = renderer.transform.parent; t && t != face; t = t.parent)
                path = t.name + "/" + path;
            var materials = renderer.sharedMaterials;
            for (int slot = 0; slot < materials.Length; slot++) {
                var material = materials[slot];
                if (!material) continue;
                var textures = new List<TextureState>();
                for (int i = 0; i < material.shader.GetPropertyCount(); i++) {
                    if (material.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var name = material.shader.GetPropertyName(i);
                    var texture = material.GetTexture(name);
                    var state = new TextureState {property = name, missing = !texture};
                    if (texture) {
                        state.name = texture.name; state.width = texture.width; state.height = texture.height;
                        state.format = texture.graphicsFormat.ToString(); state.filter = (int)texture.filterMode;
                        state.wrapU = (int)texture.wrapModeU; state.wrapV = (int)texture.wrapModeV;
                        state.wrapW = (int)texture.wrapModeW;
                        state.anisoLevel = texture.anisoLevel; state.mipMapBias = texture.mipMapBias;
                    }
                    textures.Add(state);
                }
                rows.Add(new Surface {path = path, slot = slot, material = material.name,
                    shader = material.shader.name, textures = textures.ToArray()});
            }
        }
        var output = Path.Combine(root, "logs/runtime/card-surfaces-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
        File.WriteAllText(output, JsonUtility.ToJson(new Report {surfaces = rows.ToArray(),
            anisotropicFiltering = (int)QualitySettings.anisotropicFiltering}, true));
        log.Add("CARD_SURFACE_PROBE=" + output);
    }
}
