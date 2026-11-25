# Model Space entity audit – follow-up to v1.0.49.19

## Purpose

Keep Model Space statistics separate from Paper Space layouts and block-definition contents when opening DWG/DXF files. This is important when comparing consultant sample drawings and detecting import-side duplication.

## Changes

- `CadDrawing.NativeEntityTypes` now represents **Model Space only**.
- `CadDrawing.NativeEntityTypesBySpace` stores top-level entity type counts for each named layout/space.
- `CadDrawing.NativeModelSpaceLayerCounts` stores Model Space entity counts grouped by layer.
- `DocumentEntityCount` remains a database-wide count across all block records; it is deliberately not used as the Model Space count.
- The open command history now reports Model Space entity types and its busiest layers separately, then reports entity type summaries for non-Model layouts.
- Snapshot cloning copies the new statistics so undo/session snapshots do not lose the audit data.

## Interpretation

- Model Space count: `NativeEntityCount` / `GetSpaceEntityCount("Model")`.
- Model Space type histogram: `NativeEntityTypes`.
- Per-layout type histogram: `NativeEntityTypesBySpace[layoutName]`.
- Database-wide block-record entity count: `DocumentEntityCount`.

A block INSERT is counted once as a Model Space entity. Its nested block-definition children are not counted as additional Model Space entities. This avoids reporting exploded preview geometry as if it were independent native Model Space objects.

## Validation status

Source-level consistency checks only. This environment does not contain the .NET 8 SDK/runtime, so the WPF solution and ACadSharp DWG reader could not be built or executed here. Consequently, no actual per-file entity counts are claimed for `x1.dwg`, `x5.dwg`, `x6.dwg`, or `x7.dwg`. Run these four files through the Windows istCAD build to collect measured histograms and compare them against AutoCAD/ODA output.
