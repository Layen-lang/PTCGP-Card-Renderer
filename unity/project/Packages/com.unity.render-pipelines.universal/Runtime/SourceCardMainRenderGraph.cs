using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal.Internal;
namespace UnityEngine.Rendering.Universal
{
    public partial class UniversalRenderer
    {
        protected void RecordSourceCardMain(RenderGraph renderGraph, ScriptableRenderContext context, Lettuce.Graphics.Rendering.CustomDrawObjectsPass sourceOpaque, Lettuce.Graphics.Rendering.CustomDrawObjectsPass sourceTransparent)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalPostProcessingData postProcessingData = frameData.Get<UniversalPostProcessingData>();

            if (!renderGraph.nativeRenderPassesEnabled)
            {
                RTClearFlags clearFlags = (RTClearFlags) GetCameraClearFlag(cameraData);

                if (clearFlags != RTClearFlags.None)
                    ClearTargetsPass.Render(renderGraph, resourceData.activeColorTexture, resourceData.activeDepthTexture, clearFlags, cameraData.backgroundColor);
            }

            RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.BeforeRenderingPrePasses);

            RenderPassInputSummary renderPassInputs = GetRenderPassInputs(cameraData.IsTemporalAAEnabled(), postProcessingData.isEnabled, cameraData.isSceneViewCamera);

            if (m_RenderingLayerProvidesByDepthNormalPass)
                renderPassInputs.requiresNormalsTexture = true;

            bool isDeferred = this.renderingModeActual == RenderingMode.Deferred;

            bool requiresDepthPrepass = RequireDepthPrepass(cameraData, ref renderPassInputs);
            bool isDepthOnlyPrepass = requiresDepthPrepass && !renderPassInputs.requiresNormalsTexture;
            bool isDepthNormalPrepass = requiresDepthPrepass && renderPassInputs.requiresNormalsTexture;

            // The depth prepass is considered "full" (renders the entire scene, not a partial subset), when we either:
            // - Have a depth only prepass (URP always renders the full scene in depth only mode)
            // - Have a depth normals prepass that does not allow the partial prepass optimization
            bool hasFullPrepass = isDepthOnlyPrepass || (isDepthNormalPrepass && !AllowPartialDepthNormalsPrepass(isDeferred, renderPassInputs.requiresDepthNormalAtEvent));

            TextureCopySchedules copySchedules = CalculateTextureCopySchedules(cameraData, renderPassInputs, isDeferred, requiresDepthPrepass, hasFullPrepass);

            bool needsOccluderUpdate = cameraData.useGPUOcclusionCulling;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled && cameraData.xr.hasMotionVectorPass)
            {
                // Update prevView and View matrices.
                m_XRDepthMotionPass?.Update(ref cameraData);

                // Record depthMotion pass and import XR resources into the rendergraph.
                m_XRDepthMotionPass?.Render(renderGraph, frameData);
            }
