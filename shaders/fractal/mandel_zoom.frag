#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/palette.glsl"

// Slow autonomous zoom with a HARD bounded depth and a period reset, so it
// never leaves fp32 precision. No perturbation theory here on purpose.

uniform float uIter;
uniform float uEscape;
uniform float uZoomPeriod;   // seconds per zoom cycle
uniform float uZoomDepth;    // ln(max magnification)
uniform float uPaint;

in vec2 vUv;
out vec4 fragColor;

void main() {
    vec2 uv = vUv;
    float ph   = fract(F_TIME / max(uZoomPeriod, 8.0));
    float zoom = exp(ph * clamp(uZoomDepth, 1.0, 9.0));
    float fade = smoothstep(0.0, 0.07, ph) * smoothstep(0.0, 0.12, 1.0 - ph);

    vec2 target = vec2(-0.743643887, 0.131825904);
    vec2 c = (uv - 0.5) * 3.0;
    c.x *= F_ASPECT;
    c = rot2(0.05 * F_TIME * 0.1) * c / zoom + target;

    vec2 z = vec2(0.0);
    int N = int(clamp(uIter, 24.0, 200.0));
    float i = 0.0;
    for (int k = 0; k < 200; k++) {
        if (k >= N) break;
        z = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
        if (dot(z, z) > uEscape * uEscape) break;
        i += 1.0;
    }

    float sm = i / float(N);
    // escape-time coloured by the chroma vector: the pitch class the current
    // band lands on picks the stop, so a key change recolours the shells.
    int pc = int(mod(floor(sm * 24.0), 12.0));
    float t = palSpiralT(uv, fract(sm * 3.1 + 0.30 * chroma(pc) + 0.16 * F_CENTROID));

    vec3 col = palChroma(t) * (0.14 + 1.5 * pow(1.0 - sm, 2.6));
    col += palChroma(t + 0.2) * pow(sm, 6.0) * 0.5 * F_BRILLIANCE;

    fragColor = vec4(col * fade * uPaint * F_INTENSITY * (0.4 + 0.95 * F_RMS), 1.0);
}
