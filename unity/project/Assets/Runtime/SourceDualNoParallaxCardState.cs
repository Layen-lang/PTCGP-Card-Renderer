using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

// CardDualNoParallaxObject 1.8.1, 0x4a2cf94..0x4a2d66c.
// The original only animates the dual type icon's highlight. No substitute MRR/MSR effects.
public sealed class SourceDualNoParallaxCardState : MonoBehaviour
{
    SourceCardEffects.Effect settings;
    readonly Dictionary<string,AnimationCurve> curves = new Dictionary<string,AnimationCurve>();
    readonly List<Material> materials = new List<Material>();
    MaterialPropertyBlock block;
    Renderer[] typeIcons;
    Vector3 front, startFront;
    float elapsed, startTime, time, delta, lastDegree, degree, highlight;
    bool playing, initialized;
    List<string> initializationLog;
    public bool Manual { get; set; }
    // The source's nonserialized backing property is not consulted by any
    // update method. Preserve that behavior rather than adding an invented gate.
    public bool IsAnimationStopped { get; set; }

    [Serializable] sealed class Fields {
        public float _animStartDegree, _animTimeScale, _animDuration;
        public float[] _localFront, _worldFront, _worldFrontXY, _worldFrontAtAnimStart, _typeIconHighlightTranslate;
        public bool _isPlaying, isAnimationStopped;
        public float _elapsedTime, _time, _deltaTime, _lastTiltDegree, _tiltDegree;
    }
    [Serializable] sealed class DiagnosticConfig { public string cardId, rarity, prefabName; }
    [Serializable] sealed class SourceIdentity { public string @class; }
    [Serializable] sealed class SourceBracket { public Fields fields; }
    [Serializable] sealed class SourceObservation {
        public string cardId, size, release, illustration, prefabName;
        public bool poseBracketStable, parameterBracketStable, rootStillStable;
        public SourceIdentity controller;
        public SourceBracket before, after;
    }

