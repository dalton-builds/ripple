#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/palette.glsl"

// Julia set with c travelling a closed path. Path radius <- dynamics,
// position along the path <- beat phase. Morphing c on the beat is the
// classic mapping and still the best one.

uniform float uIter;
uniform float uEscape;
uniform float uOrbitR;
uniform float uBeatMorph;
uniform float uTrapGain;
uniform float uPaint;

in vec2 vUv;
out vec4 fragColor;

void main() {
    vec2 uv = vUv;
    vec2 z = (uv - 0.5) * 2.0;
    z.x *= F_ASPECT;
    z *= 1.38 - 0.26 * F_SUB;
    z = rot2(0.012 * F_TIME + 0.7 * F_STEREO_WIDTH) * z;

    float ph = 6.28318531 * (F_BEAT_PHASE * uBeatMorph + F_TIME * 0.014);

    // |c| is held near 0.79 — the boundary of the Mandelbrot set — because that
    // is where the Julia set is a connected filigree. An orbit that wanders
    // freely spends a good third of its time deep INSIDE the cardioid, where
    // the set is solid: every point stays bounded, escape time saturates, and
    // the frame goes black. Path radius still reads dynamics, position still
    // reads beat phase; the radius is just bounded to the interesting annulus.
    float R = clamp(0.58 + 0.75 * uOrbitR * (0.80 + 0.40 * F_DYNAMICS), 0.60, 0.88);
    vec2 c = R * vec2(cos(ph), sin(ph * 1.06));

    int N = int(clamp(uIter, 8.0, 160.0));
    float i = 0.0, trap = 0.0, minr = 1e9;
    for (int k = 0; k < 160; k++) {
        if (k >= N) break;
        z = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
        float r2 = dot(z, z);
        trap += exp(-3.4 * abs(sqrt(r2) - 0.88));   // orbit trap -> filigree
        minr = min(minr, r2);
        if (r2 > uEscape * uEscape) break;
        i += 1.0;
    }

    float sm    = i / float(N);
    float trapN = trap / float(N);              // iteration-count independent
    float inside = step(0.999, sm);
    float t  = palSpiralT(uv, fract(sm * 2.1 + 0.30 * trapN + 0.18 * F_CENTROID));

    vec3 col = palChroma(t) * (0.12 + 1.15 * pow(1.0 - sm, 2.4));
    // The trap carries the interior: a connected Julia reads as lace instead of
    // as a hole, so the look survives every position on the orbit.
    col += palChroma(t + 0.13) * trapN * (0.35 + 0.75 * inside) * uTrapGain * (0.45 + F_HIMID);
    col *= 0.55 + 0.65 * smoothstep(0.0, 0.30, sqrt(minr));

    fragColor = vec4(col * uPaint * F_INTENSITY * (0.42 + 0.95 * F_RMS), 1.0);
}
