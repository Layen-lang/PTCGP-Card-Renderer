using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

// CardDualMRRObject 1.8.1, RVAs 0x4a29f0c..0x4a2b6c0.
// Evidence: evidence/v1.8.1/dual-rr/native. This is not the single-type MRR controller.
public sealed class SourceDualMrrCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    readonly Dictionary<string,AnimationCurve> curves = new Dictionary<string,AnimationCurve>();
    readonly List<Material> materials = new List<Material>();
    ShaderTagId SearchTag;
    MaterialPropertyBlock block;
    Renderer[] surfaces, effects, flashes, transitions, typeIcons;
    Vector3 startFront, worldFront;
    float startTime, elapsed, lastTilt, minRadial, maxRadial, progress;
    bool initialized, playing;
    List<string> initializationLog;
    [Serializable] sealed class Snapshot {
        public bool playing, isAnimationStopped;
        public float elapsed, progress, tiltDegree, tiltToBoundary;
        public Vector3 startFront, worldFront;
        public float changeColor, lightColorIntensity, lightEmitIntensity, lightPower;
        public float layerTranslation, layer2ColorPower, layer2EmissiveIntensity;
        public float effSwitchColor, effAdditiveIntensity, effColor3Blend, effEmissiveIntensity;
        public float flashIntensity, radialScaling, radialAnimation, highlightTranslation;
        public float gradationBoundsOffset, gradationRange, textureSwitch;
    }
    [Serializable] sealed class DiagnosticConfig {
        public string cardId, rarity, prefabName;
    }
    [Serializable] sealed class SourceObservation {
        public string cardId, size, release, illustration, prefabName;
        public bool poseBracketStable, parameterBracketStable, rootStillStable;
        public SourceIdentity controller;
        public SourceBracket before, after;
    }
    [Serializable] sealed class SourceIdentity { public string @class; }
    [Serializable] sealed class SourceBracket { public SourceFields fields; }
    [Serializable] sealed class SourceFields {
        public float _animStartDegree, _animTimeScale, _animDuration, _flashRadialStartOffset, _transitionAngle;
        public float[] _transitionBoundaryOffset, _worldFront, _worldFrontAtAnimStart;
        public float _elapsedTime, _lastTiltDegree;
        public bool _isAnimationStopped, _isPlaying, _isInitialized;
    }

    public bool Manual { get; set; }
    public bool IsAnimationStopped { get; set; }
    public bool Playing => playing;
    public float Elapsed => elapsed;
    public float TiltDegree { get; private set; }
    public float TiltToBoundary { get; private set; }
    public float GradationBoundsOffset { get; private set; }
    public float GradationRange { get; private set; }
    public float TextureSwitch { get; private set; }
    public float LayerTranslation { get; private set; }
    public float RadialScaling { get; private set; }
    public float RadialAnimation { get; private set; }
    public float HighlightTranslation { get; private set; }
    public int SurfaceCount => surfaces == null ? 0 : surfaces.Length;
    public int EffectCount => effects == null ? 0 : effects.Length;
    public int FlashCount => flashes == null ? 0 : flashes.Length;
    public int TransitionCount => transitions == null ? 0 : transitions.Length;
    public int TypeIconCount => typeIcons == null ? 0 : typeIcons.Length;

    public void Initialize(SourceCardEffects.Effect source, List<string> log)
    {
        settings = source;
        initializationLog = log;
        IsAnimationStopped = source.isAnimationStopped;
        foreach (var curve in source.curves) {
            var value = JsonUtility.FromJson<SourceCardEffects.NativeCurve>(curve.nativeJson).curve;
            if (value == null || value.length != curve.keys.Length)
                throw new InvalidOperationException("Original DualMRR curve failed to deserialize: " + curve.property);
            curves.Add(curve.property, value);
        }
        // Original Awake also executes for disabled components, but is delayed
        // when the owning GameObject has not become active yet.
        if (gameObject.activeInHierarchy) InitializeNative();
    }

    void Awake() { if (settings != null && !initialized) InitializeNative(); }

    void InitializeNative()
    {
        SearchTag = new ShaderTagId("SearchTag");
        block = new MaterialPropertyBlock();
        var radial = curves["FlashRadialScaling"].keys;
        if (radial.Length == 0) throw new InvalidOperationException("Original DualMRR radial curve is empty");
        minRadial = radial[0].value;
        maxRadial = radial[radial.Length - 1].value;
        ResetState();
        initialized = true;
        // The original Awake calls Initialize and then LateUpdate immediately.
        Step(transform.TransformDirection(Vector3.back), 0);
        ImportDiagnosticPhase(initializationLog);
        initializationLog.Add("OriginalCardEffect=CardDualMRRObject sourceId=" + settings.sourceId
            + " surfaces=" + SurfaceCount + " effects=" + EffectCount + " flashes=" + FlashCount
            + " transitions=" + TransitionCount + " typeIcons=" + TypeIconCount
            + " curves=" + curves.Count + " native=1.8.1 fidelityValidated=False");
        initializationLog.Add("OriginalDualMrrState=" + DiagnosticJson());
        initializationLog = null;
    }

    public void ResetState()
    {
        startTime = Time.time;
        startFront = Vector3.zero;
        elapsed = lastTilt = 0;
        playing = false;
        GradationBoundsOffset = GradationRange = TextureSwitch = 0;
        if (settings != null) EvaluateAnim(0);
    }

    void LateUpdate()
    {
        if (initialized && !Manual)
            Step(transform.TransformDirection(Vector3.back), Time.time > startTime ? Time.deltaTime : 0);
    }

    // Native order: Validate -> UpdateTilt/EvaluateTransition -> UpdateAnimation -> ApplyParams.
    public void Step(Vector3 front, float delta)
    {
        if (!initialized) return;
        CollectRenderers();
        UpdateTilt(front);
        if (playing) AdvanceAnimation(delta);
        else if (lastTilt < settings.animStartDegree && TiltDegree > settings.animStartDegree) {
            playing = true;
            elapsed = 0;
            startFront = front;
            AdvanceAnimation(delta);
        }
        lastTilt = TiltDegree;
        Apply();
    }

    void AdvanceAnimation(float delta)
    {
        elapsed += delta;
        EvaluateAnim(elapsed / settings.animTimeScale);
        playing = elapsed <= settings.animDuration * settings.animTimeScale;
    }

    void UpdateTilt(Vector3 front)
    {
        worldFront = front;
        TiltDegree = Mathf.Atan2(Mathf.Sqrt(front.x * front.x + front.y * front.y), -front.z) * Mathf.Rad2Deg;
        var rotated = Quaternion.Euler(0, 0, -settings.transitionAngle) * front;
        TiltToBoundary = Mathf.Atan2(rotated.y, Mathf.Sqrt(rotated.x * rotated.x + rotated.z * rotated.z)) * Mathf.Rad2Deg;
        // This source flag gates transition evaluation only. Its native
        // UpdateAnimation continues to advance the triggered MRR sequence.
        if (IsAnimationStopped) return;
        float amount = Mathf.Abs(TiltToBoundary) / 30f;
        if (TiltToBoundary < 0) {
            TextureSwitch = curves["TypeEffectTransitionSwitchCurve"].Evaluate(amount);
            GradationBoundsOffset = curves["TransitionBoundsCurve"].Evaluate(amount);
        } else if (TiltToBoundary >= 0) {
            TextureSwitch = -curves["TypeEffectTransitionSwitchCurve"].Evaluate(amount);
            GradationBoundsOffset = -curves["TransitionBoundsCurve"].Evaluate(amount);
        }
        GradationRange = curves["TransitionRangeCurve"].Evaluate(amount);
    }

    float Value(string name) { return curves[name].Evaluate(progress); }

    void EvaluateAnim(float value)
    {
        progress = value;
        LayerTranslation = Value(startFront.x > 0 ? "Layer2UVXTranslateByTiltingRight" : "Layer2UVXTranslateByTiltingLeft");
        float offset = settings.flashRadialStartOffset;
        float range = maxRadial - minRadial;
        RadialScaling = minRadial + offset * range
            + ((range * (1 - offset)) * (Value("FlashRadialScaling") - minRadial)) / range;
        RadialAnimation = offset + (1 - offset) * Value("FlashRadialAnim");
        HighlightTranslation = Value(startFront.x > 0 ? "TypeIconHighlightTranslateByTiltingRight" : "TypeIconHighlightTranslateByTiltingLeft");
    }

    void CollectRenderers()
    {
        // Validate recollects all five groups on every native LateUpdate.
        surfaces = Collect("MRR-ChangeColor-Lighting", "Frame-Holo-2Layer");
        effects = Collect("Card-Effect-Emit");
        flashes = Collect("MRR-Parallax-Flash");
        transitions = Collect("Card-Dual-Transition");
        typeIcons = Collect("Dual-ShadowBoxUI-TypeIcon");
    }

    Renderer[] Collect(params string[] tags)
    {
        var wanted = Array.ConvertAll(tags, tag => new ShaderTagId(tag));
        var found = new List<Renderer>();
        foreach (var renderer in GetComponentsInChildren<Renderer>()) {
            renderer.GetSharedMaterials(materials);
            foreach (var material in materials) {
                if (!material) continue;
                var shader = material.shader;
                for (int pass = 0; pass < shader.passCount; pass++) {
                    var tag = shader.FindPassTagValue(pass, SearchTag);
                    foreach (var candidate in wanted)
                        if (tag == candidate && !found.Contains(renderer)) found.Add(renderer);
                }
            }
        }
        return found.ToArray();
    }

    public void Apply()
    {
        if (!initialized) return;
        ApplyGroup(surfaces, 0); ApplyGroup(effects, 1); ApplyGroup(flashes, 2);
        ApplyGroup(transitions, 3); ApplyGroup(typeIcons, 4);
    }

    void Float(string property, string curve) { block.SetFloat(property, Value(curve)); }

    void ApplyGroup(Renderer[] renderers, int group)
    {
        foreach (var renderer in renderers) {
            renderer.GetSharedMaterials(materials);
            for (int index = 0; index < materials.Count; index++) {
                renderer.GetPropertyBlock(block, index);
                // The host binds dynamic UI textures at renderer level. Copy
                // that block when creating the original indexed block, as in
                // the other source controllers, so texture bindings survive.
                if (block.isEmpty) renderer.GetPropertyBlock(block);
                if (group == 0) {
                    Float("_ChangeColor", "ChangeColorCurve"); Float("_LightColorIntensity", "LightColorIntensityCurve");
                    Float("_LightEmitIntensity", "LightEmitIntensityCurve"); Float("_LightPower", "LightPower");
                    block.SetVector("_Layer2UVTranslate", new Vector4(LayerTranslation, 0, 0, 0));
                    Float("_Layer2EmissiveIntensity", "Layer2EmissiveIntensity"); Float("_Layer2ColorPower", "Layer2ColorPower");
                } else if (group == 1) {
                    Float("_Switch", "EffSwitchColor"); Float("_AdditiveIntensity", "EffAdditiveIntensity");
                    Float("_Color3Blend", "EffColor3Blend"); Float("_EmissiveIntensity", "EffEmissiveIntensity");
                } else if (group == 2) {
                    Float("_FlashIntensity", "FlashIntensity");
                    block.SetFloat("_RadialScaling", RadialScaling); block.SetFloat("_RadialAnim", RadialAnimation);
                } else if (group == 3) {
                    block.SetInt("_IsLeft", TiltToBoundary >= 0 ? 1 : 0);
                    block.SetFloat("_GradationBoundsOffset", GradationBoundsOffset);
                    block.SetFloat("_GradationRange", GradationRange);
                    block.SetFloat("_TextureSwitch", TextureSwitch);
                    block.SetFloat("_MainColorModMultiplier", 1);
                    block.SetFloat("_SubColorModMultiplier", 1);
                } else block.SetVector("_HighlightTranslate", new Vector4(HighlightTranslation, 0, 0, 0));
                renderer.SetPropertyBlock(block, index);
            }
        }
    }

    // Diagnostic evaluation uses original elapsed time, trigger direction and
    // current front. It does not replace ordinary native lifecycle playback.
    public float[] EvaluateRecorded(float sourceElapsed, Vector3 sourceStartFront, Vector3 front)
    {
        elapsed = sourceElapsed;
        startFront = sourceStartFront;
        EvaluateAnim(sourceElapsed / settings.animTimeScale);
        UpdateTilt(front);
        return new[] { Value("ChangeColorCurve"), Value("LightColorIntensityCurve"), Value("LightEmitIntensityCurve"), Value("LightPower"),
            LayerTranslation, Value("Layer2ColorPower"), Value("Layer2EmissiveIntensity"),
            Value("EffSwitchColor"), Value("EffAdditiveIntensity"), Value("EffColor3Blend"), Value("EffEmissiveIntensity"),
            Value("FlashIntensity"), RadialScaling, RadialAnimation, HighlightTranslation,
            TiltToBoundary, GradationBoundsOffset, GradationRange, TextureSwitch };
    }

    static string Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new InvalidDataException("Missing value for " + key);
        return args[index + 1];
    }

    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static Vector3 SourceVector(float[] values, string name)
    {
        if (values == null || values.Length != 3 || !Finite(values[0]) || !Finite(values[1]) || !Finite(values[2]))
            throw new InvalidDataException("Invalid DualMRR source vector " + name);
        return new Vector3(values[0], values[1], values[2]);
    }

    void ImportDiagnosticPhase(List<string> log)
    {
        var args = Environment.GetCommandLineArgs();
        var sourcePath = Argument(args, "--source-dual-rr-json");
        if (sourcePath == null) return;
        var project = Argument(args, "--project-root")
            ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        var config = JsonUtility.FromJson<DiagnosticConfig>(JsonUtility.ToJson(CardSelection.Config));
        sourcePath = Path.GetFullPath(Path.Combine(project, sourcePath));
        var bytes = File.ReadAllBytes(sourcePath);
        var source = JsonUtility.FromJson<SourceObservation>(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
        if (source == null || config == null || source.cardId != "PK_10_022190_00" || source.cardId != config.cardId
            || config.rarity != "RR" || source.size != "L"
            || source.illustration + "_L" != config.prefabName || source.prefabName != source.illustration + "_Root"
            || source.controller == null || source.controller.@class != "Lettuce.Infrastructure.Card.Core.CardDualMRRObject")
            throw new InvalidDataException("DualMRR source/config identity or L/RR release differs");
        if (!source.poseBracketStable || !source.parameterBracketStable || !source.rootStillStable
            || source.before == null || source.after == null || source.before.fields == null || source.after.fields == null)
            throw new InvalidDataException("DualMRR source observation is not bracket-stable");
        var before = source.before.fields;
        if (JsonUtility.ToJson(before) != JsonUtility.ToJson(source.after.fields))
            throw new InvalidDataException("DualMRR source phase/settings changed during observation");
        if (!before._isInitialized || before._isPlaying || before._isAnimationStopped
            || !Finite(before._elapsedTime) || before._elapsedTime < 0 || !Finite(before._lastTiltDegree))
            throw new InvalidDataException("DualMRR diagnostic requires an initialized, nonplaying source with active transition evaluation");
        if (before._animStartDegree != settings.animStartDegree || before._animTimeScale != settings.animTimeScale
            || before._animDuration != settings.animDuration || before._flashRadialStartOffset != settings.flashRadialStartOffset
            || before._transitionAngle != settings.transitionAngle || before._transitionBoundaryOffset == null
            || before._transitionBoundaryOffset.Length != 2
            || before._transitionBoundaryOffset[0] != settings.transitionBoundaryOffset.x
            || before._transitionBoundaryOffset[1] != settings.transitionBoundaryOffset.y)
            throw new InvalidDataException("DualMRR source controller settings differ from the original prefab plan");
        var recordedFront = SourceVector(before._worldFront, "worldFront");
        var recordedStart = SourceVector(before._worldFrontAtAnimStart, "worldFrontAtAnimStart");
        Manual = true;
        playing = before._isPlaying;
        IsAnimationStopped = before._isAnimationStopped;
        lastTilt = before._lastTiltDegree;
        EvaluateRecorded(before._elapsedTime, recordedStart, recordedFront);
        Apply();
        string digest;
        using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        log.Add("OriginalDualMrrSource path=" + sourcePath + " sha256=" + digest + " cardId=" + source.cardId
            + " size=L sourcePhaseImported=True motionReplay=False fidelityValidated=False");
    }

    public string DiagnosticJson()
    {
        return JsonUtility.ToJson(new Snapshot {
            playing=playing, isAnimationStopped=IsAnimationStopped, elapsed=elapsed, progress=progress,
            tiltDegree=TiltDegree, tiltToBoundary=TiltToBoundary, startFront=startFront, worldFront=worldFront,
            changeColor=Value("ChangeColorCurve"), lightColorIntensity=Value("LightColorIntensityCurve"),
            lightEmitIntensity=Value("LightEmitIntensityCurve"), lightPower=Value("LightPower"),
            layerTranslation=LayerTranslation, layer2ColorPower=Value("Layer2ColorPower"),
            layer2EmissiveIntensity=Value("Layer2EmissiveIntensity"), effSwitchColor=Value("EffSwitchColor"),
            effAdditiveIntensity=Value("EffAdditiveIntensity"), effColor3Blend=Value("EffColor3Blend"),
            effEmissiveIntensity=Value("EffEmissiveIntensity"), flashIntensity=Value("FlashIntensity"),
            radialScaling=RadialScaling, radialAnimation=RadialAnimation, highlightTranslation=HighlightTranslation,
            gradationBoundsOffset=GradationBoundsOffset, gradationRange=GradationRange, textureSwitch=TextureSwitch
        });
    }
}
