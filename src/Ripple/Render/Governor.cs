namespace Ripple.Render;

/// <summary>
/// Adaptive quality (plan §11). Phase 1 ships the dial table and the clamp;
/// the closed control loop lands in Phase 4, where it gets the automated budget
/// test that makes it meaningful.
///
/// The dials are pulled in the plan's order — render scale first, because it is
/// the largest and cheapest win — and tier 0 is the identity, so a parity run
/// pinned to tier 0 is unaffected by anything in this file.
/// </summary>
public sealed class Governor
{
    public readonly record struct Tier(double Scale, double Particles, int Octaves, int BloomTaps);

    public static readonly Tier[] Tiers =
    [
        new(0.75, 1.00, 0, 0),
        new(0.60, 0.55, -1, 0),
        new(0.50, 0.28, -1, -1),
        new(0.40, 0.12, -2, -2),
        new(0.30, 0.00, -3, -3),
        new(0.22, 0.00, -4, -3),
    ];

    /// <summary>Auto tiering is off until Phase 4 brings its control loop and hysteresis.</summary>
    public bool Auto { get; set; }
    public int Level { get; private set; }
    public bool Panic { get; private set; }

    public Tier Current => Tiers[Level];
    public double RenderScale => Tiers[Level].Scale;

    /// <summary>Pin to a tier. A parity run pins to 0 and never moves.</summary>
    public void SetLevel(int level)
    {
        Level = Math.Clamp(level, 0, Tiers.Length - 1);
        Panic = false;
    }

    /// <summary>
    /// CONTRACTS §4: the governor clamp is last and absolute. A preset cannot
    /// ask for 64k particles and win against a machine that cannot afford them.
    /// </summary>
    public void Apply(DrawState d)
    {
        var t = Tiers[Level];
        d.Octaves = Math.Max(2, d.Octaves + t.Octaves);
        d.PCount *= t.Particles;
        d.BloomTaps = Math.Max(0, d.BloomTaps + t.BloomTaps);
        if (!Panic) return;
        d.PCount = 0;
        d.BloomTaps = 0;
        d.BloomGain *= 0.5;
        d.Octaves = 2;
    }
}
