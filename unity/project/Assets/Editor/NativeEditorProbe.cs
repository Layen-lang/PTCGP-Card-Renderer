using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public static class NativeEditorProbe
{
    public static void Run()
    {
        // Assets -> unity/project -> unity -> repository; everything generated lives in data/.
        string root = Path.Combine(Directory.GetParent(Application.dataPath).Parent.Parent.FullName, "data");
        File.WriteAllText(Path.Combine(root, "logs/native-editor-result.txt"),
            "Unity=" + Application.unityVersion + "\nURP=" + typeof(UniversalRenderPipeline).Assembly.FullName + "\nTMP=" + typeof(TMPro.TMP_Text).Assembly.FullName);
        EditorApplication.Exit(0);
    }
}
