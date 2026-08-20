using System.Globalization;
using System.Text;
using Ripple.Render;

namespace Ripple.Audio;

/// <summary>
/// Records real-audio Feature Frames in the timeline JSON format the harness already reads.
///
/// This is the payoff for having frozen one contract for both hosts, and it is nearly free:
/// the harness can load a timeline, the native host can write one, and the format is the
/// same 56 floats per row either way. So you can capture sixty seconds of actual Grateful
/// Dead audio, open it in the browser harness, and tune presets against real feature data
/// instead of guessing with sliders.
///
/// It also closes the coverage gap the constructed fixture works around: real music has
/// section changes, so a captured timeline exercises novelty for free.
///
/// Rows are buffered in memory and written once at the end — a 60 s capture at 93 fps is
/// 5600 rows of 56 floats, about 2.5 MB of JSON, and writing incrementally would put file
/// I/O on the path of a thread that must not stall.
/// </summary>
public sealed class FeatureRecorder
{
    private readonly List<float[]> _rows;
    private readonly double _rate;
    private readonly int _maxRows;

    public FeatureRecorder(double rate, double seconds)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        _rate = rate;
        _maxRows = Math.Max(1, (int)Math.Round(rate * seconds));
        _rows = new List<float[]>(_maxRows);
    }

    public int Count => _rows.Count;
    public bool Full => _rows.Count >= _maxRows;
    public double Seconds => _rows.Count / _rate;

    /// <summary>
    /// Append one frame. The host-owned fields are zeroed rather than recorded: on replay
    /// each host overwrites time and resolution with its own, exactly as the harness's
    /// export note says, so recording them would only invite someone to trust them.
    /// </summary>
    public void Add(in FeatureFrame frame)
    {
        if (Full) return;

        var row = new float[FeatureFrame.FloatCount];
        frame.AsSpan().CopyTo(row);
        foreach (int i in FF.HostOwned) row[i] = 0;
        row[52] = row[53] = row[54] = row[55] = 0;      // reserved stays zero-filled
        _rows.Add(row);
    }

    /// <summary>
    /// Write the timeline. Hand-rolled rather than System.Text.Json because the whole file
    /// is one big array of numbers and the round-tripping of float formatting matters more
    /// than the convenience: "R" keeps every value exactly as recorded, and invariant
    /// culture means a machine with a comma decimal separator produces the same file.
    /// </summary>
    public void Write(string path, string note, AudioFormat format, string? source = null)
    {
        var sb = new StringBuilder(_rows.Count * 56 * 8);
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": 1,\n");
        sb.Append("  \"kind\": \"ripple-feature-timeline\",\n");
        sb.Append("  \"generator\": \"ripple-native/1 real-audio capture\",\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"recorded\": \"{DateTime.UtcNow:o}\",\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"rate\": {_rate.ToString("0.####", CultureInfo.InvariantCulture)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"seconds\": {Seconds.ToString("0.###", CultureInfo.InvariantCulture)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"frameBytes\": {FeatureFrame.ByteSize},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"sampleRate\": {format.SampleRate},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"channels\": {format.Channels},\n");
        if (!string.IsNullOrEmpty(source))
            sb.Append(CultureInfo.InvariantCulture, $"  \"source\": {Quote(source)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"note\": {Quote(note)},\n");

        sb.Append("  \"fieldOrder\": [");
        for (int i = 0; i < FF.Names.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Quote(FF.Names[i]));
        }
        sb.Append("],\n");

        sb.Append("  \"frames\": [\n");
        for (int r = 0; r < _rows.Count; r++)
        {
            sb.Append("    [");
            var row = _rows[r];
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(row[i].ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append(r == _rows.Count - 1 ? "]\n" : "],\n");
        }
        sb.Append("  ]\n}\n");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
