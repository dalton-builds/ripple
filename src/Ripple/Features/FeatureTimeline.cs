using System.Text.Json;
using Ripple.Render;

namespace Ripple.Features;

/// <summary>
/// A recorded Feature Frame timeline, as exported by the harness.
///
/// This is the cross-host parity contract in file form: 56 floats per row, in
/// Feature Frame declaration order, one row per rendered frame. Replay is
/// EXACT — row i renders as frame i, uploaded verbatim, with no interpolation
/// and no wraparound — because any resampling would make the diff depend on
/// each host's frame rate rather than on its shaders.
///
/// The host owns six of the 56 indices (uTime.xy and all of uRes) and
/// overwrites them on replay; see <see cref="FF.HostOwned"/>.
/// </summary>
public sealed class FeatureTimeline
{
    private readonly float[] _flat;   // Count * 56, contiguous

    public int Count { get; }
    public double Rate { get; }
    public string Source { get; }

    private FeatureTimeline(float[] flat, int count, double rate, string source)
    {
        _flat = flat;
        Count = count;
        Rate = rate;
        Source = source;
    }

    public static FeatureTimeline Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "ripple-feature-timeline")
            throw new InvalidDataException($"{path}: not a ripple feature timeline");

        if (root.TryGetProperty("frameBytes", out var fb) && fb.GetInt32() != FeatureFrame.ByteSize)
            throw new InvalidDataException(
                $"{path}: frameBytes is {fb.GetInt32()}, expected {FeatureFrame.ByteSize} — schema mismatch");

        double rate = root.TryGetProperty("rate", out var r) ? r.GetDouble() : 60;
        if (rate <= 0) throw new InvalidDataException($"{path}: rate must be positive, got {rate}");

        // fieldOrder is advisory, but a mismatch means the recording came from
        // a different schema version and every value would land in the wrong
        // slot. Fail loudly rather than render nonsense.
        if (root.TryGetProperty("fieldOrder", out var fo) && fo.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var name in fo.EnumerateArray())
            {
                if (i >= FF.Names.Length) break;
                if (name.GetString() != FF.Names[i])
                    throw new InvalidDataException(
                        $"{path}: fieldOrder[{i}] is '{name.GetString()}', expected '{FF.Names[i]}'");
                i++;
            }
        }

        if (!root.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: no frames array");

        int count = frames.GetArrayLength();
        var flat = new float[Math.Max(1, count) * FeatureFrame.FloatCount];
        int row = 0;
        foreach (var frame in frames.EnumerateArray())
        {
            if (frame.GetArrayLength() != FeatureFrame.FloatCount)
                throw new InvalidDataException(
                    $"{path}: frame {row} has {frame.GetArrayLength()} values, expected {FeatureFrame.FloatCount}");
            int col = 0;
            foreach (var v in frame.EnumerateArray())
                flat[row * FeatureFrame.FloatCount + col++] = (float)v.GetDouble();
            row++;
        }

        return new FeatureTimeline(flat, count, rate, path);
    }

    /// <summary>
    /// Copy row <paramref name="index"/> verbatim. Past the end, the last row
    /// is held — no wraparound, because looping back to frame 0 against a full
    /// feedback buffer produces a frame that exists in neither host's history.
    /// </summary>
    public void CopyRow(int index, Span<float> dst)
    {
        if (Count == 0) { dst.Clear(); return; }
        int i = Math.Clamp(index, 0, Count - 1);
        _flat.AsSpan(i * FeatureFrame.FloatCount, FeatureFrame.FloatCount).CopyTo(dst);
    }
}
