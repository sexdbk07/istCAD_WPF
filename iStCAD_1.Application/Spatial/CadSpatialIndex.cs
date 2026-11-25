using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;
using System.Threading;
using System.Diagnostics;

namespace iStCAD_1.Application.Spatial;

public enum OsnapMode { Endpoint, Midpoint, Center, Quadrant, Intersection, Insertion, Parallel, Perpendicular }
public readonly record struct OsnapResult(Point2 Point, OsnapMode Mode);

/// <summary>Packed R-tree for entity bounds and a compact hash grid for object-snap candidates.</summary>
public sealed class CadSpatialIndex
{
    private const int NodeCapacity = 16;
    private const int SnapGridSize = 96;
    // OSNAP is prepared only when a drawing command starts. Keep its optional cache
    // bounded so a very large DWG cannot allocate millions of endpoint/midpoint records.
    private const int MaximumSnapCandidates = 180_000;
    private const int MaximumPolylineVerticesForSnap = 12_000;
    // Hover-time hard limits: OSNAP runs on the UI thread for every pointer move.
    private const int MaximumHoverSnapEntities = 48;
    private const int MaximumHoverSnapSegments = 64;
    private static readonly IReadOnlySet<OsnapMode> AllSnapModes = Enum.GetValues<OsnapMode>().ToHashSet();
    private readonly IReadOnlyList<CadEntity> _entities;
    private readonly Dictionary<int, Bounds2> _boundsByIndex;
    private readonly Dictionary<Guid, Bounds2> _boundsById;
    private readonly Dictionary<Guid, CadEntity> _entitiesById;
    private readonly Dictionary<Guid, int> _indexById;
    // Entities added after the R-tree was built (scanned linearly; bounded by WithChanges).
    private readonly List<int> _extraIndices = new();
    private volatile Dictionary<(int X, int Y), List<OsnapResult>> _snapCells = new();
    private int _snapBuildStarted;
    private int _snapCandidatesReady;
    private readonly Bounds2 _scene;
    private readonly RTreeNode? _root;

    public int IndexedEntityCount => _boundsByIndex.Count;
    public TimeSpan BoundsIndexDuration { get; private set; }
    public TimeSpan SnapCandidateBuildDuration { get; private set; }
    public bool SnapCandidatesReady => Volatile.Read(ref _snapCandidatesReady) != 0;
    public bool TryGetExtents(out (double MinX, double MinY, double MaxX, double MaxY) extents)
    {
        if (_root is not null)
        {
            extents = (_scene.MinX, _scene.MinY, _scene.MaxX, _scene.MaxY);
            return true;
        }
        extents = default;
        return false;
    }

