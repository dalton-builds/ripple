// Simplex, fBm, curl and hashes. Shared by every paint pass.
// Cost matters here: this file is the single largest GPU expense in the
// pipeline, so every consumer passes an explicit octave count.

vec3 rp_permute(vec3 x) { return mod(((x * 34.0) + 1.0) * x, 289.0); }

// 2D simplex, roughly -1..1.
float snoise(vec2 v) {
    const vec4 C = vec4(0.211324865405187, 0.366025403784439,
                       -0.577350269189626, 0.024390243902439);
    vec2 i  = floor(v + dot(v, C.yy));
    vec2 x0 = v - i + dot(i, C.xx);
    vec2 i1 = (x0.x > x0.y) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
    vec4 x12 = x0.xyxy + C.xxzz;
    x12.xy -= i1;
    i = mod(i, 289.0);
    vec3 p = rp_permute(rp_permute(i.y + vec3(0.0, i1.y, 1.0))
                        + i.x + vec3(0.0, i1.x, 1.0));
    vec3 m = max(0.5 - vec3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m = m * m; m = m * m;
    vec3 x  = 2.0 * fract(p * C.www) - 1.0;
    vec3 h  = abs(x) - 0.5;
    vec3 ox = floor(x + 0.5);
    vec3 a0 = x - ox;
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
    vec3 g;
    g.x  = a0.x * x0.x + h.x * x0.y;
    g.yz = a0.yz * x12.xz + h.yz * x12.yw;
    return 130.0 * dot(m, g);
}

// Normalized fBm, -1..1. oct is clamped to 8 by the fixed loop bound so the
// compiler can still unroll.
float fbm(vec2 p, int oct, float lac, float gain) {
    float a = 0.5, s = 0.0, n = 0.0;
    for (int i = 0; i < 6; i++) {
        if (i >= oct) break;
        s += a * snoise(p);
        n += a;
        p *= lac;
        a *= gain;
    }
    return s / max(n, 1e-4);
}
float fbm2(vec2 p) { return fbm(p, 5, 2.02, 0.52); }

// Cheap curl for particle advection and warp jitter. Two octaves, 4 taps —
// this is called from a vertex shader at up to 64k invocations, so it is
// deliberately the coarsest thing in the file.
vec2 curl2lo(vec2 p) {
    const float e = 0.11;
    float n1 = snoise(p + vec2(0.0, e)) + 0.5 * snoise((p + vec2(0.0, e)) * 2.1);
    float n2 = snoise(p - vec2(0.0, e)) + 0.5 * snoise((p - vec2(0.0, e)) * 2.1);
    float n3 = snoise(p + vec2(e, 0.0)) + 0.5 * snoise((p + vec2(e, 0.0)) * 2.1);
    float n4 = snoise(p - vec2(e, 0.0)) + 0.5 * snoise((p - vec2(e, 0.0)) * 2.1);
    return vec2(n1 - n2, n4 - n3) / (3.0 * e);
}

float hash11(float p) {
    p = fract(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return fract(p);
}
float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}
vec2 hash22(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.xx + p3.yz) * p3.zy);
}
mat2 rot2(float a) { float s = sin(a), c = cos(a); return mat2(c, -s, s, c); }
