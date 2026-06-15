using System;
using System.Collections.Generic;

namespace Groundwork;

internal static class GroundworkTerrainToolNormalizer
{
    internal static List<NormalizedTerrainToolConfig> Normalize(
        IReadOnlyDictionary<string, Dictionary<string, TerrainToolPieceConfig>> terrainTools)
    {
        List<NormalizedTerrainToolConfig> normalized = new();
        foreach ((string toolPrefabName, Dictionary<string, TerrainToolPieceConfig> pieceConfigs) in terrainTools)
        {
            if (string.IsNullOrWhiteSpace(toolPrefabName) || pieceConfigs == null)
            {
                continue;
            }

            string normalizedToolPrefabName = toolPrefabName.Trim();
            foreach ((string piecePrefabName, TerrainToolPieceConfig raw) in pieceConfigs)
            {
                if (string.IsNullOrWhiteSpace(piecePrefabName) || raw == null)
                {
                    continue;
                }

                Dictionary<string, int> cost = new(StringComparer.OrdinalIgnoreCase);
                if (raw.Cost != null)
                {
                    foreach ((string itemPrefabName, int amount) in raw.Cost)
                    {
                        if (string.IsNullOrWhiteSpace(itemPrefabName))
                        {
                            continue;
                        }

                        cost[itemPrefabName.Trim()] = Math.Max(0, amount);
                    }
                }

                TerrainToolRangeConfig? range = raw.Range;
                normalized.Add(new NormalizedTerrainToolConfig
                {
                    ToolPrefabName = normalizedToolPrefabName,
                    PiecePrefabName = piecePrefabName.Trim(),
                    Cost = cost,
                    HasCostOverride = raw.Cost != null,
                    RangeEnabled = range?.Enabled == true,
                    HasRangeEnabled = range?.Enabled.HasValue == true,
                    MinRange = Math.Max(0.1f, range?.Min ?? 0f),
                    MaxRange = Math.Max(0.1f, range?.Max ?? 0f),
                    HasMaxRange = range?.Max.HasValue == true,
                    DefaultRange = Math.Max(0f, range?.Default ?? 0f),
                    RadiusMax = Math.Max(0f, range?.RadiusMax ?? 0f),
                    HasRadiusMax = range?.RadiusMax.HasValue == true,
                    DepthMax = Math.Max(0f, range?.DepthMax ?? 0f),
                    HasDepthMax = range?.DepthMax.HasValue == true,
                    MaterialCostFactor = Math.Max(0f, range?.MaterialCostFactor ?? 1f),
                    StaminaCostFactor = Math.Max(0f, range?.StaminaCostFactor ?? 1f),
                    HasStaminaCostFactor = range?.StaminaCostFactor.HasValue == true,
                    DurabilityFactor = Math.Max(0f, range?.DurabilityFactor ?? 1f),
                    HasDurabilityFactor = range?.DurabilityFactor.HasValue == true
                });
            }
        }

        return normalized;
    }
}
