using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ripple;

/// <summary>
/// The user's config, at <c>%APPDATA%\Ripple\config.json</c>, hot-reloaded on save
/// alongside the existing shader and preset watchers.
///
/// Three rules shape this file:
///
/// FIRST RUN MUST JUST WORK. Every field has a default, a missing file is not an error, and
/// a malformed file is a warning plus the previous values — never a failure to launch.
/// Someone double-clicking the exe for the first time should get a window, not a message
/// about configuration.
///
/// IT IS WRITTEN ONCE, ON FIRST RUN. A config that does not exist on disk cannot be
/// discovered or edited, and "look at the source for the key names" is not a first-run
/// experience. It is not rewritten after that, so hand edits and comments survive.
///
/// A RELOAD MAY NOT RESTART ANYTHING. Fields that can be applied live are applied live;
/// fields that cannot (window size at creation, audio device) are marked and simply take
/// effect next launch rather than tearing down a running window under the user.
/// </summary>
public sealed class RippleConfig
{
    public const int SchemaVersion = 1;

    // --- window ------------------------------------------------------------
    public int Width = 960, Height = 540;

    /// <summary>Remembered position. null means "put it in the configured corner".</summary>
    public int? X, Y;

    public bool Borderless = true;
    public bool AlwaysOnTop;

    /// <summary>Monitor index for fullscreen, or -1 for windowed.</summary>
    public int FullscreenMonitor = -1;

    /// <summary>Where to place the window on first run: one of the four corners, or "center".</summary>
    public string Corner = "bottom-right";

    /// <summary>Margin from the screen edge when placing into a corner, in pixels.</summary>
    public int CornerMargin = 24;

    // --- render ------------------------------------------------------------
    public double RenderScale = 0.75;
    public double TargetFps = 60;
    public bool VSync = true;

    /// <summary>§11's control loop. On by default — the whole point is that it is unattended.</summary>
    public bool GovernorAuto = true;

    // --- audio -------------------------------------------------------------
    /// <summary>
    /// Default TRUE here, unlike the command line. A desk prototype that does not react to
    /// audio until you pass a flag is not a desk prototype.
    /// </summary>
    public bool Audio = true;

    // --- presets -----------------------------------------------------------
    public string Preset = "";

    /// <summary>
    /// Advance the preset on a strong novelty event. OFF by default, as specified: the
    /// novelty feature is a good proxy for a section change but it is not song metadata, and
    /// a preset that changes when you did not ask it to reads as a bug until you know why.
    /// </summary>
    public bool AutoAdvanceOnNovelty;
    public double NoveltyThreshold = 0.72;

    /// <summary>Floor on how often novelty may advance, so a busy passage cannot strobe presets.</summary>
    public double MinSecondsBetweenAdvances = 45;

    // --- safety ------------------------------------------------------------
    /// <summary>
    /// Master intensity, the hotkey-adjustable one. Clamped to §12's ceiling on load: this
    /// scales the picture DOWN and is not a route around the limiter.
    /// </summary>
    public double MasterIntensity = 1.0;

    // --- misc --------------------------------------------------------------
    /// <summary>null means <c>~\Pictures\Ripple</c>.</summary>
    public string? ScreenshotDir;

    public bool Hotkeys = true;
    public bool TrayIcon = true;

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ripple");

    public static string DefaultPath => Path.Combine(DefaultDir, "config.json");

    /// <summary>Where a screenshot goes, with the default resolved.</summary>
    public string ResolvedScreenshotDir => string.IsNullOrWhiteSpace(ScreenshotDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Ripple")
        : ScreenshotDir;

    /// <summary>
    /// Load, creating the file with defaults if it is absent. Never throws: a config problem
    /// must not be a reason the app does not start.
    /// </summary>
    public static RippleConfig LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        var cfg = new RippleConfig();

        try
        {
            if (File.Exists(path))
            {
                cfg.ApplyJson(File.ReadAllText(path));
                return cfg;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, cfg.ToJson());
            Console.WriteLine($"[config] first run — wrote defaults to {path}");
        }
        catch (Exception ex)
        {
            // Including the unwritable-directory case: a read-only or roaming-blocked
            // %APPDATA% is somebody else's problem, and the answer is still "run anyway".
            Console.Error.WriteLine($"[config] {ex.Message} — using defaults");
        }

        return cfg;
    }

