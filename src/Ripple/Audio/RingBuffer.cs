using System.Runtime.CompilerServices;

namespace Ripple.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer float ring.
///
/// Plan §6's threading model: the audio callback thread writes here and does nothing
/// else — no allocation, no logging, no locks — and the analysis thread reads. That is
/// the whole reason this type exists. Anything else on the callback thread risks a glitch
/// in whatever else the machine is playing, and the app is supposed to be invisible while
/// running under a real workload.
///
/// Correctness rests on STRICT single ownership: the producer is the only writer of
/// <see cref="_write"/>, the consumer the only writer of <see cref="_read"/>. Neither ever
/// writes the other's index. Both are monotonic longs masked into the array, so there is
/// no ambiguous full-versus-empty state and no CAS anywhere.
///
/// That ownership rule is not stylistic. An earlier version had the producer advance
/// _read to "make room" on overflow, which let the consumer — holding a stale local copy —
/// publish an index that moved BACKWARDS, and the reader then saw a sample it had already
/// consumed. The SPSC test caught it. Overrun is therefore the CONSUMER's business: it
/// notices that the producer has lapped it and skips forward.
///
/// On overrun the OLDEST samples are dropped. The alternative — dropping the newest —
/// would make the analyzer fall progressively further behind the audio it is describing,
/// which is worse than a gap: late features look like a bug in the visuals.
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _buf;
    private readonly int _mask;

    // Written by the producer, read by the consumer, and vice versa. C# will not allow
    // `volatile` on a long, so the ordering comes from explicit Volatile.Read/Write —
    // which is what orders the data writes against the index publish. On 64-bit these are
    // also atomic loads and stores, so a reader can never observe a torn index.
    private long _write;
    private long _read;

    private long _dropped;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        int cap = 1;
        while (cap < capacity) cap <<= 1;      // power of two, so the mask works
        _buf = new float[cap];
        _mask = cap - 1;
    }

    public int Capacity => _buf.Length;

    /// <summary>
    /// Samples available to read right now, saturated at <see cref="Capacity"/> — the
    /// producer's index can legitimately be further ahead than that, but only the last
    /// Capacity samples still exist.
    /// </summary>
    public int Count => (int)Math.Min(
        Volatile.Read(ref _write) - Volatile.Read(ref _read), _buf.Length);

    /// <summary>Total samples ever written, including ones later dropped.</summary>
    public long TotalWritten => Volatile.Read(ref _write);

    /// <summary>
    /// Samples lost to overflow. Nonzero means the analysis thread is not keeping up, and
    /// the diagnostic HUD surfaces it rather than letting it pass silently.
    /// </summary>
    public long TotalDropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Producer side. Never blocks, never allocates.
    /// </summary>
    public void Write(ReadOnlySpan<float> src)
    {
        if (src.Length == 0) return;

        // A write longer than the whole buffer can only keep its tail, so skip the front
        // rather than copying data we are about to overwrite.
        if (src.Length > _buf.Length)
        {
            Interlocked.Add(ref _dropped, src.Length - _buf.Length);
            src = src[^_buf.Length..];
        }

        // The producer never inspects or advances _read: it writes, publishes, and moves
        // on. If that overwrites samples the consumer had not reached, the consumer finds
        // out on its next Read and skips forward.
        long w = _write;
        int start = (int)(w & _mask);
        int first = Math.Min(src.Length, _buf.Length - start);
        src[..first].CopyTo(_buf.AsSpan(start));
        if (first < src.Length) src[first..].CopyTo(_buf.AsSpan(0));

        Volatile.Write(ref _write, w + src.Length);   // publish last: data is in place first
    }

    /// <summary>
    /// Consumer side. Returns how many samples were copied, up to
    /// <paramref name="dst"/>.Length.
    /// </summary>
    public int Read(Span<float> dst)
    {
        long r = _read;                        // consumer owns this one outright
        long w = Volatile.Read(ref _write);
        long avail = w - r;
        if (avail <= 0 || dst.Length == 0) return 0;

        // Overrun: the producer has lapped us, so the oldest (w - Capacity) samples are
        // already gone. Skip forward and count what was lost.
        //
        // Resync to half a buffer behind rather than to the oldest surviving sample. At
        // the very edge the producer is actively overwriting the region we would copy, and
        // a copy that races it returns a MIX of new and old samples — not a gap but
        // garbage, with a discontinuity in the middle. The SPSC test caught exactly that:
        // the reader saw 199988 followed by 195892.
        //
        // Half a buffer of headroom means the producer would have to write 8192 samples
        // during a single memcpy for a read to tear. That is a mitigation, not a proof:
        // the guarantee is that we are never reading within a hair of the write cursor.
        // Overrun means the analysis thread is not keeping up, which TotalDropped reports.
        if (avail > _buf.Length)
        {
            long target = w - _buf.Length / 2;
            Interlocked.Add(ref _dropped, target - r);
            r = target;
            avail = _buf.Length / 2;
        }

        // Never read more than half the buffer at once, for the same reason.
        int n = (int)Math.Min(Math.Min(avail, dst.Length), _buf.Length / 2);
        int start = (int)(r & _mask);
        int first = Math.Min(n, _buf.Length - start);
        _buf.AsSpan(start, first).CopyTo(dst);
        if (first < n) _buf.AsSpan(0, n - first).CopyTo(dst[first..]);

        Volatile.Write(ref _read, r + n);
        return n;
    }

    /// <summary>Discard everything buffered. Consumer side only.</summary>
    public void Clear() => Volatile.Write(ref _read, Volatile.Read(ref _write));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAtLeast(int samples) => Count >= samples;
}
