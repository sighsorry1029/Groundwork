using System;
using System.Collections.Generic;

namespace Groundwork;

internal sealed class NormalizedTerrainToolConfig
{
    public string ToolPrefabName { get; set; } = "";

    public string PiecePrefabName { get; set; } = "";

    public bool HasCostOverride { get; set; }

    public Dictionary<string, int> Cost { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RangeEnabled { get; set; }

    public bool HasRangeEnabled { get; set; }

    public float MinRange { get; set; }

    public float MaxRange { get; set; }

    public bool HasMaxRange { get; set; }

    public float DefaultRange { get; set; }

    public float RadiusMax { get; set; }

    public bool HasRadiusMax { get; set; }

    public float DepthMax { get; set; }

    public bool HasDepthMax { get; set; }

    public float MaterialCostFactor { get; set; } = 1f;

    public float StaminaCostFactor { get; set; } = 1f;

    public bool HasStaminaCostFactor { get; set; }

    public float DurabilityFactor { get; set; } = 1f;

    public bool HasDurabilityFactor { get; set; }
}
