# Full-entity preview and import responsiveness — v1.0.49.20

## Goals

Improve viewport completeness for the supplied DWG samples without exploding native objects into the drawing database or allowing a large INSERT to consume the preview budget for unrelated top-level Model Space entities.

## Changes

- Hatch boundaries are sampled directly from native ACadSharp edge records: line, circular arc, elliptical arc, bulged polyline, and spline.
- The Hatch remains one native entity with its original handle and database relationships. No boundary edges are added as independent CAD entities.
- Hatch boundaries nested in an INSERT are transformed by the owning INSERT/block matrix before being stored as preview paths.
- Top-level Dimensions use their own bounded preview budget.
- Top-level proxy entities use their own bounded proxy-graphics budget. A complex INSERT earlier in the file no longer exhausts the shared recursive INSERT budget and blanks later proxy entities.
- Top-level non-INSERT geometry previews continue even after the recursive INSERT budget is exhausted.
- Rendering remains on the existing cancellable background geometry worker, with R-tree viewport culling, geometry caching, and LOD.

## What this does not claim

- Hatch boundary outlines are shown, but pattern/gradient fill is not yet rendered by the WPF viewport.
- Dynamic Block stretch/lookup/flip/array and all Visibility State evaluation are not implemented. Unsafe unresolved blocks keep the existing bounds-only fallback rather than displaying geometry from inactive states.
- Extremely complex nested INSERTs can still hit per-INSERT safety limits. Native DWG objects are preserved; only the viewport preview may be partial.
- This is not a claim that every ACadSharp entity type has a fully native editable tool in the current editor. Unsupported entities are retained in the native document and displayed through supported preview geometry or extents where possible.

## Validation status

- Static source review and consistency checks only.
- .NET 8 SDK and Windows WPF runtime are unavailable in this environment.
- Build, opening `x1.dwg`, `x5.dwg`, `x6.dwg`, `x7.dwg`, visual parity against AutoCAD, and measured open/zoom/pan/hover latency and RAM remain pending on Windows.
