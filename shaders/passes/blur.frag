// Separable blur, 1-3 taps per side. Plan 8.1 explicitly wants a 2-tap
// down/up rather than a full pyramid — the bloom is an accent, not the look.
uniform sampler2D uSrc;
uniform vec2 uDir;
uniform float uTaps;
in vec2 vUv;
out vec4 fragColor;
void main() {
    vec3 s = texture(uSrc, vUv).rgb * 0.34;
    float w = 0.34;
    s += (texture(uSrc, vUv + uDir).rgb + texture(uSrc, vUv - uDir).rgb) * 0.24;
    w += 0.48;
    if (uTaps > 1.5) {
        s += (texture(uSrc, vUv + uDir * 2.6).rgb + texture(uSrc, vUv - uDir * 2.6).rgb) * 0.12;
        w += 0.24;
    }
    if (uTaps > 2.5) {
        s += (texture(uSrc, vUv + uDir * 5.1).rgb + texture(uSrc, vUv - uDir * 5.1).rgb) * 0.06;
        w += 0.12;
    }
    fragColor = vec4(s / w, 1.0);
}
