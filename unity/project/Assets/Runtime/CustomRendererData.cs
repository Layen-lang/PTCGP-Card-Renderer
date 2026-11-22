using UnityEngine.Rendering.Universal;

namespace Lettuce.Graphics.Rendering
{
    public sealed class CustomRendererData : UniversalRendererData
    {
        protected override ScriptableRenderer Create()
        {
            return new CustomRendererRenderGraph(this);
        }
    }
}
