using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Groundwork;

internal static class PickedVisualSystem
{
    internal const string FiddleheadPrefabName = "Pickable_Fiddlehead";
    private const string VisualName = "Groundwork_PickedVisual";
    private const string PlantedFernVisualName = "Groundwork_PlantedFiddleheadFern";
    private const float RetainedHeightFraction = 0.30f;
    private static readonly Dictionary<int, CachedVisual?> Cache = new();
    private static readonly HashSet<string> WarnedPrefabs = new(StringComparer.Ordinal);
    private static readonly HashSet<int> UnsupportedFernSources = new();

    // Fixed visual policy, independent of cultivation recipes and Farming eligibility.
    // Unknown meshes are not cut automatically; their normal hover handling remains available.
    internal static bool TryGetPreset(string prefabName, out bool keepTop)
    {
        keepTop = string.Equals(prefabName, "Pickable_RoyalJelly", StringComparison.OrdinalIgnoreCase);
        return keepTop ||
               string.Equals(prefabName, "Pickable_Mushroom", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefabName, "Pickable_Mushroom_yellow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefabName, "Pickable_Dandelion", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefabName, "Pickable_Thistle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefabName, "Pickable_SmokePuff", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefabName, FiddleheadPrefabName, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Refresh(Pickable pickable)
    {
        if (pickable == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return;
        }

        Transform existing = pickable.transform.Find(VisualName);
        bool validInstance = pickable.gameObject.scene.IsValid() && pickable.gameObject.activeInHierarchy &&
                             pickable.GetEnabled == 1 && pickable.m_nview != null && pickable.m_nview.IsValid();
        bool hasPlantedFern = RefreshPlantedFern(pickable, validInstance);
        string prefabName = Utils.GetPrefabName(pickable.gameObject);
        if (!validInstance || hasPlantedFern || !pickable.GetPicked() ||
            !TryGetPreset(prefabName, out bool keepTop) ||
            !FarmingSkillSystem.TryGetPickableRespawnTiming(pickable, out _))
        {
            if (existing != null)
            {
                existing.gameObject.SetActive(false);
            }

            return;
        }

        if (existing != null)
        {
            existing.gameObject.SetActive(true);
            return;
        }

        GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
        Pickable? source = prefab != null ? prefab.GetComponent<Pickable>() : null;
        if (source == null)
        {
            return;
        }

        int key = source.gameObject.GetInstanceID();
        if (!Cache.TryGetValue(key, out CachedVisual? cached))
        {
            cached = Build(source, keepTop, prefabName);
            Cache.Add(key, cached);
        }

        if (cached == null)
        {
            return;
        }

        GameObject visual = new(VisualName);
        visual.SetActive(false);
        visual.transform.SetParent(pickable.transform, worldPositionStays: false);
        visual.AddComponent<GroundworkPickedVisual>();
        List<Renderer> renderers = new(cached.Parts.Count);
        foreach (VisualPart part in cached.Parts)
        {
            GameObject child = new(part.Mesh.name) { layer = part.Layer };
            child.transform.SetParent(visual.transform, worldPositionStays: false);
            child.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = part.Materials;
            renderer.shadowCastingMode = part.Shadows;
            renderer.receiveShadows = part.ReceiveShadows;
            renderer.lightProbeUsage = part.LightProbes;
            renderer.reflectionProbeUsage = part.ReflectionProbes;
            renderers.Add(renderer);
        }

        if (cached.CullHeight > 0f)
        {
            LODGroup lod = visual.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(cached.CullHeight, renderers.ToArray()) });
            lod.RecalculateBounds();
        }

        visual.SetActive(true);
    }