    /// <summary>
    /// Extents of the main body of the drawing: the 0.5–99.5 percentile of object centers (padded, objects clipped to it).
    /// Returns false when the full extents are not dominated by far-away stray objects, so callers use <see cref="TryGetExtents"/>.
    /// </summary>
    public bool TryGetRobustExtents(out (double MinX, double MinY, double MaxX, double MaxY) extents, out int outliers)
    {
        extents = default; outliers = 0;
        var count = _boundsByIndex.Count;
        if (_root is null || count < 50) return false;
        var xs = new double[count]; var ys = new double[count];
        var n = 0;
        foreach (var b in _boundsByIndex.Values)
        {
            xs[n] = (b.MinX + b.MaxX) * 0.5; ys[n] = (b.MinY + b.MaxY) * 0.5; n++;
        }
        Array.Sort(xs); Array.Sort(ys);
        var cut = (int)(n * 0.005);
        double loX = xs[cut], hiX = xs[n - 1 - cut], loY = ys[cut], hiY = ys[n - 1 - cut];
        var padX = Math.Max((hiX - loX) * 0.1, 1e-6); var padY = Math.Max((hiY - loY) * 0.1, 1e-6);
        loX -= padX; hiX += padX; loY -= padY; hiY += padY;
        var fullW = _scene.MaxX - _scene.MinX; var fullH = _scene.MaxY - _scene.MinY;
        if (fullW <= 4 * (hiX - loX) && fullH <= 4 * (hiY - loY)) return false;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var b in _boundsByIndex.Values)
        {
            var cx = (b.MinX + b.MaxX) * 0.5; var cy = (b.MinY + b.MaxY) * 0.5;
            if (cx < loX || cx > hiX || cy < loY || cy > hiY) { outliers++; continue; }
            minX = Math.Min(minX, Math.Max(b.MinX, loX)); maxX = Math.Max(maxX, Math.Min(b.MaxX, hiX));
            minY = Math.Min(minY, Math.Max(b.MinY, loY)); maxY = Math.Max(maxY, Math.Min(b.MaxY, hiY));
        }
        if (!(maxX > minX) && !(maxY > minY)) return false;
        extents = (minX, minY, maxX, maxY);
        return outliers > 0;
    }

    public CadSpatialIndex(IReadOnlyList<CadEntity> entities, CancellationToken cancellationToken = default,
        IReadOnlySet<Guid>? excludedEntityIds = null, bool buildSnapCandidates = true)
    {
        var boundsTimer = Stopwatch.StartNew();
        _entities = entities.ToArray(); // Immutable membership while background viewport queries run.
        entities = _entities;
        // Pre-size every lookup table: growing 4 dictionaries to millions of entries rehashes them repeatedly.
        var capacity = entities.Count;
        _boundsByIndex = new Dictionary<int, Bounds2>(capacity);
        _boundsById = new Dictionary<Guid, Bounds2>(capacity);
        _entitiesById = new Dictionary<Guid, CadEntity>(capacity);
        _indexById = new Dictionary<Guid, int>(capacity);
        var entries = new List<SpatialEntry>(capacity);
        for (var i = 0; i < entities.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedEntityIds?.Contains(entities[i].Id) == true) continue;
            if (!TryGetBounds(entities[i], out var bounds) || !bounds.IsFinite) continue;
            entries.Add(new SpatialEntry(i, bounds));
            _boundsByIndex[i] = bounds;
            _boundsById[entities[i].Id] = bounds;
            _entitiesById[entities[i].Id] = entities[i];
            _indexById[entities[i].Id] = i;
        }
        if (entries.Count == 0)
        {
            boundsTimer.Stop();
            BoundsIndexDuration = boundsTimer.Elapsed;
            if (buildSnapCandidates) BuildSnapCandidates(cancellationToken);
            return;
        }

        _scene = Union(entries.Select(entry => entry.Bounds));
        _root = BuildTree(entries);
        boundsTimer.Stop();
        BoundsIndexDuration = boundsTimer.Elapsed;
        if (buildSnapCandidates) BuildSnapCandidates(cancellationToken);
    }

    private CadSpatialIndex(CadSpatialIndex source, IReadOnlyList<CadEntity> removed, IReadOnlyList<CadEntity> added)
    {
        var baseCount = source._entities.Count;
        var merged = new CadEntity[baseCount + added.Count];
        for (var i = 0; i < baseCount; i++) merged[i] = source._entities[i];
        for (var k = 0; k < added.Count; k++) merged[baseCount + k] = added[k];
        _entities = merged;
        _boundsByIndex = new Dictionary<int, Bounds2>(source._boundsByIndex);
        _boundsById = new Dictionary<Guid, Bounds2>(source._boundsById);
        _entitiesById = new Dictionary<Guid, CadEntity>(source._entitiesById);
        _indexById = new Dictionary<Guid, int>(source._indexById);
        _extraIndices = new List<int>(source._extraIndices);
        _root = source._root;
        var scene = source._scene;
        foreach (var entity in removed)
        {
            if (!_indexById.Remove(entity.Id, out var index)) continue;
            _boundsByIndex.Remove(index); _boundsById.Remove(entity.Id); _entitiesById.Remove(entity.Id); _extraIndices.Remove(index);
        }
        for (var k = 0; k < added.Count; k++)
        {
            var entity = added[k];
            if (!TryGetBounds(entity, out var bounds) || !bounds.IsFinite) continue;
            var index = baseCount + k;
            _boundsByIndex[index] = bounds; _boundsById[entity.Id] = bounds; _entitiesById[entity.Id] = entity;
            _indexById[entity.Id] = index; _extraIndices.Add(index);
            scene = Union(new[] { scene, bounds });
        }
        _scene = scene;
        // OSNAP candidates are rebuilt lazily (BuildSnapCandidates) when the next drawing command starts.
    }

    /// <summary>
    /// Incremental update: shares the R-tree and returns an index that excludes <paramref name="removed"/> and includes
    /// <paramref name="added"/>. Returns null when a full rebuild is cheaper (many edits or too many tombstones).
    /// </summary>
    public CadSpatialIndex? WithChanges(IReadOnlyList<CadEntity> removed, IReadOnlyList<CadEntity> added)
    {
        if (_root is null || removed.Count + added.Count > 20_000) return null;
        if (_extraIndices.Count + added.Count > 20_000) return null;
        if (_entities.Count - _boundsByIndex.Count > 20_000 + _boundsByIndex.Count / 2) return null;
        return new CadSpatialIndex(this, removed, added);
    }

    public void BuildSnapCandidates(CancellationToken cancellationToken = default)
    {
        if (SnapCandidatesReady || Interlocked.CompareExchange(ref _snapBuildStarted, 1, 0) != 0) return;
        var snapTimer = Stopwatch.StartNew();
        var cells = new Dictionary<(int X, int Y), List<OsnapResult>>();
        try
        {
            if (_root is not null)
            {
                var sceneWidth = Math.Max(_scene.Width, 1e-9);
                var sceneHeight = Math.Max(_scene.Height, 1e-9);
                var snapPointCount = 0;
                for (var i = 0; i < _entities.Count && snapPointCount < MaximumSnapCandidates; i++)
                {
                    if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (!_boundsByIndex.ContainsKey(i)) continue;
                    // Huge polylines can contain hundreds of thousands of vertices. Building
                    // every OSNAP endpoint and segment midpoint for them is disproportionately
                    // expensive; leave them out of the global cache instead of freezing input.
                    if (_entities[i] is PolylineEntity largePolyline && largePolyline.Vertices.Count > MaximumPolylineVerticesForSnap)
                        continue;
                    foreach (var snap in GetSnapPoints(_entities[i]))
                    {
                        if ((snapPointCount & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                        if (snapPointCount >= MaximumSnapCandidates) break;
                        Add(cells, Cell(snap.Point, sceneWidth, sceneHeight), snap);
                        snapPointCount++;
                    }
                }
            }
            snapTimer.Stop();
            _snapCells = cells;
            SnapCandidateBuildDuration = snapTimer.Elapsed;
            Volatile.Write(ref _snapCandidatesReady, 1);
        }
        catch
        {
            Interlocked.Exchange(ref _snapBuildStarted, 0);
            throw;
        }
    }

    public IEnumerable<CadEntity> Query(double minX, double minY, double maxX, double maxY, CancellationToken cancellationToken = default) => QueryCore(minX, minY, maxX, maxY, preserveEntityOrder: true, cancellationToken: cancellationToken);

    /// <summary>Queries the viewport without sorting matches back into drawing order.</summary>
    public IEnumerable<CadEntity> QueryUnordered(double minX, double minY, double maxX, double maxY) => QueryCore(minX, minY, maxX, maxY, preserveEntityOrder: false);

    /// <summary>
    /// Materializes a viewport query once and preserves drawing order unless a distant,
    /// very dense viewport exceeds the supplied threshold. This avoids a second R-tree
    /// traversal merely to decide whether level-of-detail rendering is needed.
    /// </summary>
    public CadEntity[] QueryAdaptive(double minX, double minY, double maxX, double maxY,
        bool allowUnorderedWhenDense, int orderedCandidateLimit, CancellationToken cancellationToken = default)
    {
        if (_root is null) return Array.Empty<CadEntity>();
        var query = new Bounds2(Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Max(minX, maxX), Math.Max(minY, maxY));
        var matches = new List<int>();
        var stack = new Stack<RTreeNode>();
        stack.Push(_root);
        var visited = 0;
        while (stack.Count > 0)
        {
            // Viewport refresh is frequent. A superseded query is discarded as a normal
            // control-flow outcome rather than throwing OperationCanceledException.
            if (cancellationToken.IsCancellationRequested) return Array.Empty<CadEntity>();
            var node = stack.Pop();
            if (!node.Bounds.Intersects(query)) continue;
            if (node.Indices is not null)
            {
                foreach (var index in node.Indices)
                {
                    if ((++visited & 511) == 0 && cancellationToken.IsCancellationRequested)
                        return Array.Empty<CadEntity>();
                    if (_boundsByIndex.TryGetValue(index, out var leafBounds) && leafBounds.Intersects(query)) matches.Add(index);
                }
            }
            else if (node.Children is not null)
            {
                foreach (var child in node.Children)
                {
                    if (cancellationToken.IsCancellationRequested) return Array.Empty<CadEntity>();
                    if (child.Bounds.Intersects(query)) stack.Push(child);
                }
            }
        }

        foreach (var extraIndex in _extraIndices)
            if (_boundsByIndex.TryGetValue(extraIndex, out var extraBounds) && extraBounds.Intersects(query)) matches.Add(extraIndex);
        if (cancellationToken.IsCancellationRequested) return Array.Empty<CadEntity>();
        if (!allowUnorderedWhenDense || matches.Count <= Math.Max(0, orderedCandidateLimit))
            matches.Sort();
        if (cancellationToken.IsCancellationRequested) return Array.Empty<CadEntity>();
        var result = new CadEntity[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            if ((i & 511) == 0 && cancellationToken.IsCancellationRequested) return Array.Empty<CadEntity>();
            result[i] = _entities[matches[i]];
        }
        return cancellationToken.IsCancellationRequested ? Array.Empty<CadEntity>() : result;
    }

    private IEnumerable<CadEntity> QueryCore(double minX, double minY, double maxX, double maxY, bool preserveEntityOrder, CancellationToken cancellationToken = default)
    {
        if (_root is null) yield break;
        var query = new Bounds2(Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Max(minX, maxX), Math.Max(minY, maxY));
        var stack = new Stack<RTreeNode>();
        var matches = new List<int>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = stack.Pop();
            if (!node.Bounds.Intersects(query)) continue;
            if (node.Indices is not null)
            {
                foreach (var index in node.Indices)
                    if (_boundsByIndex.TryGetValue(index, out var leafBounds) && leafBounds.Intersects(query))
                    {
                        if (preserveEntityOrder) matches.Add(index);
                        else yield return _entities[index];
                    }
            }
            else if (node.Children is not null)
            {
                foreach (var child in node.Children)
                    if (child.Bounds.Intersects(query)) stack.Push(child);
            }
        }
        foreach (var extraIndex in _extraIndices)
            if (_boundsByIndex.TryGetValue(extraIndex, out var extraBounds) && extraBounds.Intersects(query))
            {
                if (preserveEntityOrder) matches.Add(extraIndex);
                else yield return _entities[extraIndex];
            }
        if (preserveEntityOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            matches.Sort();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var index in matches) yield return _entities[index];
        }
    }

    public bool TryGetBounds(Guid entityId, out (double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        if (_boundsById.TryGetValue(entityId, out var value))
        {
            bounds = (value.MinX, value.MinY, value.MaxX, value.MaxY);
            return true;
        }
        bounds = default;
        return false;
    }

    public bool TryGetEntity(Guid id, out CadEntity entity) => _entitiesById.TryGetValue(id, out entity!);

    public bool IsContained(CadEntity entity, double minX, double minY, double maxX, double maxY) =>
        _boundsById.TryGetValue(entity.Id, out var bounds) && bounds.MinX >= minX && bounds.MaxX <= maxX && bounds.MinY >= minY && bounds.MaxY <= maxY;

    public bool TryFindSnap(Point2 point, double tolerance, out Point2 snapped)
    {
        if (TryFindSnap(point, tolerance, AllSnapModes, out var result)) { snapped = result.Point; return true; }
        snapped = point;
        return false;
    }

    public bool TryFindSnap(Point2 point, double tolerance, IReadOnlySet<OsnapMode> modes, out OsnapResult snapped)
    {
        snapped = default;
        if (!SnapCandidatesReady || !double.IsFinite(tolerance) || tolerance < 0 || modes.Count == 0) return false;
        var snapCells = _snapCells;
        var width = Math.Max(_scene.Width, 1e-9);
        var height = Math.Max(_scene.Height, 1e-9);
        var range = CellRange(new Bounds2(point.X - tolerance, point.Y - tolerance, point.X + tolerance, point.Y + tolerance), width, height);
        var bestDistanceSquared = tolerance * tolerance;
        var found = false;
        for (var x = range.MinX; x <= range.MaxX; x++)
            for (var y = range.MinY; y <= range.MaxY; y++)
                if (snapCells.TryGetValue((x, y), out var bucket))
                    foreach (var candidate in bucket)
                    {
                        if (!modes.Contains(candidate.Mode)) continue;
                        var dx = candidate.Point.X - point.X; var dy = candidate.Point.Y - point.Y;
                        var distanceSquared = dx * dx + dy * dy;
                        if (distanceSquared < bestDistanceSquared || (!found && distanceSquared <= bestDistanceSquared))
                        {
                            bestDistanceSquared = distanceSquared; snapped = candidate; found = true;
                        }
                    }
        if (modes.Contains(OsnapMode.Intersection))
            foreach (var candidate in FindIntersections(point, tolerance))
            {
                var dx = candidate.Point.X - point.X; var dy = candidate.Point.Y - point.Y;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared < bestDistanceSquared || (!found && distanceSquared <= bestDistanceSquared))
                {
                    bestDistanceSquared = distanceSquared; snapped = candidate; found = true;
                }
            }
        return found;
    }

    /// <summary>Computes AutoCAD-style relation snaps from a prior construction point to nearby straight segments.</summary>
    public bool TryFindRelationSnap(Point2 point, Point2 reference, double tolerance, OsnapMode mode, out OsnapResult snapped)
    {
        snapped = default;
        if ((mode != OsnapMode.Parallel && mode != OsnapMode.Perpendicular) ||
            !double.IsFinite(tolerance) || tolerance < 0) return false;

        var minX = point.X - tolerance; var maxX = point.X + tolerance;
        var minY = point.Y - tolerance; var maxY = point.Y + tolerance;
        var nearbySegments = new List<(Point2 A, Point2 B)>(64);
        CollectNearbySegments(minX, minY, maxX, maxY);
        // For parallel tracking, the reference may have been acquired on a source
        // segment that is near the construction point rather than near the cursor.
        CollectNearbySegments(reference.X - tolerance, reference.Y - tolerance,
            reference.X + tolerance, reference.Y + tolerance);

        void CollectNearbySegments(double left, double bottom, double right, double top)
        {
            var visited = 0;
            foreach (var entity in QueryCore(left, bottom, right, top, preserveEntityOrder: false))
            {
                if (visited++ >= MaximumHoverSnapEntities || nearbySegments.Count >= MaximumHoverSnapSegments) break;
                AppendSegments(entity, nearbySegments, left, bottom, right, top, MaximumHoverSnapSegments);
            }
        }

        var bestDistanceSquared = tolerance * tolerance;
        var found = false;
        foreach (var segment in nearbySegments)
        {
            var dx = segment.B.X - segment.A.X;
            var dy = segment.B.Y - segment.A.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 1e-18) continue;

            Point2 candidate;
            if (mode == OsnapMode.Parallel)
            {
                var inverseLength = 1.0 / Math.Sqrt(lengthSquared);
                var ux = dx * inverseLength; var uy = dy * inverseLength;
                var along = (point.X - reference.X) * ux + (point.Y - reference.Y) * uy;
                candidate = new Point2(reference.X + ux * along, reference.Y + uy * along);
            }
            else
            {
                var t = ((reference.X - segment.A.X) * dx + (reference.Y - segment.A.Y) * dy) / lengthSquared;
                t = Math.Clamp(t, 0, 1);
                candidate = new Point2(segment.A.X + dx * t, segment.A.Y + dy * t);
            }

            var distanceSquared = SquaredDistance(point, candidate);
            if (distanceSquared <= bestDistanceSquared && (!found || distanceSquared < bestDistanceSquared))
            {
                bestDistanceSquared = distanceSquared;
                snapped = new OsnapResult(candidate, mode);
                found = true;
            }
        }
        return found;
    }

    private List<OsnapResult> FindIntersections(Point2 point, double tolerance)
    {
        // Avoid LINQ iterator chains and per-entity segment iterators in this pointer-move hot path.
        var nearbySegments = new List<(Point2 A, Point2 B)>(96);
        var minX = point.X - tolerance; var maxX = point.X + tolerance;
        var minY = point.Y - tolerance; var maxY = point.Y + tolerance;
        var entityCount = 0;
        foreach (var entity in QueryCore(minX, minY, maxX, maxY, preserveEntityOrder: false))
        {
            if (entityCount++ >= MaximumHoverSnapEntities || nearbySegments.Count >= MaximumHoverSnapSegments) break;
            AppendSegments(entity, nearbySegments, minX, minY, maxX, maxY, MaximumHoverSnapSegments);
        }
        var intersections = new List<OsnapResult>();
        var toleranceSquared = tolerance * tolerance;
        for (var i = 0; i < nearbySegments.Count; i++)
            for (var j = i + 1; j < nearbySegments.Count; j++)
                if (TryIntersect(nearbySegments[i].A, nearbySegments[i].B, nearbySegments[j].A, nearbySegments[j].B, out var intersection) &&
                    SquaredDistance(point, intersection) <= toleranceSquared)
                    intersections.Add(new OsnapResult(intersection, OsnapMode.Intersection));
        return intersections;
    }

    private static bool TryIntersect(Point2 a, Point2 b, Point2 c, Point2 d, out Point2 intersection)
    {
        var ab = b - a; var cd = d - c;
        var denominator = ab.X * cd.Y - ab.Y * cd.X;
        if (Math.Abs(denominator) < 1e-12) { intersection = default; return false; }
        var ac = c - a;
        var t = (ac.X * cd.Y - ac.Y * cd.X) / denominator;
        var u = (ac.X * ab.Y - ac.Y * ab.X) / denominator;
        if (t < 0 || t > 1 || u < 0 || u > 1) { intersection = default; return false; }
        intersection = new Point2(a.X + ab.X * t, a.Y + ab.Y * t);
        return true;
    }

    private static void AppendSegments(CadEntity entity, List<(Point2 A, Point2 B)> segments, double minX, double minY, double maxX, double maxY, int maximumSegments = int.MaxValue)
    {
        if (entity is LineEntity line) { AddSegmentIfNearby(line.Start, line.End); return; }
        if (entity is not PolylineEntity polyline || polyline.Vertices.Count < 2) return;
        var count = polyline.Closed ? polyline.Vertices.Count : polyline.Vertices.Count - 1;
        for (var i = 0; i < count && segments.Count < maximumSegments; i++)
            if (Math.Abs(polyline.Vertices[i].Bulge) < 1e-9)
                AddSegmentIfNearby(polyline.Vertices[i].Point, polyline.Vertices[(i + 1) % polyline.Vertices.Count].Point);

        void AddSegmentIfNearby(Point2 a, Point2 b)
        {
            if (Math.Max(a.X, b.X) < minX || Math.Min(a.X, b.X) > maxX || Math.Max(a.Y, b.Y) < minY || Math.Min(a.Y, b.Y) > maxY) return;
            segments.Add((a, b));
        }
    }

    private static double SquaredDistance(Point2 a, Point2 b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static RTreeNode BuildTree(List<SpatialEntry> entries)
    {
        var level = Pack(entries, entry => entry.Bounds, group => new RTreeNode(Union(group.Select(item => item.Bounds)), group.Select(item => item.Index).ToArray(), null));
        while (level.Count > 1)
            level = Pack(level, node => node.Bounds, group => new RTreeNode(Union(group.Select(item => item.Bounds)), null, group.ToArray()));
        return level[0];
    }

    private static List<TNode> Pack<TItem, TNode>(List<TItem> items, Func<TItem, Bounds2> boundsOf, Func<List<TItem>, TNode> createNode)
    {
        if (items.Count <= NodeCapacity) return [createNode(items)];
        var leafCount = (int)Math.Ceiling(items.Count / (double)NodeCapacity);
        var sliceCount = (int)Math.Ceiling(Math.Sqrt(leafCount));
        var sliceSize = (int)Math.Ceiling(items.Count / (double)sliceCount);
        var byX = items.ToArray();
        Array.Sort(byX, (left, right) => boundsOf(left).CenterX.CompareTo(boundsOf(right).CenterX));
        var result = new List<TNode>(leafCount);
        for (var sliceStart = 0; sliceStart < byX.Length; sliceStart += sliceSize)
        {
            var length = Math.Min(sliceSize, byX.Length - sliceStart);
            var slice = new List<TItem>(length);
            for (var i = 0; i < length; i++) slice.Add(byX[sliceStart + i]);
            slice.Sort((left, right) => boundsOf(left).CenterY.CompareTo(boundsOf(right).CenterY));
            for (var groupStart = 0; groupStart < slice.Count; groupStart += NodeCapacity)
            {
                var groupCount = Math.Min(NodeCapacity, slice.Count - groupStart);
                var group = new List<TItem>(groupCount);
                for (var i = 0; i < groupCount; i++) group.Add(slice[groupStart + i]);
                result.Add(createNode(group));
            }
        }
        return result;
    }

    private static Bounds2 Union(IEnumerable<Bounds2> source)
    {
        using var iterator = source.GetEnumerator();
        if (!iterator.MoveNext()) return default;
        var first = iterator.Current;
        var minX = first.MinX; var minY = first.MinY; var maxX = first.MaxX; var maxY = first.MaxY;
        while (iterator.MoveNext())
        {
            var bounds = iterator.Current;
            minX = Math.Min(minX, bounds.MinX); minY = Math.Min(minY, bounds.MinY);
            maxX = Math.Max(maxX, bounds.MaxX); maxY = Math.Max(maxY, bounds.MaxY);
        }
        return new Bounds2(minX, minY, maxX, maxY);
    }

    private (int MinX, int MinY, int MaxX, int MaxY) CellRange(Bounds2 bounds, double width, double height)
    {
        var min = Cell(new Point2(bounds.MinX, bounds.MinY), width, height);
        var max = Cell(new Point2(bounds.MaxX, bounds.MaxY), width, height);
        return (min.X, min.Y, max.X, max.Y);
    }

    private (int X, int Y) Cell(Point2 point, double width, double height)
    {
        var x = (int)Math.Floor((point.X - _scene.MinX) / width * SnapGridSize);
        var y = (int)Math.Floor((point.Y - _scene.MinY) / height * SnapGridSize);
        return (Math.Clamp(x, 0, SnapGridSize - 1), Math.Clamp(y, 0, SnapGridSize - 1));
    }

    private static void Add<T>(Dictionary<(int X, int Y), List<T>> grid, (int X, int Y) cell, T item)
    {
        if (!grid.TryGetValue(cell, out var bucket)) grid[cell] = bucket = new List<T>();
        bucket.Add(item);
    }

    private static bool TryGetBounds(CadEntity entity, out Bounds2 bounds)
    {
        switch (entity)
        {
            case LineEntity line:
                bounds = new Bounds2(Math.Min(line.Start.X, line.End.X), Math.Min(line.Start.Y, line.End.Y), Math.Max(line.Start.X, line.End.X), Math.Max(line.Start.Y, line.End.Y)); return true;
            case CircleEntity circle:
                var radius = Math.Abs(circle.Radius);
                bounds = new Bounds2(circle.Center.X - radius, circle.Center.Y - radius, circle.Center.X + radius, circle.Center.Y + radius); return true;
            case ArcEntity arc:
                var arcRadius = Math.Abs(arc.Radius);
                bounds = new Bounds2(arc.Center.X - arcRadius, arc.Center.Y - arcRadius, arc.Center.X + arcRadius, arc.Center.Y + arcRadius); return true;
            case PolylineEntity polyline when polyline.Vertices.Count > 0:
                var first = polyline.Vertices[0].Point;
                var minX = first.X; var maxX = first.X; var minY = first.Y; var maxY = first.Y;
                var padding = 0.0; var segments = polyline.Closed ? polyline.Vertices.Count : polyline.Vertices.Count - 1;
                for (var i = 0; i < polyline.Vertices.Count; i++)
                {
                    var current = polyline.Vertices[i];
                    minX = Math.Min(minX, current.Point.X); maxX = Math.Max(maxX, current.Point.X);
                    minY = Math.Min(minY, current.Point.Y); maxY = Math.Max(maxY, current.Point.Y);
                    if (i < segments)
                    {
                        var next = polyline.Vertices[(i + 1) % polyline.Vertices.Count];
                        padding = Math.Max(padding, Point2.Distance(current.Point, next.Point) * Math.Abs(current.Bulge) * 0.5);
                    }
                }
                bounds = new Bounds2(minX - padding, minY - padding, maxX + padding, maxY + padding); return true;
            case CadTextEntity or CadMTextEntity:
                var textBox = CadTextExtents.GetBounds(entity); bounds = new Bounds2(textBox.MinX, textBox.MinY, textBox.MaxX, textBox.MaxY); return true;
            case ProxyCadEntity proxy when proxy.HasBounds:
                bounds = new Bounds2(Math.Min(proxy.BoundsMin.X, proxy.BoundsMax.X), Math.Min(proxy.BoundsMin.Y, proxy.BoundsMax.Y), Math.Max(proxy.BoundsMin.X, proxy.BoundsMax.X), Math.Max(proxy.BoundsMin.Y, proxy.BoundsMax.Y)); return true;
            default: bounds = default; return false;
        }
    }

    private static Bounds2 TextBounds(Point2 position, string value, double height, double width)
    {
        var h = double.IsFinite(height) && height > 0 ? height : 1;
        var lines = Math.Max(1, value.Count(character => character == '\n') + 1);
        var w = double.IsFinite(width) && width > 0 ? width : Math.Max(h * 0.6, value.Length * h * 0.65);
        return new Bounds2(position.X, position.Y - h * lines, position.X + w, position.Y + h * 0.2);
    }

    private static IEnumerable<OsnapResult> GetSnapPoints(CadEntity entity)
    {
        switch (entity)
        {
            case LineEntity line:
                yield return new(line.Start, OsnapMode.Endpoint); yield return new(line.End, OsnapMode.Endpoint);
                yield return new(new Point2((line.Start.X + line.End.X) * 0.5, (line.Start.Y + line.End.Y) * 0.5), OsnapMode.Midpoint); break;
            case CircleEntity circle:
                yield return new(circle.Center, OsnapMode.Center);
                yield return new(new Point2(circle.Center.X + circle.Radius, circle.Center.Y), OsnapMode.Quadrant);
                yield return new(new Point2(circle.Center.X - circle.Radius, circle.Center.Y), OsnapMode.Quadrant);
                yield return new(new Point2(circle.Center.X, circle.Center.Y + circle.Radius), OsnapMode.Quadrant);
                yield return new(new Point2(circle.Center.X, circle.Center.Y - circle.Radius), OsnapMode.Quadrant); break;
            case ArcEntity arc:
                yield return new(arc.Center, OsnapMode.Center);
                foreach (var angle in new[] { arc.StartAngleDegrees, arc.EndAngleDegrees })
                {
                    var radians = angle * Math.PI / 180.0;
                    yield return new(new Point2(arc.Center.X + arc.Radius * Math.Cos(radians), arc.Center.Y + arc.Radius * Math.Sin(radians)), OsnapMode.Endpoint);
                }
                var sweep = arc.EndAngleDegrees - arc.StartAngleDegrees; while (sweep <= 0) sweep += 360;
                { var mid = (arc.StartAngleDegrees + sweep * 0.5) * Math.PI / 180; yield return new(new Point2(arc.Center.X + arc.Radius * Math.Cos(mid), arc.Center.Y + arc.Radius * Math.Sin(mid)), OsnapMode.Midpoint); }
                foreach (var angle in new[] { 0.0, 90.0, 180.0, 270.0 })
                {
                    var offset = angle - arc.StartAngleDegrees; while (offset < 0) offset += 360;
                    if (offset <= sweep) { var radians = angle * Math.PI / 180; yield return new(new Point2(arc.Center.X + arc.Radius * Math.Cos(radians), arc.Center.Y + arc.Radius * Math.Sin(radians)), OsnapMode.Quadrant); }
                }
                break;
            case PolylineEntity polyline:
                foreach (var vertex in polyline.Vertices) yield return new(vertex.Point, OsnapMode.Endpoint);
                var segmentCount = polyline.Closed ? polyline.Vertices.Count : polyline.Vertices.Count - 1;
                for (var i = 0; i < segmentCount; i++)
                {
                    var a = polyline.Vertices[i].Point; var b = polyline.Vertices[(i + 1) % polyline.Vertices.Count].Point;
                    yield return new(new Point2((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5), OsnapMode.Midpoint);
                }
                break;
            case CadTextEntity text:
                yield return new(text.Position, OsnapMode.Insertion); break;
            case CadMTextEntity text:
                yield return new(text.Position, OsnapMode.Insertion); break;
        }
    }

    private readonly record struct SpatialEntry(int Index, Bounds2 Bounds);
    private sealed record RTreeNode(Bounds2 Bounds, int[]? Indices, RTreeNode[]? Children);
    private readonly record struct Bounds2(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double CenterX => (MinX + MaxX) * 0.5;
        public double CenterY => (MinY + MaxY) * 0.5;
        public bool IsFinite => double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MaxX) && double.IsFinite(MaxY) && MinX <= MaxX && MinY <= MaxY;
        public bool Intersects(Bounds2 other) => MaxX >= other.MinX && MinX <= other.MaxX && MaxY >= other.MinY && MinY <= other.MaxY;
    }
}
