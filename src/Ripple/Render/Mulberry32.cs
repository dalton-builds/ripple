namespace Ripple.Render;

/// <summary>
/// mulberry32, bit-identical to the harness implementation in
/// <c>harness/index.html</c>.
///
/// Anything that seeds geometry — particle spawn points, motif seeds — has to
/// be reproducible across hosts or the Phase 1 parity gate is a coin toss
/// rather than a test. JS does this arithmetic on Numbers coerced to uint32 by
/// <c>&gt;&gt;&gt;0</c> and <c>Math.imul</c>; unchecked uint arithmetic here has
/// exactly the same bit pattern at every step, and the same 2^-32 scaling.
///
/// If you change the algorithm or either seed constant, change the harness in
/// the same commit — otherwise the two hosts silently diverge and the SSIM
/// report is the only thing that will tell you.
/// </summary>
public struct Mulberry32(uint seed)
{
    private uint _a = seed;

    /// <summary>Particle spawn seeds. SHARED CONSTANT — mirrored in the harness.</summary>
    public const uint ParticleSeed = 0x9E3779B9;

    /// <summary>Motif (rose/bolt) seeds. SHARED CONSTANT — mirrored in the harness.</summary>
    public const uint MotifSeed = 0x1B873593;

    /// <summary>Next value in [0,1). Double, because JS divides by 2^32 in double.</summary>
    public double NextDouble()
    {
        unchecked
        {
            _a += 0x6D2B79F5u;
            uint a = _a;
            uint t = (a ^ (a >> 15)) * (1u | a);
            t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>Next value in [0,1) narrowed to float, matching a Float32Array store.</summary>
    public float NextFloat() => (float)NextDouble();
}