#endif

            if (requiresDepthPrepass)
            {
                // If we're in deferred mode, prepasses always render directly to the depth attachment rather than the camera depth texture.
                // In non-deferred mode, we only render to the depth attachment directly when depth priming is enabled and we're starting with an empty depth buffer.
                bool isDepthPrimingTarget = (useDepthPriming && (cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth));
                bool renderToAttachment = (isDeferred || isDepthPrimingTarget);
                TextureHandle depthTarget = renderToAttachment ? resourceData.activeDepthTexture : resourceData.cameraDepthTexture;

                var passCount = needsOccluderUpdate ? 2 : 1;
                for (int passIndex = 0; passIndex < passCount; ++passIndex)
                {
                    uint batchLayerMask = uint.MaxValue;
                    if (needsOccluderUpdate)
                    {
                        // first pass: test everything against previous frame final depth pyramid
                        // second pass: re-test culled against current frame intermediate depth pyramid
                        OcclusionTest occlusionTest = (passIndex == 0) ? OcclusionTest.TestAll : OcclusionTest.TestCulled;
                        InstanceOcclusionTest(renderGraph, cameraData, occlusionTest);
                        batchLayerMask = occlusionTest.GetBatchLayerMask();
                    }

                    // The prepasses are executed multiple times when GRD occlusion is active.
                    // We only want to set global textures after all executions are complete.
                    bool isLastPass = (passIndex == (passCount - 1));

                    // When we render to the depth attachment, a copy must happen later to populate the camera depth texture and the copy will handle setting globals.
                    // If we're rendering to the camera depth texture, we can set the globals immediately.
                    bool setGlobalDepth = isLastPass && !renderToAttachment;

                    // There's no special copy logic for the camera normals texture, so we can set the global as long as we're not performing a partial prepass.
                    // In the case of a partial prepass, the global will be set later by the gbuffer pass once it completes the data in the texture.
                    bool setGlobalTextures = isLastPass && (!isDeferred || hasFullPrepass);

                    if (isDepthNormalPrepass)
                        DepthNormalPrepassRender(renderGraph, renderPassInputs, depthTarget, batchLayerMask, setGlobalDepth, setGlobalTextures);
                    else
                        m_DepthPrepass.Render(renderGraph, frameData, ref depthTarget, batchLayerMask, setGlobalDepth);

                    if (needsOccluderUpdate)
                    {
                        // first pass: make current frame intermediate depth pyramid
                        // second pass: make current frame final depth pyramid, set occlusion test results for later passes
                        UpdateInstanceOccluders(renderGraph, cameraData, depthTarget);
                        if (passIndex != 0)
                            InstanceOcclusionTest(renderGraph, cameraData, OcclusionTest.TestAll);
                    }
                }
                needsOccluderUpdate = false;
            }

            // After the prepass completes, we should copy depth if necessary and also render motion vectors. (they're expected to be available whenever depth is)
            // In the case where depth is rendered as part of the prepass and no copy is necessary, we still need to render motion vectors here to ensure they're available
            // with depth before any user passes are executed.
            if (copySchedules.depth == DepthCopySchedule.AfterPrepass)
                ExecuteScheduledDepthCopyWithMotion(renderGraph, resourceData, renderPassInputs.requiresMotionVectors);
            else if ((copySchedules.depth == DepthCopySchedule.DuringPrepass) && renderPassInputs.requiresMotionVectors)
                RenderMotionVectors(renderGraph, resourceData);

            RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.AfterRenderingPrePasses);

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.hasValidOcclusionMesh)
                m_XROcclusionMeshPass.Render(renderGraph, frameData, resourceData.activeColorTexture, resourceData.activeDepthTexture);
#endif

            {
                RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.BeforeRenderingGbuffer, RenderPassEvent.BeforeRenderingOpaques);

                var passCount = needsOccluderUpdate ? 2 : 1;
                for (int passIndex = 0; passIndex < passCount; ++passIndex)
                {
                    uint batchLayerMask = uint.MaxValue;
                    if (needsOccluderUpdate)
                    {
                        // first pass: test everything against previous frame final depth pyramid
                        // second pass: re-test culled against current frame intermediate depth pyramid
                        OcclusionTest occlusionTest = (passIndex) == 0 ? OcclusionTest.TestAll : OcclusionTest.TestCulled;
                        InstanceOcclusionTest(renderGraph, cameraData, occlusionTest);
                        batchLayerMask = occlusionTest.GetBatchLayerMask();
                    }

                    sourceOpaque.Render(renderGraph, frameData, resourceData.activeColorTexture,
                        frameData.Get<Lettuce.Graphics.Rendering.EmissiveSourceData>().EmissiveSourceTexture, resourceData.activeDepthTexture, resourceData.mainShadowsTexture,
                        resourceData.additionalShadowsTexture, batchLayerMask);


                    if (needsOccluderUpdate)
                    {
                        // first pass: make current frame intermediate depth pyramid
                        // second pass: make current frame final depth pyramid, set occlusion test results for later passes
                        UpdateInstanceOccluders(renderGraph, cameraData, resourceData.activeDepthTexture);
                        if (passIndex != 0)
                            InstanceOcclusionTest(renderGraph, cameraData, OcclusionTest.TestAll);
                    }
                }
                needsOccluderUpdate = false;
            }

            if (copySchedules.depth == DepthCopySchedule.AfterOpaques)
                RecordCustomPassesWithDepthCopyAndMotion(renderGraph, resourceData, renderPassInputs.requiresDepthTextureEarliestEvent, RenderPassEvent.AfterRenderingOpaques, renderPassInputs.requiresMotionVectors);
            else
                RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.AfterRenderingOpaques);

            RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.BeforeRenderingSkybox);

            if (cameraData.camera.clearFlags == CameraClearFlags.Skybox && cameraData.renderType != CameraRenderType.Overlay)
            {
                cameraData.camera.TryGetComponent(out Skybox cameraSkybox);
                Material skyboxMaterial = cameraSkybox != null ? cameraSkybox.material : RenderSettings.skybox;
                if (skyboxMaterial != null)
                    m_DrawSkyboxPass.Render(renderGraph, frameData, context, resourceData.activeColorTexture, resourceData.activeDepthTexture, skyboxMaterial);
            }

            if (copySchedules.depth == DepthCopySchedule.AfterSkybox)
                ExecuteScheduledDepthCopyWithMotion(renderGraph, resourceData, renderPassInputs.requiresMotionVectors);

            RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.AfterRenderingSkybox);

            if (copySchedules.color == ColorCopySchedule.AfterSkybox)
            {
                TextureHandle activeColor = resourceData.activeColorTexture;
                Downsampling downsamplingMethod = UniversalRenderPipeline.asset.opaqueDownsampling;
                TextureHandle cameraOpaqueTexture;
                m_CopyColorPass.Render(renderGraph, frameData, out cameraOpaqueTexture, in activeColor, downsamplingMethod);
                resourceData.cameraOpaqueTexture = cameraOpaqueTexture;
            }

            RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.BeforeRenderingTransparents);

