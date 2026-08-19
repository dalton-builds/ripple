#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/palette.glsl"

// Apollonian gasket by inversive folding. Psychedelic lace, strongly
// geometric — excellent contrast against warp_fbm's softness.

uniform float uIter;
uniform float uFold;
uniform float uLine;
uniform float uPaint;

in vec2 vUv;
out vec4 fragColor;

void main() {
    vec2 uv = vUv;
    vec2 p = (uv - 0.5) * (2.7 - 0.35 * F_SUB);
    p.x *= F_ASPECT;
    p = rot2(0.018 * F_TIME + 0.35 * F_BAR_PHASE) * p;

    float s = 1.0, k = 0.0;
    int N = int(clamp(uIter * 0.22, 3.0, 22.0));
    for (int i = 0; i < 22; i++) {
        if (i >= N) break;
        p = -1.0 + 2.0 * fract(0.5 * p + 0.5);
        float r2 = max(dot(p, p), 1e-4);
        float kk = (uFold * (1.0 + 0.35 * F_BASS)) / r2;
        p *= kk;
        s *= kk;
        k += 1.0 / r2;
    }

    float d = abs(p.y) / max(s, 1e-4);
    float lace = exp(-uLine * 130.0 * d);
    float halo = exp(-uLine * 26.0 * d);

    float t = palSpiralT(uv, fract(0.14 * log(1.0 + k) + 0.28 * F_CENTROID + 0.2 * F_KEY_HUE));
    vec3 col = palChroma(t) * lace * (1.1 + 2.0 * F_HIMID);
    col += palChroma(t + 0.28) * halo * 0.45;

    fragColor = vec4(col * uPaint * F_INTENSITY * (0.35 + 1.0 * F_RMS), 1.0);
}
