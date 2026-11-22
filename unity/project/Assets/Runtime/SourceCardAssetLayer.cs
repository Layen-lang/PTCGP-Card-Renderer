using System.Collections.Generic;
using UnityEngine;

// CardFactory.Create (1.7.5 RVA480c5a4) calls Asset3DLayer.SetLayer
// (RVA4755d64): assign the root, then recursively enumerate every child.
public static class SourceCardAssetLayer
{
    public const int Detail = 21;
    public static void Apply(Transform card, List<string> report)
    {
        SetLayer(card, Detail);
        report.Add("OriginalCardAssetLayer="+Detail+" transforms="+card.GetComponentsInChildren<Transform>(true).Length+" setterRva=4755d64 callerRva=480c5a4");
    }
    static void SetLayer(Transform node, int layer)
    {
        node.gameObject.layer=layer;
        foreach(Transform child in node) SetLayer(child,layer);
    }
}
