using System.Text.Json;
using System.Text.Json.Nodes;
using Ripple.Render;

namespace Ripple.Presets;

/// <summary>
/// The feature→parameter routing targets of the §10 <c>mapping</c> block.
/// Mirrors MAP_TARGETS in the harness, name for name. Keeping the routing as
/// data is what lets a new look come from editing JSON rather than writing a
/// shader, so a target that exists on one host and not the other is a contract
/// break rather than a missing feature.
/// </summary>
public enum MapTarget
{
    None = 0,
    FeedbackZoom, FeedbackDecay, WarpAmount, WarpScale,
    FractalIterations, FractalMorph, MotifOpacity, MotifGain,
    ParticleEmit, Grain, Symmetry, PaletteRotate,
    BloomGain, Exposure, Rotate,
}

/// <summary>
/// A resolved preset flattened into plain fields. Resolution happens on preset
/// change; the render loop reads this and never touches JSON, which is what
/// keeps per-frame allocation at zero.
/// </summary>
/// <remarks>
/// Everything here is <c>double</c>, not <c>float</c>, and that is deliberate.
/// JavaScript has one number type and the harness does all of its host-side
/// arithmetic in double. Parsing <c>0.985</c> into a float would round it
/// before the first pixel is drawn, and the difference compounds through a
/// feedback buffer with a 0.99 decay. Narrowing happens once, at the
/// glUniform1f call, on both hosts.
/// </remarks>
public sealed class PresetValues
{
    public string Id = "preset";
    public string Name = "preset";
    public string Fractal = "warp_fbm";
    public int EraId = 7;

    // palette
    public string[] PaletteStops = ["#000000", "#ffffff"];
    public double ChromaBlend = 0.3;
    public double Spiral = 0.35;
    public bool RotateByKey = true;

    // feedback
    public double Decay, Zoom, Rotate, WarpAmount, WarpScale, BassGain;
    public double Symmetry, PaintGain, CenterPull, OnsetDecay;

    // fractalParams
    public double Iterations, EscapeRadius, OrbitRadius, BeatMorph;
    public double Warp, Scale, Flow, Cells, Rim, Iridescence, Mask, Octaves, RimWidth;
    public double TrapGain, ZoomPeriod, ZoomDepth, Fold, Line;

    // motifs
    public double BoltGain, BoltThreshold, BoltFork;
    public double RoseGain, RoseThreshold, RosePetals;

    // particles
    public double ParticleCount, CurlScale, Lifetime, ParticleSize, Rise;

    // post
    public double BloomThreshold, BloomGain, BloomTaps, BloomKnee;
    public double Grain, Vignette, Exposure, Saturation;

    // safety
    public double MaxLuminanceDelta = 0.08, MaxBrightness = 0.85;

    /// <summary>
    /// Feature index → target, in the JSON's own key order. Order matters:
    /// several targets accumulate (<c>+=</c>, <c>*=</c>, <c>max</c>) rather
    /// than assign, so re-sorting this list changes the image.
    /// </summary>
    public (int Field, MapTarget Target)[] Mapping = [];

