using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Groundwork;

internal static class BeehivePollinationSystem
{
    private const string TendedFarmingLevelKey = "Groundwork_BeehiveTendedFarmingLevel";
    private const string ProductThresholdKey = "Groundwork_BeehiveProductThresholdV1";
    private const float ProductThresholdEpsilon = 0.001f;
    private const float PollinationCacheLifetimeSeconds = 3f;
    private const float PollinationCachePruneIntervalSeconds = 30f;
    private const float PollinationCacheMaxIdleSeconds = 60f;
    private const float AssignmentCachePositionEpsilonSqr = 0.0001f;
    private const float UnloadedCatchupEffectiveness = 0.5f;
    private const float UnloadedCatchupThresholdSeconds = 30f;
    private const float UnloadedCatchupDaylightShare = 0.5f;
    private const int PollinationSearchMaxBufferSize = 2048;
    private const int PollinationRangePreviewSegmentCount = 96;
    private const int PollinationRangePreviewMinRadialSteps = 8;
    private const int PollinationRangePreviewMaxRadialSteps = 40;
    private const int PollinationRangePreviewRefinementIterations = 5;
    private const int PollinationTargetMarkerSegmentCount = 16;
    private const float PollinationPreviewYOffset = 0.06f;
    private const float PollinationTargetMarkerYOffset = 0.12f;
    private const float PollinationRangePreviewLineWidth = 0.045f;
    private const float PollinationPreviewRefreshSeconds = 0.25f;
    private const float PollinationRangePreviewRefreshSeconds = 0.5f;
    private const float PollinationRangePreviewRadialStepMeters = 0.5f;
    private const float PollinationRangePreviewMinimumRadius = 0.05f;
    private const float PollinationRangePreviewSphereEpsilon = 0.0001f;
    private const float PollinationTargetMarkerRadius = 0.29f;
    private const float PollinationTargetMarkerInnerRadius = PollinationTargetMarkerRadius - PollinationRangePreviewLineWidth * 0.5f;
    private const float PollinationTargetMarkerOuterRadius = PollinationTargetMarkerRadius + PollinationRangePreviewLineWidth * 0.5f;
    private static readonly Color ActivePollinationPreviewColor = new(0.35f, 1f, 0.35f, 0.45f);
    private static readonly Color InactivePollinationPreviewColor = new(0.65f, 0.65f, 0.65f, 0.375f);
    private static Collider[] PollinationHits = new Collider[256];
    private static Collider[] AssignmentHits = new Collider[128];
    private static readonly Dictionary<Beehive, PollinationCache> PollinationCaches = [];
    private static readonly Dictionary<Component, AssignmentCache> AssignmentCaches = [];
    private static readonly Dictionary<Component, long> LoadedSinceTicksByTarget = [];
    private static readonly List<PollinationTargetCandidate> PollinationCandidates = [];
    private static readonly List<Beehive> StalePollinationCacheHives = [];
    private static readonly List<Component> StaleAssignmentCacheTargets = [];
    private static readonly List<Component> StaleLoadedSinceTargets = [];
    private static readonly HashSet<Plant> SeenPlants = [];
    private static readonly HashSet<Pickable> SeenPickables = [];
    private static readonly HashSet<Beehive> SeenHives = [];
    private static readonly Vector3[] PollinationRangePreviewPositions = new Vector3[PollinationRangePreviewSegmentCount + 1];
    private static readonly List<Vector3> PollinationTargetPreviewVertices = [];
    private static readonly List<int> PollinationTargetPreviewIndices = [];
    private static readonly List<Color> PollinationTargetPreviewColors = [];
    private static GameObject? PollinationRangePreviewObject;
    private static LineRenderer? PollinationRangePreviewLine;
    private static GameObject? PollinationTargetPreviewObject;
    private static MeshFilter? PollinationTargetPreviewMeshFilter;
    private static MeshRenderer? PollinationTargetPreviewRenderer;
    private static Mesh? PollinationTargetPreviewMesh;
    private static Material? PollinationPreviewMaterial;
    private static Beehive? PollinationRangePreviewHive;
    private static Beehive? PollinationTargetPreviewHive;
    private static Beehive? PollinationPreviewStatusHive;
    private static float PollinationRangePreviewRadius = -1f;
    private static float _nextPollinationRangePreviewRefreshAt;
    private static float _nextPollinationTargetPreviewRefreshAt;
    private static float _nextPollinationPreviewStatusRefreshAt;
    private static bool _pollinationRangePreviewHasGeometry;
    private static bool _pollinationTargetPreviewActive;
    private static bool _pollinationPreviewStructurallyActive;
    private static bool _pollinationPreviewCurrentlyActive;
    private static Player? _placingPlayer;
    private static int _pollinationMask;
    private static float _nextPollinationCachePruneAt;
    private static bool _reportedPollinationSearchSaturation;
    private static bool _reportedAssignmentSearchSaturation;

    internal readonly struct PollinationSummary(int count, int maxCount, float honeyMultiplier)
    {
        internal readonly int Count = count;
        internal readonly int MaxCount = maxCount;
        internal readonly float HoneyMultiplier = honeyMultiplier;
    }

    private readonly struct PollinationTargetCandidate(Plant? plant, Pickable? pickable, float horizontalDistance, float heightDistance, int instanceId)
    {
        internal readonly Plant? Plant = plant;
        internal readonly Pickable? Pickable = pickable;
        internal readonly float HorizontalDistance = horizontalDistance;
        internal readonly float HeightDistance = heightDistance;
        internal readonly int InstanceId = instanceId;
    }

    private sealed class PollinationCache
    {
        internal readonly HashSet<Plant> Plants = [];
        internal readonly HashSet<Pickable> Pickables = [];
        internal float RefreshedAt = -PollinationCacheMaxIdleSeconds;
        internal int MaxCount;
        internal float Radius;

        internal int Count => Plants.Count + Pickables.Count;

        internal bool IsFresh(float now, int maxCount, float radius)
        {
            return now - RefreshedAt <= PollinationCacheLifetimeSeconds &&
                   MaxCount == maxCount &&
                   Mathf.Approximately(Radius, radius);
        }

        internal void Clear()
        {
            Plants.Clear();
            Pickables.Clear();
        }
    }

    private sealed class AssignmentCache
    {
        internal Beehive? AssignedHive;
        internal float RefreshedAt = -PollinationCacheMaxIdleSeconds;
        internal float Radius;
        internal Vector3 TargetPosition;

        internal bool IsFresh(float now, float radius, Vector3 targetPosition)
        {
            return now - RefreshedAt <= PollinationCacheLifetimeSeconds &&
                   Mathf.Approximately(Radius, radius) &&
                   (TargetPosition - targetPosition).sqrMagnitude <= AssignmentCachePositionEpsilonSqr;
        }

        internal void Set(Beehive? assignedHive, float now, float radius, Vector3 targetPosition)
        {
            AssignedHive = assignedHive;
            RefreshedAt = now;
            Radius = radius;
            TargetPosition = targetPosition;
        }
    }

    internal static void Shutdown()
    {
        DestroyHoverPreview();
        PollinationCaches.Clear();
        AssignmentCaches.Clear();
        LoadedSinceTicksByTarget.Clear();
        PollinationCandidates.Clear();
        StalePollinationCacheHives.Clear();
        StaleAssignmentCacheTargets.Clear();
        StaleLoadedSinceTargets.Clear();
        SeenPlants.Clear();
        SeenPickables.Clear();
        SeenHives.Clear();
        PollinationHits = new Collider[256];
        AssignmentHits = new Collider[128];
        _placingPlayer = null;
        _pollinationMask = 0;
        _nextPollinationCachePruneAt = 0f;
        _reportedPollinationSearchSaturation = false;
        _reportedAssignmentSearchSaturation = false;
    }

    internal static void InvalidateTargetCaches()
    {
        PollinationCaches.Clear();
        AssignmentCaches.Clear();
    }

