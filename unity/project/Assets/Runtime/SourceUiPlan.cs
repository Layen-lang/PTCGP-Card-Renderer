using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using TMPro;
using UnityEngine;

// Diagnostic port. Unported text keys are blank, never presented as card data.
public static class SourceUiPlan
{
    [Serializable] public class Plan { public int language, stage, rule, serial, serialNumerator, serialDenominator, isMegaEvolution, isUltraBeast, isFuture, isAncient, trainersType,hasSubType,conditionSchema,isDual,isRGBFormat; public bool isTrainer; public int[] nativeSkippedKeys; public ConditionView[] conditions; public Setting[] settings; public TextBinding[] texts; public Activation[] containers, emptySprites, textActivations; public SpriteBinding[] sprites; public FontReference[] fontReferences; public EnergyIcon[] energyIcons; public NameLayout[] names; public Padding[] skillPadding; public Spacing[] skillSpacing; public DynamicView[] dynamicViews; }
    [Serializable] public class DynamicView { public int[] indices; public string label, selectedName; }
    [Serializable] public class SpriteBinding { public int[] indices; public Asset sprite; }
    [Serializable] public class FontReference { public Asset font, material; }
    [Serializable] public class Padding { public int[] indices; public int value; }
    [Serializable] public class Spacing { public int[] indices; public int threshold; public float lessEqual, greater; }
    [Serializable] public class NameLayout { public int[] indices, exIndices, additionalIndices; public bool active, splitEx, isEx = true; public float maxWidth, maxSplitWidth, exWidth, minWidth, spaceLeftCenter, spaceCenterRight; }
    [Serializable] public class Activation { public int[] indices; public bool active; public int key; public string path; }
    [Serializable] public class EnergyIcon { public int[] indices, imageIndices; public bool active; public Asset sprite; }
    [Serializable] public class ConditionView { public int[] indices; public Condition[] alternatives; }
    [Serializable] public class Condition {
        public int _conditionType, _language, _stage, _trainersType, _hasSubType, _pokemonRuleType,
            _serialNumberType, _isUltraBeast, _isMegaEvolution, _isFuture, _isAncient, _isDual, _isRGBFormat;
    }
    [Serializable] public class Setting { public int[] indices; public Parameters parameters; public int shrink; }
    [Serializable] public class Parameters {
        public int _overrideFontSize, _overrideCharacterSpacing, _overrideLineSpacing, _overrideWordWrappingRatios;
        public float _fontSize, _characterSpacing, _lineSpacing, _positionYOffset, _wordWrappingRatios;
    }
    [Serializable] public class Asset { public string bundle, name, sourceId; }
    [Serializable] public class TextBinding { public int[] indices, additionalIndices; public int key; public Asset font, material, boldFont, boldMaterial, spriteAsset; public bool setValue, wrap,applyScale; public float scaleValue,autoMinScale,autoMinSize; public int lineSlot; public string value, additionalValue,scaleFormat; }

