//! A minimal, dependency-free bounding-volume hierarchy (BVH) over axis-aligned
//! boxes, used as the spatial index for "which entities are visible in this
//! viewport rectangle" queries on heavy drawings.
//!
//! Design notes (why a BVH and not a grid or an R*-tree crate):
//! - This crate must compile with ZERO external dependencies (see Cargo.toml
//!   comment) so it can be built and unit-tested entirely offline. A full
//!   R*-tree (the usual "proper" choice) is available as the `rstar` crate,
//!   but pulling in crates.io dependencies is not an option here.
//! - A plain uniform grid is simpler but degrades badly on the kind of
//!   unevenly-dense CAD drawings this feature targets (a cluster of hatching
//!   or thousands of small door/furniture blocks next to huge empty space).
//! - A median-split BVH (recursively split the longer axis at the median,
//!   merge child boxes bottom-up) is a well-understood, easy-to-verify
//!   algorithm that handles skewed density well and is only moderately more
//!   code than a grid. That is the trade-off made here.

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Aabb {
    pub min_x: f64,
    pub min_y: f64,
    pub max_x: f64,
    pub max_y: f64,
}

impl Aabb {
    #[inline]
    pub fn intersects(&self, other: &Aabb) -> bool {
        self.min_x <= other.max_x
            && self.max_x >= other.min_x
            && self.min_y <= other.max_y
            && self.max_y >= other.min_y
    }

    #[inline]
    fn center(&self, axis: u8) -> f64 {
        if axis == 0 {
            (self.min_x + self.max_x) * 0.5
        } else {
            (self.min_y + self.max_y) * 0.5
        }
    }

    fn union(&self, other: &Aabb) -> Aabb {
        Aabb {
            min_x: self.min_x.min(other.min_x),
            min_y: self.min_y.min(other.min_y),
            max_x: self.max_x.max(other.max_x),
            max_y: self.max_y.max(other.max_y),
        }
    }
}

const LEAF_SIZE: usize = 8;
/// Sentinel marking a leaf node (a real node index can never reach this since
/// we never build anywhere near u32::MAX nodes).
const LEAF_MARK: u32 = u32::MAX;

struct Node {
    bbox: Aabb,
    left: u32,
    right: u32,
    /// For leaves only: start offset and count into `Bvh::order`.
    start: u32,
    count: u32,
}

/// A BVH over a fixed set of entity bounding boxes. Build once per scene
/// (re)build (file load / edit), then query many times per frame cheaply.
pub struct Bvh {
    nodes: Vec<Node>,
    order: Vec<u32>,
    /// Each entity's own precise box, indexed by entity index (NOT by
    /// position in `order`). A leaf node's merged bbox only tells us the
    /// leaf as a whole MIGHT contain a hit; we still narrow-phase check
    /// each entity's own box here before reporting it as a match.
    boxes: Vec<Aabb>,
}

impl Bvh {
    /// `boxes[i]` is the bounding box of entity index `i`. Entity indices are
    /// opaque to this module: callers map them back to their own entity list.
    pub fn build(boxes: &[Aabb]) -> Bvh {
        let n = boxes.len();
        let mut order: Vec<u32> = (0..n as u32).collect();
        let mut nodes = Vec::with_capacity(if n == 0 { 0 } else { 2 * n });
        if n > 0 {
            Self::build_recursive(&mut order, 0, boxes, &mut nodes);
        }
        Bvh { nodes, order, boxes: boxes.to_vec() }
    }

    fn compute_bbox(order_slice: &[u32], boxes: &[Aabb]) -> Aabb {
        let mut bbox = boxes[order_slice[0] as usize];
        for &i in &order_slice[1..] {
            bbox = bbox.union(&boxes[i as usize]);
        }
        bbox
    }

    /// Returns the index (into `nodes`) of the subtree root built from
    /// `order_slice`. `start_offset` is this slice's absolute offset within
    /// the full `order` array (needed because leaves store `start`/`count`
    /// into that single flat array rather than owning their own Vec).
    fn build_recursive(order_slice: &mut [u32], start_offset: usize, boxes: &[Aabb], nodes: &mut Vec<Node>) -> u32 {
        let n = order_slice.len();
        let bbox = Self::compute_bbox(order_slice, boxes);
        if n <= LEAF_SIZE {
            let idx = nodes.len() as u32;
            nodes.push(Node { bbox, left: LEAF_MARK, right: LEAF_MARK, start: start_offset as u32, count: n as u32 });
            return idx;
        }
        let axis: u8 = if (bbox.max_x - bbox.min_x) >= (bbox.max_y - bbox.min_y) { 0 } else { 1 };
        order_slice.sort_by(|&a, &b| {
            let ca = boxes[a as usize].center(axis);
            let cb = boxes[b as usize].center(axis);
            ca.partial_cmp(&cb).unwrap_or(std::cmp::Ordering::Equal)
        });
        let mid = n / 2;
        let (left_slice, right_slice) = order_slice.split_at_mut(mid);
        let left_idx = Self::build_recursive(left_slice, start_offset, boxes, nodes);
        let right_idx = Self::build_recursive(right_slice, start_offset + mid, boxes, nodes);
        let idx = nodes.len() as u32;
        nodes.push(Node { bbox, left: left_idx, right: right_idx, start: 0, count: 0 });
        idx
    }

