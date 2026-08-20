using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class PickableRespawnHoverSystem
{
    private const string MarkerObjectName = "Groundwork_PickedRespawnMarker";
    private const string MarkerVisualObjectName = "Groundwork_PickedRespawnMarkerVisual";
    private const int MarkerSegmentCount = 24;
    // Match the terrain grid preview's 0.16 m point size while keeping this marker circular.
    private const float MarkerRadius = 0.08f;
    private const float MarkerFallbackYOffset = 0.03f;
    private const float MarkerSurfaceOffset = 0.02f;
    private const float MarkerSurfaceRayStartOffset = 0.5f;
    private const float MarkerSurfaceRayDistance = 1.5f;
    private const float MarkerSurfaceMinimumUpwardNormal = 0.1f;
    private const float MarkerSurfaceMaximumAboveTarget = 0.15f;
    private const float MarkerColliderRadius = 0.32f;
    private const float MarkerColliderCenterY = 0.15f;
    private static readonly Color MarkerColor = new(0.65f, 0.65f, 0.65f, 0.2f);
    private static readonly RaycastHit[] MarkerSurfaceHits = new RaycastHit[16];
    private static Mesh? SharedMarkerMesh;
    private static Material? SharedMarkerMaterial;
    private static int _markerSurfaceMask;

    internal static void AppendHoverText(Pickable pickable, ref string hoverText)
    {
        if (pickable == null)
        {
            return;
        }

        if (!pickable.GetPicked() ||
            pickable.CanBePicked() ||
            !FarmingSkillSystem.TryGetPickableRespawnTiming(
                pickable,
                out FarmingSkillSystem.PickableRespawnTiming timing))
        {
            return;
        }

        if (!timing.HasPickedTime)
        {
            return;
        }

        EnsurePickableName(pickable, ref hoverText);
        float farmingMultiplier = FarmingSkillSystem.GetForagingRespawnSpeedMultiplier(pickable);
        float pollinationMultiplier = BeehivePollinationSystem.GetForagingRespawnMultiplierForHover(pickable);
        float rainMultiplier = EnvironmentEffectSystem.GetWetForagingRespawnSpeedMultiplier(pickable);
        AppendEffectHoverLines(
            ref hoverText,
            timing.RemainingSeconds,
            farmingMultiplier,
            pollinationMultiplier,
            rainMultiplier);
    }

    internal static string GetMarkerHoverText(Pickable pickable)
    {
        string hoverText = "";
        AppendHoverText(pickable, ref hoverText);
        return hoverText;
    }

    internal static void RefreshMarker(Pickable pickable)
    {
        if (pickable == null)
        {
            return;
        }

        PickedPickableRespawnMarker? marker = FindMarker(pickable);
        bool shouldProvideMarker = ShouldProvideMarker(pickable);
        bool hasNaturalHoverCollider = shouldProvideMarker && HasActiveNaturalHoverCollider(pickable);
        bool hasNaturalPollinationCollider = shouldProvideMarker && HasActiveNaturalPollinationCollider(pickable);
        bool needsMarker = shouldProvideMarker &&
                           (!hasNaturalHoverCollider || !hasNaturalPollinationCollider);
        if (!needsMarker)
        {
            if (marker != null)
            {
                marker.gameObject.SetActive(false);
            }

            return;
        }

        marker ??= CreateMarker(pickable);
        if (marker == null)
        {
            return;
        }

        marker.Initialize(pickable);
        Transform markerTransform = marker.transform;
        // Keep the gameplay proxy at the Pickable. Only its visual child is projected onto a support surface.
        markerTransform.localPosition = Vector3.zero;
        markerTransform.localRotation = Quaternion.identity;
        markerTransform.localScale = GetInverseLossyScale(pickable.transform);
        bool showVisual =
            !hasNaturalHoverCollider &&
            !HasPlantEverythingPickedVisual(pickable);
        if (showVisual && marker.HasVisual)
        {
            PositionMarkerVisual(marker, pickable);
        }

        marker.SetVisualEnabled(showVisual);
        marker.gameObject.SetActive(true);
    }

    internal static void RefreshLoadedMarkers()
    {
        foreach (Pickable pickable in Resources.FindObjectsOfTypeAll<Pickable>())
        {
            if (pickable != null && pickable.gameObject.scene.IsValid())
            {
                RefreshMarker(pickable);
            }
        }
    }

    internal static void Shutdown()
    {
        foreach (PickedPickableRespawnMarker marker in
                 Resources.FindObjectsOfTypeAll<PickedPickableRespawnMarker>())
        {
            if (marker != null)
            {
                Object.Destroy(marker.gameObject);
            }
        }

        if (SharedMarkerMesh != null)
        {
            Object.Destroy(SharedMarkerMesh);
        }

        if (SharedMarkerMaterial != null)
        {
            Object.Destroy(SharedMarkerMaterial);
        }

        SharedMarkerMesh = null;
        SharedMarkerMaterial = null;
        _markerSurfaceMask = 0;
    }

    internal static void AppendPlantHoverText(Plant plant, ref string hoverText)
    {
        if (plant == null ||
            plant.GetStatus() != Plant.Status.Healthy)
        {
            return;
        }

        if (!FarmingSkillSystem.TryGetPlantRemainingGrowthSeconds(
                plant,
                out float remainingSeconds) ||
            remainingSeconds <= 0.01f)
        {
            return;
        }

        float farmingMultiplier = FarmingSkillSystem.GetPlantGrowSpeedMultiplier(plant);
        float pollinationMultiplier = BeehivePollinationSystem.GetPlantGrowthMultiplierForHover(plant);
        float rainMultiplier = EnvironmentEffectSystem.GetWetPlantGrowSpeedMultiplier();
        AppendEffectHoverLines(
            ref hoverText,
            remainingSeconds,
            farmingMultiplier,
            pollinationMultiplier,
            rainMultiplier);
    }

    private static void AppendEffectHoverLines(
        ref string hoverText,
        float remainingSeconds,
        float farmingMultiplier,
        float pollinationMultiplier,
        float rainMultiplier)
    {
        List<string> parts = [];
        AddMultiplierPart(parts, farmingMultiplier, "groundwork_factor_farming", "farming {0}");
        AddMultiplierPart(parts, pollinationMultiplier, "groundwork_factor_pollination", "pollination {0}");
        AddMultiplierPart(parts, rainMultiplier, "groundwork_factor_rain", "rain {0}");

        if (parts.Count > 0)
        {
            AppendLine(ref hoverText, Colorize(string.Join(" ", parts)));
        }

        AppendLine(ref hoverText, Colorize(GroundworkLocalization.FormatDuration(remainingSeconds)));
    }

    private static void EnsurePickableName(Pickable pickable, ref string hoverText)
    {
        if (!string.IsNullOrWhiteSpace(hoverText))
        {
            return;
        }

        string hoverName = GetSafeHoverName(pickable);
        hoverText = Localization.instance != null
            ? Localization.instance.Localize(hoverName)
            : hoverName;
    }

    internal static string GetSafeHoverName(Pickable pickable)
    {
        if (!string.IsNullOrEmpty(pickable.m_overrideName))
        {
            return pickable.m_overrideName;
        }

        ItemDrop? itemDrop = pickable.m_itemPrefab != null
            ? pickable.m_itemPrefab.GetComponent<ItemDrop>()
            : null;
        string? itemName = itemDrop?.m_itemData?.m_shared?.m_name;
        return !string.IsNullOrEmpty(itemName)
            ? itemName!
            : Utils.GetPrefabName(pickable.transform.root.gameObject);
    }

    private static void AddMultiplierPart(List<string> parts, float multiplier, string token, string fallback)
    {
        if (multiplier <= 1.001f)
        {
            return;
        }

        parts.Add(GroundworkLocalization.Format(token, fallback, FormatMultiplier(multiplier)));
    }

    private static void AppendLine(ref string text, string line)
    {
        text = string.IsNullOrEmpty(text) ? line : text + "\n" + line;
    }

    private static string Colorize(string text)
    {
        return "<color=#a8e6a1>" + text + "</color>";
    }

    private static string FormatMultiplier(float multiplier)
    {
        return "x" + multiplier.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static bool ShouldProvideMarker(Pickable pickable)
    {
        return pickable.gameObject.scene.IsValid() &&
               pickable.transform.root.gameObject.activeInHierarchy &&
               pickable.GetEnabled == 1 &&
               pickable.GetPicked() &&
               FarmingSkillSystem.IsForagingTarget(pickable);
    }

    private static bool HasActiveNaturalHoverCollider(Pickable pickable)
    {
        foreach (Collider collider in pickable.transform.root.GetComponentsInChildren<Collider>(includeInactive: false))
        {
            if (!IsActiveNaturalCollider(collider, pickable) ||
                !IsPlayerHoverLayer(collider.gameObject.layer))
            {
                continue;
            }

            GameObject hoverCandidate;
            if (collider.GetComponent<Hoverable>() != null)
            {
                hoverCandidate = collider.gameObject;
            }
            else if (collider.attachedRigidbody != null)
            {
                hoverCandidate = collider.attachedRigidbody.gameObject;
            }
            else
            {
                hoverCandidate = collider.gameObject;
            }

            Hoverable? hoverable = hoverCandidate.GetComponentInParent<Hoverable>();
            if (hoverable is Pickable hoverPickable && hoverPickable == pickable)
            {
                return true;
            }

            if (hoverable is Component hoverComponent &&
                (hoverComponent.transform == pickable.transform ||
                 hoverComponent.transform.IsChildOf(pickable.transform)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasActiveNaturalPollinationCollider(Pickable pickable)
    {
        foreach (Collider collider in pickable.transform.root.GetComponentsInChildren<Collider>(includeInactive: false))
        {
            if (IsActiveNaturalCollider(collider, pickable) &&
                BeehivePollinationSystem.IsPollinationSearchLayer(collider.gameObject.layer))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActiveNaturalCollider(Collider? collider, Pickable pickable)
    {
        return collider != null &&
               collider.enabled &&
               collider.gameObject.activeInHierarchy &&
               (!collider.isTrigger || Physics.queriesHitTriggers) &&
               collider.GetComponentInParent<PickedPickableRespawnMarker>() == null &&
               collider.GetComponentInParent<Pickable>() == pickable;
    }

    private static bool IsPlayerHoverLayer(int layer)
    {
        Player? localPlayer = Player.m_localPlayer;
        int interactMask = localPlayer?.m_interactMask ?? 0;
        if (interactMask == 0)
        {
            interactMask = LayerMask.GetMask(
                "item",
                "piece",
                "piece_nonsolid",
                "Default",
                "static_solid",
                "Default_small",
                "character",
                "character_net",
                "terrain",
                "vehicle");
        }

        return (interactMask & (1 << layer)) != 0;
    }

    private static Vector3 GetInverseLossyScale(Transform target)
    {
        Vector3 scale = target.lossyScale;
        return new Vector3(
            SafeInverse(scale.x),
            SafeInverse(scale.y),
            SafeInverse(scale.z));
    }

    private static float SafeInverse(float value)
    {
        return Mathf.Abs(value) > 0.0001f ? 1f / value : 1f;
    }

    private static bool HasPlantEverythingPickedVisual(Pickable pickable)
    {
        Transform? pickedVisual = pickable.transform.root.Find("PE_Picked");
        if (pickedVisual == null || !pickedVisual.gameObject.activeInHierarchy)
        {
            return false;
        }

        foreach (Renderer renderer in pickedVisual.GetComponentsInChildren<Renderer>(includeInactive: false))
        {
            if (renderer != null && renderer.enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static void PositionMarkerVisual(PickedPickableRespawnMarker marker, Pickable pickable)
    {
        Vector3 position = pickable.transform.position + Vector3.up * MarkerFallbackYOffset;
        Quaternion rotation = Quaternion.identity;
        if (TryFindMarkerSupportSurface(pickable, marker.transform, out RaycastHit hit))
        {
            Vector3 normal = hit.normal.normalized;
            position = hit.point + normal * MarkerSurfaceOffset;
            rotation = Quaternion.FromToRotation(Vector3.up, normal);
        }

        marker.SetVisualPose(position, rotation, GetInverseLossyScale(marker.transform));
    }

    private static bool TryFindMarkerSupportSurface(
        Pickable pickable,
        Transform markerTransform,
        out RaycastHit bestHit)
    {
        bestHit = default;
        int surfaceMask = GetMarkerSurfaceMask();
        if (surfaceMask == 0)
        {
            return false;
        }

        Vector3 origin = pickable.transform.position + Vector3.up * MarkerSurfaceRayStartOffset;
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            MarkerSurfaceHits,
            MarkerSurfaceRayDistance,
            surfaceMask,
            QueryTriggerInteraction.Ignore);
        if (hitCount >= MarkerSurfaceHits.Length)
        {
            return false;
        }

        float bestDistance = float.PositiveInfinity;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = MarkerSurfaceHits[index];
            Collider? collider = hit.collider;
            if (collider == null ||
                hit.normal.y < MarkerSurfaceMinimumUpwardNormal ||
                hit.point.y > pickable.transform.position.y + MarkerSurfaceMaximumAboveTarget ||
                collider.transform.IsChildOf(markerTransform) ||
                collider.transform.root == pickable.transform.root ||
                collider.GetComponentInParent<PickedPickableRespawnMarker>() != null ||
                collider.GetComponentInParent<Pickable>() != null ||
                hit.distance >= bestDistance)
            {
                continue;
            }

            Vector3 visualPosition = hit.point + hit.normal.normalized * MarkerSurfaceOffset;
            Vector3 hoverProxyCenter = markerTransform.TransformPoint(Vector3.up * MarkerColliderCenterY);
            float maximumVisualDistance = MarkerColliderRadius - MarkerSurfaceOffset;
            if ((visualPosition - hoverProxyCenter).sqrMagnitude >
                maximumVisualDistance * maximumVisualDistance)
            {
                continue;
            }

            bestDistance = hit.distance;
            bestHit = hit;
        }

        return bestDistance < float.PositiveInfinity;
    }

    private static int GetMarkerSurfaceMask()
    {
        if (_markerSurfaceMask == 0)
        {
            _markerSurfaceMask = LayerMask.GetMask(
                "terrain",
                "Default",
                "Default_small",
                "static_solid",
                "piece",
                "piece_nonsolid");
        }

        return _markerSurfaceMask;
    }

    private static PickedPickableRespawnMarker? FindMarker(Pickable pickable)
    {
        foreach (PickedPickableRespawnMarker marker in
                 pickable.GetComponentsInChildren<PickedPickableRespawnMarker>(includeInactive: true))
        {
            if (marker.Target == pickable ||
                (marker.Target == null &&
                 marker.transform.parent == pickable.transform &&
                 marker.gameObject.name == MarkerObjectName))
            {
                return marker;
            }
        }

        return null;
    }

    private static PickedPickableRespawnMarker? CreateMarker(Pickable pickable)
    {
        GameObject markerObject = new(MarkerObjectName)
        {
            hideFlags = HideFlags.DontSave,
            layer = ResolveMarkerLayer(pickable)
        };
        markerObject.SetActive(false);
        markerObject.transform.SetParent(pickable.transform, worldPositionStays: false);

        PickedPickableRespawnMarker marker = markerObject.AddComponent<PickedPickableRespawnMarker>();
        marker.Initialize(pickable);

        SphereCollider collider = markerObject.AddComponent<SphereCollider>();
        collider.center = Vector3.up * MarkerColliderCenterY;
        collider.radius = MarkerColliderRadius;
        // This all-peer item-layer proxy also keeps hidden foraging targets discoverable by pollination.
        collider.isTrigger = false;

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
        {
            Mesh? mesh = GetOrCreateMarkerMesh();
            Material? material = GetOrCreateMarkerMaterial();
            if (mesh != null && material != null)
            {
                GameObject visualObject = new(MarkerVisualObjectName)
                {
                    hideFlags = HideFlags.DontSave,
                    layer = markerObject.layer
                };
                visualObject.transform.SetParent(markerObject.transform, worldPositionStays: false);
                MeshFilter meshFilter = visualObject.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = mesh;
                MeshRenderer renderer = visualObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                marker.SetRenderer(renderer);
            }
        }

        return marker;
    }

    private static int ResolveMarkerLayer(Pickable pickable)
    {
        int itemLayer = LayerMask.NameToLayer("item");
        return itemLayer >= 0 ? itemLayer : pickable.gameObject.layer;
    }

    private static Mesh? GetOrCreateMarkerMesh()
    {
        if (SharedMarkerMesh != null)
        {
            return SharedMarkerMesh;
        }

        Vector3[] vertices = new Vector3[MarkerSegmentCount + 1];
        Color[] colors = new Color[MarkerSegmentCount + 1];
        int[] indices = new int[MarkerSegmentCount * 3];
        vertices[0] = Vector3.zero;
        colors[0] = Color.white;
        for (int index = 0; index < MarkerSegmentCount; index++)
        {
            float angle = index / (float)MarkerSegmentCount * Mathf.PI * 2f;
            Vector3 direction = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            vertices[index + 1] = direction * MarkerRadius;
            colors[index + 1] = Color.white;

            int next = (index + 1) % MarkerSegmentCount;
            int triangle = index * 3;
            indices[triangle] = 0;
            indices[triangle + 1] = next + 1;
            indices[triangle + 2] = index + 1;
        }

        SharedMarkerMesh = new Mesh
        {
            name = "Groundwork_PickedRespawnMarkerDotMesh",
            hideFlags = HideFlags.DontSave,
            vertices = vertices,
            colors = colors,
            triangles = indices
        };
        SharedMarkerMesh.RecalculateBounds();
        return SharedMarkerMesh;
    }

    private static Material? GetOrCreateMarkerMaterial()
    {
        if (SharedMarkerMaterial != null)
        {
            return SharedMarkerMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default") ??
                        Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        if (shader == null)
        {
            return null;
        }

        SharedMarkerMaterial = new Material(shader)
        {
            name = "Groundwork_PickedRespawnMarkerMaterial",
            color = MarkerColor,
            hideFlags = HideFlags.DontSave
        };
        return SharedMarkerMaterial;
    }
}

internal sealed class PickedPickableRespawnMarker : MonoBehaviour, Hoverable, Interactable
{
    private MeshRenderer? _renderer;

    internal Pickable? Target { get; private set; }
    internal bool HasVisual => _renderer != null;

    internal void Initialize(Pickable target)
    {
        Target = target;
    }

    internal void SetRenderer(MeshRenderer renderer)
    {
        _renderer = renderer;
    }

    internal void SetVisualEnabled(bool enabled)
    {
        if (_renderer != null)
        {
            _renderer.enabled = enabled;
        }
    }

    internal void SetVisualPose(Vector3 position, Quaternion rotation, Vector3 localScale)
    {
        if (_renderer == null)
        {
            return;
        }

        Transform visualTransform = _renderer.transform;
        visualTransform.position = position;
        visualTransform.rotation = rotation;
        visualTransform.localScale = localScale;
    }

    private void Start()
    {
        if (Target != null)
        {
            // Pickable.Awake can run before a nearby terrain or piece collider is ready.
            PickableRespawnHoverSystem.RefreshMarker(Target);
        }
    }

    public string GetHoverText()
    {
        return Target != null
            ? PickableRespawnHoverSystem.GetMarkerHoverText(Target)
            : "";
    }

    public string GetHoverName()
    {
        return Target != null
            ? PickableRespawnHoverSystem.GetSafeHoverName(Target)
            : "";
    }

    public bool Interact(Humanoid character, bool repeat, bool alt)
    {
        return false;
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item)
    {
        return false;
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
[HarmonyAfter("advize.PlantEverything")]
internal static class PickableGetHoverTextRespawnHoverPatch
{
    private static void Postfix(Pickable __instance, ref string __result)
    {
        string groundworkHoverText = "";
        PickableRespawnHoverSystem.AppendHoverText(__instance, ref groundworkHoverText);
        if (!string.IsNullOrEmpty(groundworkHoverText))
        {
            // Groundwork owns picked respawn timing so another fixed-rate timer is not shown beside it.
            __result = groundworkHoverText;
        }
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
[HarmonyAfter("advize.PlantEverything")]
internal static class PickableAwakeRespawnMarkerPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Pickable __instance)
    {
        PickableRespawnHoverSystem.RefreshMarker(__instance);
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.SetEnabled), typeof(int))]
internal static class PickableSetEnabledRespawnMarkerPatch
{
    private static void Postfix(Pickable __instance)
    {
        PickableRespawnHoverSystem.RefreshMarker(__instance);
    }
}

[HarmonyPatch(typeof(Plant), nameof(Plant.GetHoverText))]
internal static class PlantGetHoverTextGrowthHoverPatch
{
    private static void Postfix(Plant __instance, ref string __result)
    {
        PickableRespawnHoverSystem.AppendPlantHoverText(__instance, ref __result);
    }
}
