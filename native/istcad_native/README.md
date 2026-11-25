# istcad_native

Optional Rust native engine for iStCAD_1's "heavy file" rendering path: a
spatial index (for fast "what's visible in this viewport" queries) and a
software line rasterizer (antialiased, writing straight into a BGRA8888
pixel buffer), exposed to the WPF app through a plain C ABI.

**The app works fully without this.** If the DLL isn't built, or fails to
load for any reason, `NativeEngine.cs` on the C# side falls back to "not
available" automatically - nothing else in the app changes. Run the
`NATIVESTATUS` (alias `NATIVE`) command in iStCAD to check whether it is
currently built and loaded, including a short end-to-end self-test.

## Why this exists

On very large drawings, two things get expensive every frame:
1. Figuring out which of (potentially) hundreds of thousands of entities are
   actually inside the current viewport, instead of scanning all of them.
2. Rasterizing all the resulting line segments, especially while keeping
   lines antialiased (the existing C# "fast/draft" path intentionally drops
   antialiasing on heavy frames to keep up - see `RasterAliasedWorkUnits` in
   MainWindow.xaml.cs).

Native code can do both meaningfully faster than the managed fallback, which
is the whole point of moving them here.

## Zero external dependencies, on purpose

This crate depends on nothing from crates.io (see `Cargo.toml`). That is a
deliberate constraint, not an oversight: it means `cargo build` and
`cargo test` work completely offline, on any machine, with no risk of a
flaky or unreachable package registry breaking your build. The trade-off is
that the spatial index (a from-scratch median-split BVH in `src/bvh.rs`,
not the common `rstar` crate) and the rasterizer (a from-scratch Wu's-line
implementation in `src/raster.rs`) are both hand-rolled instead of using an
off-the-shelf crate. Both are covered by unit tests (`cargo test`).

## What this crate does NOT do

It has no knowledge of CAD concepts at all - no entity types, no AutoCAD
"bulge" arc math, no blocks/layers/colors-by-name. By the time geometry
reaches this crate it is already flattened into plain point lists by the
app's existing, already-correct tessellation code (`PolylineTessellator` and
friends). This is intentional: the app's bulge-arc math has had three
separate bugs fixed in it across three different rendering code paths in
this project's history (see UPDATE_NOTE.md, v1.0.229/v1.0.230) precisely
because it was implemented more than once. Keeping this crate "dumb" (pure
geometry index + pixel rasterizer) means there is only ever ONE place that
turns a bulge into an arc, in C#, and this crate cannot reintroduce that bug
class a fourth time.

## One-time setup (on your Windows machine)

1. Install Rust: https://rustup.rs (default options are fine; this installs
   the `x86_64-pc-windows-msvc` target automatically on Windows).
2. That's it. From then on, **Visual Studio builds this automatically**: the
   `BuildNativeEngine` MSBuild target in `iStCAD_1-WPF.csproj` runs
   `cargo build --release` here and copies the resulting `istcad_native.dll`
   next to the app's output. If `cargo` isn't on PATH yet, the build prints
   one message and continues without it - it never fails your build.
3. After rebuilding, run `NATIVESTATUS` inside iStCAD to confirm it loaded.

To build and test it by hand instead:
```
cd native/istcad_native
cargo test            # runs the unit tests (bvh + rasterizer + FFI round trip)
cargo build --release # produces target/release/istcad_native.dll
```

## FFI surface (`src/lib.rs`)

All entry points are `extern "C"`, `#[no_mangle]`, operating on an opaque
scene handle (`istcad_scene_new`/`_free`):

- `istcad_native_abi_version() -> i32` - bump `ABI_VERSION` in lib.rs
  whenever the shape of this surface changes, so the C# side can detect a
  stale DLL instead of misreading memory.
- `istcad_scene_build(scene, points_x, points_y, points_len, entities, entities_len) -> i32`
  - rebuilds the scene's geometry + spatial index from flat arrays. Call
    this once per edit/load, not per frame. `entities` is an array of
    `NativeEntity { id, point_offset, point_count, closed, color_bgr, min_x, min_y, max_x, max_y }`
    (`#[repr(C)]`); each entity references a contiguous range of the shared
    `points_x`/`points_y` arrays.
- `istcad_scene_query(scene, min_x, min_y, max_x, max_y, out_ids, out_capacity) -> i32`
  - fills `out_ids` with the ids of entities whose box intersects the
    rectangle; returns the count, or the *negative* of the required count if
    `out_capacity` was too small (so the caller can retry with a bigger
    buffer).
- `istcad_scene_extents(scene, &mut min_x, &mut min_y, &mut max_x, &mut max_y) -> u8`
  - overall bounding box, 0/1 for empty/non-empty.
- `istcad_scene_render(scene, center_x, center_y, scale, pixel_width, pixel_height, view_min_x, view_min_y, view_max_x, view_max_y, pixels, pixels_len, background_bgra, flip_y) -> i32`
  - queries the spatial index for the given world-space view rectangle,
    transforms the surviving entities' points into screen space
    (`screen = (world - center) * scale + halfExtent`, Y flipped when
    `flip_y != 0` for CAD's Y-up vs. screen's Y-down convention), and draws
    every segment with antialiasing directly into the caller-owned BGRA8888
    buffer (matches `PixelFormats.Bgra32`). Returns the number of segments
    drawn, or a negative error code (see doc comment in `lib.rs`).

## C# side (`NativeEngine.cs`, repo root)

`NativeEngineInterop` holds the raw `[DllImport]` declarations and the
`NativeEntity` struct (must mirror the Rust `#[repr(C)]` layout exactly -
see the comment on it). `NativeHeavyFileEngine` is the safe wrapper every
other call site should use: every public method catches
`DllNotFoundException` / `EntryPointNotFoundException` /
`BadImageFormatException` and degrades to "unavailable" instead of
throwing. `NativeHeavyFileEngine.RunSelfTest()` builds a tiny scene,
queries it, reads its extents, and renders one frame - this is what the
`NATIVESTATUS` command calls.

## Current integration status (important - please read)

This first version wires the engine up for diagnostics only
(`NATIVESTATUS`/`NATIVE`), **not yet into the live render loop**. That is a
deliberate, conservative choice: this crate and its C# bindings were
written and unit-tested in a Linux sandbox with no .NET compiler and no way
to build or run the actual Windows DLL or the WPF app, so there has been no
opportunity to verify the P/Invoke marshaling (struct layout, array
pinning, calling convention) actually works end-to-end on a real Windows
build. Wiring it directly into `MainWindow.xaml.cs`'s existing render loop
(the `TryPlotSoft`/`SoftSegmentBatch` dense-frame path) before that is
confirmed would risk breaking the one rendering path that already works.

**Next step, once you confirm `NATIVESTATUS` reports a successful
self-test on your machine:** tell me, and the spatial index + rasterizer
can be wired into the actual heavy-file render path (replacing or
accelerating the existing `SoftSegmentBatch` dense-frame rasterizer and the
managed `CadSpatialIndex` range queries) with confidence that the
foundation underneath it is solid.
