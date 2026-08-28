namespace Ripple.Render;

/// <summary>
/// Plan §11's idle behaviour — the part that makes an 8-hour run acceptable rather than
/// merely possible.
///
/// Three states, and they are not the same thing:
///
///  LIVE     audio is playing; render at the display rate.
///  QUIET    RMS has been below −50 dBFS for 3 s. Drop to 10 fps, freeze the feedback
///           decay so the buffer stops evolving, and dim to 30%. The window is still
///           visible, so it must still be a picture — just a still, dim one.
///  HIDDEN   minimized or fully occluded. Stop rendering ENTIRELY and block on the event
///           loop. Explicitly not "1 fps": a 1 fps loop still wakes the CPU, still takes
///           the GL context, and still shows up in a power profile, which is exactly what
///           someone leaving this running all day would notice.
///
/// Coming back is RAMPED over 500 ms rather than switched, because a hard cut from 30% to
/// full on the first loud frame is itself a luminance step — the safety limiter would catch
/// it and clamp, so the visible result of not ramping is a lurch followed by a dip.
/// </summary>
public sealed class IdleController
{
    public enum State { Live, Quiet, Hidden }

    /// <summary>§11: 10 fps while quiet.</summary>
    private const double QuietFps = 10.0;

    /// <summary>§11: dim to 30%.</summary>
    private const double QuietDim = 0.30;

    /// <summary>§11: ramp back over 500 ms.</summary>
    private const double RampSeconds = 0.5;

    public State Current { get; private set; } = State.Live;

    /// <summary>
    /// 1.0 live, <see cref="QuietDim"/> fully quiet, interpolated while ramping. This is
    /// the <c>uIdle</c> uniform composite.frag already takes.
    /// </summary>
    public double Dim { get; private set; } = 1.0;

    /// <summary>True while the feedback buffer should stop evolving (§11: freeze decay).</summary>
    public bool FreezeFeedback => Current == State.Quiet && Dim <= QuietDim + 0.01;

    /// <summary>Seconds the caller should wait before drawing again. 0 means draw now.</summary>
    public double SleepSeconds { get; private set; }

    public long QuietFrames { get; private set; }
    public long HiddenFrames { get; private set; }

    private double _sinceDraw;

    /// <summary>
    /// Decide what this frame should do.
    /// </summary>
    /// <param name="dt">Wall seconds since the last call.</param>
    /// <param name="audioIdle">
    /// AudioEngine.Idle — already −50 dBFS for 3 s, so the threshold and the hold live in
    /// one place rather than being restated here.
    /// </param>
    /// <param name="visible">False when minimized or fully occluded.</param>
    /// <returns>True if this frame should be rendered.</returns>
    public bool ShouldRender(double dt, bool audioIdle, bool visible)
    {
        Current = !visible ? State.Hidden : audioIdle ? State.Quiet : State.Live;

        if (Current == State.Hidden)
        {
            HiddenFrames++;
            SleepSeconds = 0;      // the caller blocks on the event loop, it does not sleep
            return false;
        }

        // Ramp the dim toward the target. Down is immediate-ish (a 1 s fall keeps the
        // transition into quiet from being its own visual event); up is the specified 500 ms.
        double target = Current == State.Quiet ? QuietDim : 1.0;
        double tau = target < Dim ? 1.0 : RampSeconds / 3.0;   // 3 tau ~ 95% of the step
        Dim += (target - Dim) * (1 - Math.Exp(-dt / Math.Max(tau, 1e-4)));
        if (Math.Abs(Dim - target) < 0.002) Dim = target;

        if (Current == State.Live)
        {
            SleepSeconds = 0;
            _sinceDraw = 0;
            return true;
        }

        // QUIET: rate-limit to 10 fps. Keep rendering at full rate until the ramp has
        // actually arrived, or the dip to 30% happens in 10 fps steps and reads as a stutter
        // rather than as a fade.
        QuietFrames++;
        if (Dim > QuietDim + 0.01)
        {
            SleepSeconds = 0;
            return true;
        }

        _sinceDraw += dt;
        double period = 1.0 / QuietFps;
        if (_sinceDraw < period)
        {
            SleepSeconds = period - _sinceDraw;
            return false;
        }
        _sinceDraw = 0;
        SleepSeconds = 0;
        return true;
    }

    public void Reset()
    {
        Current = State.Live;
        Dim = 1.0;
        SleepSeconds = 0;
        _sinceDraw = 0;
        QuietFrames = HiddenFrames = 0;
    }
}
