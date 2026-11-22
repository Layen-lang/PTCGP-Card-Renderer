using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Lettuce.Graphics.Rendering;

// Selected source card-studio bloom: seven original compiled passes and the
// already recovered TextureSheetBuilder geometry. Scoped to the IM player.
public sealed class SourceImmersiveBloom : IDisposable
{
    Material material;
    SourceBloomAtlas atlas;
    SourceBloomAtlas uiAtlas;
    class Data { public TextureHandle color,emission,prepared,sheet,blur,reconstructed; public TextureHandle[] levels; public SourceImmersiveBloom owner; public SourceBloomAtlas atlas; public Matrix4x4 view,projection; }
    public SourceImmersiveBloom()
    {
        var shader=AssetBundle.GetAllLoadedAssetBundles().SelectMany(b=>b.LoadAllAssets<Shader>()).Single(s=>s.name=="CustomPostEffect/Bloom");
        material=new Material(shader);
        // Source profile: buffer256, downSampling5, scatter0.5, intensity1.
        atlas=new SourceBloomAtlas(new Vector2Int(455,455),5,.5f,1);
        CustomRendererRenderGraph.ImmersiveBloom=Record;
    }
    TextureHandle Texture(RenderGraph graph,int width,int height,string name)
    {
        return graph.CreateTexture(new TextureDesc(width,height) {name=name,colorFormat=UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,clearBuffer=true,clearColor=Color.black});
    }
    void Record(RenderGraph graph,ContextContainer frame,TextureHandle color,TextureHandle emission)
    {
        var cameraData=frame.Get<UniversalCameraData>();var camera=cameraData.camera;
        bool model=camera.cullingMask==(1<<21);
        if(!model && camera.name!="UIAppCamera") return;
        var selected=atlas;var size=new Vector2Int(455,455);
        if(!model) {
            var descriptor=cameraData.cameraTargetDescriptor;
            float aspect=descriptor.width/(float)descriptor.height;
            size=aspect<1 ? new Vector2Int(256,Mathf.RoundToInt(256/aspect)) : new Vector2Int(Mathf.RoundToInt(256*aspect),256);
            if(uiAtlas==null || uiAtlas.levels[0].size!=size) {uiAtlas?.Dispose();uiAtlas=new SourceBloomAtlas(size,5,.5f,1);}
            selected=uiAtlas;
        }
        using(var builder=graph.AddUnsafePass<Data>("Original IM sheet bloom",out var data)) {
            data.owner=this;data.atlas=selected;data.color=color;data.emission=emission;data.view=camera.worldToCameraMatrix;data.projection=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            data.prepared=Texture(graph,size.x*2,size.y*2,"Original IM bloom preparation");
            data.levels=new TextureHandle[5];
            for(int i=0;i<5;i++) data.levels[i]=Texture(graph,size.x>>i,size.y>>i,"Original IM reduction "+i);
            data.sheet=Texture(graph,selected.size.x,selected.size.y,"Original IM bloom sheet");
            data.blur=Texture(graph,selected.size.x,selected.size.y,"Original IM bloom blur");
            data.reconstructed=Texture(graph,size.x,size.y,"Original IM bloom reconstruction");
            builder.UseTexture(color,AccessFlags.ReadWrite);builder.UseTexture(emission,AccessFlags.Read);
            foreach(var texture in data.levels.Concat(new[]{data.prepared,data.sheet,data.blur,data.reconstructed})) builder.UseTexture(texture,AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((Data p,UnsafeGraphContext context)=>p.owner.Execute(p,CommandBufferHelpers.GetNativeCommandBuffer(context.cmd)));
        }
    }
    void Execute(Data p,CommandBuffer command)
    {
        void Blit(TextureHandle input,TextureHandle output,int pass,string property="_BlitTexture") {
            var source=(Texture)input;
            command.SetRenderTarget((RenderTexture)output);
            command.SetGlobalTexture(property,source);
            command.SetGlobalVector("_BlitTexture_TexelSize",new Vector4(1f/source.width,1f/source.height,source.width,source.height));
            command.SetGlobalVector("_BlitScaleBias",new Vector4(1,1,0,0));
            command.SetGlobalVector("_GlobalMipBias",Vector4.zero);
            command.DrawProcedural(Matrix4x4.identity,material,pass,MeshTopology.Triangles,3);
        }
        Blit(p.emission,p.prepared,0);
        var input=p.prepared;
        for(int i=0;i<5;i++) {Blit(input,p.levels[i],1);input=p.levels[i];command.SetGlobalTexture("_DownSampling"+(i+1)+"Tex",(Texture)input);}
        command.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
        command.SetRenderTarget((RenderTexture)p.sheet);command.ClearRenderTarget(false,true,Color.black);
        command.DrawMesh(p.atlas.imageToSheet,Matrix4x4.identity,material,0,2);
        Blit(p.sheet,p.blur,3);Blit(p.blur,p.sheet,4);
        command.SetRenderTarget((RenderTexture)p.reconstructed);command.SetGlobalTexture("_MainTex",(Texture)p.sheet);
        command.DrawMesh(p.atlas.sheetToImage,Matrix4x4.identity,material,0,5);
        Blit(p.reconstructed,p.color,6,"_BloomTex");
        command.SetViewProjectionMatrices(p.view,p.projection);
    }
    public void Dispose()
    { CustomRendererRenderGraph.ImmersiveBloom=null;atlas?.Dispose();uiAtlas?.Dispose();if(material) UnityEngine.Object.Destroy(material); }
}
