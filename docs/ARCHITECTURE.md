# iStCAD architecture and staged development

## Current project boundaries

| Project | Responsibility |
| --- | --- |
| `iStCAD_1.Core` | CAD entities, geometry primitives, drawing state and document-independent contracts. |
| `iStCAD_1.Application` | Editing operations, command input parsing, file coordination and spatial queries. |
| `iStCAD_1.Infras` | ACadSharp integration, DWG/DXF persistence and PDF plotting implementation. |
| `iStCAD_1.Rendering` | CAD model to WPF geometry conversion and geometry caching. |
| `iStCAD_1-WPF` | Windows application entry point, window, input events and view composition. |
| `vendor/ACadSharp` | Vendored ACadSharp source and its upstream license. |

The dependency direction should stay toward the CAD model: Core has no WPF or ACadSharp dependency; Application depends on Core; Infras adapts external formats to Core; Rendering turns Core data into display geometry; the WPF application composes these services.

## Suggested extraction stages

1. **Keep the current solution build stable.** The root `Directory.Build.props` routes binaries to `_Build/bin`, separated by project and configuration; each project keeps its standard `obj` intermediates. Preserve the current project references while moving files.
2. **Separate application use cases.** Move command dispatch, selection/window-crossing logic, and dynamic-input state out of `MainWindow.xaml.cs` into focused classes under `iStCAD_1.Application`. The window should translate WPF events into application calls and display results.
3. **Separate CAD format adapters.** Keep the ACadSharp-specific mapping in `iStCAD_1.Infras`; extract shared import diagnostics and entity mapping into focused files there before adding another CAD library.
4. **Separate rendering by responsibility.** Keep the render cache, viewport culling, and entity geometry conversion behind rendering services. Add specialized builders for text, blocks and complex entities as each display path is improved.
5. **Separate plotting and recovery when they grow.** Move PDF plot configuration/export and autosave/recovery orchestration behind dedicated application contracts and infrastructure implementations. Keep UI dialogs in WPF.
6. **Add verification projects per layer.** Add Core/Application tests, format round-trip fixtures, rendering checks, and large-drawing benchmarks as separate work items. This makes each release stage reviewable without coupling the app window to file-format tests.

Avoid moving projects and changing their public contracts in the same step. Each extraction should preserve the existing DXF/DWG open, save, render, selection, and recovery flows before the next boundary changes.
