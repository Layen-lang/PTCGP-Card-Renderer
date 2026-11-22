using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

// Original 1.8.1 CardDualMSRObject, 0x4a2b6c0..0x4a2cf94.
// Separate flash/outline clocks and transition/reflection springs are intentional.
public sealed class SourceDualMsrCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    readonly Dictionary<string,AnimationCurve> curves = new Dictionary<string,AnimationCurve>();
    readonly Queue<Vector3> distances = new Queue<Vector3>();
    readonly List<Material> materials = new List<Material>();
    Renderer[] shadowboxes, transitions, frameTransitions, typeIcons, effects;
    MaterialPropertyBlock block;
    Vector3 front, startFront, lastFront;
    float tilt, degree, lastDegree, elapsedTime, previousDelta, startTime, time;
    float flashElapsed, outlineElapsed, center, intensity, noise, noiseOffset;
    float flipAnim, flipSpeed, flipBlend, flipPDSpeed, holoPower, frameBandWidth;
    float transitionTilt, transitionSpeed, highlight, effectSwitch, effectAdditive, effectColor, effectEmission;
    int flashState, outlineState, reflectionState;
    bool isLeft, initialized;
    List<string> initializationLog;
    public bool Manual { get; set; }
    public bool IsAnimationStopped { get; set; }

    // Native names make diagnostic and observation comparisons explicit. No
    // unrecorded animation phase is inferred from an idle elapsed value of zero.
    [Serializable] sealed class SourceFields {
        public float _transitionAngle, _transitionBane, _transitionNensei, _animStartDegree;
        public float _flashAnimTimeScale, _flashAnimDuration, _outlineAnimTimeScale, _outlineAnimDuration;
        public float _timeOffset, _minTiltSpeed, _maxTiltSpeed, _minAnimSpeed, _maxAnimSpeed;
        public float _intensityNoiseSpeed, _reflectFlipBookMaxSpeed, _reflectStartBane, _reflectStartNensei;
        public float _reflectEndBane, _reflectEndNensei, _checkRotatingTime, _rotatingEndThreshold, _rotatingStartThreshold;
        public bool _isAnimationStopped, _isLeft;
        public float _outlineReflectCenterX, _outlineReflectColorIntensity, _colorIntensityNoiseStrength, _reflectColorIntensityOffseet;
        public float _reflectFlipBookAnim, _reflectFlipBookSpeed, _reflectFlipBookBlend, _reflectPDSpeed;
        public int _reflectPDState, _flashAnimState, _outlineAnimState;
        public float _holoPower, _frameBandWidth, _transitionPDTilt, _transitionPDSpeed;
        public float[] _worldFront, _worldFrontAtAnimStart, _lastWorldFront, _typeIconHighlightTranslate, _localFront, _worldFrontXY;
        public float _tilt, _tiltDegree, _lastTiltDegree, _flashAnimElapsedTime, _outlineAnimElapsedTime;
        public float _time, _deltaTime, _elapsedTime;
        public float _endAnimElapsedTime, _flashPower, _appliedReflectIntensity;
        public float _effSwitchColor, _effAdditiveIntensity, _effColor3Blend, _effEmissiveIntensity;
    }
    [Serializable] sealed class DiagnosticConfig { public string cardId, rarity, prefabName; }
    [Serializable] sealed class SourceIdentity { public string @class; }
    [Serializable] sealed class SourceBracket { public SourceFields fields; }
    [Serializable] sealed class SourceObservation {
        public string cardId, size, release, illustration, prefabName;
        public bool poseBracketStable, parameterBracketStable, rootStillStable;
        public SourceIdentity controller;
        public SourceBracket before, after;
    }

    public void Initialize(SourceCardEffects.Effect source, List<string> log)
    {
        settings = source;
        initializationLog = log;
        IsAnimationStopped = source.isAnimationStopped;
        foreach (var curve in source.curves) {
            var value = JsonUtility.FromJson<SourceCardEffects.NativeCurve>(curve.nativeJson).curve;
            if (value == null || value.length != curve.keys.Length)
                throw new InvalidOperationException("Original DualMSR curve failed to deserialize: " + curve.property);
            curves.Add(curve.property, value);
        }
        if (gameObject.activeInHierarchy) InitializeNative();
    }
    void Awake() { if (settings != null && !initialized) InitializeNative(); }

    void InitializeNative()
    {
        block = new MaterialPropertyBlock();
        // Original Validate creates/caches each renderer group only once.
        shadowboxes = SourceCardEffects.Collect(transform, "ShadowBox_MSR");
        transitions = SourceCardEffects.Collect(transform, "Card-Dual-Transition");
        frameTransitions = SourceCardEffects.Collect(transform, "Card-Dual-Transition-Frame");
        typeIcons = SourceCardEffects.Collect(transform, "Dual-ShadowBoxUI-TypeIcon");
        effects = SourceCardEffects.Collect(transform, "Card-Effect-Emit");
        startTime = Time.time;
        EvaluateFlash(0); EvaluateOutline(0);
        initialized = true;
        Step(transform.TransformDirection(Vector3.back), 0);
        // As for CardMSRObject: the game's card enters rotating, so its first resting view is past these animations.
        EvaluateFlash(settings.flashAnimDuration); EvaluateOutline(settings.outlineAnimDuration); Apply();
        ImportDiagnostic(initializationLog);
        initializationLog.Add("OriginalCardEffect=CardDualMSRObject sourceId=" + settings.sourceId
            + " shadowboxes=" + shadowboxes.Length + " transitions=" + transitions.Length
            + " frameTransitions=" + frameTransitions.Length + " typeIcons=" + typeIcons.Length
            + " effects=" + effects.Length + " curves=" + curves.Count + " native=1.8.1 fidelityValidated=False");
        initializationLog.Add("OriginalDualMsrState=" + DiagnosticJson());
        initializationLog = null;
    }

    void LateUpdate()
    {
        if (initialized && !Manual)
            Step(transform.TransformDirection(Vector3.back), Time.time > startTime ? Time.deltaTime : 0);
    }
    float Value(string name, float value) { return curves[name].Evaluate(value); }

    public void Step(Vector3 worldFront, float delta)
    {
        if (!initialized) return;
        // Native elapsedTime consumes the preceding frame's delta before Validate.
        elapsedTime += previousDelta;
        previousDelta = delta;
        time = Time.time - startTime;
        front = worldFront;
        float angle = Mathf.Atan2(Mathf.Sqrt(front.x * front.x + front.y * front.y), -front.z);
        degree = angle * Mathf.Rad2Deg;
        tilt = Mathf.Sin(angle * 3);
        distances.Enqueue(new Vector3((front - lastFront).magnitude, delta, elapsedTime));
        while (distances.Count > 0 && elapsedTime - distances.Peek().z > settings.checkRotatingTime) distances.Dequeue();
        lastFront = front;
        UpdateFlash(delta);
        UpdateOutline(delta);
        UpdateTransition(delta);
        UpdateReflection(delta);
        Apply();
        lastDegree = degree;
    }

    void UpdateFlash(float delta)
    {
        if (flashState == 0 && lastDegree < settings.animStartDegree && degree > settings.animStartDegree) {
            flashState = 1;
            startFront = front;
        }
        if (flashState != 1) return;
        flashElapsed += delta;
        EvaluateFlash(flashElapsed / settings.flashAnimTimeScale);
        if (flashElapsed > settings.flashAnimDuration * settings.flashAnimTimeScale) {
            flashState = 0; flashElapsed = 0;
        }
    }
    void UpdateOutline(float delta)
    {
        if (outlineState == 0 && lastDegree < settings.animStartDegree && degree > settings.animStartDegree) {
            outlineState = reflectionState = 1;
        }
        if (outlineState != 1) return;
        outlineElapsed += delta;
        EvaluateOutline(outlineElapsed / settings.outlineAnimTimeScale);
        if (outlineElapsed > settings.outlineAnimDuration * settings.outlineAnimTimeScale) {
            outlineState = 0; outlineElapsed = 0; reflectionState = 2;
        }
    }
    void EvaluateFlash(float progress)
    {
        holoPower = Value("HoloPowerCurve", progress);
        highlight = Value(startFront.x > 0 ? "TypeIconHighlightTranslateByTiltingRight" : "TypeIconHighlightTranslateByTiltingLeft", progress);
        effectSwitch = Value("EffSwitchColor", progress);
        effectAdditive = Value("EffAdditiveIntensity", progress);
        effectColor = Value("EffColor3Blend", progress);
        effectEmission = Value("EffEmissiveIntensity", progress);
    }
    void EvaluateOutline(float progress)
    {
        center = Value("OutlineReflectCenterX", progress);
        noise = Value("ColorIntensityNoiseStrength", progress);
        intensity = Value("OutlineReflectColorIntensity", progress);
    }
    float BoundaryAngle()
    {
        var rotated = Quaternion.Euler(0, 0, -settings.transitionAngle) * front;
        return Mathf.Atan2(rotated.y, Mathf.Sqrt(rotated.x * rotated.x + rotated.z * rotated.z)) * Mathf.Rad2Deg;
    }
    void UpdateTransition(float delta)
    {
        // Native stop flag gates only this spring and its derived frame band.
        if (IsAnimationStopped) return;
        float target = ((30f - BoundaryAngle()) / 60f) * 2f + -1f;
        transitionSpeed += delta * (settings.transitionBane * ((target - transitionTilt) - delta * transitionSpeed)
            - transitionSpeed * settings.transitionNensei) / (delta * settings.transitionNensei + 1);
        transitionTilt = Mathf.Clamp(transitionTilt + delta * transitionSpeed, -1, 1);
        // These evaluations are present in the source, but their return values
        // are discarded. ApplyParams uses the raw PD tilt for both switches.
        Value("BGTransitionSwitchCurve", transitionTilt);
        Value("FrameTransitionSwitchCurve", transitionTilt);
        frameBandWidth = Value("FrameBandWidthCurve", Mathf.Abs(transitionTilt));
        isLeft = transitionTilt < 0;
    }
    void UpdateReflection(float delta)
    {
        // Noise is sampled BEFORE the reflection state transitions, using its
        // previous state (not either flash or outline animation state).
        noiseOffset = reflectionState == 0 ? 0 : Mathf.PerlinNoise(elapsedTime * settings.intensityNoiseSpeed, 0) * noise;
        float distance = 0, interval = 0;
        foreach (var sample in distances) { distance += sample.x; interval += sample.y; }
        float averageSpeed = distance / interval;
        if (reflectionState == 0 && averageSpeed > settings.rotatingStartThreshold && tilt > 0.004999999888241291f)
            reflectionState = 1;
        if ((reflectionState == 1 && outlineState != 1 || reflectionState == 2) && averageSpeed < settings.rotatingEndThreshold)
            reflectionState = 0;
        if (reflectionState == 0 || reflectionState == 1) {
            float target = reflectionState == 1 ? 1 : 0;
            float bane = reflectionState == 1 ? settings.reflectStartBane : settings.reflectEndBane;
            float nensei = reflectionState == 1 ? settings.reflectStartNensei : settings.reflectEndNensei;
            flipPDSpeed += delta * (bane * ((target - flipSpeed) - delta * flipPDSpeed)
                - flipPDSpeed * nensei) / (delta * nensei + 1);
            flipSpeed += delta * flipPDSpeed;
        }
        flipBlend = flipSpeed;
        flipAnim += (flipSpeed * settings.reflectFlipBookMaxSpeed) * delta;
    }

    public void Apply()
    {
        if (!initialized) return;
        ApplyGroup(shadowboxes, 0); ApplyGroup(transitions, 1); ApplyGroup(frameTransitions, 2);
        ApplyGroup(typeIcons, 3); ApplyGroup(effects, 4);
    }
    void ApplyGroup(Renderer[] renderers, int group)
    {
        foreach (var renderer in renderers) {
            renderer.GetSharedMaterials(materials);
            for (int index = 0; index < materials.Count; index++) {
                renderer.GetPropertyBlock(block, index);
                if (block.isEmpty) renderer.GetPropertyBlock(block); // preserve host renderer-level dynamic UI bindings
                if (group == 0) {
                    block.SetFloat("_ReflectCenterX", center);
                    block.SetFloat("_ReflectIntensity", noiseOffset + intensity);
                    block.SetFloat("_FlipAnim", flipAnim); block.SetFloat("_FlipBlend", flipBlend);
                } else if (group == 1) {
                    block.SetFloat("_TransitionSwitch", transitionTilt); block.SetInt("_IsLeft", isLeft ? 1 : 0);
                    block.SetFloat("_TransitionDiffractionPower", holoPower);
                } else if (group == 2) {
                    block.SetFloat("_TransitionSwitch", transitionTilt); block.SetFloat("_BandWidth", frameBandWidth);
                } else if (group == 3) block.SetVector("_HighlightTranslate", new Vector4(highlight, 0, 0, 0));
                else {
                    block.SetFloat("_Switch", effectSwitch); block.SetFloat("_AdditiveIntensity", effectAdditive);
                    block.SetFloat("_Color3Blend", effectColor); block.SetFloat("_EmissiveIntensity", effectEmission);
                }
                renderer.SetPropertyBlock(block, index);
            }
        }
    }

    static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }
    SourceFields Fields()
    {
        return new SourceFields {
            _transitionAngle=settings.transitionAngle, _transitionBane=settings.transitionBane, _transitionNensei=settings.transitionNensei,
            _animStartDegree=settings.animStartDegree, _flashAnimTimeScale=settings.flashAnimTimeScale, _flashAnimDuration=settings.flashAnimDuration,
            _outlineAnimTimeScale=settings.outlineAnimTimeScale, _outlineAnimDuration=settings.outlineAnimDuration, _timeOffset=settings.timeOffset,
            _minTiltSpeed=settings.minTiltSpeed, _maxTiltSpeed=settings.maxTiltSpeed, _minAnimSpeed=settings.minAnimSpeed, _maxAnimSpeed=settings.maxAnimSpeed,
            _intensityNoiseSpeed=settings.intensityNoiseSpeed, _reflectFlipBookMaxSpeed=settings.reflectFlipBookMaxSpeed,
            _reflectStartBane=settings.reflectStartBane, _reflectStartNensei=settings.reflectStartNensei,
            _reflectEndBane=settings.reflectEndBane, _reflectEndNensei=settings.reflectEndNensei, _checkRotatingTime=settings.checkRotatingTime,
            _rotatingEndThreshold=settings.rotatingEndThreshold, _rotatingStartThreshold=settings.rotatingStartThreshold,
            _isAnimationStopped=IsAnimationStopped, _isLeft=isLeft, _outlineReflectCenterX=center, _outlineReflectColorIntensity=intensity,
            _colorIntensityNoiseStrength=noise, _reflectColorIntensityOffseet=noiseOffset, _reflectFlipBookAnim=flipAnim,
            _reflectFlipBookSpeed=flipSpeed, _reflectFlipBookBlend=flipBlend, _reflectPDSpeed=flipPDSpeed, _reflectPDState=reflectionState,
            _flashAnimState=flashState, _outlineAnimState=outlineState, _holoPower=holoPower, _frameBandWidth=frameBandWidth,
            _transitionPDTilt=transitionTilt, _transitionPDSpeed=transitionSpeed, _worldFront=V(front), _worldFrontAtAnimStart=V(startFront),
            _lastWorldFront=V(lastFront), _localFront=V(Vector3.back), _worldFrontXY=new[] { front.x, front.y },
            _typeIconHighlightTranslate=new[] { highlight, 0f, 0f, 0f }, _tilt=tilt,
            _tiltDegree=degree, _lastTiltDegree=lastDegree, _flashAnimElapsedTime=flashElapsed, _outlineAnimElapsedTime=outlineElapsed,
            _time=time, _deltaTime=previousDelta, _elapsedTime=elapsedTime,
            _endAnimElapsedTime=0, _flashPower=0, _appliedReflectIntensity=noiseOffset + intensity,
            _effSwitchColor=effectSwitch, _effAdditiveIntensity=effectAdditive, _effColor3Blend=effectColor, _effEmissiveIntensity=effectEmission
        };
    }
    public string DiagnosticJson() { return JsonUtility.ToJson(Fields()); }

    static string Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new InvalidDataException("Missing value for " + key);
        return args[index + 1];
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static Vector3 Vector(float[] value, string key)
    {
        if (value == null || value.Length != 3 || !Finite(value[0]) || !Finite(value[1]) || !Finite(value[2]))
            throw new InvalidDataException("Invalid DualMSR source vector " + key);
        return new Vector3(value[0], value[1], value[2]);
    }
    static string MaterialBracketFingerprint(SourceFields value)
    {
        var copy = JsonUtility.FromJson<SourceFields>(JsonUtility.ToJson(value));
        // Clocks evolve at rest. A tiny unsubmitted spring-velocity drift does
        // not establish a full state bracket; it is logged separately below.
        copy._time = copy._deltaTime = copy._elapsedTime = copy._transitionPDSpeed = 0;
        return JsonUtility.ToJson(copy);
    }
    void ImportDiagnostic(List<string> log)
    {
        var args = Environment.GetCommandLineArgs();
        var path = Argument(args, "--source-dual-sr-json");
        if (path == null) return;
        var project = Argument(args, "--project-root") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        var config = JsonUtility.FromJson<DiagnosticConfig>(JsonUtility.ToJson(CardSelection.Config));
        path = Path.GetFullPath(Path.Combine(project, path));
        var bytes = File.ReadAllBytes(path);
        var source = JsonUtility.FromJson<SourceObservation>(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
        if (source == null || config == null || source.cardId != "PK_20_022190_00" || source.cardId != config.cardId
            || config.rarity != "SR" || source.size != "L"
            || config.prefabName != source.illustration + "_L" || source.prefabName != source.illustration + "_Root"
            || source.controller == null || source.controller.@class != "Lettuce.Infrastructure.Card.Core.CardDualMSRObject")
            throw new InvalidDataException("DualMSR source/config identity or L/SR release differs");
        if (!source.poseBracketStable || !source.rootStillStable || source.before == null || source.after == null
            || source.before.fields == null || source.after.fields == null
            || MaterialBracketFingerprint(source.before.fields) != MaterialBracketFingerprint(source.after.fields))
            throw new InvalidDataException("DualMSR source material/phase fields are not exactly bracket-stable");
        var f = source.before.fields;
        var current = Fields();
        var configNames = new[] { "_transitionAngle", "_transitionBane", "_transitionNensei", "_animStartDegree", "_flashAnimTimeScale", "_flashAnimDuration",
            "_outlineAnimTimeScale", "_outlineAnimDuration", "_timeOffset", "_minTiltSpeed", "_maxTiltSpeed", "_minAnimSpeed", "_maxAnimSpeed",
            "_intensityNoiseSpeed", "_reflectFlipBookMaxSpeed", "_reflectStartBane", "_reflectStartNensei", "_reflectEndBane", "_reflectEndNensei",
            "_checkRotatingTime", "_rotatingEndThreshold", "_rotatingStartThreshold" };
        foreach (var name in configNames) {
            var field = typeof(SourceFields).GetField(name);
            if (!field.GetValue(current).Equals(field.GetValue(f))) throw new InvalidDataException("DualMSR source settings differ: " + name);
        }
        foreach (var field in typeof(SourceFields).GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (field.FieldType == typeof(float) && !Finite((float)field.GetValue(f))) throw new InvalidDataException("Nonfinite DualMSR source field " + field.Name);
        if (f._isAnimationStopped != IsAnimationStopped || f._flashAnimState != 0 || f._outlineAnimState != 0 || f._reflectPDState != 0 || f._isAnimationStopped
            || f._flashAnimElapsedTime != 0 || f._outlineAnimElapsedTime != 0 || f._reflectColorIntensityOffseet != 0)
            throw new InvalidDataException("DualMSR diagnostic currently accepts an idle retained state with zero reflection noise only");
        var recordedFront = Vector(f._worldFront, "front");
        var recordedStart = Vector(f._worldFrontAtAnimStart, "startFront");
        var recordedLast = Vector(f._lastWorldFront, "lastFront");
        if (f._typeIconHighlightTranslate == null || f._typeIconHighlightTranslate.Length != 4
            || !Finite(f._typeIconHighlightTranslate[0]) || f._typeIconHighlightTranslate[1] != 0
            || f._typeIconHighlightTranslate[2] != 0 || f._typeIconHighlightTranslate[3] != 0)
            throw new InvalidDataException("DualMSR source highlight vector differs from its native X-only state");
        Manual = true;
        front=recordedFront; startFront=recordedStart; lastFront=recordedLast;
        elapsedTime=f._elapsedTime; previousDelta=f._deltaTime; time=f._time;
        degree=f._tiltDegree; lastDegree=f._lastTiltDegree; tilt=f._tilt;
        flashState=f._flashAnimState; outlineState=f._outlineAnimState; reflectionState=f._reflectPDState;
        flashElapsed=f._flashAnimElapsedTime; outlineElapsed=f._outlineAnimElapsedTime;
        center=f._outlineReflectCenterX; intensity=f._outlineReflectColorIntensity; noise=f._colorIntensityNoiseStrength;
        noiseOffset=0; // calculated from the observed reflection state zero, as in UpdateReflection
        flipAnim=f._reflectFlipBookAnim; flipSpeed=f._reflectFlipBookSpeed; flipBlend=f._reflectFlipBookBlend; flipPDSpeed=f._reflectPDSpeed;
        transitionTilt=f._transitionPDTilt; transitionSpeed=f._transitionPDSpeed;
        // These values remain derivable from the recorded PD tilt even though
        // the flash/outline curve evaluation phase has been reset and lost.
        frameBandWidth=Value("FrameBandWidthCurve", Mathf.Abs(transitionTilt));
        isLeft=transitionTilt < 0;
        holoPower=f._holoPower; highlight=f._typeIconHighlightTranslate[0];
        effectSwitch=f._effSwitchColor; effectAdditive=f._effAdditiveIntensity; effectColor=f._effColor3Blend; effectEmission=f._effEmissiveIntensity;
        Apply();
        string digest;
        using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        log.Add("OriginalDualMsrSource path=" + path + " sha256=" + digest + " cardId=" + source.cardId
            + " size=L sampledState=True curvesPhaseReplayed=False materialBracketStable=True stateBracketStable=" + source.parameterBracketStable
            + " sourcePhaseImported=True transitionSpeedBefore=" + f._transitionPDSpeed.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + " transitionSpeedAfter=" + source.after.fields._transitionPDSpeed.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + " motionReplay=False fidelityValidated=False");
    }
}
