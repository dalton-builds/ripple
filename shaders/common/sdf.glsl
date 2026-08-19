// 2D SDF primitives + boolean ops for the motif layer.

float sdCircle(vec2 p, float r) { return length(p) - r; }

float sdSegment(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return length(pa - ba * h);
}

float sdBox(vec2 p, vec2 b) {
    vec2 d = abs(p) - b;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

// Rose / petal lobe: an arc thickened by a cosine profile.
float sdPetal(vec2 p, float k, float len) {
    float a = atan(p.y, p.x);
    float r = length(p);
    return r - len * abs(cos(k * a * 0.5));
}

float opUnion(float a, float b) { return min(a, b); }
float opSub(float a, float b)   { return max(a, -b); }
float opSmoothUnion(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return mix(b, a, h) - k * h * (1.0 - h);
}
