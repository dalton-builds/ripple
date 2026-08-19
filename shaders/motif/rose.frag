#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/sdf.glsl"
#include "common/palette.glsl"

// Phyllotaxis rose. Opens on chromaFlux spikes — chord changes bloom roses.
// The single most legible music -> image mapping in the design, so it gets
// real attention: a filled corolla, a drawn edge, and a golden-angle seed head.

uniform float uGain;
uniform float uPetals;
uniform float uBloom;    // host-computed 0..1 envelope (attack on chromaFlux)
uniform float uSeed;
uniform float uPaint;

in vec2 vUv;
out vec4 fragColor;

void main() {
    float b = clamp(uBloom, 0.0, 1.0);
    if (b <= 0.002 || uGain <= 0.001) { fragColor = vec4(0.0); return; }

    vec2 p = vUv - 0.5;
    p.x *= F_ASPECT;
    // roses drift, they do not sit dead centre
    p -= 0.16 * vec2(sin(uSeed * 3.1 + F_TIME * 0.05), cos(uSeed * 5.7 + F_TIME * 0.037));
    p = rot2(0.10 * F_TIME + b * 0.75) * p;

    float k = max(uPetals, 3.0);
    float r = length(p);
    float open = 0.055 + 0.34 * b;

    float edge = sdPetal(p, k, open * (0.55 + 0.45 * b));
    float body = smoothstep(0.045, -0.01, edge);
    float line = exp(-140.0 * abs(edge));

    // golden-angle seed head, only evaluated inside the corolla
    float seeds = 0.0;
    if (r < open * 1.15) {
        for (int i = 0; i < 22; i++) {
            float fi = float(i);
            float ra = fi * 2.39996323;
            float rr = sqrt(fi / 22.0) * open * 0.78;
            vec2 sp = vec2(cos(ra), sin(ra)) * rr;
            seeds += exp(-2600.0 * dot(p - sp, p - sp));
        }
    }

    float t = palSpiralT(vUv, fract(0.58 + 0.22 * abs(cos(k * atan(p.y, p.x) * 0.5)) + 0.28 * F_KEY_HUE));
    vec3 col = palChroma(t) * (body * 0.30 + line * 1.15)
             + vec3(1.0, 0.88, 0.70) * seeds * 0.55;

    col *= b * b * uGain * (0.6 + 0.7 * F_MID);
    fragColor = vec4(col * uPaint * F_INTENSITY * 3.2, 1.0);
}
