using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class TerrainDigFloatingTextSystem
{
    private const float HoldDuration = 1f;
    private const float FadeDuration = 1f;
    private const float VerticalOffset = 0.45f;
    private static readonly List<Entry> Entries = [];

    internal static void Clear()
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Label != null)
            {
                Object.Destroy(entry.Label.gameObject);
            }
        }

        Entries.Clear();
    }

    internal static void Show(Vector3 worldPosition, string text, Color color)
    {
        TextMeshProUGUI? label = CreateLabel(color);
        if (label == null)
        {
            return;
        }

        label.text = text;
        Entries.Add(new Entry(label, worldPosition + Vector3.up * VerticalOffset, color, Time.time));
    }

    internal static void Update()
    {
        if (Entries.Count == 0)
        {
            return;
        }

        Camera mainCamera = Utils.GetMainCamera();
        if (mainCamera == null)
        {
            HideAll();
            return;
        }

        for (int index = Entries.Count - 1; index >= 0; index--)
        {
            Entry entry = Entries[index];
            if (entry.Label == null)
            {
                Entries.RemoveAt(index);
                continue;
            }

            float elapsed = Time.time - entry.StartTime;
            if (elapsed >= HoldDuration + FadeDuration)
            {
                Object.Destroy(entry.Label.gameObject);
                Entries.RemoveAt(index);
                continue;
            }

            Vector3 screenPoint = mainCamera.WorldToScreenPointScaled(entry.WorldPosition);
            bool visible = screenPoint.z > 0f &&
                           screenPoint.x >= 0f &&
                           screenPoint.x <= Screen.width &&
                           screenPoint.y >= 0f &&
                           screenPoint.y <= Screen.height;
            entry.Label.gameObject.SetActive(visible);
            if (!visible)
            {
                continue;
            }

            float alpha = elapsed <= HoldDuration
                ? 1f
                : Mathf.Clamp01(1f - (elapsed - HoldDuration) / FadeDuration);
            Color color = entry.Color;
            color.a *= alpha;
            entry.Label.color = color;
            entry.Label.rectTransform.position = screenPoint;
        }
    }

    private static void HideAll()
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Label != null)
            {
                entry.Label.gameObject.SetActive(false);
            }
        }
    }

    private static TextMeshProUGUI? CreateLabel(Color color)
    {
        if (Hud.instance == null || Hud.instance.m_rootObject == null)
        {
            return null;
        }

        GameObject labelObject = new("Groundwork_TerrainDigFloatingText");
        // Valheim no longer ships TMP's default LiberationSans asset. Keep the
        // component disabled until the HUD font and material have been assigned.
        labelObject.SetActive(false);
        labelObject.transform.SetParent(Hud.instance.m_rootObject.transform, false);

        RectTransform rectTransform = labelObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0f, 0f);
        rectTransform.anchorMax = new Vector2(0f, 0f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = new Vector2(460f, 30f);

        TextMeshProUGUI label = labelObject.AddComponent<TextMeshProUGUI>();
        TMP_Text? sourceText = Hud.instance.m_hoverName != null
            ? Hud.instance.m_hoverName
            : Hud.instance.m_pieceDescription;
        if (sourceText != null)
        {
            label.font = sourceText.font;
            label.fontSharedMaterial = sourceText.fontSharedMaterial;
        }

        label.alignment = TextAlignmentOptions.Center;
        label.color = color;
        label.fontSize = 18f;
        label.richText = false;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;

        Shadow shadow = labelObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(1.25f, -1.25f);

        return label;
    }

    private readonly struct Entry
    {
        internal Entry(TextMeshProUGUI label, Vector3 worldPosition, Color color, float startTime)
        {
            Label = label;
            WorldPosition = worldPosition;
            Color = color;
            StartTime = startTime;
        }

        internal TextMeshProUGUI Label { get; }

        internal Vector3 WorldPosition { get; }

        internal Color Color { get; }

        internal float StartTime { get; }
    }
}
