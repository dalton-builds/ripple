#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/palette.glsl"

// Instanced-in-spirit but attribute-cheap: two floats per particle and the
// whole trajectory is evaluated in the vertex shader from curl noise, so the
// CPU does nothing per frame and there is no transform-feedback state.

in vec2 aSeed;
in float aIdx;

uniform float uCurlScale;
uniform float uLifetime;
uniform float uSize;
uniform float uEmit;
uniform float uRise;

out float vA;
out vec3 vC;

void main() {
    float life = max(uLifetime, 0.25);
    float age  = mod(F_TIME * (0.55 + 0.85 * F_PRESENCE) + aSeed.x * life, life);
    float k    = age / life;

    vec2 p0 = aSeed * 2.0 - 1.0;
    p0 += 0.30 * (hash22(vec2(aIdx, aIdx * 1.7)) - 0.5);

    // Advection: two curl samples at different scales, integrated as a
    // quadratic in age. Visually indistinguishable from stepwise integration
    // at this scale and a fraction of the cost.
    vec2 v1 = curl2lo(p0 * uCurlScale + vec2(0.0, F_TIME * 0.06));
    float sw = snoise(p0 * uCurlScale * 2.4 - vec2(F_TIME * 0.045, 0.0));
    vec2 v2 = vec2(-v1.y, v1.x) * sw;
    vec2 p  = p0 + v1 * age * 0.135 + v2 * age * age * 0.045;
    p.y += uRise * age * 0.20;

    float fade = smoothstep(0.0, 0.09, k) * (1.0 - k) * (1.0 - k);
    float burst = F_ONSET * step(hash11(aIdx * 0.37), 0.30);
    vA = fade * uEmit * (0.16 + 0.95 * F_PRESENCE + 1.5 * burst);

    float pt = fract(0.15 + 0.55 * hash11(aIdx * 2.9) + 0.30 * F_KEY_HUE);
    vC = palChroma(palSpiralT(p * 0.5 + 0.5, pt));

    gl_PointSize = max(1.0, uSize * (0.35 + 1.7 * (1.0 - k)) * F_RENDER_SCALE);
    p.x /= max(F_ASPECT, 0.2);
    gl_Position = vec4(p, 0.0, 1.0);
}
