using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEditor.Rendering;

public static class BuildNativePreview
{
    public static void Run()
    {
        // Assets -> unity/project -> unity -> repository; everything generated lives in data/.
        string root = Path.Combine(Directory.GetParent(Application.dataPath).Parent.Parent.FullName, "data");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Artikodin preview", typeof(CardPreview));
        Object.FindFirstObjectByType<CardPreview>().gameObject.AddComponent<NativeRenderGraphSession>();
        Directory.CreateDirectory("Assets/Resources");
        var renderer = AssetDatabase.LoadAssetAtPath<Lettuce.Graphics.Rendering.CustomRendererData>("Assets/Resources/SourceCardRenderer.asset");
        if (!renderer) {
            renderer = ScriptableObject.CreateInstance<Lettuce.Graphics.Rendering.CustomRendererData>();
            AssetDatabase.CreateAsset(renderer, "Assets/Resources/SourceCardRenderer.asset");
        }
        ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
        renderer.rendererFeatures.RemoveAll(f=>!f);
        if (!renderer.rendererFeatures.Exists(f=>f is Lettuce.Graphics.Rendering.PrepareEmissiveSourceRendererFeature)) {
            var feature=ScriptableObject.CreateInstance<Lettuce.Graphics.Rendering.PrepareEmissiveSourceRendererFeature>();
            feature.name="PrepareEmissiveSourceRendererFeature";
            AssetDatabase.AddObjectToAsset(feature,renderer);
            renderer.rendererFeatures.Add(feature);
        }
        var emissionFeature=renderer.rendererFeatures.Find(f=>f is Lettuce.Graphics.Rendering.PrepareEmissiveSourceRendererFeature);
        var emissionScript=AssetDatabase.LoadAssetAtPath<MonoScript>("Packages/com.unity.render-pipelines.universal/Runtime/PrepareEmissiveSourceRendererFeature.cs");
        if (!emissionScript || emissionScript.GetClass()!=typeof(Lettuce.Graphics.Rendering.PrepareEmissiveSourceRendererFeature))
            throw new System.InvalidOperationException("Emission feature MonoScript unresolved");
        var serializedFeature=new SerializedObject(emissionFeature);
        serializedFeature.FindProperty("m_Script").objectReferenceValue=emissionScript;
        serializedFeature.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(emissionFeature);
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Resources/SourceCardPipeline.asset");
        if (!pipeline) {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, "Assets/Resources/SourceCardPipeline.asset");
        }
        ApplyOriginalPipelineValues.Apply(pipeline, renderer);
        // Images created at runtime have no scene material reference. Keep the
        // matching engine's default UGUI shader in the player instead of
        // allowing the build to strip it. This is player packaging, not a
        // replacement shader or a change to the source prefab's null material.
        var uiShader=Shader.Find("UI/Default");
        if (!uiShader) throw new System.InvalidOperationException("Default UGUI shader missing in editor");
        var graphicsAsset=AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
        var graphicsSerialized=new SerializedObject(graphicsAsset);
        var included=graphicsSerialized.FindProperty("m_AlwaysIncludedShaders");
        bool includedUI=false;
        for(int i=0;i<included.arraySize;i++) if(included.GetArrayElementAtIndex(i).objectReferenceValue==uiShader) includedUI=true;
        if(!includedUI) { int index=included.arraySize; included.InsertArrayElementAtIndex(index); included.GetArrayElementAtIndex(index).objectReferenceValue=uiShader; }
        graphicsSerialized.ApplyModifiedPropertiesWithoutUndo();
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Preview.unity");
        PlayerSettings.productName = "PTCGP Card Viewer";
        PlayerSettings.companyName = "Local research";
        PlayerSettings.defaultScreenWidth = 900;
        PlayerSettings.defaultScreenHeight = 1000;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] {GraphicsDeviceType.Vulkan});
        var destination = Path.Combine(root, "build/windows-native/CardViewer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] {"Assets/Scenes/Preview.unity"}, locationPathName = destination,
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.None
        });
        File.WriteAllText(Path.Combine(root, "logs/player-build-result.txt"), report.summary.result + " errors=" + report.summary.totalErrors);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
