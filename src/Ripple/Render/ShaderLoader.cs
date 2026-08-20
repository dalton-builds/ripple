using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// Loads the SHARED shader sources under <c>shaders/</c> unmodified.
///
/// CONTRACTS.md §2: the files carry no <c>#version</c> line, and each host
/// injects its own preamble. This host injects the desktop GL one:
///
/// <code>
/// #version 430 core
/// #define highp
/// #define mediump
/// #define lowp
/// </code>
///
/// It deliberately does NOT define <c>RIPPLE_WEBGL</c>. That guard exists in
/// features.glsl because GLSL ES 3.00 rejects <c>layout(binding = N)</c> on a
/// uniform block; desktop GL 4.2+ accepts it, so this host takes the other
/// branch and the UBO binding comes from the shader rather than from a
/// glUniformBlockBinding call.
///
/// The <c>#include</c> resolver is a flat textual substitution with cycle
/// detection and once-per-compilation-unit dedupe — deliberately dumb, and an
/// exact mirror of resolveIncludes() in the harness. If the two ever disagree,
/// a change to a common/ file silently changes only one host.
/// </summary>
public sealed partial class ShaderLoader : IDisposable
{
    // 430, not 330: features.glsl declares the UBO as layout(std140, binding = 0)
    // on the desktop branch, and the `binding` qualifier on a uniform block is
    // GLSL 4.20+. Under 330 the driver reports `'binding' : syntax error`, which
    // reads like a malformed file rather than a version floor. The context this
    // host requests is 4.3 core, so match it.
    public const string Preamble =
        "#version 430 core\n" +
        "#define highp\n" +
        "#define mediump\n" +
        "#define lowp\n" +
        "#line 1\n";

    [GeneratedRegex("^[ \\t]*#include[ \\t]+\"([^\"]+)\"[ \\t]*$", RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();

    private readonly GL _gl;
    private readonly string _root;
    private readonly Dictionary<string, string> _sourceCache = new(StringComparer.Ordinal);
    private readonly List<ProgramSpec> _specs = [];
    private FileSystemWatcher? _watcher;
    private int _reloadRequests;

    public string Root => _root;

    /// <summary>Raised after a successful hot reload, so callers can re-fetch uniforms.</summary>
    public event Action? Reloaded;

    /// <summary>Raised when a hot reload fails to compile. The old programs stay live.</summary>
    public event Action<string>? ReloadFailed;

    public ShaderLoader(GL gl, string root)
    {
        _gl = gl;
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"shader root not found: {_root}");
    }

    // ---------------------------------------------------------------- source

    /// <summary>Raw file text, newline-normalized. Cached until a reload clears it.</summary>
    public string ReadRaw(string virtualPath)
    {
        if (_sourceCache.TryGetValue(virtualPath, out var cached)) return cached;
        var full = Path.Combine(_root, virtualPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) throw new FileNotFoundException($"no such shader: {virtualPath}", full);
        // The regex anchors on '$', so a stray CRLF would leave a '\r' inside
        // the captured path and every include would miss.
        var text = File.ReadAllText(full).Replace("\r\n", "\n").Replace('\r', '\n');
        _sourceCache[virtualPath] = text;
        return text;
    }

    /// <summary>Resolve a file's includes. The result still carries no #version line.</summary>
    public string Resolve(string virtualPath)
        => ResolveText(virtualPath, ReadRaw(virtualPath));

    /// <summary>
    /// Resolve includes in a source string that is not (or not yet) on disk.
    /// Used by the std140 probe in the tests, which must include the real
    /// features.glsl rather than a copy of it.
    /// </summary>
    public string ResolveText(string virtualPath, string source)
        => Substitute(virtualPath, source, new HashSet<string>(StringComparer.Ordinal), []);

    private string Substitute(string path, string source, HashSet<string> seen, List<string> stack)
    {
        if (stack.Contains(path, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "#include cycle: " + string.Join(" -> ", stack.Append(path)));

        var nested = new List<string>(stack) { path };

        return IncludeRegex().Replace(source, m =>
        {
            var inc = m.Groups[1].Value;
            string src;
            try { src = ReadRaw(inc); }
            catch (FileNotFoundException)
            {
                throw new InvalidOperationException($"#include not found: \"{inc}\" (from {path})");
            }
            // Once per compilation unit. Matches the harness exactly, including
            // the marker comments — they keep the two hosts' line numbers, and
            // therefore their compiler error messages, comparable.
            if (!seen.Add(inc)) return $"// #include \"{inc}\" (already resolved)";
            return $"// ---- begin {inc} ----\n{Substitute(inc, src, seen, nested)}\n// ---- end {inc} ----";
        });
    }

    // --------------------------------------------------------------- compile

    private sealed class ProgramSpec(string name, string vs, string fs, ShaderProgram program, Action<uint>? preLink)
    {
        public readonly string Name = name;
        public readonly string VertexPath = vs;
        public readonly string FragmentPath = fs;
        public readonly Action<uint>? PreLink = preLink;
        public ShaderProgram Program = program;
    }

    /// <summary>
    /// Compile and link a program, and register it for hot reload. The returned
    /// handle stays valid across reloads: the GL name behind it is swapped in
    /// place, so callers can hold the reference for the life of the process.
    /// </summary>
    /// <param name="preLink">
    /// Runs between attach and link — the only window in which
    /// glBindAttribLocation has any effect. The particle program uses it to pin
    /// aSeed/aIdx to 0 and 1 rather than trusting the driver to assign
    /// locations in declaration order.
    /// </param>
    public ShaderProgram Program(string name, string vertexPath, string fragmentPath, Action<uint>? preLink = null)
    {
        var prog = new ShaderProgram(_gl, name);
        prog.Build(BuildStage(ShaderType.VertexShader, vertexPath),
                   BuildStage(ShaderType.FragmentShader, fragmentPath), preLink);
        _specs.Add(new ProgramSpec(name, vertexPath, fragmentPath, prog, preLink));
        return prog;
    }

    private string BuildStage(ShaderType type, string path) => Preamble + Resolve(path);

    // ------------------------------------------------------------ hot reload

    /// <summary>
    /// Watch <c>shaders/</c> and rebuild every registered program when anything
    /// under it changes. Worth the hour it costs: it turns post-handoff tuning
    /// from a rebuild cycle into a save-and-look cycle.
    /// </summary>
    public void WatchForChanges()
    {
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // Editors write in bursts (temp file, rename, touch). Coalesce by
        // counting requests and servicing them once on the render thread.
        _watcher.Changed += (_, _) => Interlocked.Increment(ref _reloadRequests);
        _watcher.Created += (_, _) => Interlocked.Increment(ref _reloadRequests);
        _watcher.Renamed += (_, _) => Interlocked.Increment(ref _reloadRequests);
    }

    /// <summary>
    /// Call once per frame from the render thread. Returns true if programs
    /// were rebuilt. A failed rebuild leaves every existing program untouched —
    /// a typo in a shader must not take the window down.
    /// </summary>
    public bool PollReload()
    {
        if (Interlocked.Exchange(ref _reloadRequests, 0) == 0) return false;

        // The write that raised the event may still be in flight.
        Thread.Sleep(30);
        _sourceCache.Clear();

        var rebuilt = new List<(ProgramSpec spec, uint handle)>();
        try
        {
            foreach (var spec in _specs)
            {
                var h = ShaderProgram.Link(_gl, spec.Name,
                    BuildStage(ShaderType.VertexShader, spec.VertexPath),
                    BuildStage(ShaderType.FragmentShader, spec.FragmentPath),
                    spec.PreLink);
                rebuilt.Add((spec, h));
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, h) in rebuilt) _gl.DeleteProgram(h);
            ReloadFailed?.Invoke(ex.Message);
            return false;
        }

        foreach (var (spec, h) in rebuilt) spec.Program.Swap(h);
        Reloaded?.Invoke();
        return true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        foreach (var s in _specs) s.Program.Dispose();
        _specs.Clear();
    }
}

/// <summary>
/// A linked program plus a uniform-location cache. Locations are re-resolved
/// after a hot reload, so the cache never outlives the GL program it describes.
/// </summary>
public sealed class ShaderProgram(GL gl, string name) : IDisposable
{
    private readonly GL _gl = gl;
    private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);
    public string Name { get; } = name;
    public uint Handle { get; private set; }

