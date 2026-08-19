#include "common/features.glsl"
#include "common/noise.glsl"

// PASS A — feedback displacement. This is the MilkDrop core and the reason the
// whole pipeline is cheap: the image accumulates complexity over time instead
// of being recomputed. Decay is the highest-value parameter in the system.

uniform sampler2D uPrev;
uniform float uDecay;        // 0.94 .. 0.999
uniform float uZoom;
uniform float uRotate;
uniform float uWarpAmount;
uniform float uWarpScale;
uniform float uBassGain;
uniform float uSymmetry;     // 0 = off; > 0.5 folds into 2N sectors
uniform float uOnsetDecay;   // onset-driven decay dip
uniform float uCenterPull;

in vec2 vUv;
out vec4 fragColor;

void main() {
    vec2 c = vUv - 0.5;
    c.x *= F_ASPECT;

    // Kaleidoscopic fold. Routed from stereo width by default, which gives
    // free era differentiation: a 1969 two-track and the Wall of Sound image
    // very differently for zero extra cost.
    if (uSymmetry > 0.5) {
        float sectors = floor(2.0 + uSymmetry * 5.0) * 2.0;
        float seg = 6.28318531 / sectors;
        float a = atan(c.y, c.x);
        float r = length(c);
        a = abs(mod(a + seg * 0.5, seg) - seg * 0.5);
        c = vec2(cos(a), sin(a)) * r;
    }

    float t = F_TIME;
    float bass = F_BASS * uBassGain;

    float ang  = uRotate * (1.0 + 0.9 * F_STEREO_WIDTH) + uRotate * 0.5 * sin(t * 0.11);
    float zoom = uZoom + F_SUB * 0.011 * uBassGain + beatPulse(3.0) * 0.0018 * F_BPM_CONF;
    c = rot2(ang) * c / max(zoom, 0.5);

    // fBm domain warp of the *lookup*, not of the paint. Two fields plus a
    // curl term: the curl is what stops the flow reading as a scrolling texture.
    vec2 q = c * uWarpScale;
    vec2 d = vec2(fbm(q + vec2(0.0, t * 0.09), 3, 2.03, 0.55),
                  fbm(q + vec2(11.7, -3.1) - vec2(t * 0.07, 0.0), 3, 2.03, 0.55));
    d += 0.30 * curl2lo(q * 0.55 + vec2(t * 0.04, 0.0));
    c += d * (0.008 + uWarpAmount * 0.030) * (0.40 + 0.95 * bass);

    c -= c * uCenterPull * 0.005;

    c.x /= F_ASPECT;
    vec2 uv = c + 0.5;

    float decay = uDecay - uOnsetDecay * 0.05 * F_ONSET;

    vec3 prev = texture(uPrev, uv).rgb;

    // Edge falloff. Without it the warp drags the border colour inward and the
    // frame slowly fills with a single hue.
    vec2 e = smoothstep(vec2(0.0), vec2(0.04), uv)
           * smoothstep(vec2(0.0), vec2(0.04), 1.0 - uv);
    prev *= e.x * e.y;

    fragColor = vec4(prev * clamp(decay, 0.0, 0.9995), 1.0);
}
