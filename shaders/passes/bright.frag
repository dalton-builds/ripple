uniform sampler2D uSrc;
uniform float uThreshold;
uniform float uKnee;
in vec2 vUv;
out vec4 fragColor;
void main() {
    vec3 c = texture(uSrc, vUv).rgb;
    float l = max(max(c.r, c.g), c.b);
    float s = clamp((l - uThreshold) / max(uKnee, 1e-3), 0.0, 1.0);
    fragColor = vec4(c * s * s, 1.0);
}
