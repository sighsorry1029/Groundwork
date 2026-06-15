using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Groundwork;

internal sealed class KeyHintCell
{
    private readonly bool _hideOnRestore;
    private readonly List<TMP_Text> _keys = [];
    private readonly List<GameObject> _keyParents = [];
    private readonly List<TMP_Text> _extraTexts = [];
    private readonly List<string> _originalKeyTexts = [];
    private readonly List<bool> _originalKeyParentStates = [];
    private readonly List<bool> _originalExtraTextStates = [];
    private readonly HashSet<GameObject> _generatedKeyParents = [];
    private TMP_Text? _label;
    private bool _capturedOriginals;
    private bool _originalRootActive;
    private string _originalLabel = string.Empty;
    private float? _originalLabelPreferredWidth;

    private KeyHintCell(GameObject root, bool hideOnRestore)
    {
        Root = root;
        _hideOnRestore = hideOnRestore;
        RefreshChildren();
    }

    internal GameObject Root { get; }

    internal bool IsValid => Root != null && (_label != null || _keys.Count > 0 || _extraTexts.Count > 0);

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
               template.GetComponentsInChildren<TMP_Text>(includeInactive: true).Length > 0;
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
        EnsureKeyCount(keys.Count);
        CaptureOriginals();
        Root.SetActive(true);

        if (_label != null)
        {
            SetText(_label, label);
            if (preferredTextWidth > 0f && _label.TryGetComponent(out LayoutElement layoutElement))
            {
                layoutElement.preferredWidth = preferredTextWidth;
            }
        }

        for (int i = 0; i < _keys.Count; i++)
        {
            bool show = i < keys.Count;
            if (i < _keyParents.Count && _keyParents[i] != null)
            {
                _keyParents[i].SetActive(show);
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
                    extraText.gameObject.SetActive(false);
                }
            }
        }
    }

    internal void SetText(string value)
    {
        CaptureOriginals();
        Root.SetActive(true);
        TMP_Text? target = _label ?? _keys.FirstOrDefault() ?? _extraTexts.FirstOrDefault();
        SetText(target, value);
        foreach (GameObject keyParent in _keyParents)
        {
            if (keyParent != null)
            {
                keyParent.SetActive(false);
            }
        }

        foreach (TMP_Text extraText in _extraTexts)
        {
            if (extraText != null && extraText != target)
            {
                extraText.gameObject.SetActive(false);
            }
        }
    }

    internal void Restore()
    {
        if (!_capturedOriginals)
        {
            if (_hideOnRestore)
            {
                Root.SetActive(false);
            }

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

        for (int i = 0; i < _keyParents.Count && i < _originalKeyParentStates.Count; i++)
        {
            if (_keyParents[i] != null)
            {
                bool active = !_generatedKeyParents.Contains(_keyParents[i]) && _originalKeyParentStates[i];
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
    }

    internal void SetActive(bool active)
    {
        if (Root != null)
        {
            Root.SetActive(active);
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
        }
    }

    internal void MoveToStart()
    {
        if (Root != null)
        {
            Root.transform.SetAsFirstSibling();
        }
    }

    internal void MoveToEnd()
    {
        if (Root != null)
        {
            Root.transform.SetAsLastSibling();
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
        _keys.Clear();
        _keyParents.Clear();
        _extraTexts.Clear();
        _label = null;

        TMP_Text[] texts = Root
            .GetComponentsInChildren<TMP_Text>(includeInactive: true)
            .Where(static text => text != null)
            .ToArray();
        foreach (TMP_Text text in texts)
        {
            Localization.instance?.RemoveTextFromCache(text);
            if (text is TextMeshProUGUI textMesh)
            {
                textMesh.raycastTarget = false;
            }
        }

        _keys.AddRange(texts.Where(static text => string.Equals(text.name, "Key", StringComparison.OrdinalIgnoreCase)));
        if (_keys.Count == 0)
        {
            TMP_Text? inferredKey = texts.FirstOrDefault(static text => LooksLikeKeyBindingText(text.text))
                                   ?? texts.OrderBy(static text => text.transform.position.x).LastOrDefault();
            if (inferredKey != null && texts.Length > 1)
            {
                _keys.Add(inferredKey);
            }
        }

        _label = texts.FirstOrDefault(text => string.Equals(text.name, "Text", StringComparison.OrdinalIgnoreCase) &&
                                              !_keys.Contains(text))
                 ?? texts.FirstOrDefault(text => !_keys.Contains(text) && !LooksLikeKeyBindingText(text.text))
                 ?? texts.FirstOrDefault(text => !_keys.Contains(text));

        foreach (TMP_Text key in _keys)
        {
            _keyParents.Add(key.transform.parent != null ? key.transform.parent.gameObject : key.gameObject);
        }

        _extraTexts.AddRange(texts.Where(text => text != _label && !_keys.Contains(text)));
        SortKeysBySiblingIndex();
    }

    private void SortKeysBySiblingIndex()
    {
        List<int> order = Enumerable.Range(0, _keys.Count)
            .OrderBy(i => _keyParents[i] != null ? _keyParents[i].transform.GetSiblingIndex() : i)
            .ToList();
        if (order.Count <= 1)
        {
            return;
        }

        List<TMP_Text> orderedKeys = [];
        List<GameObject> orderedParents = [];
        foreach (int index in order)
        {
            orderedKeys.Add(_keys[index]);
            orderedParents.Add(_keyParents[index]);
        }

        _keys.Clear();
        _keys.AddRange(orderedKeys);
        _keyParents.Clear();
        _keyParents.AddRange(orderedParents);
    }

    private static void SetText(TMP_Text? text, string value)
    {
        if (text == null)
        {
            return;
        }

        Localization.instance?.RemoveTextFromCache(text);
        text.gameObject.SetActive(true);
        text.text = value;
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
