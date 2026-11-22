using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Lettuce.Graphics.Rendering
{
    [RequireComponent(typeof(Camera))]
    public sealed class CameraRendererSettings : MonoBehaviour
    {
        [SerializeField] bool _useDepthTexture;
        [SerializeField] CameraStackPostEffectGroup _postEffectGroup;
        public bool UseDepthTexture => _useDepthTexture;
        public CameraStackPostEffectGroup PostEffectGroup => _postEffectGroup;
        public void Initialize(CameraStackPostEffectGroup group, bool useDepth)
        { _postEffectGroup=group; _useDepthTexture=useDepth; }
    }

}