    /// <summary>
    /// Re-read into THIS instance. Returns false and keeps every previous value if the file
    /// cannot be parsed, which is the common case while someone is mid-edit and has just
    /// saved a file with a trailing comma.
    /// </summary>
    public bool Reload(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return false;
            ApplyJson(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] reload failed, keeping previous: {ex.Message}");
            return false;
        }
    }

    private void ApplyJson(string text)
    {
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject
            ?? throw new InvalidDataException("config root is not an object");

        // Every getter tolerates a missing or wrong-typed value, so a partial config is a
        // legitimate config — someone overriding one field should not have to restate the
        // other twenty.
        var w = root["window"] as JsonObject;
        Width = Int(w, "width", Width);
        Height = Int(w, "height", Height);
        X = IntOrNull(w, "x");
        Y = IntOrNull(w, "y");
        Borderless = Bool(w, "borderless", Borderless);
        AlwaysOnTop = Bool(w, "alwaysOnTop", AlwaysOnTop);
        FullscreenMonitor = Int(w, "fullscreenMonitor", FullscreenMonitor);
        Corner = Str(w, "corner", Corner);
        CornerMargin = Int(w, "cornerMargin", CornerMargin);

        var r = root["render"] as JsonObject;
        RenderScale = Math.Clamp(Num(r, "renderScale", RenderScale), 0.2, 2.0);
        TargetFps = Math.Clamp(Num(r, "targetFps", TargetFps), 10, 240);
        VSync = Bool(r, "vsync", VSync);
        GovernorAuto = Bool(r, "governorAuto", GovernorAuto);

        var a = root["audio"] as JsonObject;
        Audio = Bool(a, "enabled", Audio);

        var p = root["presets"] as JsonObject;
        Preset = Str(p, "default", Preset);
        AutoAdvanceOnNovelty = Bool(p, "autoAdvanceOnNovelty", AutoAdvanceOnNovelty);
        NoveltyThreshold = Math.Clamp(Num(p, "noveltyThreshold", NoveltyThreshold), 0.1, 1.0);
        MinSecondsBetweenAdvances = Math.Max(5, Num(p, "minSecondsBetweenAdvances",
                                                    MinSecondsBetweenAdvances));

        var s = root["safety"] as JsonObject;
        // The clamp is the point: §12 is not preset-overridable and it is not
        // config-overridable either. Intensity may only ever scale the picture DOWN.
        MasterIntensity = Math.Clamp(Num(s, "masterIntensity", MasterIntensity), 0.05, 1.0);

        ScreenshotDir = root["screenshotDir"]?.GetValue<string>();
        Hotkeys = Bool(root, "hotkeys", Hotkeys);
        TrayIcon = Bool(root, "trayIcon", TrayIcon);
    }

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["_comment"] = "Ripple config. Saved changes are picked up live. "
                + "Window size and audio take effect on next launch.",
            ["window"] = new JsonObject
            {
                ["width"] = Width,
                ["height"] = Height,
                ["x"] = X is null ? null : JsonValue.Create(X.Value),
                ["y"] = Y is null ? null : JsonValue.Create(Y.Value),
                ["borderless"] = Borderless,
                ["alwaysOnTop"] = AlwaysOnTop,
                ["fullscreenMonitor"] = FullscreenMonitor,
                ["corner"] = Corner,
                ["cornerMargin"] = CornerMargin,
            },
            ["render"] = new JsonObject
            {
                ["renderScale"] = RenderScale,
                ["targetFps"] = TargetFps,
                ["vsync"] = VSync,
                ["governorAuto"] = GovernorAuto,
            },
            ["audio"] = new JsonObject { ["enabled"] = Audio },
            ["presets"] = new JsonObject
            {
                ["default"] = Preset,
                ["autoAdvanceOnNovelty"] = AutoAdvanceOnNovelty,
                ["noveltyThreshold"] = NoveltyThreshold,
                ["minSecondsBetweenAdvances"] = MinSecondsBetweenAdvances,
            },
            ["safety"] = new JsonObject { ["masterIntensity"] = MasterIntensity },
            ["screenshotDir"] = ScreenshotDir,
            ["hotkeys"] = Hotkeys,
            ["trayIcon"] = TrayIcon,
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Persist only the geometry, so a moved or resized window is remembered.</summary>
    public void SaveGeometry(int x, int y, int w, int h, string? path = null)
    {
        path ??= DefaultPath;
        X = x; Y = y; Width = w; Height = h;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Merge into whatever is on disk rather than serializing this object wholesale:
            // the file may carry comments and hand-edited fields this build does not know
            // about, and a geometry save must not be the thing that eats them.
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? [];
            }
            catch (Exception)
            {
                root = [];
            }

            if (root["window"] is not JsonObject win) root["window"] = win = [];
            win["x"] = x; win["y"] = y; win["width"] = w; win["height"] = h;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] could not save geometry: {ex.Message}");
        }
    }

    // --- tolerant readers --------------------------------------------------

    private static double Num(JsonObject? o, string k, double fallback)
        => o?[k] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    private static int Int(JsonObject? o, string k, int fallback)
        => o?[k] is JsonValue v && v.TryGetValue(out int i) ? i : fallback;

    private static int? IntOrNull(JsonObject? o, string k)
        => o?[k] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static bool Bool(JsonObject? o, string k, bool fallback)
        => o?[k] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    private static string Str(JsonObject? o, string k, string fallback)
        => o?[k] is JsonValue v && v.TryGetValue(out string? s) && s is not null ? s : fallback;
}
