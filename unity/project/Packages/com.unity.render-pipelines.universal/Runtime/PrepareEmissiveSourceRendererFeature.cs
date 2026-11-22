using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Lettuce.Graphics.Rendering
{
    public sealed class PrepareEmissiveSourceRendererFeature : ScriptableRendererFeature
    {
        PreparePass pass;
        public override void Create() { pass=new PreparePass {renderPassEvent=RenderPassEvent.BeforeRenderingOpaques}; }
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data) { renderer.EnqueuePass(pass); }
        sealed class PreparePass : ScriptableRenderPass
        {
            static readonly Dictionary<CameraStackPostEffectGroup,int> lastClearedFrame=new();
            class ClearData { public TextureHandle target; }
            public override void RecordRenderGraph(RenderGraph graph,ContextContainer frame)
            {
                var camera=frame.Get<UniversalCameraData>();
                if (camera.renderer is not CustomRendererRenderGraph renderer) return;
                var state=renderer.StackState;
                var emission=state.Prepare(graph,frame.Get<UniversalResourceData>().activeColorTexture);
                frame.GetOrCreate<EmissiveSourceData>().EmissiveSourceTexture=emission;
                if (!state.CurrentSettings) return;
                var group=state.CurrentSettings.PostEffectGroup;
                lastClearedFrame.TryGetValue(group,out int cleared);
                if (cleared>=Time.frameCount) return;
                using (var builder=graph.AddUnsafePass<ClearData>("Clear Emissive_"+group,out var data)) {
                    data.target=emission;
                    builder.AllowPassCulling(false);
                    builder.UseTexture(emission,AccessFlags.ReadWrite);
                    builder.SetRenderFunc((ClearData p,UnsafeGraphContext context)=> {
                        context.cmd.SetRenderTarget(p.target);
                        context.cmd.ClearRenderTarget(false,true,Color.black);
                    });
                }
                lastClearedFrame[group]=Time.frameCount;
                CustomRendererRenderGraph.GroupClearCount++;
            }
        }
    }
}