    private static bool RefreshPlantedFern(Pickable pickable, bool validInstance)
    {
        Transform existing = pickable.transform.Find(PlantedFernVisualName);
        if (!validInstance ||
            !string.Equals(Utils.GetPrefabName(pickable.gameObject), FiddleheadPrefabName, StringComparison.Ordinal) ||
            !CultivationSystem.IsPlantedPickable(pickable))
        {
            if (existing != null)
            {
                existing.gameObject.SetActive(false);
            }

            return false;
        }

        if (existing != null)
        {
            existing.gameObject.SetActive(true);
            return true;
        }

        GameObject? source = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("FernAshlands") : null;
        if (source == null || UnsupportedFernSources.Contains(source.GetInstanceID()))
        {
            return false;
        }

        GameObject? fern = null;
        try
        {
            // Build only rendering components. The source fern's destructible, viewblock,
            // colliders, ZNetView and other gameplay components must never be instantiated.
            fern = new GameObject(PlantedFernVisualName) { layer = source.layer };
            fern.SetActive(false);
            fern.transform.SetParent(pickable.transform, worldPositionStays: false);
            fern.transform.localRotation = source.transform.localRotation;
            fern.transform.localScale = source.transform.localScale;
            fern.AddComponent<GroundworkPickedVisual>();
            Dictionary<Transform, Transform> transforms = new() { [source.transform] = fern.transform };
            Dictionary<Renderer, Renderer> renderers = new();
            foreach (MeshRenderer original in source.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
            {
                MeshFilter filter = original.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    throw new NotSupportedException("a renderer has no static mesh");
                }

                Transform target = CopyFernTransform(original.transform, transforms);
                target.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                MeshRenderer renderer = target.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = original.sharedMaterials;
                renderer.shadowCastingMode = original.shadowCastingMode;
                renderer.receiveShadows = original.receiveShadows;
                renderer.lightProbeUsage = original.lightProbeUsage;
                renderer.reflectionProbeUsage = original.reflectionProbeUsage;
                renderer.motionVectorGenerationMode = original.motionVectorGenerationMode;
                renderer.allowOcclusionWhenDynamic = original.allowOcclusionWhenDynamic;
                renderer.renderingLayerMask = original.renderingLayerMask;
                renderer.sortingLayerID = original.sortingLayerID;
                renderer.sortingOrder = original.sortingOrder;
                renderer.enabled = original.enabled;
                renderers.Add(original, renderer);
            }

            if (renderers.Count == 0 || source.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true).Length > 0)
            {
                throw new NotSupportedException("the source is not a static fern model");
            }

            foreach (LODGroup original in source.GetComponentsInChildren<LODGroup>(includeInactive: true))
            {
                LOD[] sourceLods = original.GetLODs();
                LOD[] targetLods = new LOD[sourceLods.Length];
                bool hasMappedRenderer = false;
                for (int i = 0; i < sourceLods.Length; i++)
                {
                    List<Renderer> lodRenderers = new();
                    foreach (Renderer originalRenderer in sourceLods[i].renderers)
                    {
                        if (originalRenderer == null)
                        {
                            continue;
                        }

                        if (!renderers.TryGetValue(originalRenderer, out Renderer targetRenderer))
                        {
                            throw new NotSupportedException("an LOD uses an unsupported renderer");
                        }

                        lodRenderers.Add(targetRenderer);
                        hasMappedRenderer = true;
                    }

                    targetLods[i] = new LOD(sourceLods[i].screenRelativeTransitionHeight, lodRenderers.ToArray())
                    {
                        fadeTransitionWidth = sourceLods[i].fadeTransitionWidth
                    };
                }

                if (!hasMappedRenderer)
                {
                    continue;
                }

                LODGroup lod = CopyFernTransform(original.transform, transforms).gameObject.AddComponent<LODGroup>();
                lod.enabled = false;
                lod.SetLODs(targetLods);
                lod.fadeMode = original.fadeMode;
                lod.animateCrossFading = original.animateCrossFading;
                lod.localReferencePoint = original.localReferencePoint;
                lod.size = original.size;
                lod.enabled = original.enabled;
            }

            fern.SetActive(true);
            return true;
        }
        catch (Exception exception)
        {
            if (fern != null)
            {
                fern.SetActive(false);
                fern.transform.SetParent(null);
                Object.Destroy(fern);
            }

            UnsupportedFernSources.Add(source.GetInstanceID());
            if (WarnedPrefabs.Add("FernAshlands"))
            {
                GroundworkPlugin.ModLogger.LogWarning(
                    $"Cannot create planted Fiddlehead fern: {exception.Message}. The picked visual remains available.");
            }

            return false;
        }
    }

    private static Transform CopyFernTransform(Transform source, Dictionary<Transform, Transform> transforms)
    {
        if (transforms.TryGetValue(source, out Transform target))
        {
            return target;
        }

        Transform parent = CopyFernTransform(source.parent, transforms);
        GameObject child = new(source.name) { layer = source.gameObject.layer };
        child.SetActive(source.gameObject.activeSelf);
        target = child.transform;
        target.SetParent(parent, worldPositionStays: false);
        target.localPosition = source.localPosition;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
        transforms.Add(source, target);
        return target;
    }

    internal static void InvalidateCache()
    {
        // Detach renderers before releasing the shared meshes they reference.
        foreach (GroundworkPickedVisual visual in Resources.FindObjectsOfTypeAll<GroundworkPickedVisual>())
        {
            if (visual != null)
            {
                visual.gameObject.SetActive(false);
                visual.transform.SetParent(null);
                Object.Destroy(visual.gameObject);
            }
        }

        foreach (CachedVisual? cached in Cache.Values)
        {
            if (cached != null)
            {
                foreach (VisualPart part in cached.Parts)
                {
                    Object.Destroy(part.Mesh);
                }
            }
        }

        Cache.Clear();
        UnsupportedFernSources.Clear();
    }

    internal static void Shutdown()
    {
        InvalidateCache();
        WarnedPrefabs.Clear();
    }

    private static CachedVisual? Build(Pickable source, bool keepTop, string prefabName)
    {
        List<VisualPart> parts = new();
        try
        {
            if (source.m_hideWhenPicked == null || source.m_hideWhenPicked == source.gameObject)
            {
                return null;
            }

            // A bush that keeps its stem does not need an additional harvested model.
            Transform hidden = source.m_hideWhenPicked.transform;
            foreach (Renderer renderer in source.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                {
                    continue;
                }

                if (renderer.enabled && IsActiveBelow(renderer.transform, source.transform) &&
                    renderer.transform != hidden && !renderer.transform.IsChildOf(hidden))
                {
                    return null;
                }
            }

            // Only the highest-detail level is copied; otherwise overlapping LOD meshes
            // would all remain visible at the same time in the generated model.
            HashSet<Renderer> lowerLods = new();
            HashSet<Renderer> firstLods = new();
            float cullHeight = float.PositiveInfinity;
            foreach (LODGroup group in source.GetComponentsInChildren<LODGroup>(includeInactive: true))
            {
                LOD[] lods = group.GetLODs();
                if (group.enabled && lods.Length > 0)
                {
                    cullHeight = Mathf.Min(cullHeight, lods[lods.Length - 1].screenRelativeTransitionHeight);
                }

                for (int i = 0; i < lods.Length; i++)
                {
                    foreach (Renderer renderer in lods[i].renderers)
                    {
                        (i == 0 ? firstLods : lowerLods).Add(renderer);
                    }
                }
            }

            lowerLods.ExceptWith(firstLods);
            List<SourcePart> sources = new();
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            foreach (Renderer renderer in hidden.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (!renderer.enabled || !IsActiveBelow(renderer.transform, source.transform) ||
                    lowerLods.Contains(renderer) || renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                {
                    continue;
                }

                if (renderer is not MeshRenderer staticRenderer)
                {
                    throw new NotSupportedException("the hidden model contains a skinned mesh");
                }

                MeshFilter filter = staticRenderer.GetComponent<MeshFilter>();
                Mesh? mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                if (!mesh.isReadable)
                {
                    throw new NotSupportedException("the source mesh is not readable");
                }

                SourcePart part = new(staticRenderer, mesh, source.transform);
                sources.Add(part);
                foreach (Vertex vertex in part.Vertices)
                {
                    minY = Mathf.Min(minY, vertex.Position.y);
                    maxY = Mathf.Max(maxY, vertex.Position.y);
                }
            }

            if (sources.Count == 0 || !(maxY - minY > 0.0001f))
            {
                return null;
            }

            float planeY = keepTop
                ? Mathf.Lerp(maxY, minY, RetainedHeightFraction)
                : Mathf.Lerp(minY, maxY, RetainedHeightFraction);
            float offsetY = keepTop ? minY - planeY : 0f;
            foreach (SourcePart sourcePart in sources)
            {
                Mesh? mesh = Slice(sourcePart, planeY, offsetY, keepTop, prefabName);
                if (mesh != null)
                {
                    parts.Add(new VisualPart(mesh, sourcePart.Renderer));
                }
            }

            return parts.Count > 0
                ? new CachedVisual(parts, float.IsPositiveInfinity(cullHeight) ? 0f : cullHeight)
                : null;
        }
        catch (Exception exception)
        {
            foreach (VisualPart part in parts)
            {
                Object.Destroy(part.Mesh);
            }

            if (WarnedPrefabs.Add(prefabName))
            {
                GroundworkPlugin.ModLogger.LogWarning(
                    $"Cannot create picked visual for '{prefabName}': {exception.Message}. Respawn and hover remain available.");
            }

            return null;
        }
    }

    private static bool IsActiveBelow(Transform child, Transform root)
    {
        while (child != null && child != root)
        {
            if (!child.gameObject.activeSelf)
            {
                return false;
            }

            child = child.parent;
        }

        return child == root;
    }

    private static Mesh? Slice(SourcePart source, float planeY, float offsetY, bool keepTop, string prefabName)
    {
        List<Vertex> vertices = new();
        List<int>[] triangles = new List<int>[source.Mesh.subMeshCount];
        Vertex[] input = new Vertex[3];
        Vertex[] clipped = new Vertex[4];
        List<Segment> cuts = new();
        for (int subMesh = 0; subMesh < triangles.Length; subMesh++)
        {
            if (source.Mesh.GetTopology(subMesh) != MeshTopology.Triangles)
            {
                throw new NotSupportedException("the source mesh contains non-triangle geometry");
            }

            List<int> indices = triangles[subMesh] = new List<int>();
            int[] original = source.Mesh.GetTriangles(subMesh);
            cuts.Clear();
            for (int i = 0; i < original.Length; i += 3)
            {
                input[0] = source.Vertices[original[i]];
                input[1] = source.Vertices[original[i + (source.Mirrored ? 2 : 1)]];
                input[2] = source.Vertices[original[i + (source.Mirrored ? 1 : 2)]];
                int count = ClipTriangle(input, clipped, planeY, keepTop, cuts);
                for (int triangle = 1; triangle + 1 < count; triangle++)
                {
                    AddTriangle(vertices, indices, clipped[0], clipped[triangle], clipped[triangle + 1]);
                }
            }

            AddSimpleCaps(vertices, indices, cuts, keepTop);
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        Mesh result = new() { name = prefabName + "_Picked", indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        try
        {
            Vector3[] positions = new Vector3[vertices.Count];
            Vector3[] normals = new Vector3[vertices.Count];
            Vector4[] tangents = new Vector4[vertices.Count];
            Color[] colors = new Color[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                Vertex vertex = vertices[i];
                positions[i] = vertex.Position + Vector3.up * offsetY;
                normals[i] = vertex.Normal;
                tangents[i] = vertex.Tangent;
                colors[i] = vertex.Color;
            }

            result.vertices = positions;
            result.normals = normals;
            if (source.HasTangents)
            {
                result.tangents = tangents;
            }

            if (source.HasColors)
            {
                result.colors = colors;
            }

            for (int channel = 0; channel < source.UvChannels.Count; channel++)
            {
                List<Vector4> uv = new(vertices.Count);
                foreach (Vertex vertex in vertices)
                {
                    uv.Add(vertex.Uvs[channel]);
                }

                result.SetUVs(source.UvChannels[channel], uv);
            }

            result.subMeshCount = triangles.Length;
            for (int i = 0; i < triangles.Length; i++)
            {
                result.SetTriangles(triangles[i], i, calculateBounds: false);
            }

            if (!source.HasNormals)
            {
                result.RecalculateNormals();
            }

            result.RecalculateBounds();
            result.UploadMeshData(markNoLongerReadable: true);
            return result;
        }
        catch
        {
            Object.Destroy(result);
            throw;
        }
    }

    private static int ClipTriangle(Vertex[] input, Vertex[] output, float planeY, bool keepTop, List<Segment> cuts)
    {
        int count = 0;
        Vertex? firstCut = null;
        Vertex? secondCut = null;
        Vertex previous = input[2];
        bool previousInside = keepTop ? previous.Position.y >= planeY : previous.Position.y <= planeY;
        for (int i = 0; i < 3; i++)
        {
            Vertex current = input[i];
            bool inside = keepTop ? current.Position.y >= planeY : current.Position.y <= planeY;
            if (inside != previousInside)
            {
                float fraction = (planeY - previous.Position.y) / (current.Position.y - previous.Position.y);
                Vertex intersection = Vertex.Lerp(previous, current, fraction);
                intersection.Position.y = planeY;
                output[count++] = intersection;
                if (!firstCut.HasValue)
                {
                    firstCut = intersection;
                }
                else
                {
                    secondCut = intersection;
                }
            }

            if (inside)
            {
                output[count++] = current;
            }

            previous = current;
            previousInside = inside;
        }

        if (firstCut.HasValue && secondCut.HasValue &&
            (firstCut.Value.Position - secondCut.Value.Position).sqrMagnitude > 1e-12f)
        {
            cuts.Add(new Segment(firstCut.Value, secondCut.Value));
        }

        return count;
    }

    private static void AddTriangle(List<Vertex> vertices, List<int> indices, Vertex a, Vertex b, Vertex c)
    {
        if (Vector3.Cross(b.Position - a.Position, c.Position - a.Position).sqrMagnitude <= 1e-16f)
        {
            return;
        }

        int start = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
    }

    private static void AddSimpleCaps(List<Vertex> vertices, List<int> indices, List<Segment> cuts, bool keepTop)
    {
        // Thin leaf cards have open boundaries. Only simple closed cut loops are
        // capped; nested loops (holes) and self-intersections retain their open cut.
        List<Vertex> nodes = new();
        List<HashSet<int>> neighbors = new();
        foreach (Segment segment in cuts)
        {
            int a = FindOrAddNode(nodes, neighbors, segment.A);
            int b = FindOrAddNode(nodes, neighbors, segment.B);
            if (a != b)
            {
                neighbors[a].Add(b);
                neighbors[b].Add(a);
            }
        }

        HashSet<int> visited = new();
        List<List<Vertex>> loops = new();
        for (int start = 0; start < nodes.Count; start++)
        {
            if (visited.Contains(start) || neighbors[start].Count != 2)
            {
                continue;
            }

            List<Vertex> loop = new();
            int previous = -1;
            int current = start;
            bool closed = false;
            while (neighbors[current].Count == 2 && visited.Add(current))
            {
                loop.Add(nodes[current]);
                int next = -1;
                foreach (int neighbor in neighbors[current])
                {
                    if (neighbor != previous)
                    {
                        next = neighbor;
                        break;
                    }
                }

                previous = current;
                current = next;
                if (current == start)
                {
                    closed = true;
                    break;
                }
            }

            if (closed && loop.Count >= 3)
            {
                loops.Add(loop);
            }
        }

        foreach (List<Vertex> loop in loops)
        {
            bool nested = false;
            foreach (List<Vertex> other in loops)
            {
                if (other != loop &&
                    (IsInsideLoop(loop[0].Position, other) || IsInsideLoop(other[0].Position, loop)))
                {
                    nested = true;
                    break;
                }
            }

            if (nested || !TryTriangulateCap(loop, keepTop, out List<int> cap))
            {
                continue;
            }

            Vector3 normal = keepTop ? Vector3.down : Vector3.up;
            Vector4 tangent = new(1f, 0f, 0f, keepTop ? 1f : -1f);
            for (int i = 0; i < cap.Count; i += 3)
            {
                Vertex a = loop[cap[i]];
                Vertex b = loop[cap[i + 1]];
                Vertex c = loop[cap[i + 2]];
                a.Normal = b.Normal = c.Normal = normal;
                a.Tangent = b.Tangent = c.Tangent = tangent;
                AddTriangle(vertices, indices, a, b, c);
            }
        }
    }

    private static int FindOrAddNode(List<Vertex> nodes, List<HashSet<int>> neighbors, Vertex vertex)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if ((nodes[i].Position - vertex.Position).sqrMagnitude <= 1e-10f)
            {
                return i;
            }
        }

        nodes.Add(vertex);
        neighbors.Add(new HashSet<int>());
        return nodes.Count - 1;
    }

    private static bool TryTriangulateCap(List<Vertex> loop, bool keepTop, out List<int> triangles)
    {
        triangles = new List<int>();
        float area = 0f;
        for (int i = 0; i < loop.Count; i++)
        {
            Vector3 a = loop[i].Position;
            Vector3 b = loop[(i + 1) % loop.Count].Position;
            area += a.x * b.z - a.z * b.x;
            for (int j = i + 2; j < loop.Count; j++)
            {
                if (i == 0 && j == loop.Count - 1)
                {
                    continue;
                }

                Vector3 c = loop[j].Position;
                Vector3 d = loop[(j + 1) % loop.Count].Position;
                if (CrossXZ(a, b, c) * CrossXZ(a, b, d) < -1e-20f &&
                    CrossXZ(c, d, a) * CrossXZ(c, d, b) < -1e-20f)
                {
                    return false;
                }
            }
        }

        if (Mathf.Abs(area) <= 1e-10f)
        {
            return false;
        }

        // Positive X/Z winding faces downward in Unity's X/Y/Z coordinates.
        if ((area > 0f) != keepTop)
        {
            loop.Reverse();
        }

        float orientation = keepTop ? 1f : -1f;
        List<int> remaining = new(loop.Count);
        for (int i = 0; i < loop.Count; i++)
        {
            remaining.Add(i);
        }

        while (remaining.Count > 3)
        {
            bool removed = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count];
                int b = remaining[i];
                int c = remaining[(i + 1) % remaining.Count];
                float cross = CrossXZ(loop[a].Position, loop[b].Position, loop[c].Position) * orientation;
                if (cross < -1e-10f)
                {
                    continue;
                }

                bool occupied = false;
                foreach (int test in remaining)
                {
                    if (test != a && test != b && test != c &&
                        CrossXZ(loop[a].Position, loop[b].Position, loop[test].Position) * orientation >= -1e-10f &&
                        CrossXZ(loop[b].Position, loop[c].Position, loop[test].Position) * orientation >= -1e-10f &&
                        CrossXZ(loop[c].Position, loop[a].Position, loop[test].Position) * orientation >= -1e-10f)
                    {
                        occupied = true;
                        break;
                    }
                }

                if (occupied && cross > 1e-10f)
                {
                    continue;
                }

                if (cross > 1e-10f)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }

                remaining.RemoveAt(i);
                removed = true;
                break;
            }

            if (!removed)
            {
                return false;
            }
        }

        triangles.AddRange(remaining);
        return true;
    }

    private static float CrossXZ(Vector3 a, Vector3 b, Vector3 c)
    {
        return (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
    }

    private static bool IsInsideLoop(Vector3 point, List<Vertex> loop)
    {
        bool inside = false;
        Vector3 previous = loop[loop.Count - 1].Position;
        foreach (Vertex vertex in loop)
        {
            Vector3 current = vertex.Position;
            if ((current.z > point.z) != (previous.z > point.z) &&
                point.x < (previous.x - current.x) * (point.z - current.z) /
                (previous.z - current.z) + current.x)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    private sealed class CachedVisual
    {
        internal readonly List<VisualPart> Parts;
        internal readonly float CullHeight;
        internal CachedVisual(List<VisualPart> parts, float cullHeight) { Parts = parts; CullHeight = cullHeight; }
    }

    private sealed class VisualPart
    {
        internal readonly Mesh Mesh;
        internal readonly Material[] Materials;
        internal readonly int Layer;
        internal readonly ShadowCastingMode Shadows;
        internal readonly bool ReceiveShadows;
        internal readonly LightProbeUsage LightProbes;
        internal readonly ReflectionProbeUsage ReflectionProbes;

        internal VisualPart(Mesh mesh, MeshRenderer renderer)
        {
            Mesh = mesh;
            Materials = renderer.sharedMaterials;
            Layer = renderer.gameObject.layer;
            Shadows = renderer.shadowCastingMode;
            ReceiveShadows = renderer.receiveShadows;
            LightProbes = renderer.lightProbeUsage;
            ReflectionProbes = renderer.reflectionProbeUsage;
        }
    }

    private sealed class SourcePart
    {
        internal readonly MeshRenderer Renderer;
        internal readonly Mesh Mesh;
        internal readonly Vertex[] Vertices;
        internal readonly List<int> UvChannels = new();
        internal readonly bool Mirrored;
        internal readonly bool HasNormals;
        internal readonly bool HasTangents;
        internal readonly bool HasColors;

        internal SourcePart(MeshRenderer renderer, Mesh mesh, Transform root)
        {
            Renderer = renderer;
            Mesh = mesh;
            Matrix4x4 transform = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Matrix4x4 normalTransform = transform.inverse.transpose;
            Mirrored = transform.determinant < 0f;
            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Color[] colors = mesh.colors;
            HasNormals = normals.Length == positions.Length;
            HasTangents = tangents.Length == positions.Length;
            HasColors = colors.Length == positions.Length;
            List<List<Vector4>> uvValues = new();
            for (int channel = 0; channel < 8; channel++)
            {
                List<Vector4> values = new();
                mesh.GetUVs(channel, values);
                if (values.Count == positions.Length)
                {
                    UvChannels.Add(channel);
                    uvValues.Add(values);
                }
            }

            Vertices = new Vertex[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 tangentDirection = HasTangents ? transform.MultiplyVector(tangents[i]).normalized : Vector3.right;
                Vertex vertex = new()
                {
                    Position = transform.MultiplyPoint3x4(positions[i]),
                    Normal = HasNormals ? normalTransform.MultiplyVector(normals[i]).normalized : Vector3.up,
                    Tangent = new Vector4(tangentDirection.x, tangentDirection.y, tangentDirection.z,
                        HasTangents ? tangents[i].w * (Mirrored ? -1f : 1f) : 1f),
                    Color = HasColors ? colors[i] : Color.white,
                    Uvs = new Vector4[UvChannels.Count]
                };
                for (int channel = 0; channel < UvChannels.Count; channel++)
                {
                    vertex.Uvs[channel] = uvValues[channel][i];
                }

                Vertices[i] = vertex;
            }
        }
    }

    private readonly struct Segment
    {
        internal readonly Vertex A;
        internal readonly Vertex B;
        internal Segment(Vertex a, Vertex b) { A = a; B = b; }
    }

    private struct Vertex
    {
        internal Vector3 Position;
        internal Vector3 Normal;
        internal Vector4 Tangent;
        internal Color Color;
        internal Vector4[] Uvs;

        internal static Vertex Lerp(Vertex a, Vertex b, float fraction)
        {
            Vertex result = new()
            {
                Position = Vector3.LerpUnclamped(a.Position, b.Position, fraction),
                Normal = Vector3.LerpUnclamped(a.Normal, b.Normal, fraction).normalized,
                Tangent = Vector4.LerpUnclamped(a.Tangent, b.Tangent, fraction),
                Color = Color.LerpUnclamped(a.Color, b.Color, fraction),
                Uvs = new Vector4[a.Uvs.Length]
            };
            Vector3 tangent = new Vector3(result.Tangent.x, result.Tangent.y, result.Tangent.z).normalized;
            result.Tangent = new Vector4(tangent.x, tangent.y, tangent.z, result.Tangent.w < 0f ? -1f : 1f);
            for (int i = 0; i < result.Uvs.Length; i++)
            {
                result.Uvs[i] = Vector4.LerpUnclamped(a.Uvs[i], b.Uvs[i], fraction);
            }

            return result;
        }
    }
}

internal sealed class GroundworkPickedVisual : MonoBehaviour
{
}
