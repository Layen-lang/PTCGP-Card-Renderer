using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Observe configured card-camera/target state; no settings or meshes changed.
public static class SourceCardPipelineState
{
    [Serializable] public class CameraState {
        public bool orthographic,allowHDR,allowMSAA,useOcclusionCulling;
        public float fieldOfView,nearClipPlane,farClipPlane,aspect;
        public int cullingMask,clearFlags;
        public Color backgroundColor;
        public Vector3 worldPosition,localPosition;
        public Quaternion localRotation;
        public float[] projectionColumnMajor,gpuProjectionColumnMajor,worldToCameraColumnMajor;
    }
    [Serializable] public class Additional {
        public bool renderShadows,renderPostProcessing,stopNaN,dithering,clearDepth,allowXRRendering,allowHDROutput;
        public int requiresDepthOption,requiresColorOption,renderType,rendererIndex,volumeLayerMask,volumeUpdateMode,antialiasing,antialiasingQuality,cameraStackCount;
    }
    [Serializable] public class Target {
        public int width,height,graphicsFormat,depthStencilFormat,flags,msaaSamples,filterMode,mipmapCount,hideFlags;
        public bool useMipMap,autoGenerateMips;
    }
    [Serializable] public class Report {
        public string observedUtc,phase,unityVersion,graphicsDeviceName;
        public int graphicsDeviceType,colorSpace;
        public CameraState camera;
        public Additional additional;
        public Target target;
    }
    static float[] Matrix(Matrix4x4 m)=>Enumerable.Range(0,16).Select(i=>m[i]).ToArray();
    static int Field(UniversalAdditionalCameraData extra,string name)=>(int)typeof(UniversalAdditionalCameraData).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(extra);
    public static void Record(Camera camera,RenderTexture target,string root,List<string> log,string phase)
    {
        if(!CardSelection.Diagnostics) return;
        var extra=camera.GetUniversalAdditionalCameraData();var d=target.descriptor;
        var state=new Report {
            observedUtc=DateTime.UtcNow.ToString("o"),phase=phase,unityVersion=Application.unityVersion,graphicsDeviceName=SystemInfo.graphicsDeviceName,graphicsDeviceType=(int)SystemInfo.graphicsDeviceType,colorSpace=(int)QualitySettings.activeColorSpace,
            camera=new CameraState {orthographic=camera.orthographic,fieldOfView=camera.fieldOfView,nearClipPlane=camera.nearClipPlane,farClipPlane=camera.farClipPlane,aspect=camera.aspect,cullingMask=camera.cullingMask,clearFlags=(int)camera.clearFlags,backgroundColor=camera.backgroundColor,allowHDR=camera.allowHDR,allowMSAA=camera.allowMSAA,useOcclusionCulling=camera.useOcclusionCulling,worldPosition=camera.transform.position,localPosition=camera.transform.localPosition,localRotation=camera.transform.localRotation,projectionColumnMajor=Matrix(camera.projectionMatrix),gpuProjectionColumnMajor=Matrix(GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)),worldToCameraColumnMajor=Matrix(camera.worldToCameraMatrix)},
            additional=new Additional {renderShadows=extra.renderShadows,requiresDepthOption=(int)extra.requiresDepthOption,requiresColorOption=(int)extra.requiresColorOption,renderType=(int)extra.renderType,rendererIndex=Field(extra,"m_RendererIndex"),volumeLayerMask=extra.volumeLayerMask.value,volumeUpdateMode=Field(extra,"m_VolumeFrameworkUpdateModeOption"),renderPostProcessing=extra.renderPostProcessing,antialiasing=(int)extra.antialiasing,antialiasingQuality=(int)extra.antialiasingQuality,stopNaN=extra.stopNaN,dithering=extra.dithering,clearDepth=extra.clearDepth,allowXRRendering=extra.allowXRRendering,allowHDROutput=extra.allowHDROutput,cameraStackCount=extra.cameraStack.Count},
            target=new Target {width=target.width,height=target.height,graphicsFormat=(int)d.graphicsFormat,depthStencilFormat=(int)d.depthStencilFormat,flags=(int)d.flags,msaaSamples=d.msaaSamples,filterMode=(int)target.filterMode,mipmapCount=target.mipmapCount,hideFlags=(int)target.hideFlags,useMipMap=target.useMipMap,autoGenerateMips=target.autoGenerateMips}
        };
        var path=Path.Combine(root,"logs/runtime/card-pipeline-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".json");
        File.WriteAllText(path,JsonUtility.ToJson(state,true));log?.Add("CARD_PIPELINE_PROBE="+path);
    }
}
