using System;
using UnityEngine;
using UnityEngine.UI;

// Selected MtUIGradientEffect branch: Angle2, whole Graphic rect, two colors,
// zero offsets. Matrix2x3 native RVAs5bf4480/5bf44dc, ModifyMesh5c07cf8 (1.7.5).
public sealed class SourceAngleGradient : BaseMeshEffect
{
    public Color First,Second;
    public float Degrees;
    public override void ModifyMesh(VertexHelper vertices)
    {
        if(!IsActive()) return;
        Rect rect=graphic.rectTransform.rect;
        if(rect.width<=0 || rect.height<=0) throw new InvalidOperationException("Invalid source gradient rectangle");
        float radians=Degrees*Mathf.Deg2Rad;
        float cos=Mathf.Cos(radians),sin=Mathf.Sin(radians);
        float tx=-.5f-rect.x/rect.width,ty=-.5f-rect.y/rect.height;
        float m10=sin/rect.width,m11=cos/rect.height,m12=tx*sin+ty*cos+.5f;
        for(int i=0;i<vertices.currentVertCount;i++) {
            var vertex=new UIVertex(); vertices.PopulateUIVertex(ref vertex,i);
            float normalizedY=m12+vertex.position.x*m10+vertex.position.y*m11;
            // Native Angle branch extrapolates; clamping is in Color->Color32.
            Color color=Second+(First-Second)*normalizedY;
            vertex.color=(Color32)(color*(Color)vertex.color);
            vertices.SetUIVertex(vertex,i);
        }
    }
}
