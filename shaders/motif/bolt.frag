#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/sdf.glsl"
#include "common/palette.glsl"

// Forking lightning. Fires on strong onsets when percussivity is high, with a
// fast bloom-heavy decay. Original geometry in the shared idiom — a forked
// bolt is a genre motif, not a protected mark.

uniform float uGain;
uniform float uForkDepth;
uniform float uImpulse;   // host-computed strike envelope 0..1
uniform float uSeed;      // re-rolled per strike so no two bolts repeat
uniform float uPaint;

in vec2 vUv;
out vec4 fragColor;

void main() {
    float im = clamp(uImpulse, 0.0, 1.0);
    if (im <= 0.003 || uGain <= 0.001) { fragColor = vec4(0.0); return; }

    vec2 p = vUv - 0.5;
    p.x *= F_ASPECT;

    float sd = 1e9;
    int NF = int(clamp(uForkDepth, 1.0, 3.0));
    float x0 = (hash11(uSeed * 1.11) - 0.5) * 0.7;
    vec2 a = vec2(x0, 0.60);

    for (int i = 0; i < 10; i++) {
        float f = float(i + 1) / 10.0;
        vec2 b = vec2(a.x + (hash11(uSeed + float(i) * 3.77) - 0.5) * 0.30,
                      0.60 - f * 1.22);
        sd = min(sd, sdSegment(p, a, b));

        if (i > 0 && i < 9 && NF > 1 && hash11(uSeed + float(i) * 7.13) > 0.42) {
            vec2 fb = b + vec2((hash11(uSeed + float(i) * 11.9) - 0.5) * 0.46,
                               -0.20 * hash11(uSeed + float(i) * 5.51));
            sd = min(sd, sdSegment(p, b, fb) * 1.4);
            if (NF > 2 && hash11(uSeed + float(i) * 13.1) > 0.5) {
                vec2 fc = fb + vec2((hash11(uSeed + float(i) * 17.3) - 0.5) * 0.30, -0.13);
                sd = min(sd, sdSegment(p, fb, fc) * 2.0);
            }
        }
        a = b;
    }

    float core = exp(-1100.0 * sd * sd);
    float glow = exp(-38.0 * sd);
    vec3 col = vec3(0.86, 0.94, 1.0) * core * 1.7
             + palChroma(palSpiralT(vUv, 0.86)) * glow * 0.6;

    float env = im * im * (0.4 + 0.9 * F_PERCUSSIVITY);
    fragColor = vec4(col * env * uGain * uPaint * F_INTENSITY * 4.0, 1.0);
}