#if UNITY_EDITOR
            {
                TextureHandle cameraDepthTexture = resourceData.cameraDepthTexture;
                TextureHandle cameraNormalsTexture = resourceData.cameraNormalsTexture;
                m_ProbeVolumeDebugPass.Render(renderGraph, frameData, cameraDepthTexture, cameraNormalsTexture);
            }
#endif

            // TODO RENDERGRAPH: bind _CameraOpaqueTexture, _CameraDepthTexture in transparent pass?
#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
            if (needTransparencyPass)
#endif
            {
                sourceTransparent.Render(
                    renderGraph,
                    frameData,
                    resourceData.activeColorTexture,
                    frameData.Get<Lettuce.Graphics.Rendering.EmissiveSourceData>().EmissiveSourceTexture,
                    resourceData.activeDepthTexture,
                    resourceData.mainShadowsTexture,
                    resourceData.additionalShadowsTexture);
            }

            Lettuce.Graphics.Rendering.CustomRendererRenderGraph.ImmersiveBloom?.Invoke(renderGraph,frameData,
                resourceData.activeColorTexture,frameData.Get<Lettuce.Graphics.Rendering.EmissiveSourceData>().EmissiveSourceTexture);
            if (copySchedules.depth == DepthCopySchedule.AfterTransparents)
                RecordCustomPassesWithDepthCopyAndMotion(renderGraph, resourceData, renderPassInputs.requiresDepthTextureEarliestEvent, RenderPassEvent.AfterRenderingTransparents, renderPassInputs.requiresMotionVectors);
            else
                RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.AfterRenderingTransparents);

            if (context.HasInvokeOnRenderObjectCallbacks())
                m_OnRenderObjectCallbackPass.Render(renderGraph, resourceData.activeColorTexture, resourceData.activeDepthTexture);

#if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
            if (resourceData != null)
            {
                // SetupVFXCameraBuffer will interrogate VFXManager to automatically enable RequestAccess on RawColor and/or RawDepth. This must be done before SetupRawColorDepthHistory.
                // SetupVFXCameraBuffer will also provide the GetCurrentTexture from history manager to the VFXManager which can be sampled during the next VFX.Update for the following frame.
                SetupVFXCameraBuffer(cameraData);
            }
#endif

            RenderRawColorDepthHistory(renderGraph, cameraData, resourceData);

            bool shouldRenderUI = cameraData.rendersOverlayUI;
            bool outputToHDR = cameraData.isHDROutputActive;
            if (shouldRenderUI && outputToHDR)
            {
                TextureHandle overlayUI;
                m_DrawOffscreenUIPass.RenderOffscreen(renderGraph, frameData, cameraDepthAttachmentFormat, out overlayUI);
                resourceData.overlayUITexture = overlayUI;
            }
        }
    }
}
