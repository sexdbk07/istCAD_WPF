# Import object-allocation reduction — v1.0.49.19

## Confirmed source hot paths

- `SummarizeDatabase` previously walked the reachable DWG object graph through dictionaries, XData, extension dictionaries, and reactors after mapping entities. This work is diagnostic-only and can be much larger than the viewport entity set. Replaced with constant/table-count summary; detailed XData/reactor inventory is skipped during open.
- Every INSERT eagerly expanded its referenced block into new transformed preview-path and point arrays. Per-INSERT and drawing-wide preview ceilings have been reduced to bound the number of generated preview records and coordinates. The native INSERT and BlockRecords remain unchanged.
- Hatch preview previously called `Edge.ToEntity()` for every Hatch boundary edge, allocating transient Line/Arc/Spline/Polyline entities solely to construct a preview. Hatch edge preview is now skipped; native Hatch and extents remain preserved.
- INSERT preview bounds are no longer recomputed by scanning every generated preview point when the native INSERT already supplied valid extents.
- Geometry content hashing no longer traverses ProxyCadEntity preview trees; Polyline content sharing is limited to at most 1,024 vertices. Identity lookup remains O(1).

## New limits

- Per INSERT: 2,000 nested entities, 1,500 preview points, 512 paths, 64 texts.
- Whole drawing: 75,000 nested entities, 100,000 points, 8,000 paths, 2,000 texts, 20,000 proxy items.

## Trade-offs

Complex INSERTs and Hatch boundaries may show only extents/partial preview. No native DWG entity is deleted, exploded into the database, or rewritten by these preview changes. Full native editing and saving continue to use the original ACadSharp document.

## Verification

Static source checks and ZIP integrity only. No .NET SDK/WPF runtime is available here, so Windows build, open-time profiling, and DWG visual regression remain unverified.
