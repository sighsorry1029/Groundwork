using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TextCore;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Groundwork;

// Shared by key hints and inline descriptions; the image remains the game's own wheel artwork.
internal static class GroundworkInputIcons
{
    private const string AssetName = "Groundwork_InputIcons";
    private const string SpriteName = "mousew_icon";
    private const string WheelTag = "<sprite=\"" + AssetName + "\" name=\"" + SpriteName + "\" tint=0>";
    private static Sprite? _nativeWheel;
    private static TMP_SpriteAsset? _asset;
    private static Material? _material;
    private static Texture2D? _texture;
    private static Dictionary<int, TMP_SpriteAsset>? _spriteLookup;
    private static Dictionary<int, Material>? _materialLookup;
    private static int _assetHash;

    internal static string MouseWheel => _asset != null ? WheelTag : "Wheel";
    internal static Sprite? MouseWheelSprite => _nativeWheel;

    internal static void Initialize(KeyHints hints)
    {
        if (hints == null)
        {
            return;
        }

        try
        {
            Sprite? sprite = hints.GetComponentsInChildren<Image>(includeInactive: true)
                .Select(image => image.sprite)
                .FirstOrDefault(candidate => candidate != null && candidate.name == SpriteName);
            if (sprite != null) _nativeWheel = sprite;
            if (_asset != null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                sprite == null || sprite.texture == null ||
                (sprite.packed && sprite.packingRotation != SpritePackingRotation.None))
            {
                return;
            }

            // TMP has no public unregister API. Resolve the two caches before registering anything
            // so shutdown can remove only our entries instead of leaving destroyed named assets.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo? versionField = typeof(TMP_Asset).GetField("m_Version", flags);
            MaterialReferenceManager manager = MaterialReferenceManager.instance;
            _spriteLookup = typeof(MaterialReferenceManager).GetField("m_SpriteAssetReferenceLookup", flags)
                ?.GetValue(manager) as Dictionary<int, TMP_SpriteAsset>;
            _materialLookup = typeof(MaterialReferenceManager).GetField("m_FontMaterialReferenceLookup", flags)
                ?.GetValue(manager) as Dictionary<int, Material>;
            _assetHash = TMP_TextUtilities.GetHashCode(AssetName);
            if (versionField?.FieldType != typeof(string) || _spriteLookup == null || _materialLookup == null ||
                _spriteLookup.ContainsKey(_assetHash) || _materialLookup.ContainsKey(_assetHash))
            {
                return;
            }

            Shader? shader = Shader.Find("TextMeshPro/Sprite");
            if (shader == null || !shader.isSupported)
            {
                return;
            }

            _texture = CopyWheelTexture(sprite);
            _material = new Material(shader)
            {
                name = AssetName + "_Material",
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = _texture
            };
            _asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            _asset.name = AssetName;
            _asset.hideFlags = HideFlags.HideAndDontSave;
            // A new asset must use the current tables, not TMP's legacy spriteInfoList upgrade.
            versionField!.SetValue(_asset, "1.1.0");
            _asset.spriteSheet = _texture;
            _asset.material = _material;
            float width = _texture.width;
            float height = _texture.height;
            float padding = height * 0.06f;
            // Enlarge the inline icon around the text's optical center, with equal side bearings.
            TMP_SpriteGlyph glyph = new(0,
                new GlyphMetrics(width, height, padding, height * 0.85f, width + padding * 2f),
                new GlyphRect(0, 0, _texture.width, _texture.height), 1.4f, 0);
            _asset.spriteGlyphTable.Add(glyph);
            _asset.spriteCharacterTable.Add(new TMP_SpriteCharacter(0xFFFE, _asset, glyph) { name = SpriteName });
            _asset.UpdateLookupTables();
            MaterialReferenceManager.AddSpriteAsset(_assetHash, _asset);
        }
        catch (Exception exception)
        {
            Sprite? nativeWheel = _nativeWheel;
            Shutdown();
            _nativeWheel = nativeWheel;
            GroundworkPlugin.ModLogger.LogDebug("Inline mouse wheel icon unavailable; keeping text fallback. " +
                                               exception.GetBaseException().Message);
        }
    }

