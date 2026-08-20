using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ripple.Audio;

/// <summary>
/// WASAPI loopback capture on the default render endpoint, per plan §2.3.
///
/// The four failure modes §2.3 lists are not hypothetical, and every one of them is handled
/// here rather than discovered later:
///
/// 1. NO CALLBACKS AT ALL when the endpoint is idle, on some Windows versions — and silence
///    buffers on others. So nothing downstream is driven by the callback: the render loop
///    reads whatever the analyzer last published, and <see cref="SecondsSinceData"/> lets it
///    treat stale audio as silence.
///
/// 2. DEFAULT DEVICE CHANGES (headphones plugged in, Bluetooth connecting) kill the stream
///    silently. Two independent defences: the watchdog polls the default endpoint's ID and
///    notices when it is no longer the one we opened, AND it watches the data timestamp, so
///    a stream that dies without the device changing is caught too.
///
///    §2.3 asks for MMDeviceEnumerator notifications specifically. NAudio 3.0.1 declares
///    IMMNotificationClient as internal, so it cannot be implemented from outside the
///    assembly — polling the endpoint ID every 250 ms achieves the same detection without
///    an interop layer, and unlike the COM callback it cannot be missed.
///
/// 3. DEVICE FORMAT VARIES — 44.1 / 48 / 96 kHz, usually IEEE float32 but sometimes 16-bit
///    PCM. The format is read from the device and converted here; every bin frequency
///    downstream is derived from it. Nothing assumes 48000.
///
/// 4. EXCLUSIVE MODE held by another app breaks loopback. Detected, reported as one status
///    line, and the app keeps running.
///
/// The callback thread does exactly two things: convert to float and write to the ring. No
/// allocation, no logging, no locks — anything else risks glitching whatever else the
/// machine is playing, and this app is supposed to be invisible while it runs.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    /// <summary>No data for this long AFTER a live stream means re-init (§2.3).</summary>
    private const double WatchdogSeconds = 2.0;

    /// <summary>Minimum gap between reinitialization attempts.</summary>
    private const double RestartBackoffSeconds = 3.0;

    private readonly RingBuffer _ring;
    private readonly object _lifecycle = new();      // guards start/stop, never the hot path

    private MMDeviceEnumerator? _enumerator;
    private WasapiLoopbackCapture? _capture;
    private Thread? _watchdog;
    private volatile bool _running;
    private volatile bool _restartRequested;
    private volatile bool _hadDataThisSession;
    private long _lastDataTicks;
    private long _lastRestartTicks;
    private float[] _scratch = new float[8192];
    private string _deviceId = "";

    public LoopbackCapture(RingBuffer ring) => _ring = ring;

    /// <summary>The device format, once capture has started. Null before that.</summary>
    public AudioFormat? Format { get; private set; }

    public string DeviceName { get; private set; } = "(none)";

    /// <summary>One line, suitable for the diagnostic HUD.</summary>
    public string Status { get; private set; } = "not started";

    /// <summary>
    /// True while a capture client is live and we have seen data recently. An idle endpoint
    /// is NOT unhealthy — it is silent — but it does read as not-healthy here, which is
    /// what makes the renderer treat it as silence rather than freezing on a stale frame.
    /// </summary>
    public bool Healthy => _running && SecondsSinceData < WatchdogSeconds;

    /// <summary>
    /// Is the capture client itself alive? Distinct from <see cref="Healthy"/>: this stays
    /// true through a silent passage, and goes false when the stream has actually died.
    /// </summary>
    public bool ClientRunning => _capture?.CaptureState == CaptureState.Capturing;

    public double SecondsSinceData
    {
        get
        {
            long t = Interlocked.Read(ref _lastDataTicks);
            return t == 0 ? double.PositiveInfinity
                          : Stopwatch.GetElapsedTime(t).TotalSeconds;
        }
    }

    public long Restarts { get; private set; }

    /// <summary>Raised when the device or format changes, so the analyzer can be rebuilt.</summary>
    public event Action<AudioFormat>? FormatChanged;

    // ------------------------------------------------------------------ start / stop

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_running) return;

            _enumerator = new MMDeviceEnumerator();
            _running = true;
            OpenDevice();

            _watchdog = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "ripple-audio-watchdog",
            };
            _watchdog.Start();
        }
    }

    private void OpenDevice()
    {
        try
        {
            CloseCapture();

            var device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DeviceName = device.FriendlyName;
            _deviceId = device.ID;

            _capture = new WasapiLoopbackCapture(device);
            var wf = _capture.WaveFormat;

            var fmt = new AudioFormat(wf.SampleRate, wf.Channels);
            bool changed = Format is null || Format.Value != fmt;
            Format = fmt;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _hadDataThisSession = false;
            _capture.StartRecording();

            // A device that is idle may not produce a callback for a long time, so prime the
            // timestamp — otherwise the watchdog would restart us immediately and forever.
            Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());

            Status = $"{DeviceName} · {wf.SampleRate} Hz · {wf.Channels} ch · "
                   + $"{Describe(wf)}";
            Console.WriteLine($"[audio] {Status}");

            if (changed) FormatChanged?.Invoke(fmt);
        }
        catch (Exception ex)
        {
            // The usual cause is another application holding the endpoint in exclusive
            // mode. §2.3: surface one line, keep running. The watchdog keeps retrying, so
            // plugging in headphones or closing the offending app recovers on its own.
            Status = $"capture unavailable: {Reason(ex)}";
            Console.Error.WriteLine($"[audio] {Status}");
            _capture = null;
        }
    }

    private static string Describe(WaveFormat wf)
        => wf.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "float32",
            WaveFormatEncoding.Pcm => $"pcm{wf.BitsPerSample}",
            WaveFormatEncoding.Extensible => $"extensible/{wf.BitsPerSample}",
            _ => wf.Encoding.ToString(),
        };

    private static string Reason(Exception ex)
        // AUDCLNT_E_DEVICE_IN_USE — the one worth naming, because the user can act on it.
        => (uint)ex.HResult == 0x8889000A
            ? "another application holds the audio device in exclusive mode"
            : ex.Message;

    // ------------------------------------------------------------------ hot path

    /// <summary>
    /// Audio callback thread. Convert and enqueue; nothing else. Deliberately allocation
    /// free — the scratch buffer only grows, and only when a driver hands over a larger
    /// buffer than we have seen before.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var wf = _capture?.WaveFormat;
        if (wf is null) return;

        int bytesPerSample = wf.BitsPerSample / 8;
        int samples = e.BytesRecorded / bytesPerSample;
        if (samples <= 0) return;

        if (_scratch.Length < samples) _scratch = new float[samples * 2];
        var dst = _scratch.AsSpan(0, samples);
        var src = e.Buffer.AsSpan(0, e.BytesRecorded);

        // §2.3: the format is whatever the endpoint says it is. Convert here so the DSP
        // only ever sees float.
        bool ok = wf.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when bytesPerSample == 4 => ConvertFloat32(src, dst),
            WaveFormatEncoding.Pcm or WaveFormatEncoding.Extensible => bytesPerSample switch
            {
                2 => ConvertPcm16(src, dst),
                3 => ConvertPcm24(src, dst),
                4 => ConvertPcm32(src, dst),
                _ => false,
            },
            _ => false,
        };
        if (!ok) return;

        _ring.Write(dst);
        Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
        _hadDataThisSession = true;
    }

    private static bool ConvertFloat32(ReadOnlySpan<byte> src, Span<float> dst)
    {
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src).CopyTo(dst);
        return true;
    }

    private static bool ConvertPcm16(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(src);
        for (int i = 0; i < dst.Length && i < s.Length; i++) dst[i] = s[i] / 32768f;
        return true;
    }

    private static bool ConvertPcm24(ReadOnlySpan<byte> src, Span<float> dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int o = i * 3;
            if (o + 2 >= src.Length) break;
            int v = src[o] | (src[o + 1] << 8) | ((sbyte)src[o + 2] << 16);
            dst[i] = v / 8388608f;
        }
        return true;
    }

    private static bool ConvertPcm32(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(src);
        for (int i = 0; i < dst.Length && i < s.Length; i++) dst[i] = s[i] / 2147483648f;
        return true;
    }

    // ------------------------------------------------------------------ watchdog

    /// <summary>
    /// §2.3's device-change and dead-stream watchdog.
    ///
    /// The subtlety is failure mode 1 versus failure mode 2. On some Windows versions an
    /// idle endpoint produces NO CALLBACKS AT ALL, so "no data for two seconds" does not
    /// mean the stream is broken — it usually means nothing is playing. Restarting on the
    /// data timeout alone reinitializes the capture every two seconds forever on a quiet
    /// machine, which is what the first run of this code did.
    ///
    /// So the timeout only counts as evidence once this session has actually delivered
    /// audio: that proves the endpoint does produce callbacks, so losing them is a real
    /// fault. A session that has never delivered anything is treated as silence, which is
    /// what §2.3 asks for. Device changes and a stopped client are acted on immediately,
    /// with no data needed.
    ///
    /// Phase 3 adds the last piece the plan describes — restart on a data timeout when SMTC
    /// says Playing — which is the case this cannot currently distinguish from silence.
    /// </summary>
    private void WatchdogLoop()
    {
        while (_running)
        {
            Thread.Sleep(250);
            if (!_running) break;

            string? why = null;
            if (_restartRequested) why = "requested";
            else if (_capture is null) why = "no capture client";
            else if (!ClientRunning) why = $"client state is {_capture.CaptureState}";
            else if (DefaultDeviceChanged()) why = "default device changed";
            else if (_hadDataThisSession && SecondsSinceData > WatchdogSeconds)
                why = $"{SecondsSinceData:F1}s without data after a live stream";

            if (why is null) continue;

            // Backoff, so a device that cannot be opened does not spin. 250 ms polling is
            // for responsiveness to a device change, not for retrying a failure.
            double sinceRestart = _lastRestartTicks == 0
                ? double.PositiveInfinity
                : Stopwatch.GetElapsedTime(_lastRestartTicks).TotalSeconds;
            if (sinceRestart < RestartBackoffSeconds) continue;

            _restartRequested = false;
            lock (_lifecycle)
            {
                if (!_running) break;
                Restarts++;
                _lastRestartTicks = Stopwatch.GetTimestamp();
                Console.WriteLine($"[audio] reinitializing capture: {why} (restart #{Restarts})");
                OpenDevice();
            }
        }
    }

    /// <summary>
    /// Has the default render endpoint become a different device than the one we opened?
    /// This is the headphones-and-Bluetooth case, and it is worth catching directly: the
    /// old stream sometimes keeps delivering silence rather than stopping, so the data
    /// timestamp alone would never notice.
    /// </summary>
    private bool DefaultDeviceChanged()
    {
        if (_enumerator is null || _deviceId.Length == 0) return false;
        try
        {
            var now = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (now.ID == _deviceId) return false;
            Console.WriteLine($"[audio] default render device changed to {now.FriendlyName}");
            return true;
        }
        catch
        {
            // No default endpoint at all right now — mid-switch. The data timestamp will
            // trigger a restart if this persists.
            return false;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Console.Error.WriteLine($"[audio] capture stopped: {e.Exception.Message}");
        // Let the watchdog do the reopening: it already serializes on _lifecycle, and
        // reopening from inside an NAudio callback is a good way to deadlock.
        _restartRequested = true;
    }

    // ------------------------------------------------------------------ teardown

    private void CloseCapture()
    {
        if (_capture is null) return;
        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] error closing capture: {ex.Message}");
        }
        _capture = null;
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _running = false;
            CloseCapture();
            _enumerator?.Dispose();
            _enumerator = null;
        }
        _watchdog?.Join(500);
    }
}
