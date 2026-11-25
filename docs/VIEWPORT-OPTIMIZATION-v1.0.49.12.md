# Viewport responsiveness — v1.0.49.12

## Implemented

1. **Frame-cadence preview updates:** wheel-preview transform writes are coalesced by a render-priority dispatcher timer (nominal 16 ms cadence), rather than writing every image transform for every wheel event.
2. **Temporary grid suppression:** the dot/grid image is hidden during wheel zoom and middle-button pan, then refreshed for the stopped camera. Grid toggle state still controls whether an image source is present.
3. **Cooperative cancellation in large geometry:** `CadGeometryBuilder` now checks the viewport token while walking bulged polylines and proxy path collections/points. Proxy path conversion is chunked to avoid building one huge intermediate point array. The renderer discards geometry when its token is canceled before adding it to the cache.
4. **Large-query and duplicate-geometry cache:** the R-tree adaptive query now skips drawing-order sorting for far-LOD queries above 8,000 candidates; the 12,000-candidate threshold for pixel-density LOD aggregation remains unchanged. The geometry cache now shares frozen geometry for content-identical lines, circles, arcs, and polylines up to 4,096 vertices, with exact equality confirmation after hash matching and token checks while processing large polylines.
5. **Re-render after cancellation:** worker completion now requests the latest viewport whenever its key is not the last accepted image key, including zoom-out/zoom-in sequences that return to the same key after a worker was canceled. Failed builds are not automatically retried, avoiding an error retry loop.

## Limits and verification

- The 16 ms timer is a nominal scheduling interval, not a guaranteed 60 FPS measurement.
- Duplicate content-sharing is intentionally restricted to line, circle, arc and polylines up to 4,096 vertices; larger polylines, text and proxy geometry remain entity-specific to avoid hashing huge geometry before building it.
- Dense far-LOD results may remain unordered above the 8,000-candidate sorting threshold, which can affect painter order for overlapping objects.
- Source changes were reviewed and the archive structure is checked. A Windows WPF build and interactive zoom/pan benchmark remain required; this environment does not have the .NET SDK/WPF runtime.