    internal static void Shutdown()
    {
        if (_asset != null)
        {
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text != null && text.text != null && text.text.Contains(WheelTag))
                {
                    Localization.instance?.RemoveTextFromCache(text);
                    text.text = text.text.Replace(WheelTag, "Wheel");
                }
            }
        }

        if (_spriteLookup != null && _spriteLookup.TryGetValue(_assetHash, out TMP_SpriteAsset spriteEntry) &&
            ReferenceEquals(spriteEntry, _asset))
        {
            _spriteLookup.Remove(_assetHash);
        }

        if (_materialLookup != null && _materialLookup.TryGetValue(_assetHash, out Material materialEntry) &&
            ReferenceEquals(materialEntry, _material))
        {
            _materialLookup.Remove(_assetHash);
        }

        if (_asset != null) Object.Destroy(_asset);
        if (_material != null) Object.Destroy(_material);
        if (_texture != null) Object.Destroy(_texture);
        _asset = null;
        _material = null;
        _texture = null;
        _spriteLookup = null;
        _materialLookup = null;
        _assetHash = 0;
        _nativeWheel = null;
    }

    private static Texture2D CopyWheelTexture(Sprite sprite)
    {
        Vector2[] uv = sprite.uv;
        ushort[] triangles = sprite.triangles;
        if (uv.Length < 3 || triangles.Length < 3)
        {
            throw new InvalidOperationException("The wheel sprite has no usable geometry.");
        }

        Vector2 minimum = new(uv.Min(point => point.x), uv.Min(point => point.y));
        Vector2 maximum = new(uv.Max(point => point.x), uv.Max(point => point.y));
        Vector2 size = maximum - minimum;
        int width = Mathf.CeilToInt(size.x * sprite.texture.width);
        int height = Mathf.CeilToInt(size.y * sprite.texture.height);
        if (width <= 0 || height <= 0 || width > 512 || height > 512)
        {
            throw new InvalidOperationException("The wheel sprite has invalid atlas bounds.");
        }

        RenderTexture? target = null;
        Texture2D? copy = null;
        RenderTexture previous = RenderTexture.active;
        bool previousSrgbWrite = GL.sRGBWrite;
        try
        {
            // GPU cropping works even when the vanilla atlas is not CPU-readable. UV bounds
            // avoid textureRect, which can throw for tight-packed sprites.
            target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            GL.sRGBWrite = target.sRGB;
            Graphics.Blit(sprite.texture, target, size, minimum);
            RenderTexture.active = target;
            copy = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = AssetName + "_Texture",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, recalculateMipMaps: false);
            Color32[] pixels = copy.GetPixels32();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Vector2 point = minimum + new Vector2((x + 0.5f) / width * size.x, (y + 0.5f) / height * size.y);
                bool inside = false;
                for (int i = 0; i + 2 < triangles.Length && !inside; i += 3)
                {
                    inside = IsInsideTriangle(point, uv[triangles[i]], uv[triangles[i + 1]], uv[triangles[i + 2]]);
                }

                // Tight packing may place other artwork inside the bounding rectangle's corners.
                if (!inside) pixels[y * width + x] = default;
            }

            copy.SetPixels32(pixels);
            copy.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return copy;
        }
        catch
        {
            if (copy != null) Object.Destroy(copy);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = previousSrgbWrite;
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private static bool IsInsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (Mathf.Abs(area) <= 1e-12f) return false;
        float ab = (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);
        float bc = (c.x - b.x) * (point.y - b.y) - (c.y - b.y) * (point.x - b.x);
        float ca = (a.x - c.x) * (point.y - c.y) - (a.y - c.y) * (point.x - c.x);
        return ab >= 0f && bc >= 0f && ca >= 0f || ab <= 0f && bc <= 0f && ca <= 0f;
    }
}

