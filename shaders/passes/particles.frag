#include "common/features.glsl"

uniform float uPaint;
in float vA;
in vec3 vC;
out vec4 fragColor;

void main() {
    vec2 d = gl_PointCoord - 0.5;
    float a = exp(-4.6 * dot(d, d) * 4.0);
    fragColor = vec4(vC * a * vA * uPaint * F_INTENSITY * 2.4, 1.0);
}
