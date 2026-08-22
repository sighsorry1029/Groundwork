using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class PickableRespawnHoverSystem
{
    private const string HoverProxyObjectName = "Groundwork_PickedRespawnHoverProxy";
    private const float HoverProxyColliderRadius = 0.32f;
    private const float HoverProxyColliderCenterY = 0.15f;

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

    internal static string GetHoverProxyText(Pickable pickable)
    {
        string hoverText = "";
        AppendHoverText(pickable, ref hoverText);
        if (string.IsNullOrEmpty(hoverText))
        {
            EnsurePickableName(pickable, ref hoverText);
        }

        return hoverText;
    }

    internal static void RefreshHoverProxy(Pickable pickable)
    {
        if (pickable == null)
        {
            return;
        }

        PickedPickableRespawnHoverProxy? proxy = FindHoverProxy(pickable);
        bool shouldProvideProxy = ShouldProvideHoverProxy(pickable);
        bool hasNaturalHoverCollider = shouldProvideProxy && HasActiveNaturalHoverCollider(pickable);
        bool hasNaturalPollinationCollider = shouldProvideProxy && HasActiveNaturalPollinationCollider(pickable);
        bool needsProxy = shouldProvideProxy &&
                          (!hasNaturalHoverCollider || !hasNaturalPollinationCollider);
        if (!needsProxy)
        {
            if (proxy != null)
            {
                proxy.gameObject.SetActive(false);
            }

            return;
        }

        proxy ??= CreateHoverProxy(pickable);
        proxy.Initialize(pickable);
        Transform proxyTransform = proxy.transform;
        proxyTransform.localPosition = Vector3.zero;
        proxyTransform.localRotation = Quaternion.identity;
        proxyTransform.localScale = GetInverseLossyScale(pickable.transform);
        proxy.gameObject.SetActive(true);
    }

    internal static void RefreshLoadedHoverProxies()
    {
        foreach (Pickable pickable in Resources.FindObjectsOfTypeAll<Pickable>())
        {
            if (pickable != null && pickable.gameObject.scene.IsValid())
            {
                RefreshHoverProxy(pickable);
            }
        }
    }

    internal static void Shutdown()
    {
        foreach (PickedPickableRespawnHoverProxy proxy in
                 Resources.FindObjectsOfTypeAll<PickedPickableRespawnHoverProxy>())
        {
            if (proxy != null)
            {
                Object.Destroy(proxy.gameObject);
            }
        }
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

    private static bool ShouldProvideHoverProxy(Pickable pickable)
    {
        return pickable.gameObject.scene.IsValid() &&
               pickable.transform.root.gameObject.activeInHierarchy &&
               pickable.GetEnabled == 1 &&
               pickable.GetPicked() &&
               FarmingSkillSystem.IsForagingTarget(pickable) &&
               FarmingSkillSystem.TryGetPickableRespawnTiming(pickable, out _);
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
               collider.GetComponentInParent<PickedPickableRespawnHoverProxy>() == null &&
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

    private static PickedPickableRespawnHoverProxy? FindHoverProxy(Pickable pickable)
    {
        foreach (PickedPickableRespawnHoverProxy proxy in
                 pickable.GetComponentsInChildren<PickedPickableRespawnHoverProxy>(includeInactive: true))
        {
            if (proxy.Target == pickable ||
                (proxy.Target == null &&
                 proxy.transform.parent == pickable.transform &&
                 proxy.gameObject.name == HoverProxyObjectName))
            {
                return proxy;
            }
        }

        return null;
    }

    private static PickedPickableRespawnHoverProxy CreateHoverProxy(Pickable pickable)
    {
        GameObject proxyObject = new(HoverProxyObjectName)
        {
            hideFlags = HideFlags.DontSave,
            layer = ResolveHoverProxyLayer(pickable)
        };
        proxyObject.SetActive(false);
        proxyObject.transform.SetParent(pickable.transform, worldPositionStays: false);

        PickedPickableRespawnHoverProxy proxy = proxyObject.AddComponent<PickedPickableRespawnHoverProxy>();
        proxy.Initialize(pickable);

        SphereCollider collider = proxyObject.AddComponent<SphereCollider>();
        collider.center = Vector3.up * HoverProxyColliderCenterY;
        collider.radius = HoverProxyColliderRadius;
        // Prefer a trigger to avoid an invisible physical obstacle. If global trigger queries are disabled,
        // fall back to a solid proxy so hover and pollination discovery still work.
        collider.isTrigger = Physics.queriesHitTriggers;
        return proxy;
    }

    private static int ResolveHoverProxyLayer(Pickable pickable)
    {
        int itemLayer = LayerMask.NameToLayer("item");
        return itemLayer >= 0 ? itemLayer : pickable.gameObject.layer;
    }
}

internal sealed class PickedPickableRespawnHoverProxy : MonoBehaviour, Hoverable, Interactable
{
    internal Pickable? Target { get; private set; }

    internal void Initialize(Pickable target)
    {
        Target = target;
    }

    public string GetHoverText()
    {
        return Target != null
            ? PickableRespawnHoverSystem.GetHoverProxyText(Target)
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
internal static class PickableAwakeRespawnHoverProxyPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Pickable __instance)
    {
        PickableRespawnHoverSystem.RefreshHoverProxy(__instance);
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.SetEnabled), typeof(int))]
internal static class PickableSetEnabledRespawnHoverProxyPatch
{
    private static void Postfix(Pickable __instance)
    {
        PickableRespawnHoverSystem.RefreshHoverProxy(__instance);
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
