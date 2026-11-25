//! istcad_native: a small, dependency-free native engine for iStCAD_1's
//! "heavy file" rendering path.
//!
//! Scope (see README.md for the full design rationale):
//!   1. A spatial index (`bvh` module) answering "which entities overlap this
//!      viewport rectangle" queries in roughly O(log n + k) instead of the
//!      O(n) scan a huge drawing would otherwise need every frame.
//!   2. A software line rasterizer (`raster` module) that draws a frame's
//!      visible, screen-transformed geometry directly into a BGRA8888 pixel
//!      buffer with Xiaolin Wu antialiasing, fast enough to stay smooth even
//!      on drawings with millions of segments.
//!
//! Both are exposed across a plain C ABI (`extern "C"`, `#[repr(C)]`) so the
//! WPF app can call them via P/Invoke. The .NET side owns all CAD semantics
//! (entity types, AutoCAD bulge-arc math, layers, colors): by the time
//! geometry reaches this crate it is already flattened into plain point
//! lists, by the SAME tessellation code already used (and already fixed,
//! three times, for the exact "arc rendered as a straight line" bug class)
//! elsewhere in the app. This crate deliberately knows nothing about bulges,
//! blocks, or any other CAD concept, precisely so that bug class cannot
//! reappear in a fourth place.
//!
//! IMPORTANT: this crate has ZERO external dependencies (see Cargo.toml) so
//! it can be built and unit-tested completely offline.

mod bvh;
mod raster;

use bvh::{Aabb, Bvh};
use std::os::raw::c_void;
use std::slice;

/// Bump this whenever the FFI surface changes shape, so the C# side can
/// detect a stale/mismatched DLL instead of crashing on a layout mismatch.
pub const ABI_VERSION: i32 = 1;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct NativeEntity {
    /// Caller-defined id (e.g. an index into the app's own rendered-entity
    /// array). Opaque to this crate; returned verbatim from queries.
    pub id: i32,
    /// Offset into the flat `points_x`/`points_y` arrays passed to
    /// `istcad_scene_build` where this entity's points start.
    pub point_offset: u32,
    pub point_count: u32,
    /// Nonzero: also connect the last point back to the first when drawing.
    pub closed: u8,
    /// Packed 0x00BBGGRR (blue, green, red) — matches the byte order the
    /// rest of the app already uses for its own CPU rasterizer.
    pub color_bgr: u32,
    pub min_x: f64,
    pub min_y: f64,
    pub max_x: f64,
    pub max_y: f64,
}

struct Entity {
    id: i32,
    point_offset: u32,
    point_count: u32,
    closed: bool,
    color: (u8, u8, u8),
}

pub struct Scene {
    points_x: Vec<f64>,
    points_y: Vec<f64>,
    entities: Vec<Entity>,
    bvh: Bvh,
}

impl Scene {
    fn empty() -> Scene {
        Scene { points_x: Vec::new(), points_y: Vec::new(), entities: Vec::new(), bvh: Bvh::build(&[]) }
    }

    fn build(points_x: Vec<f64>, points_y: Vec<f64>, native_entities: &[NativeEntity]) -> Result<Scene, i32> {
        let point_len = points_x.len();
        if point_len != points_y.len() {
            return Err(-1);
        }
        let mut entities = Vec::with_capacity(native_entities.len());
        let mut boxes = Vec::with_capacity(native_entities.len());
        for ne in native_entities {
            let start = ne.point_offset as usize;
            let count = ne.point_count as usize;
            let end = start.checked_add(count).ok_or(-2)?;
            if end > point_len {
                return Err(-2);
            }
            entities.push(Entity {
                id: ne.id,
                point_offset: ne.point_offset,
                point_count: ne.point_count,
                closed: ne.closed != 0,
                color: ((ne.color_bgr >> 16) as u8, (ne.color_bgr >> 8) as u8, ne.color_bgr as u8),
            });
            boxes.push(Aabb { min_x: ne.min_x, min_y: ne.min_y, max_x: ne.max_x, max_y: ne.max_y });
        }
        let bvh = Bvh::build(&boxes);
        Ok(Scene { points_x, points_y, entities, bvh })
    }

