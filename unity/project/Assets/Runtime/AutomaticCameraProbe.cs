using System;
using System.IO;
using UnityEngine;

// Observes an ordinary camera frame; never invokes Camera.Render itself.
public sealed class AutomaticCameraProbe : MonoBehaviour
{
    public string destination;
    public bool captured;
    void OnRenderImage(RenderTexture source, RenderTexture output)
    {
        if (!captured && !string.IsNullOrEmpty(destination)) {
            var previous = RenderTexture.active;
            RenderTexture.active = source;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(destination, image.EncodeToPNG());
            Destroy(image);
            RenderTexture.active = previous;
            captured = true;
        }
        Graphics.Blit(source, output);
    }
}
