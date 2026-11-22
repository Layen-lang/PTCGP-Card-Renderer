using System;
using System.Collections.Generic;
using UnityEngine;

// CardBehaviour.UpdateHologramRotation, RVA 0x47df354, native 1.7.1.
// The UI detail/simple loaders supply Vector3.zero. This is a card-data
// parameter, not the current host pointer or card transform rotation.
public static class SourceCardShaderState
{
    static readonly int Rotation = Shader.PropertyToID("_Rotation");
    public static void ApplyHologramRotation(Transform face, Vector3 rotation, List<string> log)
    {
        var block = new MaterialPropertyBlock();
        var value = new Vector4(rotation.x, rotation.y, rotation.z, 0);
        int count = 0, preservedTextures = 0;
        // Match the original Component.GetComponentsInChildren<Renderer>()
        // overload, including its default inactive-object behavior.
        foreach (var renderer in face.GetComponentsInChildren<Renderer>())
        {
            renderer.GetPropertyBlock(block);
            var uiTexture = block.GetTexture("_DynamicUITex");
            block.SetVector(Rotation, value);
            renderer.SetPropertyBlock(block);
            block.Clear();
            renderer.GetPropertyBlock(block);
            if (block.GetVector(Rotation) != value)
                throw new InvalidOperationException("Original hologram vector did not reach renderer property block");
            if (block.GetTexture("_DynamicUITex") != uiTexture)
                throw new InvalidOperationException("Hologram update replaced the original dynamic UI texture binding");
            if (uiTexture) preservedTextures++;
            count++;
        }
        log.Add("OriginalHologramRotation property=_Rotation vector=" + value
            + " renderers=" + count + " dynamicUITexturesPreserved=" + preservedTextures
            + " source=UIDetailLoaderVector3Zero native=1.7.1 crossVersionUnverified");
    }
}