    fn query(&self, rect: &Aabb, out: &mut Vec<u32>) {
        self.bvh.query(rect, out);
    }

    fn render(
        &self,
        center_x: f64,
        center_y: f64,
        scale: f64,
        pixel_width: i32,
        pixel_height: i32,
        view: &Aabb,
        pixels: &mut [u8],
        background_bgra: u32,
        flip_y: bool,
    ) -> i32 {
        raster::clear(pixels, background_bgra);
        if pixel_width <= 0 || pixel_height <= 0 || scale <= 0.0 || !scale.is_finite() || self.bvh.is_empty() {
            return 0;
        }
        let half_w = pixel_width as f64 / 2.0;
        let half_h = pixel_height as f64 / 2.0;
        let to_screen = |wx: f64, wy: f64| -> (f64, f64) {
            let sx = (wx - center_x) * scale + half_w;
            let sy = if flip_y { (center_y - wy) * scale + half_h } else { (wy - center_y) * scale + half_h };
            (sx, sy)
        };

        let mut visible = Vec::new();
        self.query(view, &mut visible);

        let mut segments_drawn: i64 = 0;
        for &entity_idx in &visible {
            let e = &self.entities[entity_idx as usize];
            let start = e.point_offset as usize;
            let count = e.point_count as usize;
            if count < 2 {
                continue;
            }
            let last = if e.closed { count } else { count - 1 };
            for i in 0..last {
                let a = start + i;
                let b = start + ((i + 1) % count);
                let (ax, ay) = to_screen(self.points_x[a], self.points_y[a]);
                let (bx, by) = to_screen(self.points_x[b], self.points_y[b]);
                raster::draw_line_aa(pixels, pixel_width, pixel_height, ax, ay, bx, by, e.color);
                segments_drawn += 1;
            }
        }
        segments_drawn.min(i32::MAX as i64) as i32
    }

    fn extents(&self) -> Option<Aabb> {
        self.bvh.extents()
    }
}

// ---------------------------------------------------------------------------
// C ABI surface
// ---------------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn istcad_native_abi_version() -> i32 {
    ABI_VERSION
}

#[no_mangle]
pub extern "C" fn istcad_scene_new() -> *mut c_void {
    Box::into_raw(Box::new(Scene::empty())) as *mut c_void
}

#[no_mangle]
pub unsafe extern "C" fn istcad_scene_free(scene: *mut c_void) {
    if scene.is_null() {
        return;
    }
    drop(Box::from_raw(scene as *mut Scene));
}

/// Rebuilds the scene's geometry + spatial index from flat arrays. Safe to
/// call repeatedly on the same handle (e.g. after every edit); each call
/// fully replaces the previous content.
///
/// Returns 0 on success, or a negative error code:
///   -1: points_x/points_y length mismatch reported by caller
///   -2: an entity's point_offset/point_count range is out of bounds
///   -3: null scene handle
#[no_mangle]
pub unsafe extern "C" fn istcad_scene_build(
    scene: *mut c_void,
    points_x: *const f64,
    points_y: *const f64,
    points_len: usize,
    entities: *const NativeEntity,
    entities_len: usize,
) -> i32 {
    if scene.is_null() {
        return -3;
    }
    let scene = &mut *(scene as *mut Scene);
    let px = if points_len == 0 { &[][..] } else { slice::from_raw_parts(points_x, points_len) };
    let py = if points_len == 0 { &[][..] } else { slice::from_raw_parts(points_y, points_len) };
    let ents = if entities_len == 0 { &[][..] } else { slice::from_raw_parts(entities, entities_len) };
    match Scene::build(px.to_vec(), py.to_vec(), ents) {
        Ok(built) => {
            *scene = built;
            0
        }
        Err(code) => code,
    }
}