    /// Appends every entity index whose box intersects `query` into `out`.
    pub fn query(&self, query: &Aabb, out: &mut Vec<u32>) {
        if self.nodes.is_empty() {
            return;
        }
        self.query_node(self.nodes.len() as u32 - 1, query, out);
    }

    fn query_node(&self, node_idx: u32, query: &Aabb, out: &mut Vec<u32>) {
        let node = &self.nodes[node_idx as usize];
        if !node.bbox.intersects(query) {
            return;
        }
        if node.left == LEAF_MARK {
            let start = node.start as usize;
            let end = start + node.count as usize;
            // Narrow-phase: the leaf's merged bbox intersecting the query
            // does not mean every entity inside it does too, so check each
            // entity's own precise box before reporting a match.
            for &entity_idx in &self.order[start..end] {
                if self.boxes[entity_idx as usize].intersects(query) {
                    out.push(entity_idx);
                }
            }
            return;
        }
        self.query_node(node.left, query, out);
        self.query_node(node.right, query, out);
    }

    /// Overall bounding box of every entity in the tree, if any.
    pub fn extents(&self) -> Option<Aabb> {
        self.nodes.last().map(|n| n.bbox)
    }

    pub fn is_empty(&self) -> bool {
        self.nodes.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bx(min_x: f64, min_y: f64, max_x: f64, max_y: f64) -> Aabb {
        Aabb { min_x, min_y, max_x, max_y }
    }

    #[test]
    fn empty_tree_queries_nothing() {
        let bvh = Bvh::build(&[]);
        assert!(bvh.is_empty());
        assert!(bvh.extents().is_none());
        let mut out = Vec::new();
        bvh.query(&bx(-1.0, -1.0, 1.0, 1.0), &mut out);
        assert!(out.is_empty());
    }

    #[test]
    fn query_finds_only_intersecting_boxes() {
        // A grid of 100 small 1x1 boxes at (i,j) for i,j in 0..10.
        let mut boxes = Vec::new();
        for j in 0..10 {
            for i in 0..10 {
                boxes.push(bx(i as f64, j as f64, i as f64 + 1.0, j as f64 + 1.0));
            }
        }
        let bvh = Bvh::build(&boxes);
        assert_eq!(bvh.extents(), Some(bx(0.0, 0.0, 10.0, 10.0)));

        // Query a 2x2 region around (5,5)-(7,7): should hit exactly the boxes
        // whose [min,max) overlaps that rectangle.
        let mut out = Vec::new();
        bvh.query(&bx(5.0, 5.0, 7.0, 7.0), &mut out);
        out.sort_unstable();
        let mut expected = Vec::new();
        for j in 0..10 {
            for i in 0..10 {
                let b = boxes[(j * 10 + i) as usize];
                if b.intersects(&bx(5.0, 5.0, 7.0, 7.0)) {
                    expected.push((j * 10 + i) as u32);
                }
            }
        }
        expected.sort_unstable();
        assert_eq!(out, expected);
        assert!(!out.is_empty());
    }

    #[test]
    fn query_outside_everything_is_empty() {
        let boxes = vec![bx(0.0, 0.0, 1.0, 1.0), bx(5.0, 5.0, 6.0, 6.0)];
        let bvh = Bvh::build(&boxes);
        let mut out = Vec::new();
        bvh.query(&bx(100.0, 100.0, 200.0, 200.0), &mut out);
        assert!(out.is_empty());
    }

    #[test]
    fn handles_large_skewed_density_without_panicking() {
        // Simulate a CAD-like distribution: a tight cluster of 2000 tiny
        // boxes plus a handful of huge sparse ones, to exercise the
        // median-split logic under skew (the scenario a uniform grid
        // handles poorly).
        let mut boxes = Vec::new();
        for i in 0..2000 {
            let x = (i % 50) as f64 * 0.01;
            let y = (i / 50) as f64 * 0.01;
            boxes.push(bx(x, y, x + 0.005, y + 0.005));
        }
        boxes.push(bx(-1000.0, -1000.0, 1000.0, 1000.0));
        boxes.push(bx(-5000.0, 0.0, 5000.0, 1.0));
        let bvh = Bvh::build(&boxes);
        let mut out = Vec::new();
        bvh.query(&bx(0.0, 0.0, 0.02, 0.02), &mut out);
        // Should find some cluster members plus both huge boxes.
        assert!(out.len() >= 3);
        assert!(out.contains(&2000));
        assert!(out.contains(&2001));
    }
}
