using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class SourceNativeUiCapture
{
    public static void Render(Camera camera,RenderTexture texture)
    {
        if(!Environment.GetCommandLineArgs().Contains("--probe-native-ui")) {camera.Render();return;}
        var settings=GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>();
        if(!settings || !GraphicsSettings.TrySetCurrentRenderPipelineGlobalSettings(settings))
            throw new InvalidOperationException("UI capture pipeline global settings missing");
        var pipeline=Resources.Load<UniversalRenderPipelineAsset>("SourceCardPipeline");
        GraphicsSettings.defaultRenderPipeline=pipeline; QualitySettings.renderPipeline=pipeline;
        try {
            var request=new UniversalRenderPipeline.SingleCameraRequest {destination=texture};
            if(!RenderPipeline.SupportsRenderRequest(camera,request)) throw new InvalidOperationException("Source UI request unsupported");
            RenderPipeline.SubmitRenderRequest(camera,request);
        } finally {GraphicsSettings.defaultRenderPipeline=null;QualitySettings.renderPipeline=null;}
    }
}