    static Transform Locate(Transform root, int[] indices) {
        foreach (var index in indices) root = root.GetChild(index);
        return root;
    }
    static bool Matches(Condition c, Plan p) {
        int f = c._conditionType;
        if (f == 0) return false;
        // The 1.8.1 schema inserts Dual/RGB and moves HasSubName from 512 to
        // 2048. Plans without this version retain the original 1.7.5 meaning.
        bool modern = p.conditionSchema >= 181;
        int hasSubName = modern ? 2048 : 512;
        if ((f & ~(modern ? 4095 : 1023)) != 0) throw new InvalidOperationException("Unknown condition flag " + f);
        return ((f & 1) == 0 || c._language == p.language)
            && (p.isTrainer || (f & 2) == 0 || c._stage == p.stage)
            && (!p.isTrainer || (f & 4) == 0 || c._trainersType == p.trainersType)
            && (!p.isTrainer || (f & hasSubName) == 0 || c._hasSubType == p.hasSubType)
            && (!modern || p.isTrainer || (f & 512) == 0 || c._isDual == p.isDual)
            && (!modern || p.isTrainer || (f & 1024) == 0 || c._isRGBFormat == p.isRGBFormat)
            && (p.isTrainer || (f & 8) == 0 || c._pokemonRuleType == p.rule)
            && ((f & 16) == 0 || c._serialNumberType == p.serial)
            && (p.isTrainer || (f & 32) == 0 || c._isUltraBeast == p.isUltraBeast)
            && (p.isTrainer || (f & 64) == 0 || c._isMegaEvolution == p.isMegaEvolution)
            && ((f & 128) == 0 || c._isFuture == p.isFuture)
            && ((f & 256) == 0 || c._isAncient == p.isAncient);
    }
    static T Resolve<T>(Asset asset, IDictionary<string, AssetBundle> bundles) where T : UnityEngine.Object {
        var matches = bundles[asset.bundle].LoadAllAssets<T>().Where(a => a.name == asset.name).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Ambiguous asset " + asset.sourceId + " " + asset.name + " matches=" + matches.Length);
        return matches[0];
    }
    public static void Apply(GameObject instance, Plan plan, IDictionary<string, AssetBundle> bundles, List<string> log) {
        if (plan.serial!=1 || plan.serialNumerator!=999 || plan.serialDenominator!=999)
            throw new InvalidOperationException("Unexpected original RR serial data");
        foreach (var text in instance.GetComponentsInChildren<TMP_Text>(true)) text.text = "";
        // Original CardDynamicUIView.Apply selects existing children by name.
        foreach (var binding in plan.dynamicViews ?? new DynamicView[0]) {
            var parent = Locate(instance.transform, binding.indices);
            int selected = 0;
            foreach (Transform child in parent) {
                bool active = child.name == binding.selectedName;
                child.gameObject.SetActive(active);
                if (active) selected++;
            }
            if (!string.IsNullOrEmpty(binding.selectedName) && selected == 0)
                throw new InvalidOperationException("Dynamic UI child missing: " + binding.label);
            log.Add("DYNAMIC_UI_VIEW label=" + binding.label + " selected=" + (binding.selectedName ?? "None"));
        }
        foreach (var c in plan.conditions) Locate(instance.transform, c.indices).gameObject.SetActive(c.alternatives.Any(a => Matches(a, plan)));
        foreach (var c in plan.containers) Locate(instance.transform, c.indices).gameObject.SetActive(c.active);
        foreach (var p in plan.skillPadding) Locate(instance.transform, p.indices).GetComponent<UnityEngine.UI.HorizontalLayoutGroup>().padding.left = p.value;
        foreach (var c in plan.emptySprites) {
            var image = Locate(instance.transform, c.indices).GetComponent<UnityEngine.UI.Image>();
            image.enabled = false;
            image.sprite = null;
        }
        foreach (var binding in plan.sprites ?? new SpriteBinding[0]) {
            var image = Locate(instance.transform, binding.indices).GetComponent<UnityEngine.UI.Image>();
            image.enabled = true;
            image.sprite = Resolve<Sprite>(binding.sprite, bundles);
        }
        foreach (var reference in plan.fontReferences ?? new FontReference[0])
            RegisterFont(reference.font, reference.material, bundles);
        foreach (var icon in plan.energyIcons) {
            Locate(instance.transform, icon.indices).gameObject.SetActive(icon.active);
            if (icon.active) Locate(instance.transform, icon.imageIndices).GetComponent<UnityEngine.UI.Image>().sprite = Resolve<Sprite>(icon.sprite, bundles);
        }
        foreach (var b in plan.texts) {
            var text = Locate(instance.transform, b.indices).GetComponent<TMP_Text>();
            if (!text) throw new InvalidOperationException("Missing TMP key " + b.key);
            // PokemonCardNameView.UpdateText uses the same FontData for the
            // separate suffix shown after the ex sprite on Team Rocket cards.
            if (b.additionalIndices != null && b.additionalIndices.Length > 0) {
                var additional = Locate(instance.transform,b.additionalIndices).GetComponent<TextMeshProUGUI>();
                if (!additional) throw new InvalidOperationException("Missing additional name TMP");
                additional.text = b.additionalValue ?? "";
                additional.font = Resolve<TMP_FontAsset>(b.font,bundles);
                additional.fontMaterial = Resolve<Material>(b.material,bundles);
                additional.UpdateFontAsset();
                additional.SetLayoutDirty();
                additional.ForceMeshUpdate(false,true);
            }
            text.font = Resolve<TMP_FontAsset>(b.font, bundles);
            // CardTextView.UpdateText assigns fontMaterial, explicitly refreshes
            // the font asset, then updates the text/mesh (RVA0x481b540).
            text.fontMaterial = Resolve<Material>(b.material, bundles);
            ((TextMeshProUGUI)text).UpdateFontAsset();
            if ((b.key == 111 || b.key == 121) && b.boldFont != null && !string.IsNullOrEmpty(b.boldFont.bundle)) {
                RegisterFont(b.boldFont, b.boldMaterial, bundles);
                // TMP 6 hashes rich-text values in uppercase; TMP 3's material
                // lookup uses the case-sensitive hash. Register both aliases
                // while the old comparison player remains available.
            }
            if (b.spriteAsset != null && !string.IsNullOrEmpty(b.spriteAsset.bundle)) {
                text.spriteAsset = Resolve<TMP_SpriteAsset>(b.spriteAsset, bundles);
                text.spriteAsset.UpdateLookupTables();
                if (text.spriteAsset.spriteGlyphTable.Count != 4 || text.spriteAsset.spriteCharacterTable.Count != 4
                    || text.spriteAsset.spriteCharacterTable.Any(c => c.glyph == null))
                    throw new InvalidOperationException("Original ex sprite glyphs did not deserialize");
                log.Add("EX_SPRITE glyphs=" + text.spriteAsset.spriteGlyphTable.Count + " characters=" + text.spriteAsset.spriteCharacterTable.Count);
            }
            text.enableWordWrapping = b.wrap;
            if (b.setValue) {
                text.text = b.applyScale ? string.Format(CultureInfo.InvariantCulture,b.scaleFormat,b.value,b.scaleValue) : b.value;
                // Native 1.7.5 CardTextView.UpdateText: virtual slot27 is
                // SetLayoutDirty, slot101 is ForceMeshUpdate(false,true).
                text.SetLayoutDirty();
                text.ForceMeshUpdate(false,true);
            }
            log.Add("BIND " + b.key + " " + text.name + " active=" + text.gameObject.activeInHierarchy + " value=" + (b.setValue ? b.value : "UNPORTED"));
        }
        foreach (var s in plan.settings) {
            var node = Locate(instance.transform, s.indices);
            var text = node.GetComponent<TMP_Text>();
            if (!text) throw new InvalidOperationException("Missing config TMP " + node.name);
            var p = s.parameters;
            if (p != null) {
                if (p._overrideFontSize != 0) text.fontSize = p._fontSize;
                if (p._overrideCharacterSpacing != 0) text.characterSpacing = p._characterSpacing;
                if (p._overrideLineSpacing != 0) text.lineSpacing = p._lineSpacing;
                if (p._overrideWordWrappingRatios != 0) text.wordWrappingRatios = p._wordWrappingRatios;
                node.localPosition += Vector3.up * p._positionYOffset;
            }
            if (s.shrink == 1) text.enableAutoSizing = false;
            else if (s.shrink == 0 || s.shrink == 2) {
                if (s.shrink == 0) {
                    text.overflowMode = TextOverflowModes.Overflow;
                    text.enableWordWrapping = false;
                }
                text.enableAutoSizing = true;
                text.fontSizeMax = text.fontSize;
                text.fontSizeMin = text.fontSize * .1f;
                text.characterWidthAdjustment = 50;
            } else throw new InvalidOperationException("Unknown source shrink mode " + s.shrink);
        }
        foreach (var activation in plan.textActivations) {
            var node=Locate(instance.transform,activation.indices);
            node.gameObject.SetActive(activation.active);
            log.Add("SOURCE_TEXT_ACTIVATION key="+activation.key+" active="+activation.active+" path="+activation.path);
        }
        foreach (var name in plan.names) {
            var node = (RectTransform)Locate(instance.transform, name.indices);
            node.gameObject.SetActive(name.active);
            if (!name.active) continue;
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(node);
            float width = UnityEngine.UI.LayoutUtility.GetPreferredWidth(node);
            // UpdateExLayout (1.7.5 RVA 0x4813664) reserves this
            // width only for EX cards. Other names keep their prefab bounds.
            if (name.isEx && width > name.maxWidth) {
                width = name.maxWidth;
                node.sizeDelta = new Vector2(width, node.sizeDelta.y);
            }
            var ex = (RectTransform)Locate(instance.transform, name.exIndices);
            if (name.splitEx && name.isEx) {
                // Native LayoutExBetweenTexts (1.7.5 RVA 0x4812DB0): reserve
                // the sprite and gaps, then share available width proportionally.
                var additional = Locate(instance.transform, name.additionalIndices).GetComponent<TMP_Text>();
                float left = Mathf.Max(width, name.minWidth);
                float right = Mathf.Max(additional.preferredWidth, name.minWidth);
                float available = Mathf.Max(0, name.maxSplitWidth - name.exWidth - name.spaceLeftCenter - name.spaceCenterRight);
                if (left + right > available) {
                    float factor = available / (left + right);
                    left *= factor;
                    right *= factor;
                }
                SetNameBounds(node, 0, left);
                SetNameBounds(ex, left + name.spaceLeftCenter, name.exWidth);
                SetNameBounds(additional.rectTransform, left + name.spaceLeftCenter + name.exWidth + name.spaceCenterRight, right);
            } else ex.anchoredPosition = new Vector2(width, ex.anchoredPosition.y);
            ex.gameObject.SetActive(name.isEx);
        }
    }
    static void SetNameBounds(RectTransform child, float position, float width)
    {
        // SetChildBounds changes only horizontal anchors, pivot and bounds.
        child.anchorMin = new Vector2(0, child.anchorMin.y);
        child.anchorMax = new Vector2(0, child.anchorMax.y);
        child.pivot = new Vector2(0, child.pivot.y);
        child.anchoredPosition = new Vector2(position, child.anchoredPosition.y);
        child.sizeDelta = new Vector2(width, child.sizeDelta.y);
    }
    static void RegisterFont(Asset font, Asset material, IDictionary<string, AssetBundle> bundles)
    {
        MaterialReferenceManager.AddFontAsset(Resolve<TMP_FontAsset>(font, bundles));
        var resolved = Resolve<Material>(material, bundles);
        foreach (int hash in new[] { TMP_TextUtilities.GetHashCode(resolved.name), TMP_TextUtilities.GetSimpleHashCode(resolved.name) }.Distinct()) {
            if (!MaterialReferenceManager.TryGetMaterial(hash, out var registered))
                MaterialReferenceManager.AddFontMaterial(hash, resolved);
            else if (registered != resolved)
                throw new InvalidOperationException("Conflicting original font material " + resolved.name);
        }
    }
    const int SkillLineBudget = 6, TrainerMaxLines = 8;
    // PokemonCardUIBehaviour.ClampLineCount (1.8.1 RVA 0x4A41B38): take lines one by one from the text
    // keeping the largest share of its own length.
    static int[] ClampLineCount(int[] counts, int budget)
    {
        var clamped = (int[])counts.Clone();
        for (int excess = counts.Sum() - budget; excess > 0; excess--) {
            int best = 0; float bestShare = float.NegativeInfinity;
            for (int i = 0; i < counts.Length; i++) {
                float share = (clamped[i] - 1f) / counts[i];
                if (share > bestShare) { bestShare = share; best = i; }
            }
            clamped[best]--;
        }
        return clamped;
    }
    // CardUIBehaviour.GetScaleText (RVA 0x4A35BF4): lower the scale down to its minimum, then the font size.
    static string ScaleText(TMP_Text text, float minScale, float minSize, int maxLines)
    {
        string source = text.text, current = source;
        double scale = 1, lowest = minScale;
        float size = text.fontSize;
        if (text.GetTextInfo(current).lineCount <= maxLines) return source;
        do {
            scale = Math.Max(scale - .05, lowest);
            current = string.Format(CultureInfo.InvariantCulture, "<scale={1}>{0}</scale>", source, scale);
        } while (text.GetTextInfo(current).lineCount > maxLines && scale > lowest);
        while (text.GetTextInfo(current).lineCount > maxLines) {
            size = Mathf.Max(size - 1, minSize);
            current = string.Format(CultureInfo.InvariantCulture, "<size={2}><scale={1}>{0}</scale></size>", source, scale, size);
            if (size <= minSize) break;
        }
        return current;
    }
    // Returns the total line count used for the skill spacing.
    static int FitLines(Transform root, Plan plan)
    {
        // Views of the other layouts stay inactive: they are never displayed and TMP cannot measure them.
        var slots = plan.texts.Where(b => b.lineSlot > 0 && Locate(root, b.indices).gameObject.activeInHierarchy).ToArray();
        if (plan.isTrainer) {
            foreach (var binding in slots) {
                var text = Locate(root, binding.indices).GetComponent<TMP_Text>();
                if (binding.autoMinScale > 0) { text.text = ScaleText(text, binding.autoMinScale, binding.autoMinSize, TrainerMaxLines); text.ForceMeshUpdate(false, true); }
            }
            return 0;
        }
        var counts = new int[slots.Length == 0 ? 0 : slots.Max(b => b.lineSlot)];
        // UpdateSkillText keeps the last view of each slot.
        foreach (var binding in slots)
            counts[binding.lineSlot - 1] = Locate(root, binding.indices).GetComponent<TMP_Text>().GetTextInfo(Locate(root, binding.indices).GetComponent<TMP_Text>().text).lineCount;
        var clamped = ClampLineCount(counts, SkillLineBudget);
        if (CardSelection.Diagnostics) Debug.Log("LINE_FIT counts=" + string.Join(",", counts) + " clamped=" + string.Join(",", clamped));
        foreach (var binding in slots) {
            if (binding.autoMinScale <= 0) continue;
            var text = Locate(root, binding.indices).GetComponent<TMP_Text>();
            text.text = ScaleText(text, binding.autoMinScale, binding.autoMinSize, clamped[binding.lineSlot - 1]);
            text.ForceMeshUpdate(false, true);
        }
        return clamped.Sum();
    }
    public static void FinalizeLayout(GameObject instance, Plan plan, List<string> log)
    {
        Canvas.ForceUpdateCanvases();
        int lines = FitLines(instance.transform, plan);
        foreach (var s in plan.skillSpacing) {
            var group = Locate(instance.transform, s.indices).GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            group.spacing = lines <= s.threshold ? s.lessEqual : s.greater;
        }
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)instance.transform);
        Canvas.ForceUpdateCanvases();
        log.Add("SKILL_LAYOUT descriptionLines=" + lines);
        int unsupportedActive=0;
        foreach (var binding in plan.texts) {
            var text=Locate(instance.transform,binding.indices).GetComponent<TMP_Text>();
            if (!binding.setValue && text.gameObject.activeInHierarchy) unsupportedActive++;
        }
        if (unsupportedActive!=0) throw new InvalidOperationException("Active card text has unported source data: "+unsupportedActive);
        log.Add("SOURCE_TEXT_COVERAGE activeUnported=0 serial=None(1),999,999");
        foreach (var binding in plan.texts) {
            if (!binding.setValue || !binding.value.Contains("<font=")) continue;
            var text = Locate(instance.transform, binding.indices).GetComponent<TMP_Text>();
            if (!text.gameObject.activeInHierarchy) continue;
            text.ForceMeshUpdate();
            string parsed = text.GetParsedText();
            if (parsed.Contains("<font=") || parsed.Contains("</font>"))
                throw new InvalidOperationException("Original font markup lookup failed for " + text.name);
            log.Add("PARSED_FONT_TEXT " + text.name + "=" + parsed);
        }
    }
}
