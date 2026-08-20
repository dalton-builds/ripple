using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ripple.Presets;

/// <summary>
/// Preset loading and the CONTRACTS.md §4 merge.
///
/// Merge order, lowest precedence first:
/// <code>presets/_defaults.json → era/authored preset → song mode override</code>
/// (the user-config and governor-clamp stages arrive in Phases 4–5).
///
/// Every key in an override is PARTIAL — <c>{"feedback":{"decay":0.995}}</c>
/// changes the decay and inherits the rest of the block — so this is a
/// recursive dictionary merge and never a replace. Arrays are replaced whole,
/// matching mergeDeep() in the harness.
///
/// The library order matters for parity: authored presets first, then eras,
/// exactly as the harness builds <c>LIBRARY = AUTHORED_PRESETS.concat(ERA_PRESETS)</c>.
/// </summary>
public sealed class PresetStore : IDisposable
{
    private readonly string _presetRoot;
    private readonly string _dataRoot;
    private JsonObject _defaults = [];
    private readonly List<JsonObject> _library = [];
    private readonly Dictionary<string, JsonObject> _songModes = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private int _reloadRequests;

    public IReadOnlyList<JsonObject> Library => _library;
    public IReadOnlyDictionary<string, JsonObject> SongModes => _songModes;
    public event Action? Reloaded;

    public PresetStore(string presetRoot, string dataRoot)
    {
        _presetRoot = Path.GetFullPath(presetRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        Load();
    }

    /// <summary>
    /// Read everything from disk. Built into locals and committed at the end,
    /// so a half-written file during a hot reload leaves the live set intact
    /// rather than emptying the library.
    /// </summary>
    public void Load()
    {
        var library = new List<JsonObject>();
        var songModes = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        var defaultsPath = Path.Combine(_presetRoot, "_defaults.json");
        if (!File.Exists(defaultsPath))
            throw new FileNotFoundException(
                "presets/_defaults.json is missing. Generate it with `node tools/extract-defaults.mjs` — "
                + "it is the base every preset merges over and the harness is its source of truth.",
                defaultsPath);
        var defaults = StripMeta(ReadObject(defaultsPath));

        // Authored first, then eras: the harness's library order, which is what
        // "preset 3" means on both sides of the parity diff.
        foreach (var dir in new[] { "authored", "eras", "generic", "songs" })
        {
            var d = Path.Combine(_presetRoot, dir);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
                library.Add(ReadObject(f));
        }

        var songs = Path.Combine(_dataRoot, "songs.json");
        if (File.Exists(songs))
        {
            foreach (var kv in ReadObject(songs))
            {
                if (kv.Key.StartsWith('_')) continue;   // _comment / _normalization
                if (kv.Value is JsonObject o) songModes[kv.Key] = o;
            }
        }

        _defaults = defaults;
        _library.Clear();
        _library.AddRange(library);
        _songModes.Clear();
        foreach (var kv in songModes) _songModes[kv.Key] = kv.Value;
    }

    private static JsonObject ReadObject(string path)
    {
        using var s = File.OpenRead(path);
        var node = JsonNode.Parse(s, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return node as JsonObject
            ?? throw new InvalidDataException($"{path}: expected a JSON object at the root");
    }

    private static JsonObject StripMeta(JsonObject o)
    {
        var result = new JsonObject();
        foreach (var kv in o)
            if (!kv.Key.StartsWith('_')) result[kv.Key] = kv.Value?.DeepClone();
        return result;
    }

    /// <summary>Index a preset by its <c>id</c>, or -1.</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < _library.Count; i++)
            if (string.Equals((string?)_library[i]["id"], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Defaults → preset → optional song mode, as a fresh JsonObject.</summary>
    public JsonObject ResolveNode(int index, string? songMode = null)
    {
        if (index < 0 || index >= _library.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"no preset {index} (library has {_library.Count})");

        var merged = MergeDeep(_defaults, _library[index]);
        if (!string.IsNullOrEmpty(songMode)
            && !songMode.Equals("(none)", StringComparison.OrdinalIgnoreCase)
            && _songModes.TryGetValue(songMode, out var sm))
        {
            merged = MergeDeep(merged, sm);
        }
        return merged;
    }

    /// <summary>The resolved preset flattened into the values the renderer reads.</summary>
    public PresetValues Resolve(int index, string? songMode = null)
        => PresetValues.From(ResolveNode(index, songMode));

    /// <summary>
    /// CONTRACTS §4 recursive merge. Base key order is preserved and keys only
    /// present in the override are appended — the same ordering JS object
    /// iteration gives the harness, which matters because the mapping block is
    /// applied in key order.
    /// </summary>
    public static JsonObject MergeDeep(JsonObject baseObj, JsonObject over)
    {
        var outObj = new JsonObject();
        foreach (var kv in baseObj)
        {
            if (over.TryGetPropertyValue(kv.Key, out var ov))
            {
                outObj[kv.Key] = (kv.Value is JsonObject b && ov is JsonObject o)
                    ? MergeDeep(b, o)
                    : ov?.DeepClone();
            }
            else outObj[kv.Key] = kv.Value?.DeepClone();
        }
        foreach (var kv in over)
            if (!baseObj.ContainsKey(kv.Key)) outObj[kv.Key] = kv.Value?.DeepClone();
        return outObj;
    }

    // ------------------------------------------------------------ hot reload

    public void WatchForChanges()
    {
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(_presetRoot)
        {
            IncludeSubdirectories = true,
            Filter = "*.json",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Interlocked.Increment(ref _reloadRequests);
        _watcher.Created += (_, _) => Interlocked.Increment(ref _reloadRequests);
        _watcher.Renamed += (_, _) => Interlocked.Increment(ref _reloadRequests);
    }

    /// <summary>Poll from the render thread. Bad JSON leaves the live set alone.</summary>
    public bool PollReload()
    {
        if (Interlocked.Exchange(ref _reloadRequests, 0) == 0) return false;
        Thread.Sleep(30);
        try { Load(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[presets] reload failed, keeping previous: {ex.Message}");
            return false;
        }
        Reloaded?.Invoke();
        return true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
