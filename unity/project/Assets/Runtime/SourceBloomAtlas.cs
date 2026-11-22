using System;
using System.Collections.Generic;
using UnityEngine;

// Port of TextureSheetBuilder from libil2cpp1.7.1. See bloom-findings.md.
// This supplies geometry only; it does not replace the game's render pipeline.
public sealed class SourceBloomAtlas : IDisposable
{
    public readonly Mesh imageToSheet, sheetToImage;
    public readonly Vector2Int size;
    public readonly RectInt[] levels;
    public readonly float[] weights;
    const int MaxLevels = 7, Margin = 9;

    public SourceBloomAtlas(Vector2Int baseSize, int count, float scatter, float intensity)
    {
        if (baseSize.x < 1 || baseSize.y < 1 || count < 1 || count > MaxLevels)
            throw new ArgumentOutOfRangeException("Invalid source bloom atlas dimensions or level count");
        size = new Vector2Int(baseSize.x + baseSize.x / 2 + 4 * Margin, baseSize.y + 2 * Margin);
        levels = new RectInt[count];
        weights = new float[count];
        var imageVertices = new Vector3[(MaxLevels + 1) * 4];
        var imageUvs = new Vector3[imageVertices.Length];
        var sheetVertices = new Vector3[imageVertices.Length];
        var sheetUvs = new Vector3[imageVertices.Length];
        var sheetColors = new Color[imageVertices.Length];
        var triangles = new int[(count + 1) * 6];
        var quad = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(-1,1,0), new Vector3(1,1,0) };
        var uv = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(0,1,0), new Vector3(1,1,0) };
        for (int q = 0; q <= MaxLevels; q++) for (int v = 0; v < 4; v++) {
            imageVertices[q * 4 + v] = sheetVertices[q * 4 + v] = quad[v];
            imageUvs[q * 4 + v] = sheetUvs[q * 4 + v] = uv[v];
            sheetColors[q * 4 + v] = q == 0 ? Color.clear : Color.white;
        }
        for (int q = 0; q <= count; q++) {
            int t = q * 6, v = q * 4;
            triangles[t] = v; triangles[t+1] = v+2; triangles[t+2] = v+1;
            triangles[t+3] = v+2; triangles[t+4] = v+3; triangles[t+5] = v+1;
        }
        float sum = 0;
        for (int i = 0; i < count; i++) sum += Mathf.Pow(scatter, i);
        if (sum == 0) throw new ArgumentOutOfRangeException("Zero source bloom weight sum");
        float inverseWidth = 1f / size.x, inverseHeight = 1f / size.y;
        float marginX = inverseWidth * Margin, marginY = inverseHeight * Margin;
        float offsetX = 0, offsetY = 0;
        int width = baseSize.x, height = baseSize.y;
        for (int i = 1; i <= count; i++) {
            float x = marginX + offsetX, y = marginY + offsetY;
            float extentX = inverseWidth * width, extentY = inverseHeight * height;
            float right = extentX + x, top = y + extentY;
            float weight = intensity * (Mathf.Pow(scatter, i - 1) / sum);
            weights[i-1] = weight;
            levels[i-1] = new RectInt(Mathf.RoundToInt(x * size.x), Mathf.RoundToInt(y * size.y), width, height);
            for (int v = 0; v < 4; v++) {
                float u = v % 2 == 0 ? x : right, w = v < 2 ? y : top;
                imageVertices[i * 4 + v] = new Vector3(u + u - 1, w + w - 1, 0);
                imageUvs[i * 4 + v] = new Vector3(uv[v].x, uv[v].y, i);
                sheetUvs[i * 4 + v] = new Vector3(u, w, weight);
            }
            if ((i & 1) == 1) offsetX += marginX + marginX + extentX;
            else offsetY += marginY + marginY + extentY;
            width /= 2; height /= 2;
        }
        imageToSheet = new Mesh { name = "Source Image2SheetMesh", vertices = imageVertices };
        imageToSheet.SetUVs(0, new List<Vector3>(imageUvs));
        imageToSheet.SetTriangles(triangles, 0);
        sheetToImage = new Mesh { name = "Source Sheet2ImageMesh", vertices = sheetVertices, colors = sheetColors };
        sheetToImage.SetUVs(0, new List<Vector3>(sheetUvs));
        sheetToImage.SetTriangles(triangles, 0);
    }

    public void Dispose()
    {
        UnityEngine.Object.Destroy(imageToSheet);
        UnityEngine.Object.Destroy(sheetToImage);
    }
}
