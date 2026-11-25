//! A small, dependency-free software line rasterizer writing directly into a
//! BGRA8888 pixel buffer (the same byte order WPF's `PixelFormats.Bgra32`
//! and the app's existing CPU rasterizer already use: buf[0]=B, [1]=G,
//! [2]=R, [3]=A).
//!
//! Lines are drawn with Xiaolin Wu's antialiasing algorithm. The point of
//! moving this to Rust is precisely that native speed affords always-on
//! antialiasing, even for the "heavy file" case where the existing C#
//! fast-path intentionally drops to aliased rendering to keep up.

#[inline]
fn blend_pixel(buf: &mut [u8], width: i32, height: i32, x: i32, y: i32, color: (u8, u8, u8), coverage: f64) {
    if x < 0 || y < 0 || x >= width || y >= height || coverage <= 0.0 {
        return;
    }
    let coverage = if coverage > 1.0 { 1.0 } else { coverage };
    let idx = (y as usize * width as usize + x as usize) * 4;
    if idx + 3 >= buf.len() {
        return;
    }
    let inv = 1.0 - coverage;
    buf[idx] = (buf[idx] as f64 * inv + color.2 as f64 * coverage).round() as u8; // B
    buf[idx + 1] = (buf[idx + 1] as f64 * inv + color.1 as f64 * coverage).round() as u8; // G
    buf[idx + 2] = (buf[idx + 2] as f64 * inv + color.0 as f64 * coverage).round() as u8; // R
    buf[idx + 3] = 255;
}

#[inline]
fn ipart(x: f64) -> f64 {
    x.floor()
}
#[inline]
fn fpart(x: f64) -> f64 {
    x - x.floor()
}
#[inline]
fn rfpart(x: f64) -> f64 {
    1.0 - fpart(x)
}
#[inline]
fn round_half_up(x: f64) -> f64 {
    ipart(x + 0.5)
}

/// Draws one antialiased line segment (screen-space pixel coordinates) into
/// `buf`, a `width*height*4` BGRA8888 buffer. Faithful implementation of
/// Xiaolin Wu's line algorithm (see module docs for byte order).
pub fn draw_line_aa(buf: &mut [u8], width: i32, height: i32, x0: f64, y0: f64, x1: f64, y1: f64, color: (u8, u8, u8)) {
    if !(x0.is_finite() && y0.is_finite() && x1.is_finite() && y1.is_finite()) {
        return;
    }
    let steep = (y1 - y0).abs() > (x1 - x0).abs();

    let (mut x0, mut y0, mut x1, mut y1) = if steep { (y0, x0, y1, x1) } else { (x0, y0, x1, y1) };
    if x0 > x1 {
        std::mem::swap(&mut x0, &mut x1);
        std::mem::swap(&mut y0, &mut y1);
    }

    let dx = x1 - x0;
    let dy = y1 - y0;
    let gradient = if dx.abs() < 1e-9 { 1.0 } else { dy / dx };

    let plot = |x: i32, y: i32, c: f64, buf: &mut [u8]| {
        if steep {
            blend_pixel(buf, width, height, y, x, color, c);
        } else {
            blend_pixel(buf, width, height, x, y, color, c);
        }
    };

    // First endpoint.
    let xend = round_half_up(x0);
    let yend = y0 + gradient * (xend - x0);
    let xgap = rfpart(x0 + 0.5);
    let xpxl1 = xend as i32;
    let ypxl1 = ipart(yend) as i32;
    plot(xpxl1, ypxl1, rfpart(yend) * xgap, buf);
    plot(xpxl1, ypxl1 + 1, fpart(yend) * xgap, buf);
    let mut intery = yend + gradient;

    // Second endpoint.
    let xend2 = round_half_up(x1);
    let yend2 = y1 + gradient * (xend2 - x1);
    let xgap2 = fpart(x1 + 0.5);
    let xpxl2 = xend2 as i32;
    let ypxl2 = ipart(yend2) as i32;
    plot(xpxl2, ypxl2, rfpart(yend2) * xgap2, buf);
    plot(xpxl2, ypxl2 + 1, fpart(yend2) * xgap2, buf);

    // Main loop.
    let mut x = xpxl1 + 1;
    while x <= xpxl2 - 1 {
        plot(x, ipart(intery) as i32, rfpart(intery), buf);
        plot(x, ipart(intery) as i32 + 1, fpart(intery), buf);
        intery += gradient;
        x += 1;
    }
}

/// Fills the whole buffer with a flat BGRA color (used to clear the frame
/// before drawing).
pub fn clear(buf: &mut [u8], color_bgra: u32) {
    let bytes = color_bgra.to_le_bytes(); // [B, G, R, A] matching little-endian packed 0xAARRGGBB write order below
    // Caller packs color as 0xAARRGGBB in a u32; decompose explicitly instead
    // of relying on endianness so behavior is identical on any host.
    let a = ((color_bgra >> 24) & 0xFF) as u8;
    let r = ((color_bgra >> 16) & 0xFF) as u8;
    let g = ((color_bgra >> 8) & 0xFF) as u8;
    let b = (color_bgra & 0xFF) as u8;
    let _ = bytes;
    let mut i = 0;
    while i + 3 < buf.len() {
        buf[i] = b;
        buf[i + 1] = g;
        buf[i + 2] = r;
        buf[i + 3] = a;
        i += 4;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_buf(w: i32, h: i32) -> Vec<u8> {
        vec![0u8; (w * h * 4) as usize]
    }

    #[test]
    fn horizontal_line_colors_expected_row() {
        let (w, h) = (20, 10);
        let mut buf = make_buf(w, h);
        draw_line_aa(&mut buf, w, h, 2.0, 5.0, 10.0, 5.0, (255, 0, 0));
        // Row y=5 across x in [2,10] should have strong red coverage somewhere.
        let mut any_red = false;
        for x in 2..=10 {
            let idx = ((5 * w + x) * 4) as usize;
            if buf[idx + 2] > 200 {
                any_red = true;
            }
        }
        assert!(any_red, "expected red pixels along the horizontal line");
    }

    #[test]
    fn diagonal_line_stays_in_bounds_and_does_not_panic() {
        let (w, h) = (16, 16);
        let mut buf = make_buf(w, h);
        draw_line_aa(&mut buf, w, h, -5.0, -5.0, 100.0, 100.0, (0, 255, 0));
        // Should not panic, and should have touched at least one pixel within bounds.
        let any_set = buf.iter().any(|&b| b != 0);
        assert!(any_set);
    }

    #[test]
    fn clear_fills_every_pixel() {
        let (w, h) = (4, 4);
        let mut buf = make_buf(w, h);
        clear(&mut buf, 0xFF102030); // A=FF R=10 G=20 B=30
        assert_eq!(&buf[0..4], &[0x30, 0x20, 0x10, 0xFF]);
        assert_eq!(&buf[buf.len() - 4..], &[0x30, 0x20, 0x10, 0xFF]);
    }

    #[test]
    fn vertical_line_works_via_steep_branch() {
        let (w, h) = (10, 20);
        let mut buf = make_buf(w, h);
        draw_line_aa(&mut buf, w, h, 5.0, 2.0, 5.0, 15.0, (0, 0, 255));
        let mut any_blue = false;
        for y in 2..=15 {
            let idx = ((y * w + 5) * 4) as usize;
            if buf[idx] > 200 {
                any_blue = true;
            }
        }
        assert!(any_blue, "expected blue pixels along the vertical line");
    }
}
