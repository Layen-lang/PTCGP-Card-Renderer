using System.IO;
using UnityEditor;
using UnityEngine;

// Export the matching engine's runtime serialization schema. The generated
// type trees will be used to decode original game bytes, never as game assets.
public static class ExportPipelineSchema
{
    public static void Run()
    {
        // Assets -> unity/project -> unity -> repository; everything generated lives in data/.
        string root = Path.Combine(Directory.GetParent(Application.dataPath).Parent.Parent.FullName, "data");
        string destination = Path.Combine(root, "logs/native/pipeline-schema");
        Directory.CreateDirectory(destination);
        var cameraObject = new GameObject("CameraSchema", typeof(Camera), typeof(UnityEngine.Rendering.Universal.UniversalAdditionalCameraData));
        PrefabUtility.SaveAsPrefabAsset(cameraObject, "Assets/Editor/Schema/CameraSchema.prefab");
        Object.DestroyImmediate(cameraObject);
        var clipPaths = new string[3];
        for (int i=0;i<clipPaths.Length;i++) {
            clipPaths[i] = "Assets/Editor/Schema/FlipTemplate"+i+".anim";
            if (!AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPaths[i])) {
                var clip = new AnimationClip { name = "FlipTemplate"+i };
                AssetDatabase.CreateAsset(clip,clipPaths[i]);
            }
        }
        var tmp = AssetDatabase.LoadAssetAtPath<TMPro.TMP_Settings>("Assets/Resources/OriginalTMPSettingsSchema.asset");
        if (!tmp) {
            tmp = ScriptableObject.CreateInstance<TMPro.TMP_Settings>();
            tmp.name = "TMP Settings";
            AssetDatabase.CreateAsset(tmp,"Assets/Resources/OriginalTMPSettingsSchema.asset");
        }
        var manifest = BuildPipeline.BuildAssetBundles(destination, new[] {
            new AssetBundleBuild {
                assetBundleName = "pipeline-schema.bundle",
                assetNames = new[] { "Assets/Resources/SourceCardPipeline.asset", "Assets/Resources/SourceCardRenderer.asset" }
            }
            ,new AssetBundleBuild {
                assetBundleName = "tmp-settings-schema.bundle",
                assetNames = new[] { "Assets/Resources/OriginalTMPSettingsSchema.asset", "Assets/Editor/Schema/Leading.txt", "Assets/Editor/Schema/Following.txt" }
            }
            ,new AssetBundleBuild { assetBundleName="flip-clips-schema.bundle", assetNames=clipPaths }
            ,new AssetBundleBuild { assetBundleName="camera-schema.bundle", assetNames=new[] { "Assets/Editor/Schema/CameraSchema.prefab" } }
        }, BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.StandaloneWindows64);
        EditorApplication.Exit(manifest ? 0 : 1);
    }
}