    public static PresetValues From(JsonObject p)
    {
        var v = new PresetValues
        {
            Id = Str(p, "id", "preset"),
            Name = Str(p, "name", "preset"),
            Fractal = Str(p, "fractal", "warp_fbm"),
            EraId = (int)Num(p, "eraId", 7),
        };

        if (p["palette"] is JsonObject pal)
        {
            if (pal["stops"] is JsonArray stops && stops.Count > 0)
                v.PaletteStops = [.. stops.Select(s => (string?)s ?? "#000000")];
            v.ChromaBlend = Num(pal, "chromaBlend", 0.3);
            v.Spiral = Num(pal, "spiral", 0.35);
            v.RotateByKey = Bool(pal, "rotateByKey", true);
        }

        if (p["feedback"] is JsonObject fb)
        {
            v.Decay = Num(fb, "decay", 0.985);
            v.Zoom = Num(fb, "zoom", 1.0035);
            v.Rotate = Num(fb, "rotate", 0.0015);
            v.WarpAmount = Num(fb, "warpAmount", 0.38);
            v.WarpScale = Num(fb, "warpScale", 2.2);
            v.BassGain = Num(fb, "bassGain", 1.15);
            v.Symmetry = Num(fb, "symmetry", 0);
            v.PaintGain = Num(fb, "paintGain", 0.095);
            v.CenterPull = Num(fb, "centerPull", 0.5);
            v.OnsetDecay = Num(fb, "onsetDecay", 0.6);
        }

        if (p["fractalParams"] is JsonObject fp)
        {
            v.Iterations = Num(fp, "iterations", 88);
            v.EscapeRadius = Num(fp, "escapeRadius", 4);
            v.OrbitRadius = Num(fp, "orbitRadius", 0.26);
            v.BeatMorph = Num(fp, "beatMorph", 0.55);
            v.Warp = Num(fp, "warp", 3.4);
            v.Scale = Num(fp, "scale", 2.6);
            v.Flow = Num(fp, "flow", 0.16);
            v.Cells = Num(fp, "cells", 4.6);
            v.Rim = Num(fp, "rim", 1.6);
            v.Iridescence = Num(fp, "iridescence", 0.55);
            v.Mask = Num(fp, "mask", 0);
            v.Octaves = Num(fp, "octaves", 5);
            v.RimWidth = Num(fp, "rimWidth", 2.4);
            v.TrapGain = Num(fp, "trapGain", 1);
            v.ZoomPeriod = Num(fp, "zoomPeriod", 42);
            v.ZoomDepth = Num(fp, "zoomDepth", 7);
            v.Fold = Num(fp, "fold", 1);
            v.Line = Num(fp, "line", 1);
        }

        if (p["motifs"] is JsonObject mo)
        {
            if (mo["bolt"] is JsonObject bolt)
            {
                v.BoltGain = Num(bolt, "gain", 0.8);
                v.BoltThreshold = Num(bolt, "threshold", 0.55);
                v.BoltFork = Num(bolt, "forkDepth", 3);
            }
            if (mo["rose"] is JsonObject rose)
            {
                v.RoseGain = Num(rose, "gain", 1);
                v.RoseThreshold = Num(rose, "chromaFluxThreshold", 0.4);
                v.RosePetals = Num(rose, "petals", 13);
            }
        }

        if (p["particles"] is JsonObject pa)
        {
            v.ParticleCount = Num(pa, "count", 24000);
            v.CurlScale = Num(pa, "curlScale", 1.8);
            v.Lifetime = Num(pa, "lifetime", 3.2);
            v.ParticleSize = Num(pa, "size", 2.6);
            v.Rise = Num(pa, "rise", 0);
        }

        if (p["post"] is JsonObject po)
        {
            v.BloomThreshold = Num(po, "bloomThreshold", 0.4);
            v.BloomGain = Num(po, "bloomGain", 0.55);
            v.BloomTaps = Num(po, "bloomTaps", 2);
            v.BloomKnee = Num(po, "bloomKnee", 0.3);
            v.Grain = Num(po, "grain", 0.03);
            v.Vignette = Num(po, "vignette", 0.25);
            v.Exposure = Num(po, "exposure", 1);
            v.Saturation = Num(po, "saturation", 1.7);
        }

        if (p["safety"] is JsonObject sa)
        {
            v.MaxLuminanceDelta = Num(sa, "maxLuminanceDelta", 0.08);
            v.MaxBrightness = Num(sa, "maxBrightness", 0.85);
        }

        if (p["mapping"] is JsonObject map)
        {
            var list = new List<(int, MapTarget)>(map.Count);
            foreach (var kv in map)
            {
                int field = FieldIndex(kv.Key);
                var target = ParseTarget((string?)kv.Value);
                // Unknown source or unknown target is skipped, exactly as the
                // harness's `if (!tgt || !f) continue`.
                if (field < 0 || target == MapTarget.None) continue;
                list.Add((field, target));
            }
            v.Mapping = [.. list];
        }

        return v;
    }

    private static int FieldIndex(string name)
    {
        for (int i = 0; i < FF.Names.Length; i++)
            if (string.Equals(FF.Names[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static MapTarget ParseTarget(string? s) => s switch
    {
        "feedbackZoom" => MapTarget.FeedbackZoom,
        "feedbackDecay" => MapTarget.FeedbackDecay,
        "warpAmount" => MapTarget.WarpAmount,
        "warpScale" => MapTarget.WarpScale,
        "fractalIterations" => MapTarget.FractalIterations,
        "fractalMorph" => MapTarget.FractalMorph,
        "motifOpacity" => MapTarget.MotifOpacity,
        "motifGain" => MapTarget.MotifGain,
        "particleEmit" => MapTarget.ParticleEmit,
        "grain" => MapTarget.Grain,
        "symmetry" => MapTarget.Symmetry,
        "paletteRotate" => MapTarget.PaletteRotate,
        "bloomGain" => MapTarget.BloomGain,
        "exposure" => MapTarget.Exposure,
        "rotate" => MapTarget.Rotate,
        _ => MapTarget.None,
    };

    private static double Num(JsonObject o, string key, double dflt)
        => o.TryGetPropertyValue(key, out var n) && n is not null && n.GetValueKind() == JsonValueKind.Number
            ? n.GetValue<double>() : dflt;

    private static string Str(JsonObject o, string key, string dflt)
        => o.TryGetPropertyValue(key, out var n) && n is not null && n.GetValueKind() == JsonValueKind.String
            ? n.GetValue<string>() : dflt;

    private static bool Bool(JsonObject o, string key, bool dflt)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return dflt;
        return n.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => dflt,
        };
    }
}
