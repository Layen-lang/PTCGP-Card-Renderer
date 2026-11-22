using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using TMPro;
using UnityEngine;

// Observation after the original two UI captures, with no layout/mesh updates.
public static class SourceUiStateProbe
{
    [Serializable] public class Glyph {
        public int character, lineNumber;
        public bool visible;
        public float pointSize, origin, xAdvance, baseline;
        public Vector3 bottomLeft, topLeft, topRight, bottomRight;
        public Vector4 uvBottomLeft, uvTopLeft, uvTopRight, uvBottomRight;
        public Vector2 uv2BottomLeft, uv2TopLeft, uv2TopRight, uv2BottomRight;
        public Color32 colorBottomLeft, colorTopLeft, colorTopRight, colorBottomRight;
    }
    [Serializable] public class TextState {
        public int[] indices;
        public string name, text, font, sharedMaterial;
        public bool active, autoSizing, extraPadding, richText, orthographic;
        public float fontSize, fontSizeMin, fontSizeMax, characterSpacing, characterWidthAdjustment, wordSpacing, lineSpacing, paragraphSpacing, wrappingRatio, padding, baselineOffset;
        public int fontStyle, fontWeight, horizontalAlignment, verticalAlignment, wrappingMode, overflowMode;
        public Vector4 margin;
        public Vector2 rectPosition, rectSize, anchorMin, anchorMax, anchoredPosition, sizeDelta, pivot;
        public Glyph[] glyphs;
        public MaterialState materialState;
    }
    [Serializable] public class AtlasState {
        public string name;
        public int width, height, filterMode, mipmapCount, anisoLevel;
        public float mipMapBias;
    }
    [Serializable] public class PropertyState {
        public string name;
        public int type;
        public float floatValue;
        public Vector4 vectorValue;
        public AtlasState texture;
    }
    [Serializable] public class MaterialState {
        public string name, shader;
        public string[] keywords;
        public PropertyState[] properties;
    }
    [Serializable] public class TextureState {
        public int width, height, filterMode, mipmapCount, flags, graphicsFormat, depthStencilFormat, msaaSamples;
        public bool useMipMap, autoGenerateMips;
    }
    [Serializable] public class TransformState {
        public int[] indices;
        public string name;
        public bool active, isRect;
        public Vector3 localPosition, localScale, worldPosition;
        public Quaternion localRotation;
        public float[] localToWorldColumnMajor;
        public Vector2 rectPosition, rectSize, anchorMin, anchorMax, anchoredPosition, sizeDelta, pivot;
    }
    [Serializable] public class CameraState {
        public bool orthographic;
        public float orthographicSize, nearClipPlane, farClipPlane, aspect;
        public Vector2 pixelRectPosition, pixelRectSize;
        public float[] projectionMatrixColumnMajor, gpuProjectionMatrixColumnMajor, worldToCameraMatrixColumnMajor;
        public TransformState transform;
    }
    [Serializable] public class CanvasState {
        public float scaleFactor, referencePixelsPerUnit, planeDistance;
        public int renderMode;
        public bool pixelPerfect;
    }
    [Serializable] public class Report { public string capturedUtc; public TextState[] texts; public TextureState[] textures; public TransformState[] nodes, ancestry; public CameraState camera; public CanvasState canvas; }
    static float[] Matrix(Matrix4x4 value) { return Enumerable.Range(0,16).Select(i=>value[i]).ToArray(); }
    static TransformState Transform(Transform node,int[] indices=null) {
        var s=new TransformState {indices=indices,name=node.name,active=node.gameObject.activeInHierarchy,localPosition=node.localPosition,localRotation=node.localRotation,localScale=node.localScale,worldPosition=node.position,localToWorldColumnMajor=Matrix(node.localToWorldMatrix)};
        if (node is RectTransform r) {
            s.isRect=true;s.rectPosition=r.rect.position;s.rectSize=r.rect.size;s.anchorMin=r.anchorMin;s.anchorMax=r.anchorMax;s.anchoredPosition=r.anchoredPosition;s.sizeDelta=r.sizeDelta;s.pivot=r.pivot;
        }
        return s;
    }
    static T Field<T>(TMP_Text text,string name) {
        var field=typeof(TMP_Text).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
        if (field==null) throw new InvalidOperationException("Missing TMP observation field "+name);
        return (T)field.GetValue(text);
    }
    static MaterialState Material(Material mat) {
        var properties=new List<PropertyState>();
        var shader=mat.shader;
        for(int i=0;i<shader.GetPropertyCount();i++) {
            var name=shader.GetPropertyName(i);var type=shader.GetPropertyType(i);
            var state=new PropertyState {name=name,type=(int)type};
            if(type==UnityEngine.Rendering.ShaderPropertyType.Float || type==UnityEngine.Rendering.ShaderPropertyType.Range) state.floatValue=mat.GetFloat(name);
            else if(type==UnityEngine.Rendering.ShaderPropertyType.Int) state.floatValue=mat.GetInteger(name);
            else if(type==UnityEngine.Rendering.ShaderPropertyType.Vector) state.vectorValue=mat.GetVector(name);
            else if(type==UnityEngine.Rendering.ShaderPropertyType.Color) state.vectorValue=mat.GetColor(name);
            else if(type==UnityEngine.Rendering.ShaderPropertyType.Texture) {
                var texture=mat.GetTexture(name);
                if(texture) state.texture=new AtlasState {name=texture.name,width=texture.width,height=texture.height,filterMode=(int)texture.filterMode,mipmapCount=texture.mipmapCount,anisoLevel=texture.anisoLevel,mipMapBias=texture.mipMapBias};
            }
            properties.Add(state);
        }
        return new MaterialState {name=mat.name,shader=shader.name,keywords=mat.shaderKeywords,properties=properties.ToArray()};
    }
    public static void Record(GameObject ui,RenderTexture[] textures,Camera camera,string root,List<string> log) {
        if(Environment.GetCommandLineArgs().Contains("--probe-font-atlas")) {
            var font=ui.GetComponentsInChildren<TMP_Text>().Select(t=>t.font).First(f=>f.name=="GillSansNova-Medium SDF");
            string folder=Path.Combine(root,"logs/runtime/font-atlas-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder,"atlas.png"),font.atlasTexture.EncodeToPNG());
            File.WriteAllText(Path.Combine(folder,"font.json"),JsonUtility.ToJson(font,true));
            log.Add("FONT_ATLAS_PROBE="+folder);
        }
        var states=new List<TextState>();
        foreach (var text in ui.GetComponentsInChildren<TMP_Text>(true)) {
            var indices=new List<int>();
            for (var node=text.transform;node!=ui.transform;node=node.parent) {
                if (!node) throw new InvalidOperationException("TMP outside original UI root");
                indices.Insert(0,node.GetSiblingIndex());
            }
            var r=text.rectTransform;
            var s=new TextState {
                indices=indices.ToArray(),name=text.name,text=text.text,font=text.font.name,sharedMaterial=text.fontSharedMaterial.name,
                active=text.gameObject.activeInHierarchy,fontSize=text.fontSize,fontSizeMin=text.fontSizeMin,fontSizeMax=text.fontSizeMax,
                autoSizing=text.enableAutoSizing,characterSpacing=text.characterSpacing,characterWidthAdjustment=text.characterWidthAdjustment,
                wordSpacing=text.wordSpacing,lineSpacing=text.lineSpacing,paragraphSpacing=text.paragraphSpacing,wrappingRatio=text.wordWrappingRatios,
                fontStyle=(int)text.fontStyle,fontWeight=(int)text.fontWeight,horizontalAlignment=(int)text.horizontalAlignment,verticalAlignment=(int)text.verticalAlignment,
                wrappingMode=(int)text.textWrappingMode,overflowMode=(int)text.overflowMode,extraPadding=text.extraPadding,richText=text.richText,orthographic=text.isOrthographic,
                padding=Field<float>(text,"m_padding"),baselineOffset=Field<float>(text,"m_baselineOffset"),margin=text.margin,
                rectPosition=r.rect.position,rectSize=r.rect.size,anchorMin=r.anchorMin,anchorMax=r.anchorMax,anchoredPosition=r.anchoredPosition,sizeDelta=r.sizeDelta,pivot=r.pivot,
            };
            var glyphs=new List<Glyph>();
            if (s.active) {
                var info=text.textInfo;
                for (int i=0;i<info.characterCount;i++) {
                    var c=info.characterInfo[i];
                    glyphs.Add(new Glyph {character=c.character,lineNumber=c.lineNumber,visible=c.isVisible,pointSize=c.pointSize,origin=c.origin,xAdvance=c.xAdvance,baseline=c.baseLine,bottomLeft=c.vertex_BL.position,topLeft=c.vertex_TL.position,topRight=c.vertex_TR.position,bottomRight=c.vertex_BR.position,uvBottomLeft=c.vertex_BL.uv,uvTopLeft=c.vertex_TL.uv,uvTopRight=c.vertex_TR.uv,uvBottomRight=c.vertex_BR.uv,uv2BottomLeft=c.vertex_BL.uv2,uv2TopLeft=c.vertex_TL.uv2,uv2TopRight=c.vertex_TR.uv2,uv2BottomRight=c.vertex_BR.uv2,colorBottomLeft=c.vertex_BL.color,colorTopLeft=c.vertex_TL.color,colorTopRight=c.vertex_TR.color,colorBottomRight=c.vertex_BR.color});
                }
            }
            s.glyphs=glyphs.ToArray();if(s.active) s.materialState=Material(text.fontSharedMaterial);states.Add(s);
        }
        var rt=new List<TextureState>();
        foreach (var texture in textures) {
            var d=texture.descriptor;
            rt.Add(new TextureState {width=texture.width,height=texture.height,filterMode=(int)texture.filterMode,mipmapCount=texture.mipmapCount,flags=(int)d.flags,graphicsFormat=(int)d.graphicsFormat,depthStencilFormat=(int)d.depthStencilFormat,msaaSamples=d.msaaSamples,useMipMap=texture.useMipMap,autoGenerateMips=texture.autoGenerateMips});
        }
        var stamp=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var path=Path.Combine(root,"logs/runtime/ui-state-"+stamp+".json");
        var nodes=new List<TransformState>();
        foreach (var node in ui.GetComponentsInChildren<Transform>(true)) {
            var indices=new List<int>();
            for (var t=node;t!=ui.transform;t=t.parent) indices.Insert(0,t.GetSiblingIndex());
            nodes.Add(Transform(node,indices.ToArray()));
        }
        var ancestry=new List<TransformState>();
        for (var node=ui.transform;node;node=node.parent) ancestry.Add(Transform(node));
        var canvas=ui.GetComponentInParent<Canvas>();
        var cameraState=new CameraState {orthographic=camera.orthographic,orthographicSize=camera.orthographicSize,nearClipPlane=camera.nearClipPlane,farClipPlane=camera.farClipPlane,aspect=camera.aspect,pixelRectPosition=camera.pixelRect.position,pixelRectSize=camera.pixelRect.size,projectionMatrixColumnMajor=Matrix(camera.projectionMatrix),gpuProjectionMatrixColumnMajor=Matrix(GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)),worldToCameraMatrixColumnMajor=Matrix(camera.worldToCameraMatrix),transform=Transform(camera.transform)};
        var canvasState=new CanvasState {scaleFactor=canvas.scaleFactor,referencePixelsPerUnit=canvas.referencePixelsPerUnit,planeDistance=canvas.planeDistance,renderMode=(int)canvas.renderMode,pixelPerfect=canvas.pixelPerfect};
        File.WriteAllText(path,JsonUtility.ToJson(new Report {capturedUtc=DateTime.UtcNow.ToString("o"),texts=states.ToArray(),textures=rt.ToArray(),nodes=nodes.ToArray(),ancestry=ancestry.ToArray(),camera=cameraState,canvas=canvasState},true));
        log.Add("UI_STATE_PROBE="+path);
    }
}
