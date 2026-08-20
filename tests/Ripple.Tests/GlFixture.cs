using Ripple.Render;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Xunit;

namespace Ripple.Tests;

/// <summary>
/// One hidden GL 4.3 core context for the whole test assembly.
///
/// The std140 round-trip cannot be faked: the question it answers is what a
/// real GLSL compiler thinks the layout of the real features.glsl is, which
/// means a real driver has to be in the loop. The window is never shown and
/// the event loop never runs — Initialize() alone is enough to get a current
/// context.
/// </summary>
public sealed class GlFixture : IDisposable
{
    public GL Gl { get; }
    public ShaderLoader Shaders { get; }

    private readonly IWindow _window;

    public GlFixture()
    {
        var wo = WindowOptions.Default with
        {
            Size = new Vector2D<int>(64, 64),
            Title = "Ripple.Tests",
            IsVisible = false,
            VSync = false,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
                                  ContextFlags.ForwardCompatible, new APIVersion(4, 3)),
        };

        _window = Window.Create(wo);
        _window.Initialize();
        Gl = GL.GetApi(_window);
        Shaders = new ShaderLoader(Gl, FindShaderRoot());
    }

    /// <summary>
    /// shaders/ is copied next to the test assembly, but running from the repo
    /// root is the normal case. Resolve either way — same walk Program.cs does.
    /// </summary>
    private static string FindShaderRoot()
    {
        foreach (var b in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(b);
            for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "shaders");
                if (File.Exists(Path.Combine(candidate, "common", "features.glsl"))) return candidate;
            }
        }
        throw new DirectoryNotFoundException("could not find shaders/common/features.glsl");
    }

    public void Dispose()
    {
        Shaders.Dispose();
        _window.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class GlCollection : ICollectionFixture<GlFixture>
{
    public const string Name = "gl";
}
