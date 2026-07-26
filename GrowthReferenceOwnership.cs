using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Groundwork;

internal static class GrowthReferenceOwnership
{
    internal const string ValheimOwnerName = "Valheim";
    internal const string UnknownOwnerName = "Unknown / Untracked";

    private const string ManifestPathMarker = "path in bundle:";
    private static readonly object VanillaPrefabSync = new();
    private static readonly object ModPrefabSync = new();
    private static HashSet<string>? _vanillaPrefabNames;
    private static Dictionary<string, string>? _modPrefabOwners;

    internal static string GetOwnerName(string prefabName)
    {
        HashSet<string> vanillaPrefabNames = GetVanillaPrefabNames();
        foreach (string candidate in EnumerateLookupCandidates(prefabName))
        {
            if (vanillaPrefabNames.Contains(candidate))
            {
                return ValheimOwnerName;
            }
        }

        Dictionary<string, string> modPrefabOwners = GetModPrefabOwners();
        foreach (string candidate in EnumerateLookupCandidates(prefabName))
        {
            if (modPrefabOwners.TryGetValue(candidate, out string ownerName) &&
                !string.IsNullOrWhiteSpace(ownerName))
            {
                return ownerName;
            }
        }

        return UnknownOwnerName;
    }

    internal static void InvalidateModOwners()
    {
        lock (ModPrefabSync)
        {
            _modPrefabOwners = null;
        }
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string? prefabName)
    {
        string normalizedName = NormalizePrefabName(prefabName);
        if (normalizedName.Length == 0)
        {
            yield break;
        }

        yield return normalizedName;

        int aliasSeparatorIndex = normalizedName.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            yield return normalizedName.Substring(0, aliasSeparatorIndex);
        }
    }

    private static string NormalizePrefabName(string? prefabName)
    {
        return (prefabName ?? "").Replace("(Clone)", "").Trim();
    }

    private static HashSet<string> GetVanillaPrefabNames()
    {
        lock (VanillaPrefabSync)
        {
            return _vanillaPrefabNames ??= LoadVanillaPrefabNames();
        }
    }

    private static HashSet<string> LoadVanillaPrefabNames()
    {
        HashSet<string> prefabNames = new(StringComparer.OrdinalIgnoreCase);
        string manifestPath = Path.Combine(
            Application.dataPath,
            "StreamingAssets",
            "SoftRef",
            "manifest_extended");
        if (!File.Exists(manifestPath))
        {
            GroundworkPlugin.ModLogger.LogWarning(
                $"Vanilla asset manifest was not found at '{manifestPath}'. " +
                $"Growth reference ownership may place vanilla prefabs under '{UnknownOwnerName}'.");
            return prefabNames;
        }

        try
        {
            foreach (string rawLine in File.ReadLines(manifestPath))
            {
                int markerIndex = rawLine.IndexOf(ManifestPathMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                string assetPath = rawLine.Substring(markerIndex + ManifestPathMarker.Length).Trim();
                if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(assetPath);
                if (!string.IsNullOrWhiteSpace(prefabName))
                {
                    prefabNames.Add(prefabName);
                }
            }
        }
        catch (Exception exception)
        {
            GroundworkPlugin.ModLogger.LogWarning(
                $"Could not read vanilla asset manifest '{manifestPath}'. " +
                $"Growth reference ownership may place vanilla prefabs under '{UnknownOwnerName}'. " +
                exception.GetBaseException().Message);
        }

        return prefabNames;
    }

    private static Dictionary<string, string> GetModPrefabOwners()
    {
        lock (ModPrefabSync)
        {
            return _modPrefabOwners ??= BuildModPrefabOwners();
        }
    }

    private static Dictionary<string, string> BuildModPrefabOwners()
    {
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        List<PluginSnapshot> plugins = CapturePluginSnapshots();

        foreach (AssetBundle assetBundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (assetBundle == null || string.IsNullOrWhiteSpace(assetBundle.name))
            {
                continue;
            }

            string ownerName = ResolveBundleOwnerName(assetBundle.name, plugins);
            if (ownerName.Length == 0)
            {
                continue;
            }

            try
            {
                foreach (string assetPath in assetBundle.GetAllAssetNames())
                {
                    if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string prefabName = Path.GetFileNameWithoutExtension(assetPath);
                    if (!string.IsNullOrWhiteSpace(prefabName))
                    {
                        owners[prefabName] = ownerName;
                    }
                }
            }
            catch (Exception exception)
            {
                GroundworkPlugin.ModLogger.LogDebug(
                    $"Could not inspect asset bundle '{assetBundle.name}' for growth reference ownership. " +
                    exception.GetBaseException().Message);
            }
        }

        return owners;
    }

    private static List<PluginSnapshot> CapturePluginSnapshots()
    {
        return Chainloader.PluginInfos.Values
            .Select(pluginInfo =>
            {
                string pluginName = (pluginInfo.Metadata.Name ?? "").Trim();
                string pluginGuid = (pluginInfo.Metadata.GUID ?? "").Trim();
                string assemblyName = "";
                string[] resourceNames = Array.Empty<string>();
                try
                {
                    assemblyName = pluginInfo.Instance?.GetType().Assembly.GetName().Name ?? "";
                    resourceNames = pluginInfo.Instance?.GetType().Assembly.GetManifestResourceNames() ??
                                    Array.Empty<string>();
                }
                catch
                {
                    // A plugin can still be initializing while the reference snapshot is built.
                }

                return new PluginSnapshot
                {
                    OwnerName = pluginName.Length > 0 ? pluginName : pluginGuid,
                    PluginName = pluginName,
                    PluginGuid = pluginGuid,
                    AssemblyName = assemblyName,
                    ResourceNames = resourceNames
                };
            })
            .Where(plugin => plugin.OwnerName.Length > 0)
            .ToList();
    }

    private static string ResolveBundleOwnerName(string bundleName, IEnumerable<PluginSnapshot> plugins)
    {
        PluginSnapshot? embeddedOwner = plugins.FirstOrDefault(plugin =>
            plugin.ResourceNames.Any(resourceName =>
                resourceName.EndsWith(bundleName, StringComparison.OrdinalIgnoreCase)));
        if (embeddedOwner != null)
        {
            return embeddedOwner.OwnerName;
        }

        string normalizedBundleName = NormalizeToken(Path.GetFileNameWithoutExtension(bundleName));
        if (normalizedBundleName.Length == 0)
        {
            return "";
        }

        PluginSnapshot? tokenOwner = plugins.FirstOrDefault(plugin =>
            IsTokenMatch(normalizedBundleName, NormalizeToken(plugin.PluginName)) ||
            IsTokenMatch(normalizedBundleName, NormalizeToken(plugin.PluginGuid)) ||
            IsTokenMatch(normalizedBundleName, NormalizeToken(plugin.AssemblyName)));
        return tokenOwner?.OwnerName ?? "";
    }

    private static bool IsTokenMatch(string bundleName, string pluginToken)
    {
        return pluginToken.Length > 0 &&
               (bundleName.IndexOf(pluginToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                pluginToken.IndexOf(bundleName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string NormalizeToken(string value)
    {
        return new string((value ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private sealed class PluginSnapshot
    {
        internal string OwnerName { get; set; } = "";

        internal string PluginName { get; set; } = "";

        internal string PluginGuid { get; set; } = "";

        internal string AssemblyName { get; set; } = "";

        internal string[] ResourceNames { get; set; } = Array.Empty<string>();
    }
}
