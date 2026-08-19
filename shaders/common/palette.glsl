// Palette lookup. The preset's colour stops are baked into a 256x1 LUT on the
// host so the shader cost is one texture fetch. Requires common/features.glsl.

uniform sampler2D uPalette;
uniform float uPalRot;        // rotation of the LUT (keyHue routing lands here)
uniform float uChromaBlend;   // preset palette.chromaBlend
uniform float uSpiral;        // tie-dye spiral amount (plan 8.3)
uniform float uSpiralSpin;    // host-advanced phase, BPM/4

vec3 rp_hsv2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

vec3 palSample(float t) {
    return texture(uPalette, vec2(fract(t + uPalRot), 0.5)).rgb;
}

// Respond to the detected key by ROTATING the palette, not by blending toward
// a raw HSV hue. Tinting toward an arbitrary hue drags colours into the frame
// that the preset never declared — a blue-green wash over a magenta/amber
// scheme — and it is the fastest way to make a carefully chosen palette read
// as mud. Rotating stays inside the ramp, so a modulation reads as a shift
// *within* the colour scheme. Weighted by key confidence, so an unconfident
// read never yanks the scheme around.
vec3 palChroma(float t) {
    float w = clamp(uChromaBlend * (0.35 + 0.65 * F_KEY_CONF), 0.0, 1.0);
    return mix(palSample(t), palSample(t + F_KEY_HUE * 0.5), w);
}

// The tie-dye spiral is applied to the palette LOOKUP, never to geometry.
// Cheap, and it does more for period authenticity than any amount of mesh.
float palSpiralT(vec2 uv, float t) {
    vec2 c = uv - 0.5;
    c.x *= F_ASPECT;
    float r = length(c);
    float a = atan(c.y, c.x);
    return t + uSpiral * (a * 0.15915494 + r * 1.35 - uSpiralSpin);
}
