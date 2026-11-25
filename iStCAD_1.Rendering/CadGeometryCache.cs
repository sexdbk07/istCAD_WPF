using System.Threading;
using System.Windows.Media;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Rendering;

/// <summary>Bounded LRU cache for immutable viewport geometry, with content sharing for duplicate primitives.</summary>
public sealed class CadGeometryCache
{
    private const int MaximumEntries = 90_000;
    private static long s_maximumEstimatedUnits = 4_000_000;
    private static long MaximumEstimatedUnits => Volatile.Read(ref s_maximumEstimatedUnits);

    /// <summary>Scales the cache budget to the machine: the cache held ~4M units (hundreds of MB) even on 8 GB PCs that were already short of RAM.</summary>
    public static void ConfigureForMemory(ulong totalPhysicalBytes)
    {
        var gb = totalPhysicalBytes / 1073741824.0;
        Volatile.Write(ref s_maximumEstimatedUnits, gb <= 8.5 ? 2_500_000L : gb <= 16.5 ? 4_000_000L : 6_000_000L);
    }
    private const int MaximumContentProbes = 8;
    private const int MaximumContentGroupSize = 32;
    private readonly Dictionary<(Guid Id, int Lod), LinkedListNode<Entry>> _entries = new();
    private readonly Dictionary<ContentSignature, List<LinkedListNode<Entry>>> _byContent = new();
    private readonly LinkedList<Entry> _recent = new();
    private long _estimatedUnits;
    private readonly object _gate = new();

    public int Count { get { lock (_gate) return _entries.Count; } }
    public long EstimatedUnits { get { lock (_gate) return _estimatedUnits; } }

