#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/palette.glsl"

// ---------------------------------------------------------------------------
//  HERO. Recursive domain-warped fBm, tuned to read as an oil-and-water
//  liquid light projection rather than as a noise demo.
//
//  What makes it read as liquid rather than as clouds — in order of how much
//  each one matters:
//
//   1. TWO immiscible fields, differenced. A single fBm is a cloud. The
//      difference of two independently warped fields has zero-crossings that
//      behave like a meniscus: they stretch, pinch and snap.
//   2. BANDING with thin bright rims. Real oil on a clock glass has flat
//      interior colour and a bright edge where light refracts through the
//      film boundary. Quantising the interference field into cells and
//      lighting only the cell walls is the whole trick.
//   3. THIN-FILM IRIDESCENCE on those rims, driven by the local gradient.
//      This is the specific thing that makes it look wet.
//   4. NEGATIVE SPACE. A low-frequency mask keeps roughly a third of the
//      frame dark. Without it the feedback buffer saturates and the trails —
//      which are doing most of the visual work — stop reading at all.
//   5. PER-CELL TINT. Each cell samples the palette at its own offset, so the
//      frame looks like several different pigments, not one gradient.
// ---------------------------------------------------------------------------

uniform float uWarp;      // domain-warp strength (the recursion gain)
uniform float uScale;     // base spatial frequency
uniform float uFlow;      // time rate
uniform float uCells;     // oil-cell density
uniform float uRim;       // rim highlight gain
uniform float uRimWidth;  // rim thickness in pixels (screen-space, not field-space)
uniform float uIrid;      // thin-film iridescence 0..1
uniform float uMask;      // negative-space bias
uniform float uOct;       // detail octaves (governor dial)
uniform float uPaint;     // paint gain, decay-normalized on the host

in vec2 vUv;
out vec4 fragColor;

// One recursion of Inigo Quilez's domain warp. The intermediate field is
// returned so the second layer can reuse it instead of paying for its own.
float liquid(vec2 p, float t, float amt, int oct, out vec2 mid) {
    vec2 q = vec2(fbm(p + vec2(0.0, 0.55 * t), 2, 2.05, 0.52),
                  fbm(p + vec2(5.2, 1.3) - vec2(0.42 * t, 0.0), 2, 2.05, 0.52));
    vec2 r = vec2(fbm(p + amt * q + vec2(1.7, 9.2) + vec2(0.28 * t, 0.0), 3, 2.05, 0.52),
                  fbm(p + amt * q + vec2(8.3, 2.8) - vec2(0.0, 0.21 * t), 3, 2.05, 0.52));
    mid = r;
    return fbm(p + amt * r, oct, 2.02, 0.52);
}

void main() {
    float t = F_TIME * uFlow;
    vec2 uv = vUv;
    vec2 p = uv - 0.5;
    p.x *= F_ASPECT;

    // Sub bass breathes the whole field in and out. Small number, large effect.
    p *= uScale * (1.0 - 0.12 * F_SUB);
    p = rot2(0.018 * t + 0.35 * F_STEREO_WIDTH) * p;

    int oct = int(clamp(uOct, 2.0, 6.0));
    float amt = uWarp * (0.65 + 1.15 * F_BASS);

    vec2 mid;
    float f = liquid(p, t, amt, oct, mid);
    // Second field: reuses the first field's warp vector, counter-rotating in
    // time. One extra fbm instead of five.
    float g = fbm(p * 0.63 + mid * amt * 0.75 + vec2(19.3, 4.7)
                  - vec2(t * 0.46, t * 0.13), oct, 2.02, 0.52);

    float iso = f * 1.25 - g * 0.85;

    // --- oil cells -------------------------------------------------------
    float dens = uCells * (1.0 + 0.30 * F_LOWMID);
    float n    = iso * dens + 0.14 * t + 0.22 * F_BAR_PHASE;
    float cell = floor(n);
    float fr   = fract(n);

    // The meniscus is the whole trick, and it has to be a CONSTANT number of
    // pixels wide or it reads as a soft gradient instead of a film boundary.
    // fwidth(n) is the field's rate of change per pixel, so dividing by it
    // converts a field-space distance into a screen-space one — thin bright
    // edges everywhere, including where the field is nearly flat.
    float aa    = max(fwidth(n), 1e-5);
    float dWall = min(fr, 1.0 - fr);
    float rimW  = min(uRimWidth * aa, 0.42);
    float rim   = 1.0 - smoothstep(0.0, rimW, dWall);
    float wall  = smoothstep(0.0, rimW * 3.2, dWall);

    // --- pigment + negative space ----------------------------------------
    // The pigment field is DELIBERATELY the lowest frequency thing here. A
    // high-frequency palette lookup averages to grey inside the feedback
    // trail; big single-hue blobs survive it, which is what makes the frame
    // look like several different oils rather than one gradient. The same
    // field carries the negative space, so a pigment always has an edge.
    float pig  = fbm(p * 0.24 + vec2(0.0, t * 0.085), 2, 2.1, 0.5);
    float mask = smoothstep(0.34, 0.66, pig * 0.5 + 0.5 + 0.09 * F_RMS - uMask);

    float grad = length(mid - vec2(g)) * 0.5;
    // A NARROW palette window, drifting slowly, is what keeps the image
    // chromatic. The feedback trail sums ~1/(1-decay) samples of the palette;
    // if that window is wide the sum averages to grey no matter how saturated
    // each sample was. Narrow window + slow drift = one coherent pigment per
    // region, and the trail stays the colour it started.
    float ct = 0.5 + 0.22 * pig + 0.11 * hash11(cell * 1.37) + 0.06 * F_CENTROID;
    vec3 body = palChroma(palSpiralT(uv, ct));
    // Thin-film shimmer, sampled from the PALETTE rather than from a raw hue
    // wheel — iridescence that cannot introduce an off-scheme colour.
    vec3 film = palChroma(palSpiralT(uv, ct + 0.09 + 0.30 * fract(grad * 1.7 + 0.30 * iso + 0.09 * t))) * 1.55;

    // The paint per frame is deliberately SPARSE and high contrast: a dim body
    // and a hot, thin meniscus. That matters more than it looks. The feedback
    // buffer reaches equilibrium at paint/(1-decay), so a paint layer that
    // covers the whole frame evenly accumulates to a flat wash no matter how it
    // is scaled. Paint only the moving edges and the trail becomes ribbons.
    vec3 col = body * (0.05 + 0.34 * wall) * (0.62 + 0.55 * f);
    // The meniscus is bright but must stay CHROMATIC — pushing a palette colour
    // past ~1.4 clips its largest channel and the highlight turns white, which
    // is how a colour scheme quietly becomes grey. Bloom supplies the heat.
    col += mix(palChroma(palSpiralT(uv, ct + 0.17)) * 1.45, film * 0.9, clamp(uIrid, 0.0, 1.0))
         * rim * uRim * (0.50 + 1.20 * F_HIMID + 0.60 * F_BRILLIANCE);

    col *= mask;
    col *= 0.45 + 0.95 * F_RMS + 0.55 * beatPulse(4.0) * F_BPM_CONF
         + 0.65 * F_ONSET * F_PERCUSSIVITY;

    fragColor = vec4(col * uPaint * F_INTENSITY, 1.0);
}