    internal static void UpdateHoverPreview(Player player)
    {
        if (player == null ||
            player != Player.m_localPlayer ||
            !GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            !GroundworkToolsDomain.BeehivePollinationPreviewEnabled)
        {
            ClearHoverPreview();
            return;
        }

        GameObject? hoverObject = player.GetHoverObject();
        Beehive? beehive = hoverObject != null
            ? hoverObject.GetComponent<Beehive>() ??
              hoverObject.GetComponentInParent<Beehive>() ??
              hoverObject.GetComponentInChildren<Beehive>()
            : null;
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        if (beehive == null || !IsValid(beehive) || maxCount <= 0 || radius <= 0f)
        {
            ClearHoverPreview();
            return;
        }

        RefreshPollinationPreviewStatus(beehive);

        UpdatePollinationRangePreview(beehive, radius, _pollinationPreviewCurrentlyActive);

        if (_pollinationPreviewStructurallyActive)
        {
            UpdatePollinationTargetPreview(beehive, maxCount, radius, _pollinationPreviewCurrentlyActive);
            return;
        }

        HidePollinationTargetPreview();
    }

    internal static void ClearHoverPreview()
    {
        HidePollinationRangePreview();
        HidePollinationTargetPreview();
        PollinationPreviewStatusHive = null;
        _nextPollinationPreviewStatusRefreshAt = 0f;
        _pollinationPreviewStructurallyActive = false;
        _pollinationPreviewCurrentlyActive = false;
    }

    internal static void DestroyHoverPreview()
    {
        ClearHoverPreview();

        if (PollinationRangePreviewObject != null)
        {
            UnityEngine.Object.Destroy(PollinationRangePreviewObject);
        }

        if (PollinationTargetPreviewObject != null)
        {
            UnityEngine.Object.Destroy(PollinationTargetPreviewObject);
        }

        if (PollinationTargetPreviewMesh != null)
        {
            UnityEngine.Object.Destroy(PollinationTargetPreviewMesh);
        }

        if (PollinationPreviewMaterial != null)
        {
            UnityEngine.Object.Destroy(PollinationPreviewMaterial);
        }

        PollinationRangePreviewObject = null;
        PollinationRangePreviewLine = null;
        PollinationTargetPreviewObject = null;
        PollinationTargetPreviewMeshFilter = null;
        PollinationTargetPreviewRenderer = null;
        PollinationTargetPreviewMesh = null;
        PollinationPreviewMaterial = null;
        PollinationTargetPreviewVertices.Clear();
        PollinationTargetPreviewIndices.Clear();
        PollinationTargetPreviewColors.Clear();
    }

    private static void UpdatePollinationRangePreview(Beehive beehive, float radius, bool active)
    {
        LineRenderer? line = EnsurePollinationRangePreviewLine();
        if (line == null || PollinationRangePreviewObject == null)
        {
            return;
        }

        Color color = active ? ActivePollinationPreviewColor : InactivePollinationPreviewColor;
        line.startColor = color;
        line.endColor = color;

        Vector3 center = beehive.transform.position;
        bool changed = PollinationRangePreviewHive != beehive ||
                       !Mathf.Approximately(PollinationRangePreviewRadius, radius);
        if (changed)
        {
            PollinationRangePreviewHive = beehive;
            PollinationRangePreviewRadius = radius;
            _nextPollinationRangePreviewRefreshAt = 0f;
            _pollinationRangePreviewHasGeometry = false;
        }

        float now = Time.realtimeSinceStartup;
        if (now >= _nextPollinationRangePreviewRefreshAt)
        {
            _nextPollinationRangePreviewRefreshAt = now + PollinationRangePreviewRefreshSeconds;
            if (TryBuildPollinationRangePreview(center, radius))
            {
                line.SetPositions(PollinationRangePreviewPositions);
                _pollinationRangePreviewHasGeometry = true;
            }
            else
            {
                _pollinationRangePreviewHasGeometry = false;
            }
        }

        PollinationRangePreviewObject.SetActive(_pollinationRangePreviewHasGeometry);
    }

    private static bool TryBuildPollinationRangePreview(Vector3 center, float radius)
    {
        // A single closed line can represent only the terrain footprint connected to the point below the hive.
        // Actual collider-based targets remain authoritative and are shown by the separate target markers.
        if (!TryGetHeightmapSurfaceHeight(center, out float centerTerrainHeight))
        {
            return false;
        }

        float radiusSquared = radius * radius;
        float centerHeightDifference = centerTerrainHeight - center.y;
        float centerHorizontalRadiusSquared = radiusSquared - centerHeightDifference * centerHeightDifference;
        if (centerHorizontalRadiusSquared <= PollinationRangePreviewMinimumRadius * PollinationRangePreviewMinimumRadius)
        {
            return false;
        }

        int radialSteps = Mathf.Clamp(
            Mathf.CeilToInt(radius / PollinationRangePreviewRadialStepMeters),
            PollinationRangePreviewMinRadialSteps,
            PollinationRangePreviewMaxRadialSteps);
        float radialStep = radius / radialSteps;

        for (int index = 0; index < PollinationRangePreviewSegmentCount; index++)
        {
            float angle = index / (float)PollinationRangePreviewSegmentCount * Mathf.PI * 2f;
            Vector3 direction = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            float insideDistance = 0f;
            float insideHeight = centerTerrainHeight;

            for (int step = 1; step <= radialSteps; step++)
            {
                float sampleDistance = step == radialSteps ? radius : radialStep * step;
                Vector3 samplePoint = center + direction * sampleDistance;
                if (!TryGetHeightmapSurfaceHeight(samplePoint, out float sampleHeight))
                {
                    return false;
                }

                if (IsTerrainPointWithinPollinationSphere(
                        sampleDistance,
                        sampleHeight,
                        center.y,
                        radiusSquared))
                {
                    insideDistance = sampleDistance;
                    insideHeight = sampleHeight;
                    continue;
                }

                float outsideDistance = sampleDistance;
                for (int refinement = 0; refinement < PollinationRangePreviewRefinementIterations; refinement++)
                {
                    float midpointDistance = (insideDistance + outsideDistance) * 0.5f;
                    Vector3 midpoint = center + direction * midpointDistance;
                    if (!TryGetHeightmapSurfaceHeight(midpoint, out float midpointHeight))
                    {
                        return false;
                    }

                    if (IsTerrainPointWithinPollinationSphere(
                            midpointDistance,
                            midpointHeight,
                            center.y,
                            radiusSquared))
                    {
                        insideDistance = midpointDistance;
                        insideHeight = midpointHeight;
                    }
                    else
                    {
                        outsideDistance = midpointDistance;
                    }
                }

                break;
            }

            Vector3 point = center + direction * insideDistance;
            point.y = insideHeight + PollinationPreviewYOffset;
            PollinationRangePreviewPositions[index] = point;
        }

        PollinationRangePreviewPositions[PollinationRangePreviewSegmentCount] = PollinationRangePreviewPositions[0];
        return true;
    }

    private static bool IsTerrainPointWithinPollinationSphere(
        float horizontalDistance,
        float terrainHeight,
        float centerHeight,
        float radiusSquared)
    {
        float heightDifference = terrainHeight - centerHeight;
        return horizontalDistance * horizontalDistance + heightDifference * heightDifference <=
               radiusSquared + PollinationRangePreviewSphereEpsilon;
    }