internal sealed class KeyHintCell
{
    private const string WheelImageName = "Groundwork_MouseWheel";
    private const string WheelSeparatorName = "Groundwork_MouseWheelSeparator";
    private readonly bool _hideOnRestore;
    private readonly Transform? _originalParent;
    private readonly int _originalSiblingIndex;
    private readonly List<TMP_Text> _keys = [];
    private readonly List<GameObject> _keyParents = [];
    private readonly List<TMP_Text> _extraTexts = [];
    private readonly List<TMP_Text> _texts = [];
    private readonly List<TMP_Text> _textScan = [];
    private readonly List<string> _originalKeyTexts = [];
    private readonly List<bool> _originalKeyParentStates = [];
    private readonly List<bool> _originalExtraTextStates = [];
    private readonly HashSet<GameObject> _generatedKeyParents = [];
    private TMP_Text? _label;
    private Image? _wheelImage;
    private TextMeshProUGUI? _wheelSeparator;
    private bool _capturedOriginals;
    private bool _originalRootActive;
    private string _originalLabel = string.Empty;
    private float? _originalLabelPreferredWidth;
    private bool _layoutChanged;

    private KeyHintCell(GameObject root, bool hideOnRestore)
    {
        Root = root;
        _hideOnRestore = hideOnRestore;
        _originalParent = root.transform.parent;
        _originalSiblingIndex = root.transform.GetSiblingIndex();
        _wheelImage = root.GetComponentsInChildren<Image>(includeInactive: true)
            .FirstOrDefault(image => image.name == WheelImageName);
        _wheelSeparator = root.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true)
            .FirstOrDefault(text => text.name == WheelSeparatorName);
        HideWheelImage();
        RefreshChildren();
    }

    internal GameObject Root { get; }

    internal bool IsValid => Root != null && (_label != null || _keys.Count > 0 || _extraTexts.Count > 0);

    internal int OriginalSiblingIndex => _originalSiblingIndex;

    internal static KeyHintCell? Resolve(Transform owner, string transformPath)
    {
        Transform transform = owner.Find(transformPath);
        return transform != null && IsUsableTemplate(transform.gameObject)
            ? new KeyHintCell(transform.gameObject, hideOnRestore: false)
            : null;
    }

    internal static KeyHintCell? CloneFrom(KeyHintCell? template, string name, bool hideOnRestore)
    {
        return CloneFrom(template?.Root, name, hideOnRestore);
    }

    internal static KeyHintCell? CloneFrom(GameObject? template, string name, bool hideOnRestore)
    {
        if (!IsUsableTemplate(template) || template!.transform.parent == null)
        {
            return null;
        }

        GameObject clone = Object.Instantiate(template, template.transform.parent, false);
        clone.name = name;
        clone.SetActive(false);
        return new KeyHintCell(clone, hideOnRestore);
    }

    internal static bool IsUsableTemplate(GameObject? template)
    {
        return template != null &&
               template.transform.parent != null &&
               !template.name.StartsWith("Groundwork_") &&
               template.GetComponentInChildren<TMP_Text>(includeInactive: true) != null;
    }

    internal static Transform? FindParentWithTemplates(GameObject root, string name)
    {
        Transform transform = root.transform.Find(name);
        if (transform == null)
        {
            return null;
        }

        return transform
            .Cast<Transform>()
            .Any(static child => IsUsableTemplate(child.gameObject))
            ? transform
            : null;
    }

    internal void Set(string label, IReadOnlyList<string> keys, float preferredTextWidth = 0f, bool hideExtraTexts = false)
    {
        HideWheelImage();
        Apply(label, keys, preferredTextWidth, hideExtraTexts);
    }

    private void Apply(string label, IReadOnlyList<string> keys, float preferredTextWidth, bool hideExtraTexts, bool activateRoot = true)
    {
        EnsureKeyCount(keys.Count);
        CaptureOriginals();
        if (activateRoot)
        {
            SetActive(true);
        }

        if (_label != null)
        {
            SetText(_label, label);
            if (preferredTextWidth > 0f && _label.TryGetComponent(out LayoutElement layoutElement))
            {
                if (layoutElement.preferredWidth != preferredTextWidth)
                {
                    layoutElement.preferredWidth = preferredTextWidth;
                    _layoutChanged = true;
                }
            }
        }

        for (int i = 0; i < _keys.Count; i++)
        {
            bool show = i < keys.Count;
            if (i < _keyParents.Count && _keyParents[i] != null)
            {
                SetActive(_keyParents[i], show);
            }

            if (show)
            {
                SetText(_keys[i], keys[i]);
            }
        }

        if (hideExtraTexts)
        {
            foreach (TMP_Text extraText in _extraTexts)
            {
                if (extraText != null)
                {
                    SetActive(extraText.gameObject, false);
                }
            }
        }
    }

    internal void SetWithMouseWheel(string label, IReadOnlyList<string> modifierKeys,
        float preferredTextWidth = 0f, bool hideExtraTexts = false)
    {
        Sprite? sprite = GroundworkInputIcons.MouseWheelSprite;
        if (sprite == null || _keyParents.Count == 0 || _keyParents[0].transform.parent == null)
        {
            Set(label, modifierKeys.Concat(new[] { "Wheel" }).ToArray(), preferredTextWidth, hideExtraTexts);
            return;
        }

        Apply(label, modifierKeys, preferredTextWidth, hideExtraTexts || modifierKeys.Count == 0);
        if (!hideExtraTexts && modifierKeys.Count > 0)
        {
            for (int i = 0; i < _extraTexts.Count && i < _originalExtraTextStates.Count; i++)
            {
                if (_extraTexts[i] != null)
                {
                    // A template's '+' can still separate multiple modifier boxes; the wheel owns its separator.
                    bool show = _originalExtraTextStates[i] &&
                                (modifierKeys.Count > 1 || _extraTexts[i].text.Trim() != "+");
                    SetActive(_extraTexts[i].gameObject, show);
                }
            }
        }

        if (_wheelImage == null)
        {
            GameObject icon = new(WheelImageName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(LayoutElement));
            icon.layer = Root.layer;
            icon.transform.SetParent(_keyParents[0].transform.parent, worldPositionStays: false);
            _wheelImage = icon.GetComponent<Image>();
            _wheelImage.color = Color.white;
            _wheelImage.preserveAspect = true;
            _wheelImage.useSpriteMesh = true;
            _wheelImage.raycastTarget = false;
            _layoutChanged = true;
        }

        // Match vanilla Rotate's 60px-high wheel without cloning a keyboard-key background.
        float height = 60f;
        float width = height * sprite.rect.width / Mathf.Max(1f, sprite.rect.height);
        LayoutElement layout = _wheelImage.GetComponent<LayoutElement>();
        if (layout.minWidth != width || layout.preferredWidth != width ||
            layout.minHeight != height || layout.preferredHeight != height ||
            layout.flexibleWidth != 0f || layout.flexibleHeight != 0f)
        {
            layout.minWidth = layout.preferredWidth = width;
            layout.minHeight = layout.preferredHeight = height;
            layout.flexibleWidth = layout.flexibleHeight = 0f;
            _layoutChanged = true;
        }
        Vector2 size = new(width, height);
        if (_wheelImage.rectTransform.sizeDelta != size)
        {
            _wheelImage.rectTransform.sizeDelta = size;
            _layoutChanged = true;
        }
        if (_wheelImage.sprite != sprite)
        {
            _wheelImage.sprite = sprite;
            _layoutChanged = true;
        }
        SetActive(_wheelImage.gameObject, true);
        UpdateWheelSeparator(modifierKeys.Count > 0);
    }

    private void UpdateWheelSeparator(bool hasModifier)
    {
        if (_wheelImage == null)
        {
            return;
        }

        if (hasModifier && _wheelSeparator == null)
        {
            TMP_Text source = _label ?? _keys[0];
            GameObject separator = new(WheelSeparatorName, typeof(RectTransform));
            separator.SetActive(false);
            separator.layer = Root.layer;
            separator.transform.SetParent(_wheelImage.transform.parent, false);
            _wheelSeparator = separator.AddComponent<TextMeshProUGUI>();
            _wheelSeparator.font = source.font;
            _wheelSeparator.fontSharedMaterial = source.fontSharedMaterial;
            _wheelSeparator.fontSize = source.fontSize;
            _wheelSeparator.alignment = TextAlignmentOptions.Center;
            _wheelSeparator.textWrappingMode = TextWrappingModes.NoWrap;
            _wheelSeparator.raycastTarget = false;
            _wheelSeparator.text = "+";
            LayoutElement layout = separator.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = Mathf.Max(12f, source.fontSize * 0.75f);
            layout.minHeight = layout.preferredHeight = source.fontSize * 1.5f;
            layout.flexibleWidth = layout.flexibleHeight = 0f;
            _wheelSeparator.rectTransform.sizeDelta = new Vector2(layout.preferredWidth, layout.preferredHeight);
            _layoutChanged = true;
        }

        // Keep this pair after the keys without moving native template children or reordering each frame.
        int lastIndex = _wheelImage.transform.parent.childCount - 1;
        if (_wheelImage.transform.GetSiblingIndex() != lastIndex)
        {
            _wheelImage.transform.SetAsLastSibling();
            _layoutChanged = true;
        }
        if (_wheelSeparator != null)
        {
            SetActive(_wheelSeparator.gameObject, hasModifier);
            if (_wheelSeparator.transform.GetSiblingIndex() != lastIndex - 1)
            {
                _wheelSeparator.transform.SetSiblingIndex(lastIndex - 1);
                _layoutChanged = true;
            }
        }
    }

    internal void SetText(string value)
    {
        HideWheelImage();
        CaptureOriginals();
        SetActive(true);
        TMP_Text? target = _label ?? _keys.FirstOrDefault() ?? _extraTexts.FirstOrDefault();
        SetText(target, value);
        foreach (GameObject keyParent in _keyParents)
        {
            if (keyParent != null)
            {
                SetActive(keyParent, false);
            }
        }

        foreach (TMP_Text extraText in _extraTexts)
        {
            if (extraText != null && extraText != target)
            {
                SetActive(extraText.gameObject, false);
            }
        }
    }

    internal void Restore()
    {
        if (Root == null)
        {
            return;
        }

        HideWheelImage();
        if (!_capturedOriginals)
        {
            if (_hideOnRestore)
            {
                Root.SetActive(false);
            }

            RestoreSiblingIndex();
            return;
        }

        Root.SetActive(_hideOnRestore ? false : _originalRootActive);
        if (_label != null)
        {
            SetText(_label, _originalLabel);
            if (_originalLabelPreferredWidth.HasValue &&
                _label.TryGetComponent(out LayoutElement layoutElement))
            {
                layoutElement.preferredWidth = _originalLabelPreferredWidth.Value;
            }
        }

        for (int i = 0; i < _keys.Count && i < _originalKeyTexts.Count; i++)
        {
            SetText(_keys[i], _originalKeyTexts[i]);
        }

        for (int i = 0; i < _keyParents.Count; i++)
        {
            if (_keyParents[i] != null)
            {
                bool active = i < _originalKeyParentStates.Count &&
                              !_generatedKeyParents.Contains(_keyParents[i]) && _originalKeyParentStates[i];
                _keyParents[i].SetActive(active);
            }
        }

        for (int i = 0; i < _extraTexts.Count && i < _originalExtraTextStates.Count; i++)
        {
            if (_extraTexts[i] != null)
            {
                _extraTexts[i].gameObject.SetActive(_originalExtraTextStates[i]);
            }
        }

        RestoreSiblingIndex();
    }

    internal void SetActive(bool active)
    {
        SetActive(Root, active);
    }

    internal void Hide()
    {
        if (Root == null)
        {
            return;
        }
        HideWheelImage();
        Apply(string.Empty, Array.Empty<string>(), 0f, hideExtraTexts: true, activateRoot: false);
        SetActive(false);
    }

    private void SetActive(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
        {
            target.SetActive(active);
            _layoutChanged = true;
        }
    }

    internal void MoveBefore(GameObject? template)
    {
        if (template == null ||
            Root == null ||
            Root == template ||
            Root.transform.parent != template.transform.parent)
        {
            return;
        }

        int currentIndex = Root.transform.GetSiblingIndex();
        int templateIndex = template.transform.GetSiblingIndex();
        int targetIndex = currentIndex < templateIndex ? templateIndex - 1 : templateIndex;
        if (currentIndex != targetIndex)
        {
            Root.transform.SetSiblingIndex(Mathf.Max(0, targetIndex));
            _layoutChanged = true;
        }
    }

    internal void MoveAfter(GameObject? template)
    {
        if (template == null ||
            Root == null ||
            Root == template ||
            Root.transform.parent != template.transform.parent)
        {
            return;
        }

        int currentIndex = Root.transform.GetSiblingIndex();
        int templateIndex = template.transform.GetSiblingIndex();
        int targetIndex = currentIndex < templateIndex ? templateIndex : templateIndex + 1;
        if (currentIndex != targetIndex)
        {
            Root.transform.SetSiblingIndex(Mathf.Max(0, targetIndex));
            _layoutChanged = true;
        }
    }

    internal void MoveToStart()
    {
        if (Root != null && Root.transform.GetSiblingIndex() != 0)
        {
            Root.transform.SetAsFirstSibling();
            _layoutChanged = true;
        }
    }

    internal void MoveToEnd()
    {
        if (Root != null && Root.transform.parent != null &&
            Root.transform.GetSiblingIndex() != Root.transform.parent.childCount - 1)
        {
            Root.transform.SetAsLastSibling();
            _layoutChanged = true;
        }
    }

    internal bool Contains(TMP_Text? text)
    {
        return text != null && Root != null && text.transform.IsChildOf(Root.transform);
    }

    internal void RebuildParentLayout()
    {
        if (Root != null && Root.transform.parent is RectTransform parent)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
        }
    }

    internal bool ConsumeLayoutChange()
    {
        bool changed = _layoutChanged;
        _layoutChanged = false;
        return changed;
    }

    private void CaptureOriginals()
    {
        if (_capturedOriginals)
        {
            return;
        }

        _capturedOriginals = true;
        _originalRootActive = Root.activeSelf;
        _originalLabel = _label != null ? _label.text : string.Empty;
        _originalLabelPreferredWidth = _label != null &&
                                       _label.TryGetComponent(out LayoutElement layoutElement)
            ? layoutElement.preferredWidth
            : null;

        _originalKeyTexts.Clear();
        foreach (TMP_Text key in _keys)
        {
            _originalKeyTexts.Add(key != null ? key.text : string.Empty);
        }

        _originalKeyParentStates.Clear();
        foreach (GameObject keyParent in _keyParents)
        {
            _originalKeyParentStates.Add(keyParent != null && keyParent.activeSelf);
        }

        _originalExtraTextStates.Clear();
        foreach (TMP_Text extraText in _extraTexts)
        {
            _originalExtraTextStates.Add(extraText != null && extraText.gameObject.activeSelf);
        }
    }

    private void HideWheelImage()
    {
        if (_wheelImage != null) SetActive(_wheelImage.gameObject, false);
        if (_wheelSeparator != null) SetActive(_wheelSeparator.gameObject, false);
    }

    private void RestoreSiblingIndex()
    {
        if (_originalParent != null && Root.transform.parent == _originalParent)
        {
            Root.transform.SetSiblingIndex(Mathf.Clamp(
                _originalSiblingIndex,
                0,
                Mathf.Max(0, _originalParent.childCount - 1)));
        }
    }

    private void EnsureKeyCount(int count)
    {
        RefreshChildren();
        if (count <= _keys.Count || _keyParents.Count == 0)
        {
            return;
        }

        GameObject template = _keyParents[0];
        Transform parent = template.transform.parent;
        while (_keys.Count < count)
        {
            GameObject clone = Object.Instantiate(template, parent, false);
            clone.name = _keys.Count == 1 ? "key_bkg (1)" : $"key_bkg ({_keys.Count})";
            _generatedKeyParents.Add(clone);
            RefreshChildren();
            if (_keys.Count == 0)
            {
                break;
            }
        }
    }

    private void RefreshChildren()
    {
        // Keep discovering external UI changes, but reuse the scan storage between updates.
        Root.GetComponentsInChildren(true, _textScan);
        for (int i = _textScan.Count - 1; i >= 0; i--)
        {
            if (_textScan[i] == null || _textScan[i].name == WheelSeparatorName)
            {
                _textScan.RemoveAt(i);
            }
        }

        bool childrenChanged = _texts.Count != _textScan.Count;
        for (int i = 0; !childrenChanged && i < _texts.Count; i++)
        {
            childrenChanged = _texts[i] != _textScan[i];
        }
        if (childrenChanged)
        {
            _layoutChanged = true;
        }
        _texts.Clear();
        _texts.AddRange(_textScan);
        _textScan.Clear();
        _keys.Clear();
        _keyParents.Clear();
        _extraTexts.Clear();
        _label = null;

        foreach (TMP_Text text in _texts)
        {
            Localization.instance?.RemoveTextFromCache(text);
            if (text is TextMeshProUGUI textMesh)
            {
                textMesh.raycastTarget = false;
            }
            if (string.Equals(text.name, "Key", StringComparison.OrdinalIgnoreCase))
            {
                _keys.Add(text);
            }
        }

        if (_keys.Count == 0)
        {
            TMP_Text? inferredKey = null;
            TMP_Text? rightmostText = null;
            foreach (TMP_Text text in _texts)
            {
                if (inferredKey == null && LooksLikeKeyBindingText(text.text))
                {
                    inferredKey = text;
                }
                // The previous stable ordering selected the last text when x positions tied.
                if (rightmostText == null || text.transform.position.x.CompareTo(rightmostText.transform.position.x) >= 0)
                {
                    rightmostText = text;
                }
            }
            inferredKey ??= rightmostText;
            if (inferredKey != null && _texts.Count > 1)
            {
                _keys.Add(inferredKey);
            }
        }

        TMP_Text? firstNonKey = null;
        foreach (TMP_Text text in _texts)
        {
            if (_keys.Contains(text))
            {
                continue;
            }
            firstNonKey ??= text;
            if (string.Equals(text.name, "Text", StringComparison.OrdinalIgnoreCase))
            {
                _label = text;
                break;
            }
        }
        if (_label == null)
        {
            foreach (TMP_Text text in _texts)
            {
                if (!_keys.Contains(text) && !LooksLikeKeyBindingText(text.text))
                {
                    _label = text;
                    break;
                }
            }
            _label ??= firstNonKey;
        }

        foreach (TMP_Text key in _keys)
        {
            _keyParents.Add(key.transform.parent != null ? key.transform.parent.gameObject : key.gameObject);
        }

        foreach (TMP_Text text in _texts)
        {
            if (text != _label && !_keys.Contains(text))
            {
                _extraTexts.Add(text);
            }
        }
        SortKeysBySiblingIndex();
    }

    private void SortKeysBySiblingIndex()
    {
        // Hint cells contain only a few keys; stable insertion avoids temporary sort lists.
        for (int index = 1; index < _keys.Count; index++)
        {
            TMP_Text key = _keys[index];
            GameObject parent = _keyParents[index];
            int siblingIndex = parent != null ? parent.transform.GetSiblingIndex() : index;
            int target = index;
            while (target > 0)
            {
                GameObject previousParent = _keyParents[target - 1];
                int previousSiblingIndex = previousParent != null ? previousParent.transform.GetSiblingIndex() : target - 1;
                if (previousSiblingIndex <= siblingIndex)
                {
                    break;
                }
                _keys[target] = _keys[target - 1];
                _keyParents[target] = previousParent!;
                target--;
            }
            _keys[target] = key;
            _keyParents[target] = parent!;
        }
    }

    private void SetText(TMP_Text? text, string value)
    {
        if (text == null)
        {
            return;
        }

        Localization.instance?.RemoveTextFromCache(text);
        SetActive(text.gameObject, true);
        if (!string.Equals(text.text, value, StringComparison.Ordinal))
        {
            text.text = value;
            _layoutChanged = true;
        }
    }

    private static bool LooksLikeKeyBindingText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalized = new(text
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Contains("mouse") ||
               normalized.Contains("ctrl") ||
               normalized.Contains("shift") ||
               normalized.Contains("alt") ||
               normalized.Contains("button") ||
               normalized.Contains("key") ||
               normalized.Contains("sprite") ||
               normalized.Length <= 2;
    }
}