/// Fills `out_ids` with the (caller-defined) ids of every entity whose
/// precomputed bounding box intersects the given rectangle. Returns the
/// number of ids written. If `out_capacity` is too small to hold every
/// match, returns the NEGATIVE of the number actually needed so the caller
/// can grow its buffer and retry.
#[no_mangle]
pub unsafe extern "C" fn istcad_scene_query(
    scene: *const c_void,
    min_x: f64,
    min_y: f64,
    max_x: f64,
    max_y: f64,
    out_ids: *mut i32,
    out_capacity: usize,
) -> i32 {
    if scene.is_null() {
        return 0;
    }
    let scene = &*(scene as *const Scene);
    let rect = Aabb { min_x, min_y, max_x, max_y };
    let mut hits = Vec::new();
    scene.query(&rect, &mut hits);
    if hits.len() > out_capacity {
        return -(hits.len() as i32);
    }
    if out_capacity > 0 && !out_ids.is_null() {
        let out = slice::from_raw_parts_mut(out_ids, out_capacity);
        for (i, &idx) in hits.iter().enumerate() {
            out[i] = scene.entities[idx as usize].id;
        }
    }
    hits.len() as i32
}

/// Writes the scene's overall extents. Returns 1 if the scene has at least
/// one entity (and the outputs were written), 0 if it is empty.
#[no_mangle]
pub unsafe extern "C" fn istcad_scene_extents(
    scene: *const c_void,
    out_min_x: *mut f64,
    out_min_y: *mut f64,
    out_max_x: *mut f64,
    out_max_y: *mut f64,
) -> u8 {
    if scene.is_null() {
        return 0;
    }
    let scene = &*(scene as *const Scene);
    match scene.extents() {
        Some(bbox) => {
            if !out_min_x.is_null() { *out_min_x = bbox.min_x; }
            if !out_min_y.is_null() { *out_min_y = bbox.min_y; }
            if !out_max_x.is_null() { *out_max_x = bbox.max_x; }
            if !out_max_y.is_null() { *out_max_y = bbox.max_y; }
            1
        }
        None => 0,
    }
}

