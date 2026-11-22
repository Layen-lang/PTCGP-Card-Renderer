using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

namespace Lettuce.Graphics.Rendering
{
    // Port of the recovered native renderer orchestration for the card studio.
    // Camera groups and dynamic effect modifiers are still under investigation.
    public sealed class CustomRendererRenderGraph : UniversalRenderer
    {
        readonly CustomDrawObjectsPass opaque;
        readonly CustomDrawObjectsPass transparent;
        internal readonly SourceCameraStackState StackState=new();
        public static int GroupClearCount;
        public static Action<RenderGraph, ContextContainer, TextureHandle, TextureHandle> Diagnostic;
        public static Action<RenderGraph, ContextContainer, TextureHandle, TextureHandle> ImmersiveBloom;
        public static int RecordedFrames;
        public static string LastColorDescriptor;
        public CustomRendererRenderGraph(UniversalRendererData data) : base(data)
        {
            opaque = new CustomDrawObjectsPass("Source Draw Opaque", true,
                RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque,
                data.opaqueLayerMask, m_DefaultStencilState, data.defaultStencilState.stencilReference);
            transparent = new CustomDrawObjectsPass("Source Draw Transparent", false,
                RenderPassEvent.BeforeRenderingTransparents, RenderQueueRange.transparent,
                data.transparentLayerMask, m_DefaultStencilState, data.defaultStencilState.stencilReference);
        }

        internal override void OnRecordRenderGraph(RenderGraph graph, ScriptableRenderContext context)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var rendering = frameData.Get<UniversalRenderingData>();
            var camera = frameData.Get<UniversalCameraData>();
            var lights = frameData.Get<UniversalLightData>();
            StackState.Setup(camera);
            useRenderPassEnabled = graph.nativeRenderPassesEnabled;
            MotionVectorRenderPass.SetRenderGraphMotionVectorGlobalMatrices(graph, camera);
            SetupRenderGraphLights(graph, rendering, camera, lights);
            SetupRenderingLayers(camera.cameraTargetDescriptor.msaaSamples);
            bool depthOnly = camera.camera.targetTexture != null && camera.camera.targetTexture.format == RenderTextureFormat.Depth;
            CreateRenderGraphCameraRenderTargets(graph, depthOnly);
            if (DebugHandler != null) DebugHandler.Setup(graph, camera.isPreviewCamera);
            RecordCustomRenderGraphPasses(graph, RenderPassEvent.BeforeRendering);
            SetupRenderGraphCameraProperties(graph, resources.isActiveTargetBackBuffer);
            camera.renderer.useDepthPriming = useDepthPriming;
            if (depthOnly) {
                OnOffscreenDepthTextureRendering(graph, context, resources, camera);
                return;
            }
            OnBeforeRendering(graph);
            BeginRenderGraphXRRendering(graph);
            var descriptor = graph.GetTextureDesc(resources.activeColorTexture);
            descriptor.name = "Source Emissive";
            descriptor.clearBuffer = true;
            descriptor.clearColor = Color.black;
            LastColorDescriptor = descriptor.width+"x"+descriptor.height+" format="+descriptor.colorFormat+" samples="+descriptor.msaaSamples;
            RecordSourceCardMain(graph, context, opaque, transparent);
            var emission=frameData.Get<EmissiveSourceData>().EmissiveSourceTexture;
            // The loaded RR shader passes have no EmissiveType tag. The sheet
            // bloom activation/group machinery is not claimed complete here.
            Diagnostic?.Invoke(graph, frameData, resources.activeColorTexture, emission);
            OnAfterRendering(graph);
            EndRenderGraphXRRendering(graph);
            RecordedFrames++;
        }
        protected override void Dispose(bool disposing) { StackState.Dispose(); base.Dispose(disposing); }
    }
}