    public void Initialize(SourceCardEffects.Effect source, List<string> log)
    {
        settings=source; initializationLog=log; IsAnimationStopped=source.isAnimationStopped;
        foreach (var curve in source.curves) {
            var value=JsonUtility.FromJson<SourceCardEffects.NativeCurve>(curve.nativeJson).curve;
            if (value == null || value.length != curve.keys.Length)
                throw new InvalidOperationException("Original DualNoParallax curve failed to deserialize: " + curve.property);
            curves.Add(curve.property, value);
        }
        if (gameObject.activeInHierarchy) InitializeNative();
    }
    void Awake() { if (settings != null && !initialized) InitializeNative(); }
    void InitializeNative()
    {
        block=new MaterialPropertyBlock();
        // SearchTagId is initialized by the existing collector on the main
        // Unity path, not by this MonoBehaviour's constructor/static initializer.
        typeIcons=SourceCardEffects.Collect(transform, "Dual-ShadowBoxUI-TypeIcon");
        startTime=Time.time; playing=false;
        Evaluate(0); initialized=true;
        Step(transform.TransformDirection(Vector3.back), 0);
        ImportDiagnostic(initializationLog);
        initializationLog.Add("OriginalCardEffect=CardDualNoParallaxObject sourceId=" + settings.sourceId
            + " typeIcons=" + typeIcons.Length + " curves=" + curves.Count + " native=1.8.1 fidelityValidated=False");
        initializationLog.Add("OriginalDualNoParallaxState=" + DiagnosticJson());
        initializationLog=null;
    }
    void LateUpdate()
    {
        if (initialized && !Manual) Step(transform.TransformDirection(Vector3.back), Time.time > startTime ? Time.deltaTime : 0);
    }
    void SetFront(Vector3 worldFront)
    {
        front=worldFront;
        degree=Mathf.Atan2(Mathf.Sqrt(front.x * front.x + front.y * front.y), -front.z) * Mathf.Rad2Deg;
    }
    public void Step(Vector3 worldFront, float deltaTime)
    {
        if (!initialized) return;
        delta=deltaTime; time=Time.time - startTime;
        SetFront(worldFront);
        if (playing) Advance(deltaTime);
        else if (lastDegree < settings.animStartDegree && degree > settings.animStartDegree) {
            playing=true; elapsed=0; startFront=front;
            Advance(deltaTime);
        }
        lastDegree=degree;
        Apply();
    }
    void Advance(float deltaTime)
    {
        elapsed += deltaTime;
        Evaluate(elapsed / settings.animTimeScale);
        playing=elapsed <= settings.animDuration * settings.animTimeScale;
    }
    void Evaluate(float progress)
    {
        highlight=curves[startFront.x > 0 ? "TypeIconHighlightTranslateByTiltingRight" : "TypeIconHighlightTranslateByTiltingLeft"].Evaluate(progress);
    }
    public void Apply()
    {
        if (!initialized) return;
        foreach (var renderer in typeIcons) {
            renderer.GetSharedMaterials(materials);
            for (int index=0; index<materials.Count; index++) {
                renderer.GetPropertyBlock(block, index);
                if (block.isEmpty) renderer.GetPropertyBlock(block); // preserve host dynamic UI bindings
                block.SetVector("_HighlightTranslate", new Vector4(highlight, 0, 0, 0));
                renderer.SetPropertyBlock(block, index);
            }
        }
    }
    public float EvaluateRecorded(float sourceElapsed, Vector3 sourceStartFront, Vector3 sourceFront)
    {
        elapsed=sourceElapsed; startFront=sourceStartFront; SetFront(sourceFront);
        Evaluate(elapsed / settings.animTimeScale);
        return highlight;
    }
    static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }
    Fields Snapshot()
    {
        return new Fields {
            _animStartDegree=settings.animStartDegree, _animTimeScale=settings.animTimeScale, _animDuration=settings.animDuration,
            _localFront=V(Vector3.back), _worldFront=V(front), _worldFrontXY=new[] { front.x, front.y }, _worldFrontAtAnimStart=V(startFront),
            _typeIconHighlightTranslate=new[] { highlight, 0f, 0f, 0f }, _isPlaying=playing, isAnimationStopped=IsAnimationStopped,
            _elapsedTime=elapsed, _time=time, _deltaTime=delta, _lastTiltDegree=lastDegree, _tiltDegree=degree
        };
    }
    public string DiagnosticJson()
    {
        return JsonUtility.ToJson(Snapshot()).Replace("\"isAnimationStopped\":", "\"<IsAnimationStopped>k__BackingField\":");
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static Vector3 Vector(float[] values, string name)
    {
        if (values == null || values.Length != 3 || !Finite(values[0]) || !Finite(values[1]) || !Finite(values[2]))
            throw new InvalidDataException("Invalid DualNoParallax source vector " + name);
        return new Vector3(values[0], values[1], values[2]);
    }
    static string Argument(string[] args, string key)
    {
        int index=Array.IndexOf(args, key);
        if (index<0) return null;
        if (index+1>=args.Length || args[index+1].StartsWith("--", StringComparison.Ordinal)) throw new InvalidDataException("Missing value for " + key);
        return args[index+1];
    }
    static string StableFingerprint(Fields fields)
    {
        var copy=JsonUtility.FromJson<Fields>(JsonUtility.ToJson(fields));
        copy._time=copy._deltaTime=0;
        return JsonUtility.ToJson(copy);
    }
    void ImportDiagnostic(List<string> log)
    {
        var args=Environment.GetCommandLineArgs();
        var path=Argument(args, "--source-dual-sar-json");
        if (path == null) return;
        var project=Argument(args, "--project-root") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        var config=JsonUtility.FromJson<DiagnosticConfig>(JsonUtility.ToJson(CardSelection.Config));
        path=Path.GetFullPath(Path.Combine(project, path));
        var bytes=File.ReadAllBytes(path);
        var json=System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff')
            .Replace("\"<IsAnimationStopped>k__BackingField\":", "\"isAnimationStopped\":");
        var source=JsonUtility.FromJson<SourceObservation>(json);
        if (source == null || config == null || source.cardId != "PK_20_022190_01" || source.cardId != config.cardId
            || config.rarity != "SAR" || source.size != "L"
            || config.prefabName != source.illustration + "_L" || source.prefabName != source.illustration + "_Root"
            || source.controller == null || source.controller.@class != "Lettuce.Infrastructure.Card.Core.CardDualNoParallaxObject")
            throw new InvalidDataException("DualNoParallax source/config identity or L/SAR release differs");
        if (!source.poseBracketStable || !source.parameterBracketStable || !source.rootStillStable
            || source.before == null || source.after == null || source.before.fields == null || source.after.fields == null
            || StableFingerprint(source.before.fields) != StableFingerprint(source.after.fields))
            throw new InvalidDataException("DualNoParallax source phase is not bracket-stable");
        var f=source.before.fields;
        if (f._animStartDegree != settings.animStartDegree || f._animTimeScale != settings.animTimeScale || f._animDuration != settings.animDuration
            || f._isPlaying || !Finite(f._elapsedTime) || f._elapsedTime < 0 || !Finite(f._lastTiltDegree))
            throw new InvalidDataException("DualNoParallax source settings or idle phase differ from the supported diagnostic");
        var recordedStart=Vector(f._worldFrontAtAnimStart, "startFront");
        var recordedFront=Vector(f._worldFront, "front");
        Manual=true; playing=f._isPlaying; IsAnimationStopped=f.isAnimationStopped;
        lastDegree=f._lastTiltDegree; time=f._time; delta=f._deltaTime;
        EvaluateRecorded(f._elapsedTime, recordedStart, recordedFront);
        Apply();
        string digest;
        using (var sha=SHA256.Create()) digest=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        log.Add("OriginalDualNoParallaxSource path=" + path + " sha256=" + digest + " cardId=" + source.cardId
            + " size=L sourcePhaseImported=True sampledMaterialValues=False curvePhaseReplayed=True motionReplay=False fidelityValidated=False");
    }
}