/// Renders one frame directly into a caller-owned BGRA8888 buffer
/// (`pixel_width * pixel_height * 4` bytes, e.g. a WPF `WriteableBitmap`'s
/// back buffer). World-to-screen transform matches the app's existing
/// convention: `screen = (world - center) * scale + halfExtent`, with
/// `flip_y != 0` flipping Y for CAD's Y-up world vs. screen's Y-down axis.
///
/// `view_*` is the world-space rectangle to query the spatial index with
/// (normally the visible world extents, optionally padded by the caller).
///
/// Returns the number of line segments drawn, or a negative error code:
///   -1: pixel buffer too small for pixel_width*pixel_height*4
///   -2: null scene handle
#[no_mangle]
pub unsafe extern "C" fn istcad_scene_render(
    scene: *const c_void,
    center_x: f64,
    center_y: f64,
    scale: f64,
    pixel_width: i32,
    pixel_height: i32,
    view_min_x: f64,
    view_min_y: f64,
    view_max_x: f64,
    view_max_y: f64,
    pixels: *mut u8,
    pixels_len: usize,
    background_bgra: u32,
    flip_y: u8,
) -> i32 {
    if scene.is_null() {
        return -2;
    }
    if pixel_width <= 0 || pixel_height <= 0 {
        return 0;
    }
    let needed = (pixel_width as i64) * (pixel_height as i64) * 4;
    if (pixels_len as i64) < needed || pixels.is_null() {
        return -1;
    }
    let scene = &*(scene as *const Scene);
    let buf = slice::from_raw_parts_mut(pixels, pixels_len);
    let view = Aabb { min_x: view_min_x, min_y: view_min_y, max_x: view_max_x, max_y: view_max_y };
    scene.render(center_x, center_y, scale, pixel_width, pixel_height, &view, buf, background_bgra, flip_y != 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entity(id: i32, offset: u32, count: u32, closed: bool, bbox: (f64, f64, f64, f64)) -> NativeEntity {
        NativeEntity {
            id,
            point_offset: offset,
            point_count: count,
            closed: closed as u8,
            color_bgr: 0x00FF0000, // red (R=FF,G=0,B=0 packed as 0x00BBGGRR -> 0x00_00_00_FF conceptually; see below)
            min_x: bbox.0,
            min_y: bbox.1,
            max_x: bbox.2,
            max_y: bbox.3,
        }
    }

    #[test]
    fn ffi_roundtrip_build_query_extents_render() {
        unsafe {
            let scene = istcad_scene_new();
            assert!(!scene.is_null());

            // Two triangles: one near origin, one far away, so a viewport
            // query can distinguish them.
            let xs = vec![0.0, 1.0, 0.0, 100.0, 101.0, 100.0];
            let ys = vec![0.0, 0.0, 1.0, 100.0, 100.0, 101.0];
            let entities = vec![
                entity(42, 0, 3, true, (0.0, 0.0, 1.0, 1.0)),
                entity(99, 3, 3, true, (100.0, 100.0, 101.0, 101.0)),
            ];
            let rc = istcad_scene_build(scene, xs.as_ptr(), ys.as_ptr(), xs.len(), entities.as_ptr(), entities.len());
            assert_eq!(rc, 0);

            // Extents should cover both triangles.
            let (mut min_x, mut min_y, mut max_x, mut max_y) = (0.0, 0.0, 0.0, 0.0);
            let has = istcad_scene_extents(scene, &mut min_x, &mut min_y, &mut max_x, &mut max_y);
            assert_eq!(has, 1);
            assert_eq!((min_x, min_y, max_x, max_y), (0.0, 0.0, 101.0, 101.0));

            // Query near origin only finds entity 42.
            let mut out = [0i32; 8];
            let n = istcad_scene_query(scene, -1.0, -1.0, 2.0, 2.0, out.as_mut_ptr(), out.len());
            assert_eq!(n, 1);
            assert_eq!(out[0], 42);

            // Query everything finds both, in some order.
            let n_all = istcad_scene_query(scene, -10.0, -10.0, 200.0, 200.0, out.as_mut_ptr(), out.len());
            assert_eq!(n_all, 2);

            // Query with too-small buffer reports the negative required count.
            let mut tiny = [0i32; 1];
            let n_small = istcad_scene_query(scene, -10.0, -10.0, 200.0, 200.0, tiny.as_mut_ptr(), tiny.len());
            assert_eq!(n_small, -2);

            // Render a frame centered on the near triangle; expect some
            // non-background pixels and a positive segment count.
            let (w, h) = (64, 64);
            let mut pixels = vec![0u8; (w * h * 4) as usize];
            let drawn = istcad_scene_render(
                scene, 0.5, 0.5, 20.0, w, h, -5.0, -5.0, 5.0, 5.0,
                pixels.as_mut_ptr(), pixels.len(), 0xFF000000, 1,
            );
            assert!(drawn > 0);
            let any_colored = pixels.chunks_exact(4).any(|p| p[2] > 0); // red channel
            assert!(any_colored, "expected at least one red-tinted pixel from the rendered triangle");

            istcad_scene_free(scene);
        }
    }

    #[test]
    fn build_rejects_out_of_range_entity() {
        unsafe {
            let scene = istcad_scene_new();
            let xs = vec![0.0, 1.0];
            let ys = vec![0.0, 1.0];
            let bad = vec![entity(1, 0, 10, false, (0.0, 0.0, 1.0, 1.0))]; // count exceeds array length
            let rc = istcad_scene_build(scene, xs.as_ptr(), ys.as_ptr(), xs.len(), bad.as_ptr(), bad.len());
            assert_eq!(rc, -2);
            istcad_scene_free(scene);
        }
    }

    #[test]
    fn abi_version_is_stable_constant() {
        assert_eq!(istcad_native_abi_version(), 1);
    }
}