    private static bool TryGetTerrainHeight(Vector3 point, out float height)
    {
        if (TryGetHeightmapSurfaceHeight(point, out height))
        {
            return true;
        }

        if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out height))
        {
            return true;
        }

        return Heightmap.GetHeight(point, out height);
    }

    private static bool TryGetHeightmapSurfaceHeight(Vector3 point, out float height)
    {
        Heightmap? heightmap = Heightmap.FindHeightmap(point);
        if (heightmap == null || heightmap.m_width <= 0 || heightmap.m_scale <= 0f)
        {
            height = 0f;
            return false;
        }

        Vector3 localPoint = heightmap.transform.InverseTransformPoint(point);
        float halfWidth = heightmap.m_width * heightmap.m_scale * 0.5f;
        float gridX = (localPoint.x + halfWidth) / heightmap.m_scale;
        float gridZ = (localPoint.z + halfWidth) / heightmap.m_scale;
        if (gridX < 0f || gridZ < 0f || gridX > heightmap.m_width || gridZ > heightmap.m_width)
        {
            height = 0f;
            return false;
        }

        int cellX = Mathf.Min(Mathf.FloorToInt(gridX), heightmap.m_width - 1);
        int cellZ = Mathf.Min(Mathf.FloorToInt(gridZ), heightmap.m_width - 1);
        float cellXFactor = gridX - cellX;
        float cellZFactor = gridZ - cellZ;
        float height00 = heightmap.GetHeight(cellX, cellZ);
        float height10 = heightmap.GetHeight(cellX + 1, cellZ);
        float height01 = heightmap.GetHeight(cellX, cellZ + 1);
        float height11 = heightmap.GetHeight(cellX + 1, cellZ + 1);
        // Match Heightmap.RebuildCollisionMesh's v00-v01-v10 / v10-v01-v11 diagonal.
        float localHeight = cellXFactor + cellZFactor <= 1f
            ? height00 + (height10 - height00) * cellXFactor + (height01 - height00) * cellZFactor
            : height11 + (height01 - height11) * (1f - cellXFactor) +
              (height10 - height11) * (1f - cellZFactor);
        height = heightmap.transform.TransformPoint(new Vector3(localPoint.x, localHeight, localPoint.z)).y;
        return true;
    }

    private static LineRenderer? EnsurePollinationRangePreviewLine()
    {
        if (PollinationRangePreviewLine != null && PollinationRangePreviewObject != null)
        {
            return PollinationRangePreviewLine;
        }

        PollinationRangePreviewObject = new GameObject("Groundwork_BeehivePollinationRangePreview")
        {
            hideFlags = HideFlags.DontSave
        };
        PollinationRangePreviewLine = PollinationRangePreviewObject.AddComponent<LineRenderer>();
        Material? material = GetPollinationPreviewMaterial();
        if (material != null)
        {
            PollinationRangePreviewLine.sharedMaterial = material;
        }

        PollinationRangePreviewLine.useWorldSpace = true;
        PollinationRangePreviewLine.loop = false;
        PollinationRangePreviewLine.positionCount = PollinationRangePreviewSegmentCount + 1;
        PollinationRangePreviewLine.widthMultiplier = PollinationRangePreviewLineWidth;
        PollinationRangePreviewLine.numCapVertices = 2;
        PollinationRangePreviewLine.numCornerVertices = 2;
        PollinationRangePreviewLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        PollinationRangePreviewLine.receiveShadows = false;
        PollinationRangePreviewObject.SetActive(false);
        return PollinationRangePreviewLine;
    }

    private static void RefreshPollinationPreviewStatus(Beehive beehive)
    {
        float now = Time.realtimeSinceStartup;
        if (PollinationPreviewStatusHive == beehive && now < _nextPollinationPreviewStatusRefreshAt)
        {
            return;
        }

        PollinationPreviewStatusHive = beehive;
        _nextPollinationPreviewStatusRefreshAt = now + PollinationPreviewRefreshSeconds;
        _pollinationPreviewStructurallyActive = CanHivePollinate(beehive, respectCurrentEnvironment: false);
        _pollinationPreviewCurrentlyActive = _pollinationPreviewStructurallyActive && IsLoadedPollinationEnvironmentActive();
    }

    private static void UpdatePollinationTargetPreview(
        Beehive beehive,
        int maxCount,
        float radius,
        bool active)
    {
        bool changed = PollinationTargetPreviewHive != beehive || _pollinationTargetPreviewActive != active;
        if (changed)
        {
            PollinationTargetPreviewHive = beehive;
            _pollinationTargetPreviewActive = active;
            _nextPollinationTargetPreviewRefreshAt = 0f;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextPollinationTargetPreviewRefreshAt)
        {
            return;
        }

        _nextPollinationTargetPreviewRefreshAt = now + PollinationPreviewRefreshSeconds;
        PollinationCache? cache = GetPollinationCache(beehive, maxCount, radius);
        MeshRenderer? renderer = EnsurePollinationTargetPreviewRenderer();
        if (cache == null || renderer == null || PollinationTargetPreviewObject == null || PollinationTargetPreviewMesh == null)
        {
            return;
        }

        PollinationTargetPreviewVertices.Clear();
        PollinationTargetPreviewIndices.Clear();
        PollinationTargetPreviewColors.Clear();
        Color color = active ? ActivePollinationPreviewColor : InactivePollinationPreviewColor;
        foreach (Plant plant in cache.Plants)
        {
            if (plant != null)
            {
                AddPollinationTargetMarker(plant.transform.position, color);
            }
        }

        foreach (Pickable pickable in cache.Pickables)
        {
            if (pickable != null)
            {
                AddPollinationTargetMarker(pickable.transform.position, color);
            }
        }

        PollinationTargetPreviewMesh.Clear();
        if (PollinationTargetPreviewVertices.Count == 0)
        {
            PollinationTargetPreviewObject.SetActive(false);
            return;
        }

        PollinationTargetPreviewMesh.SetVertices(PollinationTargetPreviewVertices);
        PollinationTargetPreviewMesh.SetColors(PollinationTargetPreviewColors);
        PollinationTargetPreviewMesh.SetIndices(PollinationTargetPreviewIndices, MeshTopology.Triangles, 0);
        PollinationTargetPreviewMesh.RecalculateBounds();
        PollinationTargetPreviewObject.SetActive(true);
    }

    private static void AddPollinationTargetMarker(Vector3 center, Color color)
    {
        if (TryGetTerrainHeight(center, out float terrainHeight) && Mathf.Abs(center.y - terrainHeight) <= 1f)
        {
            center.y = terrainHeight;
        }

        center.y += PollinationTargetMarkerYOffset;
        int vertexStart = PollinationTargetPreviewVertices.Count;
        for (int index = 0; index < PollinationTargetMarkerSegmentCount; index++)
        {
            float angle = index / (float)PollinationTargetMarkerSegmentCount * Mathf.PI * 2f;
            Vector3 direction = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            PollinationTargetPreviewVertices.Add(center + direction * PollinationTargetMarkerInnerRadius);
            PollinationTargetPreviewVertices.Add(center + direction * PollinationTargetMarkerOuterRadius);
            PollinationTargetPreviewColors.Add(color);
            PollinationTargetPreviewColors.Add(color);
        }

        for (int index = 0; index < PollinationTargetMarkerSegmentCount; index++)
        {
            int next = (index + 1) % PollinationTargetMarkerSegmentCount;
            int inner = vertexStart + index * 2;
            int outer = inner + 1;
            int nextInner = vertexStart + next * 2;
            int nextOuter = nextInner + 1;
            AddPollinationTargetTriangle(inner, outer, nextOuter);
            AddPollinationTargetTriangle(inner, nextOuter, nextInner);
        }
    }

    private static void AddPollinationTargetTriangle(int first, int second, int third)
    {
        PollinationTargetPreviewIndices.Add(first);
        PollinationTargetPreviewIndices.Add(second);
        PollinationTargetPreviewIndices.Add(third);
    }

    private static MeshRenderer? EnsurePollinationTargetPreviewRenderer()
    {
        if (PollinationTargetPreviewRenderer != null &&
            PollinationTargetPreviewMeshFilter != null &&
            PollinationTargetPreviewObject != null &&
            PollinationTargetPreviewMesh != null)
        {
            return PollinationTargetPreviewRenderer;
        }

        PollinationTargetPreviewObject = new GameObject("Groundwork_BeehivePollinationTargetPreview")
        {
            hideFlags = HideFlags.DontSave
        };
        PollinationTargetPreviewMeshFilter = PollinationTargetPreviewObject.AddComponent<MeshFilter>();
        PollinationTargetPreviewRenderer = PollinationTargetPreviewObject.AddComponent<MeshRenderer>();
        Material? material = GetPollinationPreviewMaterial();
        if (material != null)
        {
            PollinationTargetPreviewRenderer.sharedMaterial = material;
        }

        PollinationTargetPreviewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        PollinationTargetPreviewRenderer.receiveShadows = false;
        PollinationTargetPreviewMesh = new Mesh
        {
            name = "Groundwork_BeehivePollinationTargetPreviewMesh",
            hideFlags = HideFlags.DontSave
        };
        PollinationTargetPreviewMesh.MarkDynamic();
        PollinationTargetPreviewMeshFilter.sharedMesh = PollinationTargetPreviewMesh;
        PollinationTargetPreviewObject.SetActive(false);
        return PollinationTargetPreviewRenderer;
    }

    private static Material? GetPollinationPreviewMaterial()
    {
        if (PollinationPreviewMaterial != null)
        {
            return PollinationPreviewMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default") ??
                        Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                        Shader.Find("Standard");
        if (shader == null)
        {
            return null;
        }

        PollinationPreviewMaterial = new Material(shader)
        {
            color = Color.white,
            hideFlags = HideFlags.DontSave
        };
        return PollinationPreviewMaterial;
    }

    private static void HidePollinationRangePreview()
    {
        if (PollinationRangePreviewObject != null)
        {
            PollinationRangePreviewObject.SetActive(false);
        }

        PollinationRangePreviewHive = null;
        PollinationRangePreviewRadius = -1f;
        _nextPollinationRangePreviewRefreshAt = 0f;
        _pollinationRangePreviewHasGeometry = false;
    }

    private static void HidePollinationTargetPreview()
    {
        if (PollinationTargetPreviewObject != null)
        {
            PollinationTargetPreviewObject.SetActive(false);
        }

        PollinationTargetPreviewHive = null;
        _nextPollinationTargetPreviewRefreshAt = 0f;
        _pollinationTargetPreviewActive = false;
    }

    // Beehive hover text and harvest bookkeeping.
    internal static void AppendHoverText(Beehive beehive, ref string hoverText)
    {
        if (beehive == null ||
            !IsValid(beehive) ||
            !PrivateArea.CheckAccess(beehive.transform.position, 0f, flash: false))
        {
            return;
        }

        int honeyLevel = GetHoneyLevel(beehive);
        int maxHoney = GetEffectiveMaxHoney(beehive);
        int farmingCapacityBonus = GetFarmingCapacityBonusHoney(beehive);
        ReplaceHoverHeader(beehive, ref hoverText, honeyLevel, maxHoney, farmingCapacityBonus);

        if (TryGetCoverPercentage(beehive, out float coverPercentage))
        {
            float coverMultiplier = GetCoverProductionMultiplier(beehive, coverPercentage);
            AppendLine(
                ref hoverText,
                Colorize(GroundworkLocalization.Format(
                    "groundwork_beehive_cover",
                    "Cover: {0} ({1})",
                    FormatPercent(coverPercentage),
                    FormatMultiplier(coverMultiplier))));
        }

        PollinationSummary summary = GetPollinationSummary(beehive);
        if (summary.MaxCount > 0)
        {
            AppendLine(
                ref hoverText,
                Colorize(GroundworkLocalization.Format(
                    "groundwork_beehive_pollination",
                    "Pollination: {0}/{1} ({2})",
                    summary.Count,
                    summary.MaxCount,
                    FormatMultiplier(summary.HoneyMultiplier))));
        }

        AppendCurrentHoneyRateLine(
            ref hoverText,
            GetNightProductionMultiplier(unloadedCatchup: false),
            "groundwork_beehive_night_rate",
            "Night: {0}",
            EnvironmentEffectSystem.GetBeehiveRainHoneyRate(unloadedCatchup: false),
            "groundwork_beehive_rain_rate",
            "Rain: {0}");

        string nextHoney = FormatNextHoney(beehive, honeyLevel, maxHoney, out float honeyRateMultiplier);
        string nextHoneyLine = honeyRateMultiplier > 0.001f
            ? GroundworkLocalization.Format(
                "groundwork_beehive_next_honey_rate",
                "Next honey: {0} (Honey rate {1})",
                nextHoney,
                FormatMultiplier(honeyRateMultiplier))
            : GroundworkLocalization.Format(
                "groundwork_beehive_next_honey",
                "Next honey: {0}",
                nextHoney);
        AppendLine(ref hoverText, Colorize(nextHoneyLine));
    }

    internal static void StoreTendedFarmingLevel(Beehive beehive, long sender)
    {
        StoreTendedFarmingLevel(beehive, ResolveSenderFarmingLevel(sender));
    }

    internal static void RaiseFarmingSkillForHarvest(long sender, int harvestedHoney)
    {
        Player? player = ResolveSenderPlayer(sender);
        if (player != null)
        {
            RaiseFarmingSkillForHarvest(player, harvestedHoney);
        }
    }

    internal static void RegisterBeehiveHarvest(Beehive beehive, Player player, int harvestedHoney)
    {
        if (beehive == null || player == null || harvestedHoney <= 0)
        {
            return;
        }

        StoreTendedFarmingLevel(beehive, player);
        RaiseFarmingSkillForHarvest(player, harvestedHoney);
    }

    private static void StoreTendedFarmingLevel(Beehive beehive, Player player)
    {
        StoreTendedFarmingLevel(beehive, Mathf.Clamp(player.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f));
    }

    private static void StoreTendedFarmingLevel(Beehive beehive, float farmingLevel)
    {
        ZDO? zdo = GetZdo(beehive);
        if (zdo == null || !beehive.m_nview.IsOwner())
        {
            return;
        }

        zdo.Set(TendedFarmingLevelKey, Mathf.Clamp(farmingLevel, 0f, 100f));
    }

    private static void RaiseFarmingSkillForHarvest(Player player, int harvestedHoney)
    {
        float skillGainPerHoney = GroundworkToolsDomain.BeehiveFarmingSkillGainPerHoney;
        if (player == null || harvestedHoney <= 0 || skillGainPerHoney <= 0f)
        {
            return;
        }

        player.RaiseSkill(Skills.SkillType.Farming, harvestedHoney * skillGainPerHoney);
    }

    internal static void BeginPlacePiece(Player player, Piece piece)
    {
        _placingPlayer = player != null &&
                         piece != null &&
                         GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney > 0 &&
                         piece.GetComponentInChildren<Beehive>(includeInactive: true) != null
            ? player
            : null;
    }

    internal static void EndPlacePiece()
    {
        _placingPlayer = null;
    }

    internal static void TryStoreBuilderFarmingLevel(Beehive beehive)
    {
        Player? player = _placingPlayer;
        ZDO? zdo = GetZdo(beehive);
        if (player == null ||
            zdo == null ||
            !beehive.m_nview.IsOwner() ||
            GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney <= 0 ||
            zdo.GetFloat(TendedFarmingLevelKey, -1f) >= 0f)
        {
            return;
        }

        zdo.Set(TendedFarmingLevelKey, Mathf.Clamp(player.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f));
    }

    internal static int GetEffectiveMaxHoney(Beehive beehive, bool preserveStoredHoney = true)
    {
        int baseMax = Mathf.Max(1, beehive.m_maxHoney);
        int effectiveMax = baseMax + GetFarmingCapacityBonusHoney(beehive);

        if (preserveStoredHoney)
        {
            effectiveMax = Mathf.Max(effectiveMax, GetHoneyLevel(beehive));
        }

        return Mathf.Max(baseMax, effectiveMax);
    }

    internal static float GetProductionSpeedMultiplier(Beehive beehive, bool unloadedCatchup = false)
    {
        return GetProductionSpeedMultiplierCore(
            beehive,
            unloadedCatchup,
            evaluateProductionState: false,
            out _);
    }

    internal static float GetProductionSpeedMultiplier(
        Beehive beehive,
        bool unloadedCatchup,
        out bool canProcessProduction)
    {
        return GetProductionSpeedMultiplierCore(
            beehive,
            unloadedCatchup,
            evaluateProductionState: true,
            out canProcessProduction);
    }

    internal static float PrepareProductionThreshold(
        Beehive beehive,
        float multiplier,
        bool canProcessProduction)
    {
        if (beehive == null)
        {
            return 0f;
        }

        float baseSecondsPerHoney = beehive.m_secPerUnit;
        if (baseSecondsPerHoney <= 0f || multiplier <= ProductThresholdEpsilon)
        {
            return baseSecondsPerHoney;
        }

        float currentThreshold = baseSecondsPerHoney / multiplier;
        ZDO? zdo = GetZdo(beehive);
        if (zdo == null || !beehive.m_nview.IsOwner() || !canProcessProduction)
        {
            return currentThreshold;
        }

        float previousThreshold = ResolvePreviousProductThreshold(zdo, currentThreshold);
        bool thresholdChanged = !ThresholdsApproximatelyEqual(previousThreshold, currentThreshold);
        if (thresholdChanged)
        {
            // s_product remains raw seconds; convert it so the completed fraction does not change with the rate.
            float product = zdo.GetFloat(ZDOVars.s_product);
            float projectedProduct = ProjectProductToThreshold(product, previousThreshold, currentThreshold);
            if (!Mathf.Approximately(product, projectedProduct))
            {
                zdo.Set(ZDOVars.s_product, projectedProduct);
            }
        }

        float storedThreshold = zdo.GetFloat(ProductThresholdKey, 0f);
        if (!IsValidProductThreshold(storedThreshold) || thresholdChanged)
        {
            zdo.Set(ProductThresholdKey, currentThreshold);
        }

        return currentThreshold;
    }

    private static float GetProductionSpeedMultiplierCore(
        Beehive beehive,
        bool unloadedCatchup,
        bool evaluateProductionState,
        out bool canProcessProduction)
    {
        canProcessProduction = false;
        if (beehive == null || !IsValid(beehive))
        {
            return 1f;
        }

        bool biomeAllowed = !evaluateProductionState || IsBiomeAllowed(beehive);
        canProcessProduction = evaluateProductionState && biomeAllowed && beehive.m_maxCover <= 0f;
        float multiplier = 1f;
        if (TryGetCoverPercentage(beehive, out float coverPercentage))
        {
            canProcessProduction = evaluateProductionState &&
                                   biomeAllowed &&
                                   HasFreeSpace(beehive, coverPercentage);
            float coverMultiplier = GetCoverProductionMultiplier(beehive, coverPercentage);
            multiplier *= unloadedCatchup ? ApplyUnloadedCatchupEffectiveness(coverMultiplier) : coverMultiplier;
        }

        float pollinationMultiplier = GetPollinationSummary(
            beehive,
            respectCurrentEnvironment: !unloadedCatchup).HoneyMultiplier;
        multiplier *= unloadedCatchup ? ApplyUnloadedCatchupEffectiveness(pollinationMultiplier) : pollinationMultiplier;
        multiplier *= GetNightProductionMultiplier(unloadedCatchup);
        multiplier *= EnvironmentEffectSystem.GetBeehiveRainHoneyRate(unloadedCatchup);
        return Mathf.Max(0f, multiplier);
    }

    private static float ResolvePreviousProductThreshold(ZDO zdo, float currentThreshold)
    {
        float storedThreshold = zdo.GetFloat(ProductThresholdKey, 0f);
        return IsValidProductThreshold(storedThreshold) ? storedThreshold : currentThreshold;
    }

    private static float ProjectProductToThreshold(
        float product,
        float previousThreshold,
        float currentThreshold)
    {
        if (float.IsNaN(product) || float.IsInfinity(product))
        {
            return 0f;
        }

        if (product <= 0f ||
            previousThreshold <= ProductThresholdEpsilon ||
            currentThreshold <= ProductThresholdEpsilon)
        {
            return Mathf.Max(0f, product);
        }

        if (ThresholdsApproximatelyEqual(previousThreshold, currentThreshold))
        {
            return product;
        }

        float progress = Mathf.Clamp01(product / previousThreshold);
        return progress * currentThreshold;
    }

    private static bool ThresholdsApproximatelyEqual(float left, float right)
    {
        float scale = Mathf.Max(1f, Mathf.Max(Mathf.Abs(left), Mathf.Abs(right)));
        return Mathf.Abs(left - right) <= ProductThresholdEpsilon * scale;
    }

    private static bool IsValidProductThreshold(float threshold)
    {
        return threshold > ProductThresholdEpsilon &&
               !float.IsNaN(threshold) &&
               !float.IsInfinity(threshold);
    }

    internal static bool ShouldUseUnloadedProductionCatchup(Beehive beehive)
    {
        ZDO? zdo = GetZdo(beehive);
        return zdo != null && GetSecondsSinceLastUpdate(zdo) > UnloadedCatchupThresholdSeconds;
    }

    internal static void TrackLoadedTarget(Component target)
    {
        if (target == null || ZNet.instance == null)
        {
            return;
        }

        PrunePollinationCaches(Time.realtimeSinceStartup);
        LoadedSinceTicksByTarget[target] = ZNet.instance.GetTime().Ticks;
    }

    internal static long GetLoadedSinceTicks(Component target)
    {
        return target != null && LoadedSinceTicksByTarget.TryGetValue(target, out long loadedSinceTicks)
            ? loadedSinceTicks
            : 0L;
    }

    internal static bool IsPlantGrowthBonusConfigured()
    {
        return GroundworkToolsDomain.BeehivePollinationRadius > 0f &&
               GroundworkToolsDomain.BeehivePollinationMaxPlants > 0 &&
               GroundworkToolsDomain.BeehivePollinationPlantGrowSpeedFactor > 1.001f;
    }

    internal static bool IsForagingRespawnBonusConfigured()
    {
        return GroundworkToolsDomain.BeehivePollinationRadius > 0f &&
               GroundworkToolsDomain.BeehivePollinationMaxPlants > 0 &&
               GroundworkToolsDomain.BeehivePollinationForagingRespawnSpeedFactor > 1.001f;
    }

    internal static void GetPlantGrowthMultipliers(
        Plant plant,
        out float loadedMultiplier,
        out float unloadedMultiplier)
    {
        float structuralMultiplier = GetStructuralPlantGrowthMultiplierForTarget(plant);
        loadedMultiplier = GetLoadedPollinationMultiplier(structuralMultiplier) *
                           EnvironmentEffectSystem.GetWetPlantGrowSpeedMultiplier();
        unloadedMultiplier = ApplyUnloadedCatchupEffectiveness(structuralMultiplier);
        loadedMultiplier = Mathf.Max(1f, loadedMultiplier);
        unloadedMultiplier = Mathf.Max(1f, unloadedMultiplier);
    }

    internal static void GetForagingRespawnMultipliers(
        Pickable pickable,
        out float loadedMultiplier,
        out float unloadedMultiplier)
    {
        float structuralMultiplier = GetStructuralForagingRespawnMultiplierForTarget(pickable);
        loadedMultiplier = GetLoadedPollinationMultiplier(structuralMultiplier) *
                           EnvironmentEffectSystem.GetWetForagingRespawnSpeedMultiplier(pickable);
        unloadedMultiplier = ApplyUnloadedCatchupEffectiveness(structuralMultiplier);
        loadedMultiplier = Mathf.Max(1f, loadedMultiplier);
        unloadedMultiplier = Mathf.Max(1f, unloadedMultiplier);
    }

    internal static float GetPlantGrowthMultiplierForHover(Plant plant)
    {
        return plant != null && IsLoadedPollinationEnvironmentActive()
            ? GetStructuralPlantGrowthMultiplierForTarget(plant)
            : 1f;
    }

    internal static float GetForagingRespawnMultiplierForHover(Pickable pickable)
    {
        return pickable != null &&
               IsLoadedPollinationEnvironmentActive() &&
               FarmingSkillSystem.IsForagingTarget(pickable)
            ? GetStructuralForagingRespawnMultiplierForTarget(pickable)
            : 1f;
    }

    private static string FormatNextHoney(Beehive beehive, int honeyLevel, int maxHoney, out float honeyRateMultiplier)
    {
        honeyRateMultiplier = 0f;
        if (honeyLevel >= maxHoney)
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_full", "full");
        }

        if (!IsBiomeAllowed(beehive))
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_wrong_biome", "wrong biome");
        }

        if (!TryGetCoverPercentage(beehive, out float coverPercentage) ||
            !HasFreeSpace(beehive, coverPercentage))
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_blocked", "blocked");
        }

        if (EnvironmentEffectSystem.IsLoadedBeehiveProductionPausedByWetEnvironment())
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_paused_by_rain", "paused by rain");
        }

        ZDO? zdo = GetZdo(beehive);
        if (zdo == null)
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_unknown", "unknown");
        }

        float multiplier = GetProductionSpeedMultiplier(beehive);
        if (multiplier <= 0.001f)
        {
            return GroundworkLocalization.Text("groundwork_beehive_status_paused_by_night", "paused by night");
        }

        honeyRateMultiplier = multiplier;
        float effectiveSecondsPerHoney = beehive.m_secPerUnit / Mathf.Max(0.001f, multiplier);
        float previousThreshold = ResolvePreviousProductThreshold(zdo, effectiveSecondsPerHoney);
        float product = ProjectProductToThreshold(
            zdo.GetFloat(ZDOVars.s_product),
            previousThreshold,
            effectiveSecondsPerHoney);
        float elapsed = GetSecondsSinceLastUpdate(zdo);
        float remainingSeconds = Mathf.Max(0f, effectiveSecondsPerHoney - product - elapsed);
        return GroundworkLocalization.FormatDuration(remainingSeconds);
    }

    // Pollination assignment and cache refresh.
    private static PollinationSummary GetPollinationSummary(Beehive beehive, bool respectCurrentEnvironment = true)
    {
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        if (beehive == null ||
            maxCount <= 0 ||
            radius <= 0f)
        {
            return new PollinationSummary(0, 0, 1f);
        }

        if (!CanHivePollinate(beehive, respectCurrentEnvironment))
        {
            return new PollinationSummary(0, maxCount, 1f);
        }

        PollinationCache? cache = GetPollinationCache(
            beehive,
            maxCount,
            radius);
        int count = cache?.Count ?? 0;
        float honeyMultiplier = 1f + count * GroundworkToolsDomain.BeehivePollinationHoneySpeedBonusPercentPerTarget / 100f;
        return new PollinationSummary(count, maxCount, honeyMultiplier);
    }

    private static PollinationCache? GetPollinationCache(
        Beehive beehive,
        int maxCount,
        float radius)
    {
        if (beehive == null || maxCount <= 0 || radius <= 0f)
        {
            return null;
        }

        float now = Time.realtimeSinceStartup;
        PrunePollinationCaches(now);
        if (!PollinationCaches.TryGetValue(beehive, out PollinationCache? cache))
        {
            cache = new PollinationCache();
            PollinationCaches[beehive] = cache;
        }

        if (!cache.IsFresh(now, maxCount, radius))
        {
            RefreshPollinationCache(
                beehive,
                cache,
                maxCount,
                radius,
                now);
        }

        return cache;
    }

    private static void RefreshPollinationCache(
        Beehive beehive,
        PollinationCache cache,
        int maxCount,
        float radius,
        float now)
    {
        cache.Clear();
        cache.RefreshedAt = now;
        cache.MaxCount = maxCount;
        cache.Radius = radius;

        int hitCount = OverlapSphereWithExpandableBuffer(
            beehive.transform.position,
            radius,
            ref PollinationHits,
            ref _reportedPollinationSearchSaturation,
            "beehive target");

        SeenPlants.Clear();
        SeenPickables.Clear();
        PollinationCandidates.Clear();
        try
        {
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = PollinationHits[i];
                if (hit == null)
                {
                    continue;
                }

                Plant? plant = hit.GetComponentInParent<Plant>();
                if (plant != null)
                {
                    if (SeenPlants.Add(plant) &&
                        IsGrowingTarget(plant) &&
                        IsAssignedToHive(plant, beehive, radius))
                    {
                        AddPollinationCandidate(plant, null, plant.transform.position, beehive.transform.position, plant.GetInstanceID());
                    }

                    continue;
                }

                Pickable? pickable = hit.GetComponentInParent<Pickable>();
                if (pickable != null &&
                    SeenPickables.Add(pickable) &&
                    IsGrowingTarget(pickable) &&
                    IsAssignedToHive(pickable, beehive, radius))
                {
                    AddPollinationCandidate(null, pickable, pickable.transform.position, beehive.transform.position, pickable.GetInstanceID());
                }
            }

            PollinationCandidates.Sort(static (left, right) =>
            {
                return ComparePollinationDistance(
                    left.HorizontalDistance,
                    left.HeightDistance,
                    left.InstanceId,
                    right.HorizontalDistance,
                    right.HeightDistance,
                    right.InstanceId);
            });

            for (int i = 0; i < PollinationCandidates.Count && i < maxCount; i++)
            {
                PollinationTargetCandidate candidate = PollinationCandidates[i];
                if (candidate.Plant != null)
                {
                    cache.Plants.Add(candidate.Plant);
                }
                else if (candidate.Pickable != null)
                {
                    cache.Pickables.Add(candidate.Pickable);
                }
            }
        }
        finally
        {
            SeenPlants.Clear();
            SeenPickables.Clear();
            PollinationCandidates.Clear();
        }
    }

    // Plant and foraging growth multipliers.
    private static float GetStructuralPlantGrowthMultiplierForTarget(Plant plant)
    {
        if (!GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            plant == null)
        {
            return 1f;
        }

        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        Beehive? hive = FindAssignedHive(plant, radius);
        if (hive == null || maxCount <= 0)
        {
            return 1f;
        }

        PollinationCache? cache = GetPollinationCache(hive, maxCount, radius);
        return cache != null && cache.Plants.Contains(plant)
            ? GetHivePlantGrowthMultiplier(hive)
            : 1f;
    }

    private static float GetStructuralForagingRespawnMultiplierForTarget(Pickable pickable)
    {
        if (!GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            pickable == null ||
            !IsGrowingTarget(pickable))
        {
            return 1f;
        }

        float radius = GroundworkToolsDomain.BeehivePollinationRadius;
        int maxCount = GroundworkToolsDomain.BeehivePollinationMaxPlants;
        Beehive? hive = FindAssignedHive(pickable, radius);
        if (hive == null || maxCount <= 0)
        {
            return 1f;
        }

        PollinationCache? cache = GetPollinationCache(hive, maxCount, radius);
        return cache != null && cache.Pickables.Contains(pickable)
            ? GetHiveForagingRespawnMultiplier(hive)
            : 1f;
    }

    private static bool IsAssignedToHive(
        Component target,
        Beehive candidate,
        float radius)
    {
        return FindAssignedHive(target, radius) == candidate;
    }

    private static void AddPollinationCandidate(Plant? plant, Pickable? pickable, Vector3 targetPosition, Vector3 hivePosition, int instanceId)
    {
        PollinationCandidates.Add(new PollinationTargetCandidate(
            plant,
            pickable,
            GetHorizontalSqrDistance(targetPosition, hivePosition),
            GetHeightDistance(targetPosition, hivePosition),
            instanceId));
    }

    private static Beehive? FindAssignedHive(
        Component target,
        float radius)
    {
        if (target == null || radius <= 0f)
        {
            return null;
        }

        float now = Time.realtimeSinceStartup;
        PrunePollinationCaches(now);
        Vector3 targetPosition = target.transform.position;
        if (!AssignmentCaches.TryGetValue(target, out AssignmentCache? cache))
        {
            cache = new AssignmentCache();
            AssignmentCaches[target] = cache;
        }

        if (cache.IsFresh(now, radius, targetPosition))
        {
            if (cache.AssignedHive == null ||
                CanHivePollinate(cache.AssignedHive, respectCurrentEnvironment: false))
            {
                return cache.AssignedHive;
            }
        }

        Beehive? assignedHive = FindAssignedHiveUncached(targetPosition, radius);
        cache.Set(assignedHive, now, radius, targetPosition);
        return assignedHive;
    }

    private static Beehive? FindAssignedHiveUncached(
        Vector3 targetPosition,
        float radius)
    {
        if (radius <= 0f)
        {
            return null;
        }

        int hitCount = OverlapSphereWithExpandableBuffer(
            targetPosition,
            radius,
            ref AssignmentHits,
            ref _reportedAssignmentSearchSaturation,
            "target assignment");

        Beehive? bestHive = null;
        float bestHorizontalDistance = float.MaxValue;
        float bestHeightDistance = float.MaxValue;
        int bestInstanceId = int.MaxValue;
        SeenHives.Clear();
        try
        {
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = AssignmentHits[i];
                if (hit == null)
                {
                    continue;
                }

                Beehive? hive = hit.GetComponentInParent<Beehive>();
                if (hive == null ||
                    !SeenHives.Add(hive) ||
                    !CanHivePollinate(hive, respectCurrentEnvironment: false))
                {
                    continue;
                }

                float horizontalDistance = GetHorizontalSqrDistance(hive.transform.position, targetPosition);
                float heightDistance = GetHeightDistance(hive.transform.position, targetPosition);
                int instanceId = hive.GetInstanceID();
                if (ComparePollinationDistance(
                        horizontalDistance,
                        heightDistance,
                        instanceId,
                        bestHorizontalDistance,
                        bestHeightDistance,
                        bestInstanceId) < 0)
                {
                    bestHorizontalDistance = horizontalDistance;
                    bestHeightDistance = heightDistance;
                    bestHive = hive;
                    bestInstanceId = instanceId;
                }
            }
        }
        finally
        {
            SeenHives.Clear();
        }

        return bestHive;
    }

    private static int ComparePollinationDistance(
        float leftHorizontalDistance,
        float leftHeightDistance,
        int leftInstanceId,
        float rightHorizontalDistance,
        float rightHeightDistance,
        int rightInstanceId)
    {
        int horizontalComparison = leftHorizontalDistance.CompareTo(rightHorizontalDistance);
        if (horizontalComparison != 0)
        {
            return horizontalComparison;
        }

        int heightComparison = leftHeightDistance.CompareTo(rightHeightDistance);
        if (heightComparison != 0)
        {
            return heightComparison;
        }

        return leftInstanceId.CompareTo(rightInstanceId);
    }

    private static float GetHorizontalSqrDistance(Vector3 left, Vector3 right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return x * x + z * z;
    }

    private static float GetHeightDistance(Vector3 left, Vector3 right)
    {
        return Mathf.Abs(left.y - right.y);
    }

    private static void PrunePollinationCaches(float now)
    {
        if (now < _nextPollinationCachePruneAt)
        {
            return;
        }

        _nextPollinationCachePruneAt = now + PollinationCachePruneIntervalSeconds;
        StalePollinationCacheHives.Clear();
        foreach (KeyValuePair<Beehive, PollinationCache> entry in PollinationCaches)
        {
            Beehive hive = entry.Key;
            PollinationCache cache = entry.Value;
            if (hive == null ||
                !IsValid(hive) ||
                now - cache.RefreshedAt > PollinationCacheMaxIdleSeconds)
            {
                StalePollinationCacheHives.Add(hive!);
            }
        }

        foreach (Beehive hive in StalePollinationCacheHives)
        {
            PollinationCaches.Remove(hive);
        }

        StalePollinationCacheHives.Clear();

        StaleAssignmentCacheTargets.Clear();
        foreach (KeyValuePair<Component, AssignmentCache> entry in AssignmentCaches)
        {
            Component target = entry.Key;
            AssignmentCache cache = entry.Value;
            if (target == null || now - cache.RefreshedAt > PollinationCacheMaxIdleSeconds)
            {
                StaleAssignmentCacheTargets.Add(target!);
            }
        }

        foreach (Component target in StaleAssignmentCacheTargets)
        {
            AssignmentCaches.Remove(target);
        }

        StaleAssignmentCacheTargets.Clear();

        StaleLoadedSinceTargets.Clear();
        foreach (KeyValuePair<Component, long> entry in LoadedSinceTicksByTarget)
        {
            Component target = entry.Key;
            if (target == null)
            {
                StaleLoadedSinceTargets.Add(target!);
            }
        }

        foreach (Component target in StaleLoadedSinceTargets)
        {
            LoadedSinceTicksByTarget.Remove(target);
        }

        StaleLoadedSinceTargets.Clear();
    }

    // Unloaded catch-up helpers.
    private static float ApplyUnloadedCatchupEffectiveness(float multiplier)
    {
        return 1f + (Mathf.Max(1f, multiplier) - 1f) * UnloadedCatchupEffectiveness;
    }

    private static float GetHivePlantGrowthMultiplier(Beehive beehive)
    {
        return GetHiveEmptyScaledMultiplier(beehive, GroundworkToolsDomain.BeehivePollinationPlantGrowSpeedFactor);
    }

    private static float GetHiveForagingRespawnMultiplier(Beehive beehive)
    {
        return GetHiveEmptyScaledMultiplier(beehive, GroundworkToolsDomain.BeehivePollinationForagingRespawnSpeedFactor);
    }

    private static float GetLoadedPollinationMultiplier(float structuralMultiplier)
    {
        return IsLoadedPollinationEnvironmentActive()
            ? Mathf.Max(1f, structuralMultiplier)
            : 1f;
    }

    private static bool IsLoadedPollinationEnvironmentActive()
    {
        return !IsNight() && !EnvironmentEffectSystem.IsWetEnvironment();
    }

    private static float GetHiveEmptyScaledMultiplier(Beehive beehive, float maxMultiplier)
    {
        if (!CanHivePollinate(beehive, respectCurrentEnvironment: false))
        {
            return 1f;
        }

        int maxHoney = GetEffectiveMaxHoney(beehive);
        float emptyFactor = maxHoney > 0
            ? 1f - Mathf.Clamp01((float)GetHoneyLevel(beehive) / maxHoney)
            : 0f;
        return Mathf.Lerp(1f, Mathf.Max(1f, maxMultiplier), emptyFactor);
    }

    private static bool CanHivePollinate(Beehive beehive, bool respectCurrentEnvironment)
    {
        if (!GroundworkToolsDomain.BeehivePollinationFeatureEnabled ||
            beehive == null ||
            !IsValid(beehive) ||
            (respectCurrentEnvironment &&
             (IsNight() || EnvironmentEffectSystem.IsWetEnvironment())) ||
            GetHoneyLevel(beehive) >= GetEffectiveMaxHoney(beehive) ||
            !IsBiomeAllowed(beehive))
        {
            return false;
        }

        return !TryGetCoverPercentage(beehive, out float coverPercentage) || HasFreeSpace(beehive, coverPercentage);
    }

    private static bool IsNight()
    {
        return EnvMan.instance != null && !EnvMan.IsDaylight();
    }

    private static bool IsGrowingTarget(Plant plant)
    {
        return plant != null && plant.GetStatus() == Plant.Status.Healthy;
    }

    private static bool IsGrowingTarget(Pickable pickable)
    {
        return pickable != null &&
               !pickable.CanBePicked() &&
               FarmingSkillSystem.IsForagingTarget(pickable);
    }

    private static float GetCoverProductionMultiplier(Beehive beehive, float coverPercentage)
    {
        if (beehive == null || beehive.m_maxCover <= 0f || coverPercentage >= beehive.m_maxCover)
        {
            return 1f;
        }

        float openness = 1f - Mathf.Clamp01(coverPercentage / Mathf.Max(0.0001f, beehive.m_maxCover));
        return Mathf.Lerp(1f, GroundworkToolsDomain.BeehiveCoverMaxSpeedMultiplier, openness);
    }

    private static float GetNightProductionMultiplier(bool unloadedCatchup)
    {
        float nightRate = GroundworkToolsDomain.BeehiveNightHoneyRate;
        if (nightRate >= 0.999f)
        {
            return 1f;
        }

        if (unloadedCatchup)
        {
            return UnloadedCatchupDaylightShare + (1f - UnloadedCatchupDaylightShare) * nightRate;
        }

        return EnvMan.instance != null && !EnvMan.IsDaylight()
            ? nightRate
            : 1f;
    }

    private static void AppendCurrentHoneyRateLine(
        ref string hoverText,
        float nightMultiplier,
        string nightToken,
        string nightFallback,
        float rainMultiplier,
        string rainToken,
        string rainFallback)
    {
        List<string> parts = [];
        if (nightMultiplier < 0.999f)
        {
            parts.Add(GroundworkLocalization.Format(nightToken, nightFallback, FormatMultiplier(nightMultiplier)));
        }

        if (rainMultiplier < 0.999f)
        {
            parts.Add(GroundworkLocalization.Format(rainToken, rainFallback, FormatMultiplier(rainMultiplier)));
        }

        if (parts.Count == 0)
        {
            return;
        }

        AppendLine(ref hoverText, Colorize(string.Join("  ", parts)));
    }

    private static void ReplaceHoverHeader(Beehive beehive, ref string hoverText, int honeyLevel, int maxHoney, int farmingCapacityBonus)
    {
        if (string.IsNullOrEmpty(hoverText))
        {
            return;
        }

        string beehiveName = LocalizeVanillaText(beehive.m_name, "Beehive");
        string honeyName = LocalizeVanillaText(
            beehive.m_honeyItem?.m_itemData.m_shared.m_name,
            "Honey");
        string header = farmingCapacityBonus > 0
            ? GroundworkLocalization.Format(
                "groundwork_beehive_header_farming",
                "{0} ({1} {2}/{3}, Max +{4})",
                beehiveName,
                honeyName,
                honeyLevel,
                maxHoney,
                farmingCapacityBonus)
            : GroundworkLocalization.Format(
                "groundwork_beehive_header",
                "{0} ({1} {2}/{3})",
                beehiveName,
                honeyName,
                honeyLevel,
                maxHoney);

        int lineBreakIndex = hoverText.IndexOf('\n');
        hoverText = lineBreakIndex >= 0
            ? header + hoverText[lineBreakIndex..]
            : header;
    }

    private static string LocalizeVanillaText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string raw = value!;
        string? localizedValue = Localization.instance?.Localize(raw);
        if (string.IsNullOrWhiteSpace(localizedValue))
        {
            return raw.StartsWith("$", StringComparison.Ordinal) ? fallback : raw;
        }

        string localized = localizedValue!;
        return raw.StartsWith("$", StringComparison.Ordinal) && string.Equals(localized, raw, StringComparison.Ordinal)
            ? fallback
            : localized;
    }

    private static bool HasFreeSpace(Beehive beehive, float coverPercentage)
    {
        return beehive.m_maxCover <= 0f || coverPercentage < beehive.m_maxCover;
    }

    private static bool IsBiomeAllowed(Beehive beehive)
    {
        return (Heightmap.FindBiome(beehive.transform.position) & beehive.m_biome) != 0;
    }

    private static bool TryGetCoverPercentage(Beehive beehive, out float coverPercentage)
    {
        coverPercentage = 0f;
        if (beehive == null || beehive.m_coverPoint == null)
        {
            return false;
        }

        Cover.GetCoverForPoint(beehive.m_coverPoint.position, out coverPercentage, out _);
        coverPercentage = Mathf.Clamp01(coverPercentage);
        return true;
    }

    private static float GetSecondsSinceLastUpdate(ZDO zdo)
    {
        long ticks = zdo.GetLong(ZDOVars.s_lastTime, 0L);
        if (ticks <= 0L || ZNet.instance == null)
        {
            return 0f;
        }

        double seconds = (ZNet.instance.GetTime() - new DateTime(ticks)).TotalSeconds;
        return (float)Math.Max(0.0, seconds);
    }

    private static float GetTendedFarmingLevel(Beehive beehive)
    {
        return Mathf.Clamp(GetZdo(beehive)?.GetFloat(TendedFarmingLevelKey, 0f) ?? 0f, 0f, 100f);
    }

    private static int GetFarmingCapacityBonusHoney(Beehive beehive)
    {
        int levelsPerBonusHoney = GroundworkToolsDomain.BeehiveCapacityFarmingLevelsPerBonusHoney;
        if (levelsPerBonusHoney <= 0)
        {
            return 0;
        }

        return Mathf.Max(0, Mathf.FloorToInt(GetTendedFarmingLevel(beehive) / levelsPerBonusHoney));
    }

    private static float ResolveSenderFarmingLevel(long sender)
    {
        Player? senderPlayer = ResolveSenderPlayer(sender);
        if (senderPlayer != null)
        {
            return Mathf.Clamp(senderPlayer.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f);
        }

        Player? localPlayer = Player.m_localPlayer;
        return localPlayer != null ? Mathf.Clamp(localPlayer.GetSkillLevel(Skills.SkillType.Farming), 0f, 100f) : 0f;
    }

    private static Player? ResolveSenderPlayer(long sender)
    {
        foreach (Player player in Player.GetAllPlayers())
        {
            ZNetView? nview = ((Character)player).m_nview;
            ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo != null && zdo.m_uid.UserID == sender)
            {
                return player;
            }
        }

        return null;
    }

    private static int GetHoneyLevel(Beehive beehive)
    {
        return GetZdo(beehive)?.GetInt(ZDOVars.s_level) ?? 0;
    }

    private static ZDO? GetZdo(Beehive beehive)
    {
        if (beehive == null || beehive.m_nview == null || !beehive.m_nview.IsValid())
        {
            return null;
        }

        return beehive.m_nview.GetZDO();
    }

    private static bool IsValid(Beehive beehive)
    {
        return beehive.m_nview != null && beehive.m_nview.IsValid();
    }

    private static int GetPollinationMask()
    {
        if (_pollinationMask == 0)
        {
            _pollinationMask = LayerMask.GetMask("item", "Default", "Default_small", "piece", "piece_nonsolid");
        }

        return _pollinationMask;
    }

    internal static bool IsPollinationSearchLayer(int layer)
    {
        return (GetPollinationMask() & (1 << layer)) != 0;
    }

    private static int OverlapSphereWithExpandableBuffer(
        Vector3 center,
        float radius,
        ref Collider[] buffer,
        ref bool reported,
        string searchName)
    {
        while (true)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                center,
                radius,
                buffer,
                GetPollinationMask(),
                QueryTriggerInteraction.UseGlobal);
            if (hitCount < buffer.Length)
            {
                return hitCount;
            }

            if (buffer.Length >= PollinationSearchMaxBufferSize)
            {
                if (!reported)
                {
                    reported = true;
                    GroundworkPlugin.ModLogger.LogWarning(
                        $"Pollination {searchName} search reached the {buffer.Length}-collider maximum buffer capacity. " +
                        "Results may be truncated in this unusually dense area; reduce the pollination radius or object density.");
                }

                return hitCount;
            }

            Array.Resize(ref buffer, Math.Min(buffer.Length * 2, PollinationSearchMaxBufferSize));
        }
    }

    private static void AppendLine(ref string text, string line)
    {
        text = string.IsNullOrEmpty(text) ? line : text + "\n" + line;
    }

    private static string Colorize(string text)
    {
        return "<color=#a8e6a1>" + text + "</color>";
    }

    private static string FormatPercent(float value)
    {
        return Mathf.RoundToInt(Mathf.Clamp01(value) * 100f).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatMultiplier(float value)
    {
        return "x" + value.ToString("0.#", CultureInfo.InvariantCulture);
    }

}

