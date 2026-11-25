# Viewport cancellation — v1.0.49.11

## Symptom
Visual Studio reported `System.OperationCanceledException` at `MainWindow.xaml.cs` inside `BuildVisibleGeometryAsync` while iterating visible candidates.

## Root cause
A newer zoom/pan viewport supersedes an in-progress render and cancels its `CancellationTokenSource`. Calling `ThrowIfCancellationRequested()` turns this normal stale-frame event into a first-chance exception. The method already caught expected cancellation, but Visual Studio may still break if its debugger exception settings are configured to break when the CLR exception is thrown.

## Change
- The adaptive R-tree viewport query now checks cancellation and returns an empty candidate array when superseded.
- The worker exits cooperatively from candidate processing when cancellation is requested.
- Frame composition stops work at cancellation checkpoints; after composition the worker rejects the partial image if its token was cancelled.
- The existing catch remains to handle cancellation exceptions from any downstream dependency that still uses `ThrowIfCancellationRequested()`.
- A cancelled/stale frame is not shown and is not retried for the same obsolete camera state.

## Verification status
The source was edited and packaged. A Windows WPF build and a real zoom/pan regression test are still required; no build success is claimed.
