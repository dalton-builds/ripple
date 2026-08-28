#include "common/features.glsl"
#include "common/noise.glsl"
#include "common/safety.glsl"

// PASS C — tonemap, bloom, grain, vignette, safety clamp. Full resolution.

uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uExposure;
uniform float uBloomGain;
uniform float uGrain;
uniform float uVignette;
uniform float uSaturate;  // additive accumulation greys out; this pays it back
uniform float uIdle;      // 1 = live, 0.3 = idle dim (plan 11)

// Master intensity (plan 12), hotkey-adjustable, 0.05..1.0. Applied BEFORE
// rippleSafety so the limiter still sees and clamps the final picture: an
// intensity applied after the clamp would be a route around the ceiling, and
// this control sits INSIDE the safety envelope rather than beside it.
uniform float uIntensity;

in vec2 vUv;
out vec4 fragColor;

void main() {
    vec3 c = texture(uScene, vUv).rgb;
    c += texture(uBloom, vUv).rgb * uBloomGain;
    c *= uExposure * mix(0.30, 1.0, clamp(uIdle, 0.0, 1.0));

    // filmic curve — keeps the additive feedback buffer from clipping to white
    c = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14);

    // Many additive layers of different hues average toward grey, and the
    // filmic knee desaturates further. Pull it back after the curve, not
    // before, so the highlights keep their pigment.
    c = max(vec3(0.0), mix(vec3(dot(c, vec3(0.2126, 0.7152, 0.0722))), c, uSaturate));

    float g = hash12(vUv * vec2(1913.0, 1487.0) + fract(F_TIME * 3.7) * 137.0) - 0.5;
    c += g * uGrain * (0.35 + 0.75 * F_AIR);

    vec2 q = vUv - 0.5;
    q.x *= F_ASPECT;
    c *= 1.0 - uVignette * smoothstep(0.26, 0.88, length(q));

    c *= clamp(uIntensity, 0.0, 1.0);

    fragColor = vec4(rippleSafety(c), 1.0);
}