// Harmony patches.
[HarmonyPatch(typeof(Player), "OnDestroy")]
internal static class PlayerOnDestroyPollinationPreviewPatch
{
    private static void Prefix(Player __instance)
    {
        if (__instance == Player.m_localPlayer)
        {
            BeehivePollinationSystem.DestroyHoverPreview();
        }
    }
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.GetHoverText))]
internal static class BeehiveGetHoverTextPollinationPatch
{
    private static void Postfix(Beehive __instance, ref string __result)
    {
        BeehivePollinationSystem.AppendHoverText(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.Awake))]
internal static class BeehiveAwakePollinationPatch
{
    private static void Postfix(Beehive __instance)
    {
        BeehivePollinationSystem.TryStoreBuilderFarmingLevel(__instance);
    }
}

[HarmonyPatch(typeof(Beehive), "UpdateBees")]
internal static class BeehiveUpdateBeesPollinationPatch
{
    private static bool Prefix(Beehive __instance, ref float __state)
    {
        __state = 0f;
        bool unloadedCatchup = BeehivePollinationSystem.ShouldUseUnloadedProductionCatchup(__instance);
        if (EnvironmentEffectSystem.TryPauseBeehiveProduction(__instance, unloadedCatchup))
        {
            return false;
        }

        __state = __instance.m_secPerUnit;
        float multiplier = BeehivePollinationSystem.GetProductionSpeedMultiplier(
            __instance,
            unloadedCatchup,
            out bool canProcessProduction);
        if (multiplier <= 0.001f)
        {
            EnvironmentEffectSystem.PauseBeehiveProduction(__instance);
            return false;
        }

        float productionThreshold = BeehivePollinationSystem.PrepareProductionThreshold(
            __instance,
            multiplier,
            canProcessProduction);
        if (productionThreshold > 0f)
        {
            __instance.m_secPerUnit = productionThreshold;
        }

        return true;
    }

