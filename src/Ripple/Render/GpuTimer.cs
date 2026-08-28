using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// GPU time for the last completed frame, via <c>GL_TIME_ELAPSED</c> queries (plan §11:
/// "prefer GL timer queries for true GPU time; fall back to wall clock").
///
/// The distinction matters for the governor. Wall clock around the frame measures when the
/// CPU finished ISSUING commands, and with vsync on that is dominated by the swap wait —
/// so a GPU-bound frame and an idle one can measure the same, and a governor fed that
/// number either never steps down or oscillates on the vsync boundary.
///
/// Queries are read one frame LATE, from a small ring, and only when the driver reports the
/// result available. Blocking on <c>QUERY_RESULT</c> would insert exactly the pipeline stall
/// this class exists to measure.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    /// <summary>Deep enough that a result is always ready without ever waiting.</summary>
    private const int Ring = 4;

    private readonly GL _gl;
    private readonly uint[] _q = new uint[Ring];
    private readonly bool[] _pending = new bool[Ring];
    private int _at;
    private bool _open;

    /// <summary>False when the driver would not give us queries; the caller uses wall clock.</summary>
    public bool Available { get; private set; }

    /// <summary>Last GPU time actually read back, in milliseconds. 0 until one lands.</summary>
    public double LastMs { get; private set; }

    /// <summary>How many results have been read. Zero after a while means fall back.</summary>
    public long Samples { get; private set; }

    public GpuTimer(GL gl)
    {
        _gl = gl;
        try
        {
            for (int i = 0; i < Ring; i++) _q[i] = _gl.GenQuery();
            // A GenQuery that returns 0 is not a usable name; some software rasterizers
            // advertise the entry point and hand back nothing.
            Available = Array.TrueForAll(_q, id => id != 0);
        }
        catch (Exception)
        {
            Available = false;
        }
    }

    /// <summary>Open a query around the frame. No-op when unavailable.</summary>
    public void Begin()
    {
        if (!Available || _open) return;
        try
        {
            _gl.BeginQuery(QueryTarget.TimeElapsed, _q[_at]);
            _open = true;
        }
        catch (Exception)
        {
            Available = false;
        }
    }

    /// <summary>
    /// Close the frame's query and collect whichever earlier ones have completed. Returns
    /// the freshest GPU time in ms, or -1 if nothing has been read back yet.
    /// </summary>
    public double End()
    {
        if (!Available) return -1;

        if (_open)
        {
            try
            {
                _gl.EndQuery(QueryTarget.TimeElapsed);
                _pending[_at] = true;
                _at = (_at + 1) % Ring;
            }
            catch (Exception)
            {
                Available = false;
                return -1;
            }
            _open = false;
        }

        // Walk the ring oldest-first and drain everything ready. Never wait: an unavailable
        // result is simply read next frame.
        for (int n = 0; n < Ring; n++)
        {
            int i = (_at + n) % Ring;
            if (!_pending[i]) continue;
            try
            {
                _gl.GetQueryObject(_q[i], QueryObjectParameterName.ResultAvailable,
                                   out uint ready);
                if (ready == 0) continue;
                _gl.GetQueryObject(_q[i], QueryObjectParameterName.Result, out ulong ns);
                LastMs = ns / 1_000_000.0;
                Samples++;
                _pending[i] = false;
            }
            catch (Exception)
            {
                Available = false;
                return -1;
            }
        }

        return Samples > 0 ? LastMs : -1;
    }

    public void Dispose()
    {
        if (!Available) return;
        try
        {
            if (_open) { _gl.EndQuery(QueryTarget.TimeElapsed); _open = false; }
            foreach (uint id in _q) if (id != 0) _gl.DeleteQuery(id);
        }
        catch (Exception)
        {
            // Teardown only; a driver refusing to delete a query cannot matter here.
        }
        Available = false;
    }
}
