# Native DWG INSERT ghost geometry — v1.0.49.13

## Finding

The DWG adapter expanded `INSERT` block contents into preview paths, but the expansion path did not apply per-child display visibility. It could draw child entities even when `IsInvisible` was set or their layer was off/frozen. Layer `0` children inside blocks also need to inherit the owning `INSERT` layer for visibility. Separately, the adapter could fall back to an `INSERT`'s cached proxy graphics when expansion yielded no visible paths; stale proxy paths could therefore reintroduce hidden circles.

## Changes

- Filter expanded block and dimension preview entities by `IsInvisible`, layer On/Off state, negative layer color index, and Frozen flag.
- Propagate layer-0 inheritance through nested `INSERT` preview traversal.
- Suppress cached `INSERT` proxy paths when the block is non-empty and consists entirely of native preview-supported entity types, even when all those entities are filtered as invisible. Unknown/custom-content-only blocks retain proxy fallback.
- Apply the same frozen-layer filtering to top-level entities before they enter the viewport spatial index.
- Cache the native-preview classification per block record during a drawing open to avoid rescanning identical block definitions for every INSERT instance.
- Log the most common INSERT block-definition names in opening diagnostics, making anonymous/dynamic/constraint-like symbol blocks easier to identify from a real DWG.

## Important limitation

No affected DWG was included with the source, so the exact block names and handles behind the reported circles cannot be confirmed. If circles remain after this fix, inspect the `INSERT block-name counts` line in the command history and the affected DWG; likely remaining cases include dynamic-block visibility state or proprietary/custom block content that ACadSharp cannot fully resolve.

## Validation

- Source-level logic reviewed and XML project files parsed.
- ZIP integrity check is required after packaging.
- Windows WPF build, rendering comparison against AutoCAD, and performance measurements remain unverified because the current environment has no .NET SDK/WPF runtime and the affected DWG was not provided.
