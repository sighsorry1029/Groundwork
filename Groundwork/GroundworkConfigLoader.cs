using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Groundwork;

internal static class GroundworkConfigLoader
{
    private const string DefaultTerrainToolsResourceName = "Groundwork.Resources.Defaults.Groundwork.yml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal static void EnsureLocalFileExists(string configDirectoryPath, string terrainToolsYamlFilePath)
    {
        Directory.CreateDirectory(configDirectoryPath);
        if (!File.Exists(terrainToolsYamlFilePath))
        {
            File.WriteAllText(terrainToolsYamlFilePath, LoadDefaultTerrainToolsYaml());
        }
    }

    internal static bool TryParseTerrainToolsYaml(string yamlText, out IReadOnlyList<NormalizedTerrainToolConfig>? configs)
    {
        configs = null;
        if (!TryParseTerrainToolBlocks(yamlText, out Dictionary<string, Dictionary<string, TerrainToolPieceConfig>>? parsed))
        {
            return false;
        }

        configs = Normalize(parsed!);
        return true;
    }

    private static List<NormalizedTerrainToolConfig> Normalize(
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
                        if (!string.IsNullOrWhiteSpace(itemPrefabName))
                        {
                            cost[itemPrefabName.Trim()] = Math.Max(0, amount);
                        }
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

    private static bool TryParseTerrainToolBlocks(
        string yamlText,
        out Dictionary<string, Dictionary<string, TerrainToolPieceConfig>>? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            parsed = new Dictionary<string, Dictionary<string, TerrainToolPieceConfig>>(StringComparer.OrdinalIgnoreCase);
            return true;
        }

        try
        {
            parsed = new Dictionary<string, Dictionary<string, TerrainToolPieceConfig>>(StringComparer.OrdinalIgnoreCase);
            YamlStream stream = new();
            stream.Load(new StringReader(yamlText));
            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return true;
            }

            foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
            {
                string rootKey = (entry.Key as YamlScalarNode)?.Value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(rootKey))
                {
                    continue;
                }

                if (entry.Value is not YamlMappingNode mapping)
                {
                    parsed[rootKey] = new Dictionary<string, TerrainToolPieceConfig>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                try
                {
                    Dictionary<string, TerrainToolPieceConfig> pieceConfigs =
                        DeserializeYamlNode<Dictionary<string, TerrainToolPieceConfig>>(mapping)
                        ?? new Dictionary<string, TerrainToolPieceConfig>();
                    parsed[rootKey] = pieceConfigs.ToDictionary(
                        pieceEntry => pieceEntry.Key,
                        pieceEntry => pieceEntry.Value,
                        StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception entryException)
                {
                    GroundworkPlugin.ModLogger.LogWarning($"Skipping Groundwork.yml block '{rootKey}': {entryException.Message}");
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            GroundworkPlugin.ModLogger.LogError($"Failed to parse Groundwork.yml: {exception.Message}");
            return false;
        }
    }

    private static string LoadDefaultTerrainToolsYaml()
    {
        Assembly assembly = typeof(GroundworkConfigLoader).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(DefaultTerrainToolsResourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded default YAML resource '{DefaultTerrainToolsResourceName}' was not found.");
        }

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static T? DeserializeYamlNode<T>(YamlNode node)
    {
        using StringWriter writer = new();
        YamlStream stream = new(new YamlDocument(node));
        stream.Save(writer, assignAnchors: false);
        return Deserializer.Deserialize<T>(writer.ToString());
    }
}

internal sealed class TerrainToolPieceConfig
{
    public Dictionary<string, int>? Cost { get; set; }

    public TerrainToolRangeConfig? Range { get; set; }
}

internal sealed class TerrainToolRangeConfig
{
    public bool? Enabled { get; set; }

    public float? Min { get; set; }

    public float? Max { get; set; }

    public float? Default { get; set; }

    public float? RadiusMax { get; set; }

    public float? DepthMax { get; set; }

    public float? MaterialCostFactor { get; set; }

    public float? StaminaCostFactor { get; set; }

    public float? DurabilityFactor { get; set; }
}

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