    private static void Finalizer(Beehive __instance, float __state)
    {
        if (__state > 0f)
        {
            __instance.m_secPerUnit = __state;
        }
    }
}

[HarmonyPatch(typeof(Beehive), "IncreseLevel")]
internal static class BeehiveIncreaseLevelPollinationPatch
{
    private static void Prefix(Beehive __instance, ref int __state)
    {
        __state = __instance.m_maxHoney;
        __instance.m_maxHoney = BeehivePollinationSystem.GetEffectiveMaxHoney(__instance);
    }

    private static void Finalizer(Beehive __instance, int __state)
    {
        if (__state > 0)
        {
            __instance.m_maxHoney = __state;
        }
    }
}

[HarmonyPatch(typeof(Beehive), "RPC_Extract")]
internal static class BeehiveRpcExtractPollinationPatch
{
    private static void Prefix(Beehive __instance, ref int __state)
    {
        ZDO? zdo = __instance.m_nview != null && __instance.m_nview.IsValid()
            ? __instance.m_nview.GetZDO()
            : null;
        __state = Mathf.Max(0, zdo?.GetInt(ZDOVars.s_level) ?? 0);
    }

    private static void Postfix(Beehive __instance, long caller, int __state)
    {
        if (__state > 0)
        {
            BeehivePollinationSystem.StoreTendedFarmingLevel(__instance, caller);
            BeehivePollinationSystem.RaiseFarmingSkillForHarvest(caller, __state);
        }
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
internal static class PickableAwakePollinationPatch
{
    private static void Postfix(Pickable __instance)
    {
        BeehivePollinationSystem.TrackLoadedTarget(__instance);
    }
}
