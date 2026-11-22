using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Lettuce.Graphics.Rendering
{
    public enum CameraStackPostEffectGroup { Main=0, BattleOverride=1, UIOverride=2 }

    // RendererDataRenderGraph: selected RR/UI branches recovered on 1.7.5.
    // Shared RTs belong to one Base stack and are released before the next Base.
    internal sealed class SourceCameraStackState : IDisposable
    {
        readonly Dictionary<CameraStackPostEffectGroup,RTHandle> shared=new();
        public UniversalCameraData BaseCameraData { get; private set; }
        public UniversalCameraData CurrentCameraData { get; private set; }
        public CameraRendererSettings CurrentSettings { get; private set; }
        public void Setup(UniversalCameraData camera)
        {
            CurrentSettings=camera.camera.GetComponent<CameraRendererSettings>();
            if (CurrentSettings && (CurrentSettings.UseDepthTexture || CurrentSettings.PostEffectGroup==CameraStackPostEffectGroup.BattleOverride))
                throw new InvalidOperationException("Unported depth/BattleOverride camera group");
            if (camera.renderType==CameraRenderType.Base) {
                Dispose(); BaseCameraData=camera;
            }
            CurrentCameraData=camera;
        }
        public TextureHandle Prepare(RenderGraph graph, TextureHandle color)
        {
            var desc=graph.GetTextureDesc(color);
            if (!CurrentSettings) {
                desc.name="EmissiveSource"; desc.clearBuffer=true; desc.clearColor=Color.black;
                return graph.CreateTexture(desc);
            }
            var group=CurrentSettings.PostEffectGroup;
            if (!shared.TryGetValue(group,out var handle)) {
                // ARM64 call 46aea3c: slices1, depth0, format from color desc,
                // Point/Repeat/Tex2D, no mipmaps or random write, MSAA1.
                handle=RTHandles.Alloc(desc.width,desc.height,slices:1,depthBufferBits:DepthBits.None,
                    colorFormat:desc.colorFormat,filterMode:FilterMode.Point,wrapMode:TextureWrapMode.Repeat,
                    dimension:TextureDimension.Tex2D,enableRandomWrite:false,useMipMap:false,
                    autoGenerateMips:true,isShadowMap:false,anisoLevel:1,mipMapBias:0,
                    msaaSamples:MSAASamples.None,bindTextureMS:false,useDynamicScale:false,
                    name:"EmissiveSource_"+group);
                shared.Add(group,handle);
            }
            return graph.ImportTexture(handle);
        }
        public void Dispose()
        { foreach(var handle in shared.Values) handle.Release(); shared.Clear(); }
    }

    internal sealed class EmissiveSourceData : ContextItem
    {
        public TextureHandle EmissiveSourceTexture;
        public override void Reset() { EmissiveSourceTexture=TextureHandle.nullHandle; }
    }

}
