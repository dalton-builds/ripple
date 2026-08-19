// Photosensitivity limiter — plan 12. Applied UNCONDITIONALLY in composite.frag.
//
// A preset may make this STRICTER but never looser: the host clamps the preset
// values against the ceilings below before upload, so there is no code path in
// which a preset relaxes the limiter. These two constants are the floor.

#define RIPPLE_SAFETY_MAX_DELTA_CEIL   0.12   // max mean-luminance step / frame
#define RIPPLE_SAFETY_MAX_BRIGHT_CEIL  0.92   // never full white

uniform float uSafetyScale;   // host-computed rate-limit gain, <= 1.0
uniform float uMaxBright;     // min(preset.safety.maxBrightness, CEIL)
uniform float uHueRate;       // |d(paletteRotate)/dt|, drives the R<->B guard

float rpLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

vec3 rippleSafety(vec3 col) {
    // 1. luminance rate cap — the gain is closed-loop on the host from the
    //    previous frame's measured mean luminance.
    col *= clamp(uSafetyScale, 0.0, 1.0);

    // 2. brightness ceiling.
    float mb = min(uMaxBright, RIPPLE_SAFETY_MAX_BRIGHT_CEIL);
    float l = rpLuma(col);
    if (l > mb) col *= mb / max(l, 1e-5);

    // 3. saturated red<->blue alternation is the highest-risk pattern. As the
    //    palette rotation rate climbs, bleed saturation out of the R/B axis so
    //    the interpolation path cannot swing between those poles at speed.
    float rb   = abs(col.r - col.b) / max(max(col.r, col.b), 1e-5);
    float risk = clamp(uHueRate * 7.0, 0.0, 1.0) * rb;
    col = mix(col, vec3(rpLuma(col)), risk * 0.55);

    return clamp(col, vec3(0.0), vec3(mb));
}
