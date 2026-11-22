using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

public sealed class CardPreview : MonoBehaviour
{
    [Serializable] public class BundleRecord { public string path, blob; }
    [Serializable] public class Manifest { public BundleRecord[] bundles; }
    [Serializable] public class Binding { public int[] indices; public int type; public string property; }
    [Serializable] public class CardConfig { public string cardId, displayName, rarity, prefabBundle, prefabName, uiBundle, uiPrefabName; public string[] languages, displayNames; public bool hasGold, hasImmersive; }
    [Serializable] public class Bindings { public Binding[] bindings; }
    readonly Dictionary<string, AssetBundle> bundles = new Dictionary<string, AssetBundle>();
    readonly List<string> log = new List<string>();
    Transform card;
    Transform displayRoot;
    SourceCardTouch nativeTouch;
    Camera view;
    Shader sourceBloomShader;
    AssetBundle sourceBloomBundle;
    AssetBundle sourceTmpSettingsBundle;
    string verificationOutput;
    AutomaticCameraProbe automaticCameraProbe;
    Vector3 previousPointer;
    Vector2 angle;
    string root, status = "Loading the original assets…";
    bool ready;
    CardConfig selected;
    IEnumerator Start()
    {
        root = Environment.GetCommandLineArgs().SkipWhile(a => a != "--project-root").Skip(1).FirstOrDefault();
        if (string.IsNullOrEmpty(root)) root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        // The game runs at 60 frames (measured dt 1/60) and some effects, like GlitterFlowMaps, step per frame.
        // VSync would override this limit and follow the monitor. Changing it without a window crashes Unity on Vulkan.
        if (!Application.isBatchMode) QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        Application.runInBackground = true;
        yield return null;
        if (SourceRenderQuality.DynamicUiServiceRequested(out var serviceFolder)) {
            CardSelection.SetRoot(root);
            InitializeTextSettings();
            yield return ServeDynamicUi(serviceFolder);
            yield break;
        }
        try { Load(); } catch (Exception error) { status = error.ToString(); Debug.LogException(error); }
        if (Environment.GetCommandLineArgs().Contains("--verify-rendergraph") || Environment.GetCommandLineArgs().Contains("--verify-native-touch") || Environment.GetCommandLineArgs().Contains("--verify-native-mrr") || Environment.GetCommandLineArgs().Contains("--verify-native-msr") || Environment.GetCommandLineArgs().Contains("--verify-native-marble") || Environment.GetCommandLineArgs().Contains("--verify-native-ancient") || Environment.GetCommandLineArgs().Contains("--verify-native-future") || Environment.GetCommandLineArgs().Contains("--verify-native-mssr") || Environment.GetCommandLineArgs().Contains("--verify-native-kira-puyo") || Environment.GetCommandLineArgs().Contains("--verify-native-circular-kira")) {
            var session = GetComponent("NativeRenderGraphSession");
            if (!ready || !session) { Debug.LogError("Native RenderGraph session unavailable: " + status); Application.Quit(1); yield break; }
            yield return (IEnumerator)session.GetType().GetMethod("Run").Invoke(session, new object[] {view, displayRoot, root, log});
            yield break;
        }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-interactive")) {
            var session = GetComponent("NativeRenderGraphSession");
            if (!ready || !session) { Application.Quit(1); yield break; }
            yield return (IEnumerator)session.GetType().GetMethod("VerifyInteractive").Invoke(session, new object[] {view, root, log});
            yield break;
        }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-immersive")) {
            var session = GetComponent("NativeRenderGraphSession");
            if (!ready || !session) { Application.Quit(1); yield break; }
            yield return (IEnumerator)session.GetType().GetMethod("VerifyImmersive").Invoke(session, new object[] {view, root, log});
            yield break;
        }
        if (Environment.GetCommandLineArgs().Contains("--native-rendergraph")) {
            var session = GetComponent("NativeRenderGraphSession");
            if (!ready || !session) throw new InvalidOperationException("Native interactive session unavailable: " + status);
            yield return (IEnumerator)session.GetType().GetMethod("RunInteractive").Invoke(session, new object[] {view});
        }
        if (VerificationRequested()) {
            // A windowed player can run Start while Unity's splash still owns
            // presentation. Capturing then records neither the camera nor GUI.
            while (!Application.isBatchMode && !UnityEngine.Rendering.SplashScreen.isFinished) yield return null;
            for (int i = 0; i < 30; i++) yield return null;
            yield return new WaitForEndOfFrame();
            try {
                if (!ready) throw new InvalidOperationException(status);
                var output = verificationOutput;
                log.Add("AutomaticCameraFrame=" + automaticCameraProbe.captured);
                // Vulkan batch mode has no presentation framebuffer; reading it
                // causes a native driver crash. Camera-target probes remain valid.
                if (!Application.isBatchMode) {
                    var frame = ScreenCapture.CaptureScreenshotAsTexture();
                    if (!frame) throw new InvalidOperationException("Presentation capture unavailable");
                    File.WriteAllBytes(Path.Combine(output, "application-frame.png"), frame.EncodeToPNG());
                    log.Add("ApplicationFrame=" + Screen.width + "x" + Screen.height);
                    Destroy(frame);
                } else log.Add("ApplicationFrame=skipped: batch mode has no presentation framebuffer");
                if (!automaticCameraProbe.captured && !Application.isBatchMode)
                    throw new InvalidOperationException("No automatic display; an offscreen capture does not validate the window");
                SourceBloomProbe.Run(sourceBloomShader, output, log);
                SourceBloomAtlasProbe.Run(sourceBloomShader, output, log);
                SourceEmissionProbe.Run(view, card, output, log);
                SourceStudioProbe.Run(view, output, log);
                foreach (int degrees in new[] {0, 20, 180}) {
                    card.localRotation = Quaternion.Euler(0, degrees, 0);
                    var rt = new RenderTexture(1024, 1024, 24);
                    view.targetTexture = rt;
                    view.Render();
                    RenderTexture.active = rt;
                    var png = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
                    png.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
                    png.Apply();
                    File.WriteAllBytes(Path.Combine(output, "angle-" + degrees + ".png"), png.EncodeToPNG());
                    RenderTexture.active = null;
                    view.targetTexture = null;
                    rt.Release();
                    Destroy(png);
                }
                File.WriteAllLines(Path.Combine(output, "player-report.txt"), log);
                File.WriteAllText(Path.Combine(root, "logs/runtime/latest-player-capture.txt"), output);
                if (Environment.GetCommandLineArgs().Contains("--verify")) Application.Quit(0);
            } catch (Exception error) {
                Debug.LogException(error);
                status = error.Message;
                if (Environment.GetCommandLineArgs().Contains("--verify")) Application.Quit(1);
            }
        }
    }
    void Load()
    {
        log.Add("Unity=" + Application.unityVersion + " GPU=" + SystemInfo.graphicsDeviceName + " API=" + SystemInfo.graphicsDeviceType);
        InitializeTextSettings();
        selected = CardSelection.Load(root);
        log.Add("SelectedCard="+selected.cardId+" "+selected.displayName);
        LoadManifestBundles();
        bool nativeUi = SourceRenderQuality.NativeRenderingRequested();
        foreach (var bundle in bundles.Values) foreach (var shader in bundle.LoadAllAssets<Shader>())
            if (!shader.isSupported) throw new InvalidOperationException("Shader non pris en charge : " + shader.name);
        sourceBloomBundle = AssetBundle.LoadFromFile(Path.Combine(root, "runtime-assets/bundles/source-bloom.bundle"));
        if (!sourceBloomBundle) throw new InvalidOperationException("Bundle bloom original illisible");
        sourceBloomShader = sourceBloomBundle.LoadAllAssets<Shader>().Single();
        if (sourceBloomShader.name != "CustomPostEffect/Bloom" || !sourceBloomShader.isSupported)
            throw new InvalidOperationException("Shader bloom original incompatible");
        var bloomMaterial = new Material(sourceBloomShader);
        if (bloomMaterial.passCount != 7) throw new InvalidOperationException("Unexpected bloom pass count");
        log.Add("SourceBloomShader=" + sourceBloomShader.name + " supported=True passes=" + bloomMaterial.passCount);
        Destroy(bloomMaterial);
        var textures = ComposeDynamicUi();
        if (SourceRenderQuality.DynamicUiExportRequested(out var exportFolder)) { ExportDynamicTextures(textures,exportFolder); Application.Quit(0); return; }
        card = Instantiate(bundles[selected.prefabBundle].LoadAllAssets<GameObject>().Single(p => p.name == selected.prefabName)).transform;
        ApplyDynamicTextures(card,JsonUtility.FromJson<Bindings>(CardSelection.Text("bindings.json.gz")).bindings,textures);
        if (CardSelection.Gold)
            SourceAdditionalGoldFrame.Attach(card,CardSelection.Text("gold.json.gz"),bundles,textures,log);
        if (nativeUi) SourceCardShaderState.ApplyHologramRotation(card, Vector3.zero, log);
        if (nativeUi && CardSelection.Diagnostics) SourceCardSurfaceState.Record(card,root,log);
        if (nativeUi)
            SourceCardEffects.Attach(card,CardSelection.Text("effects.json.gz"),log);
        view = new GameObject("Card camera", typeof(Camera)).GetComponent<Camera>();
        view.transform.position = new Vector3(0, 0, -1.911506f);
        view.fieldOfView = 35;
        view.nearClipPlane = .01f;
        view.farClipPlane = 10;
        view.clearFlags = CameraClearFlags.SolidColor;
        view.backgroundColor = new Color(.45f, .47f, .52f, 1);
        view.allowHDR = false;
        view.enabled = true;
        view.targetDisplay = 0;
        view.rect = new Rect(0, 0, 1, 1);
        displayRoot=card;
        if (SourceRenderQuality.NativeRenderingRequested()) {
            SourceCardAssetLayer.Apply(card,log);
            nativeTouch=SourceCardTouch.Attach(card,root,log,Environment.GetCommandLineArgs().Contains("--native-rendergraph"));
            displayRoot=nativeTouch.Root;
            SourceRenderStudioPlacement.AttachModel(view,displayRoot,root,log);
        }
        if (VerificationRequested()) {
            verificationOutput = Path.Combine(root, "logs/runtime/player-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(verificationOutput);
            automaticCameraProbe = view.gameObject.AddComponent<AutomaticCameraProbe>();
            automaticCameraProbe.destination = Path.Combine(verificationOutput, "automatic-camera-frame.png");
        }
        log.Add("CameraState enabled=" + view.enabled + " active=" + view.gameObject.activeInHierarchy
            + " type=" + view.cameraType + " display=" + view.targetDisplay + " cameras=" + Camera.allCamerasCount
            + " pipeline=" + UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline);
        status = selected.displayName + " " + selected.rarity + " Â· " + selected.cardId;
        log.Add("Loaded=" + bundles.Count + " Renderers=" + card.GetComponentsInChildren<Renderer>(true).Length);
        ready = true;
    }
    void InitializeTextSettings()
    {
        TMP_Settings settings;
        if (SourceRenderQuality.NativeRenderingRequested()) {
            sourceTmpSettingsBundle = AssetBundle.LoadFromFile(Path.Combine(root,"runtime-assets/bundles/source-tmp-settings.bundle"));
            if (!sourceTmpSettingsBundle) throw new InvalidOperationException("Original TMP settings bundle missing");
            settings = sourceTmpSettingsBundle.LoadAllAssets<TMP_Settings>().Single();
            if (settings.name != "TMP Settings") throw new InvalidOperationException("Unexpected original TMP settings identity");
        } else settings = ScriptableObject.CreateInstance<TMP_Settings>();
        typeof(TMP_Settings).GetField("s_Instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, settings);
        if (SourceRenderQuality.NativeRenderingRequested()) {
            if (!TMP_Settings.leadingCharacters || !TMP_Settings.followingCharacters)
                throw new InvalidOperationException("Original TMP line-breaking assets unresolved");
            log.Add("OriginalTMPSettings name="+settings.name+" leading="+TMP_Settings.linebreakingRules.leadingCharacters.Count+" following="+TMP_Settings.linebreakingRules.followingCharacters.Count);
            if (CardSelection.Diagnostics) File.WriteAllText(Path.Combine(root,"logs/native/runtime-tmp-settings.json"),JsonUtility.ToJson(settings,true));
        }
    }
    void LoadManifestBundles()
    {
        var manifest = JsonUtility.FromJson<Manifest>(CardSelection.Text(CardSelection.Gold ? "gold-bundles.json.gz" : "bundles.json.gz"));
        foreach (var row in manifest.bundles.Reverse()) {
            if (bundles.ContainsKey(row.blob)) continue;
            var bundle = AssetBundle.LoadFromFile(Path.Combine(root, row.path));
            if (!bundle) throw new InvalidOperationException("Missing or unreadable bundle: " + row.blob);
            bundles.Add(row.blob, bundle);
        }
    }
    // The card UI of the selected card and language, composed in its render textures.
    RenderTexture[] ComposeDynamicUi()
    {
        var ui = Instantiate(bundles[selected.uiBundle].LoadAllAssets<GameObject>().Single(p => p.name == selected.uiPrefabName));
        var plan = JsonUtility.FromJson<SourceUiPlan.Plan>(CardSelection.Text((CardSelection.Gold ? "gold-ui." : "ui.") + CardSelection.Language + ".json.gz"));
        SourceUiPlan.Apply(ui, plan, bundles, log);
        var canvas = new GameObject("Card UI capture", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
        canvas.gameObject.layer = ui.layer;
        canvas.renderMode = RenderMode.WorldSpace;
        ((RectTransform)canvas.transform).sizeDelta = new Vector2(734, 1024);
        ui.transform.SetParent(canvas.transform, false);
        var capture = new GameObject("UI camera", typeof(Camera)).GetComponent<Camera>();
        capture.transform.position = new Vector3(0, 0, -2000);
        capture.orthographic = true;
        capture.orthographicSize = 512;
        capture.nearClipPlane = 1;
        capture.farClipPlane = 3000;
        capture.clearFlags = CameraClearFlags.SolidColor;
        capture.backgroundColor = Color.black;
        canvas.worldCamera = capture;
        bool nativeUi = SourceRenderQuality.NativeRenderingRequested();
        var quality = SourceRenderQuality.SelectedLevel();
        if (nativeUi) SourceUiStudio.Configure(canvas,capture);
        if (nativeUi) SourceRenderStudioPlacement.AttachUi(canvas,capture,ui,root,log);
        SourceUiPlan.FinalizeLayout(ui, plan, log);
        Canvas.ForceUpdateCanvases();
        foreach (var text in ui.GetComponentsInChildren<TMP_Text>()) text.ForceMeshUpdate();
        var textures = new RenderTexture[2];
        for (int type = 0; type < 2; type++) {
            capture.cullingMask = 1 << (17 + type);
            Canvas.ForceUpdateCanvases();
            int width = nativeUi ? SourceRenderQuality.DynamicTextDimension(734,quality) : 734;
            int height = nativeUi ? SourceRenderQuality.DynamicTextDimension(1024,quality) : 1024;
            textures[type] = new RenderTexture(width, height, 24, RenderTextureFormat.Default);
            textures[type].hideFlags = (HideFlags)52;
            capture.targetTexture = textures[type];
            Canvas.ForceUpdateCanvases();
            SourceNativeUiCapture.Render(capture,textures[type]);
            log.Add("DynamicUI type="+type+" size="+width+"x"+height+" factor="+(nativeUi ? SourceRenderQuality.TextFactor(quality) : 1)+" studio="+(nativeUi ? "SourceScreenSpace" : "LegacyWorldSpace"));
            if (nativeUi) log.Add("UIStudioProjection canvas="+((RectTransform)canvas.transform).rect.size+" scale="+canvas.scaleFactor+" cameraPixels="+capture.pixelRect.size);
        }
        if (nativeUi && CardSelection.Diagnostics) SourceUiStateProbe.Record(ui,textures,capture,root,log);
        capture.targetTexture = null;
        capture.enabled = false;
        canvas.gameObject.SetActive(false);
        composedUi = capture.transform.root.gameObject;
        composedCanvas = canvas.transform.root.gameObject;
        composedInstance = ui;
        return textures;
    }
    GameObject composedUi, composedCanvas, composedInstance;
    // A long-lived player serves the web export: a process start per card is what made it slow.
    // A job file holds: card, language, output folder, dynamic types and optionally "gold". Bundles stay open between jobs.
    IEnumerator ServeDynamicUi(string folder)
    {
        string job = Path.Combine(folder, "job.txt"), done = Path.Combine(folder, "done.txt");
        float idle = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - idle < 120) {
            if (!File.Exists(job)) { yield return null; continue; }
            var lines = File.ReadAllLines(job);
            File.Delete(job);
            string result = "ok";
            try {
                if (lines[0] == "quit") { Application.Quit(0); yield break; }
                if (lines[0] == "noise") {
                    // Samples for the web engine's Mathf.PerlinNoise.
                    var samples = new System.Text.StringBuilder();
                    for (int i = 0; i < 3000; i++) { float x = i * .137f - 100, y = (i * 7 % 1000) * .211f - 50; samples.AppendLine(x.ToString("R") + " " + y.ToString("R") + " " + Mathf.PerlinNoise(x, y).ToString("R") + " " + Mathf.PerlinNoise1D(x).ToString("R")); }
                    File.WriteAllText(lines[2], samples.ToString());
                    File.WriteAllText(done + ".tmp", "ok"); File.Move(done + ".tmp", done);
                    continue;
                }
                // An optional fifth line, "gold", composes the UI of the gold frame.
                selected = CardSelection.Select(lines[0], lines[1], lines.Length > 4 && lines[4] == "gold");
                LoadManifestBundles();
                var textures = ComposeDynamicUi();
                foreach (string type in lines[3].Split(',')) ExportRaw(textures[int.Parse(type)], Path.Combine(lines[2], "dynamic-ui-" + type + ".rgba"));
                foreach (var texture in textures) { texture.Release(); DestroyImmediate(texture); }
                foreach (var go in new[] { composedInstance, composedCanvas, composedUi }.Distinct()) if (go) DestroyImmediate(go);
                // Illustrations are the only large card-specific bundles: the rest is shared by every card.
                var manifest = JsonUtility.FromJson<Manifest>(CardSelection.Text("bundles.json.gz"));
                foreach (var row in manifest.bundles) if ((row.path.Contains("/CardNew/Face/") || row.path.Contains("/CardNew/Pokemon/")) && bundles.TryGetValue(row.blob, out var bundle)) {
                    bundle.Unload(true);
                    bundles.Remove(row.blob);
                }
            } catch (Exception error) { result = error.ToString(); }
            File.WriteAllText(done + ".tmp", result);
            File.Move(done + ".tmp", done);
            idle = Time.realtimeSinceStartup;
            yield return null;
        }
        Application.Quit(0);
    }
    static void ExportRaw(RenderTexture source, string path)
    {
        RenderTexture.active = source;
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
        File.WriteAllBytes(path, image.GetRawTextureData());
        Destroy(image);
        RenderTexture.active = null;
    }
    // Web packages: the composed card UI of one language, untouched, one PNG per dynamic UI type.
    static void ExportDynamicTextures(RenderTexture[] textures, string folder) {
        Directory.CreateDirectory(folder);
        for (int type = 0; type < textures.Length; type++) {
            RenderTexture.active = textures[type];
            var image = new Texture2D(textures[type].width, textures[type].height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(folder, "dynamic-ui-" + type + ".png"), image.EncodeToPNG());
            Destroy(image);
        }
        RenderTexture.active = null;
    }
    public static void ApplyDynamicTextures(Transform surface, Binding[] bindings, RenderTexture[] textures) {
        foreach (var b in bindings) {
            var t = surface;
            foreach (int index in b.indices) t = t.GetChild(index);
            var renderer = t.GetComponent<Renderer>();
            string property = string.IsNullOrEmpty(b.property) ? "_DynamicUITex" : b.property;
            if (!renderer.sharedMaterial.HasProperty(property)) throw new InvalidOperationException("Surface UI incompatible: " + property);
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetTexture(property, textures[b.type]);
            // DynamicUIObject also supplies sampler metadata explicitly.
            // MPB textures must not inherit texel size from the material's
            // original black placeholder (selected 1.7.5 live renderer).
            var dynamicTexture=textures[b.type];
            block.SetVector(property+"_TexelSize",new Vector4(1f/dynamicTexture.width,1f/dynamicTexture.height,dynamicTexture.width,dynamicTexture.height));
            block.SetVector(property+"_ST",new Vector4(1,1,0,0));
            renderer.SetPropertyBlock(block);
        }
    }
    static bool VerificationRequested()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Contains("--verify") || args.Contains("--verify-window");
    }
    void Update()
    {
        if (!ready) return;
        if (nativeTouch) return;
        if (Input.GetMouseButtonDown(0)) previousPointer = Input.mousePosition;
        if (Input.GetMouseButton(0)) {
            Vector3 delta = Input.mousePosition - previousPointer;
            angle += new Vector2(-delta.y, -delta.x) * .2f;
            angle.x = Mathf.Clamp(angle.x, -60, 60);
            previousPointer = Input.mousePosition;
        }
        if (Input.GetKeyDown(KeyCode.R)) angle = Vector2.zero;
        card.localRotation = Quaternion.Euler(angle.x, angle.y, 0);
    }
    void OnGUI()
    {
        if (Environment.GetCommandLineArgs().Contains("--native-rendergraph")) return;
        GUI.Label(new Rect(16, 12, Screen.width - 32, 80), status);
        if (ready) GUI.Label(new Rect(16, Screen.height - 32, Screen.width - 32, 28), nativeTouch ? "Drag: tilt · Quick swipe: flip · R: front" : "Drag: turn the card    ·    R: front");
    }
}
