using UnityEngine;
using UnityEngine.UI;

// Values from original UIRenderStudio prefab 5457f08913d8f44e7b398e938bcc7111.
// Its RenderOneShot native method explicitly calls Camera.Render.
public static class SourceUiStudio
{
    public static void Configure(Canvas canvas, Camera camera)
    {
        camera.transform.position = Vector3.zero;
        camera.orthographic = true;
        camera.orthographicSize = .6975476741790771f;
        camera.nearClipPlane = -1;
        camera.farClipPlane = 1;
        camera.fieldOfView = 60;
        camera.depth = 0;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 0;
        canvas.pixelPerfect = false;
        canvas.additionalShaderChannels = (AdditionalCanvasShaderChannels)25;
        var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(734,1024);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1;
        scaler.referencePixelsPerUnit = 100;
        scaler.dynamicPixelsPerUnit = 1;
        scaler.fallbackScreenDPI = 96;
        scaler.defaultSpriteDPI = 96;
    }
}
