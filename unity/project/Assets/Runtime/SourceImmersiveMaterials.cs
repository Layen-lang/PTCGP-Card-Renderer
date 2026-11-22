using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// PropertyCustomizer generates these materials at runtime in the game; the
// prefab ParticleSystemRenderers deliberately have no serialized materials.
// This restores the selected IM modifiers on the original compiled shaders.
public static class SourceImmersiveMaterials
{
    [Serializable] public class Number { public string name; public float value; }
    [Serializable] public class Vector { public string name; public float[] value; }
    [Serializable] public class Texture { public string property,name,bundleName; }
    [Serializable] public class ColorKey { public float time; public Color color; }
    [Serializable] public class AlphaKey { public float time,alpha; }
    [Serializable] public class GradientKeys { public int mode; public ColorKey[] colors; public AlphaKey[] alphas; }
    [Serializable] public class Gradation { public string property; public int resolution,filter; public GradientKeys[] keys; }
    [Serializable] public class Entry {
        public string root,shader;
        public int[] indices,streams;
        public Number[] floats;
        public Vector[] vectors;
        public Texture[] textures;
        public Gradation[] gradients;
        public string[] keywords;
        public float uiScale;
        public int queue;
    }
    public static void Apply(Entry[] entries,GameObject ui,GameObject model,Camera camera,List<string> log)
    {
        var bundles=AssetBundle.GetAllLoadedAssetBundles().ToArray();
        var shaders=bundles.SelectMany(b=>b.LoadAllAssets<Shader>()).Where(s=>s.name.StartsWith("Lettuce/Effect/Standard/")).ToArray();
        foreach(var entry in entries) {
            Transform target=entry.root=="ui" ? ui.transform : model.transform;
            foreach(int index in entry.indices) target=target.GetChild(index);
            var renderer=target.GetComponent<ParticleSystemRenderer>();
            if(!renderer) throw new InvalidOperationException("Original IM particle renderer missing: "+target.name);
            var shader=shaders.Single(s=>s.name==entry.shader);
            if(!shader.isSupported) throw new InvalidOperationException("Original IM effect shader unsupported: "+shader.name);
            var material=new Material(shader) {name="Original IM generated: "+target.name,renderQueue=entry.queue};
            var resources=target.gameObject.AddComponent<SourceImmersiveMaterialResources>();
            resources.material=material;
            foreach(var f in entry.floats) {
                if(f.name.StartsWith("_Use") || f.name.StartsWith("_Is") || f.name.EndsWith("CustomData") || f.name.EndsWith("UVMode") || f.name.EndsWith("MaskType")) material.SetInteger(f.name,(int)f.value);
                else material.SetFloat(f.name,f.value);
            }
            foreach(var v in entry.vectors) material.SetVector(v.name,new Vector4(v.value[0],v.value[1],v.value[2],v.value[3]));
            foreach(var t in entry.textures) {
                var bundle=bundles.Single(b=>string.Equals(b.name,t.bundleName,StringComparison.OrdinalIgnoreCase));
                var texture=bundle.LoadAllAssets<UnityEngine.Texture>().Single(p=>p.name==t.name);
                material.SetTexture(t.property,texture);
            }
            foreach(var g in entry.gradients) {
                // Unity's JSON Gradient importer treats key colors as packed
                // bytes. The source typetree uses floats, so set keys explicitly.
                var gradients=g.keys.Select(k=> {
                    var gradient=new Gradient {mode=(GradientMode)k.mode};
                    gradient.SetKeys(k.colors.Select(c=>new GradientColorKey(c.color,c.time)).ToArray(),k.alphas.Select(a=>new GradientAlphaKey(a.alpha,a.time)).ToArray());
                    return gradient;
                }).ToArray();
                if(gradients.Length==0) throw new InvalidOperationException("Original IM gradient missing");
                var texture=new Texture2D(g.resolution,gradients.Length,TextureFormat.RGBA32,false) {wrapMode=TextureWrapMode.Clamp,filterMode=(FilterMode)g.filter,name=g.property};
                var colors=new Color[g.resolution*gradients.Length];
                // Native GradationMapComponent.ConvertGradientToTex samples x/N.
                for(int y=0;y<gradients.Length;y++) for(int x=0;x<g.resolution;x++) colors[y*g.resolution+x]=gradients[y].Evaluate(x/(float)g.resolution);
                texture.SetPixels(colors);texture.Apply();material.SetTexture(g.property,texture);
                resources.textures.Add(texture);
                log.Add("ImmersiveGradient="+target.name+" "+g.property+" mid="+gradients[0].Evaluate(.5f));
            }
            foreach(var keyword in entry.keywords.Where(k=>!string.IsNullOrEmpty(k))) material.EnableKeyword(keyword);
            renderer.sharedMaterial=material;
            renderer.SetActiveVertexStreams(entry.streams.Select(i=>(ParticleSystemVertexStream)i).ToList());
            if(entry.shader.EndsWith("/UI")) {
                var go=new GameObject("Original particle Canvas connector",typeof(RectTransform),typeof(CanvasRenderer));
                go.layer=5;go.transform.SetParent(target,false);
                var bridge=go.AddComponent<SourceImmersiveParticleCanvas>();
                bridge.Initialize(renderer,material,camera,entry.uiScale);
            }
            log.Add("ImmersiveMaterial="+target.name+" shader="+shader.name+" textures="+entry.textures.Length+" gradients="+entry.gradients.Length+" streams="+entry.streams.Length);
        }
    }
}

public sealed class SourceImmersiveMaterialResources : MonoBehaviour
{
    public Material material;
    public readonly List<Texture2D> textures=new();
    void OnDestroy() { if(material) Destroy(material);foreach(var texture in textures) if(texture) Destroy(texture); }
}

// The source UIModifier routes baked Shuriken geometry through a CanvasRenderer.
// Keep the original UI shader, textures, gradients and custom vertex streams.
public sealed class SourceImmersiveParticleCanvas : MonoBehaviour
{
    ParticleSystemRenderer source;
    CanvasRenderer destination;
    Camera camera;
    Mesh mesh;
    float scale;
    public void Initialize(ParticleSystemRenderer renderer,Material material,Camera viewCamera,float uiScale)
    {
        source=renderer;camera=viewCamera;scale=uiScale;destination=GetComponent<CanvasRenderer>();
        destination.materialCount=1;destination.SetMaterial(material,0);destination.SetColor(Color.white);
        mesh=new Mesh {name="Original IM UI particle geometry"};mesh.MarkDynamic();
        source.enabled=false;
    }
    void LateUpdate()
    {
        if(!source || !mesh) return;
        source.BakeMesh(mesh,camera,false);
        var vertices=mesh.vertices;
        for(int i=0;i<vertices.Length;i++) vertices[i]*=scale;
        mesh.vertices=vertices;destination.SetMesh(mesh);
    }
    void OnDestroy() { if(mesh) Destroy(mesh); }
}