    internal void Build(string vertexSource, string fragmentSource, Action<uint>? preLink)
        => Handle = Link(_gl, Name, vertexSource, fragmentSource, preLink);

    internal void Swap(uint newHandle)
    {
        if (Handle != 0) _gl.DeleteProgram(Handle);
        Handle = newHandle;
        _locations.Clear();
    }

    internal static uint Link(GL gl, string name, string vertexSource, string fragmentSource,
                              Action<uint>? preLink = null)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, name, vertexSource);
        uint fs = Compile(gl, ShaderType.FragmentShader, name, fragmentSource);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, vs);
        gl.AttachShader(p, fs);
        preLink?.Invoke(p);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        gl.DetachShader(p, vs);
        gl.DetachShader(p, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (ok == 0)
        {
            var log = gl.GetProgramInfoLog(p);
            gl.DeleteProgram(p);
            throw new InvalidOperationException($"link failed: {name}\n{log}");
        }
        return p;
    }

    private static uint Compile(GL gl, ShaderType type, string name, string source)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, source);
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok != 0) return sh;

        var log = gl.GetShaderInfoLog(sh);
        gl.DeleteShader(sh);
        throw new InvalidOperationException(
            $"shader compile failed: {name} ({type})\n{log}\n{Excerpt(source, log)}");
    }

    /// <summary>
    /// Pull the offending lines out of the source. GLSL error text is close to
    /// useless without them, and this is the same courtesy the harness does.
    /// </summary>
    private static string Excerpt(string source, string log)
    {
        var m = Regex.Match(log, @":(\d+)\D");
        if (!m.Success) return string.Empty;
        if (!int.TryParse(m.Groups[1].Value, out int line)) return string.Empty;
        var lines = source.Split('\n');
        var sb = new StringBuilder();
        for (int i = Math.Max(0, line - 4); i < Math.Min(lines.Length, line + 3); i++)
            sb.Append(i + 1 == line ? ">> " : "   ").Append(i + 1).Append(" | ").AppendLine(lines[i]);
        return sb.ToString();
    }

    public void Use() => _gl.UseProgram(Handle);

    public int Loc(string uniform)
    {
        if (_locations.TryGetValue(uniform, out int l)) return l;
        l = _gl.GetUniformLocation(Handle, uniform);
        _locations[uniform] = l;
        return l;
    }

    public void U1(string n, float v) { int l = Loc(n); if (l >= 0) _gl.Uniform1(l, v); }
    public void U2(string n, float a, float b) { int l = Loc(n); if (l >= 0) _gl.Uniform2(l, a, b); }
    public void UI(string n, int v) { int l = Loc(n); if (l >= 0) _gl.Uniform1(l, v); }

    public void Dispose()
    {
        if (Handle != 0) _gl.DeleteProgram(Handle);
        Handle = 0;
    }
}
