using System.Text.Json;

namespace Ripple;

/// <summary>
/// Applies a mode from <c>tests/parity-config.json</c> to a <see cref="RippleOptions"/>.
///
/// The exclusions the strict gate depends on used to live in whichever CLI flags the
/// person running it happened to remember. That is fine once and wrong forever after:
/// when Phase 4 touches the composite pass and the gate is re-run, it has to re-run with
/// the SAME exclusions that established the baseline, or the comparison is against a
/// different experiment. So the flags become data, committed next to the fixtures.
///
/// <c>--parity-mode a</c> is now the supported way to run the gate; the individual
/// <c>--no-*</c> flags stay for diagnosis, and anything passed explicitly on the command
/// line still wins, so a one-off probe does not need a config edit.
/// </summary>
public static class ParityProfile
{
    public const string DefaultPath = "tests/parity-config.json";

    /// <summary>
    /// Resolve the config, read <paramref name="mode"/>, and set the exclusions, dump
    /// size and stride on <paramref name="o"/>. Returns a one-line summary for the log.
    /// </summary>
    public static string Apply(RippleOptions o, string mode)
    {
        string path = FindConfig(o.ParityConfig);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "ripple-parity-config")
            throw new InvalidDataException($"{path}: not a ripple parity config");

        if (!root.TryGetProperty("modes", out var modes)
            || !modes.TryGetProperty(mode, out var m))
        {
            var names = modes.ValueKind == JsonValueKind.Object
                ? string.Join(", ", modes.EnumerateObject().Select(p => p.Name))
                : "none";
            throw new InvalidDataException($"{path}: no mode '{mode}' (have: {names})");
        }

        // Dump geometry first, so a mode never has to restate it. An explicit --size or
        // --stride on the command line still overrides, via the Specified flags.
        if (root.TryGetProperty("dump", out var dump))
        {
            if (!o.SizeSpecified)
            {
                if (dump.TryGetProperty("width", out var w)) o.Width = w.GetInt32();
                if (dump.TryGetProperty("height", out var h)) o.Height = h.GetInt32();
            }
            if (!o.ScaleSpecified && dump.TryGetProperty("renderScale", out var rs))
                o.RenderScale = rs.GetDouble();
            if (!o.StrideSpecified && dump.TryGetProperty("stride", out var st))
                o.Stride = st.GetInt32();
        }

        if (!m.TryGetProperty("exclude", out var ex))
            throw new InvalidDataException($"{path}: mode '{mode}' has no exclude block");

        // Excluding a term means NOT rendering it, hence the No* naming. Read every key
        // explicitly rather than looping: an unknown key in the config should be a loud
        // failure, not a silently ignored exclusion.
        o.NoFeedback = Excluded(ex, "feedback", path, mode);
        o.NoGrain = Excluded(ex, "grain", path, mode);
        o.NoParticles = Excluded(ex, "particles", path, mode);

        foreach (var p in ex.EnumerateObject())
        {
            if (p.Name is not ("feedback" or "grain" or "particles"))
                throw new InvalidDataException(
                    $"{path}: mode '{mode}' excludes unknown term '{p.Name}'. "
                    + "Adding an exclusion needs host support and a recorded measurement.");
        }

        o.ParityMode = mode;
        string name = m.TryGetProperty("name", out var n) ? n.GetString() ?? mode : mode;
        var terms = new List<string>();
        if (o.NoFeedback) terms.Add("feedback");
        if (o.NoGrain) terms.Add("grain");
        if (o.NoParticles) terms.Add("particles");

        return $"mode {mode} ({name}) from {Path.GetFileName(path)}, "
             + $"excluding {(terms.Count == 0 ? "nothing" : string.Join(" + ", terms))}, "
             + $"{o.Width}x{o.Height} @ {o.RenderScale:0.##}, stride {o.Stride}";
    }

    private static bool Excluded(JsonElement ex, string term, string path, string mode)
        => ex.TryGetProperty(term, out var v)
            ? v.GetBoolean()
            : throw new InvalidDataException(
                $"{path}: mode '{mode}' does not say whether to exclude '{term}'. "
                + "Every term must be stated, so a gate run is never ambiguous.");

    /// <summary>
    /// Same walk Program.ResolveRoots uses: content sits next to the executable in a
    /// published build, but running from the repo root is the normal development case.
    /// </summary>
    private static string FindConfig(string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            if (File.Exists(relative)) return relative;
            throw new FileNotFoundException($"parity config not found: {relative}");
        }

        foreach (var b in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(b);
            for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException(
            $"parity config not found: {relative} (searched up from {Environment.CurrentDirectory} "
            + $"and {AppContext.BaseDirectory})");
    }
}
