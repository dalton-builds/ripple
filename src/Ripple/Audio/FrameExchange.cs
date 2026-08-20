using System.Threading;
using Ripple.Render;

namespace Ripple.Audio;

/// <summary>
/// Single-slot publish of a completed <see cref="FeatureFrame"/> from the analysis thread
/// to the render thread.
///
/// Plan §6: "publishes a completed FeatureFrame to a single-slot double-buffered exchange".
/// The render thread must never block on audio, and the analysis thread must never block on
/// the renderer, so this is a seqlock rather than a lock:
///
///   - the writer bumps a version counter to an ODD value, writes, then bumps it to EVEN
///   - the reader takes the version, copies, and re-reads the version; if it changed or was
///     odd, the copy may be torn, so it retries
///
/// A plain double buffer with a volatile index would be very nearly correct — 93 publishes
/// a second against one read per rendered frame — but "very nearly" here means a reader can
/// occasionally see half of one frame and half of another, and a 224-byte struct torn across
/// two frames produces features that never existed. Beat phase would jump, the safety
/// limiter would see a luminance step. The retry costs a few nanoseconds.
///
/// Readers that lose the race simply retry; there is no waiting and no allocation.
/// </summary>
public sealed class FrameExchange
{
    private FeatureFrame _frame;
    private int _version;

    /// <summary>Frames published since construction. Diagnostic only.</summary>
    public long PublishCount { get; private set; }

    /// <summary>Analysis thread. Never blocks.</summary>
    public void Publish(in FeatureFrame frame)
    {
        Interlocked.Increment(ref _version);        // now odd: a write is in progress
        Thread.MemoryBarrier();
        _frame = frame;
        Thread.MemoryBarrier();
        Interlocked.Increment(ref _version);        // now even: the frame is coherent
        PublishCount++;
    }

    /// <summary>
    /// Render thread. Returns the most recently published frame, or false if nothing has
    /// been published yet.
    /// </summary>
    public bool TryRead(ref FeatureFrame into)
    {
        // Bounded retries: under contention this converges immediately, and a livelock
        // here would stall the renderer, which is the one thing that must not happen.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int before = Volatile.Read(ref _version);
            if (before == 0) return false;              // nothing published yet
            if ((before & 1) != 0) continue;            // write in progress

            Thread.MemoryBarrier();
            var copy = _frame;
            Thread.MemoryBarrier();

            if (Volatile.Read(ref _version) == before)
            {
                into = copy;
                return true;
            }
        }
        return false;
    }
}