    public bool TryGet(CadEntity entity, int lod, out Geometry geometry, CancellationToken cancellationToken = default, bool shareByContent = false)
    {
        var entityLod = GetEntityLod(entity, lod);
        var key = (entity.Id, entityLod);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node) && ReferenceEquals(node.Value.Entity, entity))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                geometry = node.Value.Geometry;
                return true;
            }
            if (node is not null) Remove(key, node);
        }

        // Content matching is enabled only for the background render worker; cache probes
        // performed by UI selection overlays must remain constant-time.
        if (!shareByContent)
        {
            geometry = Geometry.Empty;
            return false;
        }
        if (!TryCreateContentSignature(entity, entityLod, cancellationToken, out var signature))
        {
            geometry = Geometry.Empty;
            return false;
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                geometry = Geometry.Empty;
                return false;
            }
            if (_entries.TryGetValue(key, out var cached) && ReferenceEquals(cached.Value.Entity, entity))
            {
                _recent.Remove(cached);
                _recent.AddFirst(cached);
                geometry = cached.Value.Geometry;
                return true;
            }
            if (_byContent.TryGetValue(signature, out var candidates))
            {
                // Never copy the candidate list (ToArray) and never probe more than a few
                // entries: drawings with tens of thousands of identical primitives made the
                // old code O(n^2) and froze the render worker and the UI waiting for it.
                var probeCount = Math.Min(candidates.Count, MaximumContentProbes);
                for (var probe = 0; probe < probeCount; probe++)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var candidate = candidates[probe];
                    if (!AreGeometryEquivalent(entity, candidate.Value.Entity, cancellationToken)) continue;
                    geometry = candidate.Value.Geometry;
                    // Store an identity-specific alias so subsequent frames get an O(1)
                    // entity lookup while reusing the immutable Geometry instance. The alias is
                    // NOT added to the content group, so the group (and List.Remove cost) stays small.
                    AddEntryUnsafe(key, entity, geometry, null, sharedGeometry: true);
                    return true;
                }
            }
        }

        geometry = Geometry.Empty;
        return false;
    }

    public void Add(CadEntity entity, int lod, Geometry geometry, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;
        if (!geometry.IsFrozen) throw new ArgumentException("Cached geometry must be frozen.", nameof(geometry));
        var entityLod = GetEntityLod(entity, lod);
        var key = (entity.Id, entityLod);
        var hasSignature = TryCreateContentSignature(entity, entityLod, cancellationToken, out var signature);
        if (cancellationToken.IsCancellationRequested) return;
        var estimatedUnits = EstimateGeometryUnits(entity, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (_entries.TryGetValue(key, out var existing)) Remove(key, existing);
            AddEntryUnsafe(key, entity, geometry, hasSignature ? signature : null, sharedGeometry: false, estimatedUnitsOverride: estimatedUnits);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _byContent.Clear();
            _recent.Clear();
            _estimatedUnits = 0;
        }
    }

    /// <summary>Drops the cached geometry of specific entities (O(changed), unlike Prune which is O(all)).</summary>
    public void Invalidate(IEnumerable<CadEntity> entities)
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            foreach (var entity in entities)
                for (var lod = 0; lod <= 2; lod++)
                    if (_entries.TryGetValue((entity.Id, lod), out var node)) Remove((entity.Id, lod), node);
        }
    }

    public void Prune(IReadOnlyList<CadEntity> currentEntities)
    {
        lock (_gate) { if (_entries.Count == 0) return; }
        var currentById = currentEntities.ToDictionary(entity => entity.Id);
        lock (_gate)
        {
            for (var node = _recent.Last; node is not null;)
            {
                var previous = node.Previous;
                if (!currentById.TryGetValue(node.Value.Entity.Id, out var current) || !ReferenceEquals(current, node.Value.Entity))
                    Remove(node.Value.Key, node);
                node = previous;
            }
        }
    }

    private void AddEntryUnsafe((Guid Id, int Lod) key, CadEntity entity, Geometry geometry,
        ContentSignature? signature, bool sharedGeometry, long? estimatedUnitsOverride = null)
    {
        if (_entries.TryGetValue(key, out var old)) Remove(key, old);
        var estimatedUnits = sharedGeometry ? 8 : estimatedUnitsOverride ?? EstimateGeometryUnits(entity, CancellationToken.None);
        var node = _recent.AddFirst(new Entry(key, entity, geometry, estimatedUnits, signature));
        _entries[key] = node;
        _estimatedUnits += estimatedUnits;
        if (signature is { } contentSignature)
        {
            if (!_byContent.TryGetValue(contentSignature, out var group)) _byContent[contentSignature] = group = new List<LinkedListNode<Entry>>();
            if (group.Count < MaximumContentGroupSize) group.Add(node);
        }
        while ((_entries.Count > MaximumEntries || _estimatedUnits > MaximumEstimatedUnits) && _recent.Last is { } oldest)
            Remove(oldest.Value.Key, oldest);
    }

    private void Remove((Guid Id, int Lod) key, LinkedListNode<Entry> node)
    {
        _entries.Remove(key);
        _estimatedUnits -= node.Value.EstimatedUnits;
        _recent.Remove(node);
        if (node.Value.Signature is { } signature && _byContent.TryGetValue(signature, out var group))
        {
            group.Remove(node);
            if (group.Count == 0) _byContent.Remove(signature);
        }
    }

    private static int GetEntityLod(CadEntity entity, int lod) =>
        entity is LineEntity or CircleEntity or ArcEntity or PolylineEntity ? 0 : lod;

    private static bool TryCreateContentSignature(CadEntity entity, int lod, CancellationToken token, out ContentSignature signature)
    {
        var hash = new HashCode();
        switch (entity)
        {
            case LineEntity line:
                hash.Add(typeof(LineEntity)); hash.Add(line.Start); hash.Add(line.End);
                signature = new ContentSignature(typeof(LineEntity), lod, hash.ToHashCode()); return true;
            case CircleEntity circle:
                hash.Add(typeof(CircleEntity)); hash.Add(circle.Center); hash.Add(Math.Abs(circle.Radius));
                signature = new ContentSignature(typeof(CircleEntity), lod, hash.ToHashCode()); return true;
            case ArcEntity arc:
                hash.Add(typeof(ArcEntity)); hash.Add(arc.Center); hash.Add(Math.Abs(arc.Radius));
                hash.Add(arc.StartAngleDegrees); hash.Add(arc.EndAngleDegrees);
                signature = new ContentSignature(typeof(ArcEntity), lod, hash.ToHashCode()); return true;
            case PolylineEntity polyline when polyline.Vertices.Count <= 1_024:
                hash.Add(typeof(PolylineEntity)); hash.Add(polyline.Closed); hash.Add(polyline.Vertices.Count);
                for (var i = 0; i < polyline.Vertices.Count; i++)
                {
                    if ((i & 255) == 0 && token.IsCancellationRequested) { signature = default; return false; }
                    hash.Add(polyline.Vertices[i].Point);
                    hash.Add(polyline.Vertices[i].Bulge);
                }
                signature = new ContentSignature(typeof(PolylineEntity), lod, hash.ToHashCode()); return true;
            default:
                signature = default;
                return false;
        }
    }

    private static bool AreGeometryEquivalent(CadEntity left, CadEntity right, CancellationToken token)
    {
        switch (left, right)
        {
            case (LineEntity a, LineEntity b):
                return a.Start == b.Start && a.End == b.End;
            case (CircleEntity a, CircleEntity b):
                return a.Center == b.Center && Math.Abs(a.Radius).Equals(Math.Abs(b.Radius));
            case (ArcEntity a, ArcEntity b):
                return a.Center == b.Center && Math.Abs(a.Radius).Equals(Math.Abs(b.Radius)) &&
                    a.StartAngleDegrees.Equals(b.StartAngleDegrees) && a.EndAngleDegrees.Equals(b.EndAngleDegrees);
            case (PolylineEntity a, PolylineEntity b):
                if (a.Closed != b.Closed || a.Vertices.Count != b.Vertices.Count) return false;
                for (var i = 0; i < a.Vertices.Count; i++)
                {
                    if ((i & 255) == 0 && token.IsCancellationRequested) return false;
                    if (a.Vertices[i].Point != b.Vertices[i].Point || !a.Vertices[i].Bulge.Equals(b.Vertices[i].Bulge)) return false;
                }
                return !token.IsCancellationRequested;
            case (ProxyCadEntity a, ProxyCadEntity b):
                return ProxyGeometryEquivalent(a, b, token);
            default:
                return false;
        }
    }

    // Cache identical bounded proxy/block previews too. The point cap keeps signature
    // generation cheaper than rebuilding geometry; larger previews remain identity-cached.
    private static bool TryHashProxyGeometry(ProxyCadEntity proxy, CancellationToken token, ref HashCode hash)
    {
        const int maxPoints = 8_192;
        const int maxTexts = 64;
        var pointCount = 0;
        var textCount = 0;
        hash.Add(proxy.HasBounds);
        if (proxy.HasBounds) { hash.Add(proxy.BoundsMin); hash.Add(proxy.BoundsMax); }
        hash.Add(proxy.PreviewClosed);
        hash.Add(proxy.PreviewFilled);
        if (proxy.PreviewPaths is not null)
        {
            hash.Add(proxy.PreviewPaths.Count);
            foreach (var path in proxy.PreviewPaths)
            {
                if (token.IsCancellationRequested || (pointCount += path.Count) > maxPoints) return false;
                hash.Add(path.Count);
                var pathIndex = 0;
                foreach (var point in path)
                {
                    if ((pathIndex++ & 255) == 0 && token.IsCancellationRequested) return false;
                    hash.Add(point);
                }
            }
        }
        else hash.Add(0);
        if (proxy.ProxyGraphicsPaths is not null)
        {
            hash.Add(proxy.ProxyGraphicsPaths.Count);
            foreach (var path in proxy.ProxyGraphicsPaths)
            {
                if (token.IsCancellationRequested || (pointCount += path.Points.Count) > maxPoints) return false;
                hash.Add(path.Closed); hash.Add(path.Points.Count);
                var pathIndex = 0;
                foreach (var point in path.Points)
                {
                    if ((pathIndex++ & 255) == 0 && token.IsCancellationRequested) return false;
                    hash.Add(point);
                }
            }
        }
        else hash.Add(0);
        hash.Add(proxy.PreviewText);
        if (!string.IsNullOrEmpty(proxy.PreviewText) && proxy.PreviewText.Length > 4_096) return false;
        hash.Add(proxy.PreviewTextHeight); hash.Add(proxy.PreviewTextRotation);
        hash.Add(proxy.PreviewTextPosition); hash.Add(proxy.PreviewTextWidth);
        hash.Add(proxy.PreviewTextLineSpacing); hash.Add(proxy.PreviewTextAttachment);
        if (!string.IsNullOrEmpty(proxy.PreviewText)) textCount++;
        if (proxy.ProxyGraphicsTexts is not null)
        {
            hash.Add(proxy.ProxyGraphicsTexts.Count);
            foreach (var text in proxy.ProxyGraphicsTexts)
            {
                if (token.IsCancellationRequested || ++textCount > maxTexts || text.Text.Length > 4_096) return false;
                hash.Add(text.Text); hash.Add(text.Position); hash.Add(text.Height); hash.Add(text.RotationRadians);
                hash.Add(text.Width); hash.Add(text.LineSpacing); hash.Add(text.AttachmentPoint);
            }
        }
        else hash.Add(0);
        // Empty proxies with bounds only are safe to share: CreateProxyGeometry renders
        // exactly the same rectangle/cross from these bounds. Avoid signatures with no geometry.
        return !token.IsCancellationRequested && (pointCount > 0 || textCount > 0 || proxy.HasBounds);
    }

    private static bool ProxyGeometryEquivalent(ProxyCadEntity a, ProxyCadEntity b, CancellationToken token)
    {
        if (a.HasBounds != b.HasBounds || (a.HasBounds && (a.BoundsMin != b.BoundsMin || a.BoundsMax != b.BoundsMax)) ||
            a.PreviewClosed != b.PreviewClosed || a.PreviewFilled != b.PreviewFilled || a.PreviewText != b.PreviewText ||
            !a.PreviewTextHeight.Equals(b.PreviewTextHeight) || !a.PreviewTextRotation.Equals(b.PreviewTextRotation) ||
            a.PreviewTextPosition != b.PreviewTextPosition || !a.PreviewTextWidth.Equals(b.PreviewTextWidth) ||
            !a.PreviewTextLineSpacing.Equals(b.PreviewTextLineSpacing) || a.PreviewTextAttachment != b.PreviewTextAttachment)
            return false;
        if (a.PreviewPaths?.Count != b.PreviewPaths?.Count || a.ProxyGraphicsPaths?.Count != b.ProxyGraphicsPaths?.Count ||
            a.ProxyGraphicsTexts?.Count != b.ProxyGraphicsTexts?.Count) return false;
        var visited = 0;
        if (a.PreviewPaths is not null)
            for (var i = 0; i < a.PreviewPaths.Count; i++)
            {
                var left = a.PreviewPaths[i]; var right = b.PreviewPaths![i];
                if (left.Count != right.Count) return false;
                for (var j = 0; j < left.Count; j++)
                {
                    if ((visited++ & 255) == 0 && token.IsCancellationRequested) return false;
                    if (left[j] != right[j]) return false;
                }
            }
        if (a.ProxyGraphicsPaths is not null)
            for (var i = 0; i < a.ProxyGraphicsPaths.Count; i++)
            {
                var left = a.ProxyGraphicsPaths[i]; var right = b.ProxyGraphicsPaths![i];
                if (left.Closed != right.Closed || left.Points.Count != right.Points.Count) return false;
                for (var j = 0; j < left.Points.Count; j++)
                {
                    if ((visited++ & 255) == 0 && token.IsCancellationRequested) return false;
                    if (left.Points[j] != right.Points[j]) return false;
                }
            }
        if (a.ProxyGraphicsTexts is not null)
            for (var i = 0; i < a.ProxyGraphicsTexts.Count; i++)
            {
                if ((visited++ & 31) == 0 && token.IsCancellationRequested) return false;
                var left = a.ProxyGraphicsTexts[i]; var right = b.ProxyGraphicsTexts![i];
                if (left.Text != right.Text || left.Position != right.Position || !left.Height.Equals(right.Height) ||
                    !left.RotationRadians.Equals(right.RotationRadians) || !left.Width.Equals(right.Width) ||
                    !left.LineSpacing.Equals(right.LineSpacing) || left.AttachmentPoint != right.AttachmentPoint ||
                    left.FontFamily != right.FontFamily || left.Bold != right.Bold || left.Italic != right.Italic || !left.WidthFactor.Equals(right.WidthFactor) ||
                    left.HAlign != right.HAlign || left.VAlign != right.VAlign || !left.Oblique.Equals(right.Oblique)) return false;
            }
        return !token.IsCancellationRequested;
    }

    private static long EstimateGeometryUnits(CadEntity entity, CancellationToken cancellationToken)
    {
        switch (entity)
        {
            case LineEntity: return 4;
            case CircleEntity or ArcEntity: return 16;
            case PolylineEntity polyline: return Math.Max(4L, (long)polyline.Vertices.Count * 2);
            case CadTextEntity text: return Math.Max(16L, (long)text.Value.Length * 12);
            case CadMTextEntity text: return Math.Max(24L, ((long)text.FormattedValue.Length + text.Value.Length) * 10);
            case ProxyCadEntity proxy:
                long units = 32;
                if (proxy.PreviewPaths is not null)
                    foreach (var path in proxy.PreviewPaths)
                    {
                        if (cancellationToken.IsCancellationRequested) return 0;
                        units += (long)path.Count * 2;
                    }
                if (proxy.ProxyGraphicsPaths is not null)
                    foreach (var path in proxy.ProxyGraphicsPaths)
                    {
                        if (cancellationToken.IsCancellationRequested) return 0;
                        units += (long)path.Points.Count * 2;
                    }
                if (proxy.PreviewText is not null) units += (long)proxy.PreviewText.Length * 10;
                if (proxy.ProxyGraphicsTexts is not null)
                    foreach (var text in proxy.ProxyGraphicsTexts)
                    {
                        if (cancellationToken.IsCancellationRequested) return 0;
                        units += (long)text.Text.Length * 10;
                    }
                return units;
            default: return 64;
        }
    }

    private readonly record struct ContentSignature(Type Type, int Lod, int Hash);
    private sealed record Entry((Guid Id, int Lod) Key, CadEntity Entity, Geometry Geometry, long EstimatedUnits, ContentSignature? Signature);
}
