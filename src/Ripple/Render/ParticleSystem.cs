using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// Attribute-cheap GPU particles: two floats of seed plus an index per point,
/// uploaded once at startup. The whole trajectory is evaluated in the vertex
/// shader from curl noise, so the CPU does nothing per frame and there is no
/// transform-feedback state to keep in sync.
///
/// The seeds come from <see cref="Mulberry32"/>, not from a system RNG. The
/// harness fills the identical buffer from the identical stream, which is what
/// makes the particle layer comparable frame for frame rather than merely
/// similar in character.
/// </summary>
public sealed class ParticleSystem : IDisposable
{
    public const int MaxParticles = 64000;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _seedBuffer;
    private readonly uint _indexBuffer;

    public ParticleSystem(GL gl)
    {
        _gl = gl;

        var seeds = new float[MaxParticles * 2];
        var idx = new float[MaxParticles];
        var rng = new Mulberry32(Mulberry32.ParticleSeed);
        for (int i = 0; i < MaxParticles; i++)
        {
            seeds[i * 2] = rng.NextFloat();
            seeds[i * 2 + 1] = rng.NextFloat();
            idx[i] = i;
        }

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _seedBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _seedBuffer);
        unsafe
        {
            fixed (float* p = seeds)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(seeds.Length * sizeof(float)),
                    p, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, null);
        }

        _indexBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _indexBuffer);
        unsafe
        {
            fixed (float* p = idx)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(idx.Length * sizeof(float)),
                    p, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 0, null);
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>
    /// Bind the attribute layout the vertex shader expects. The shader declares
    /// <c>in vec2 aSeed</c> / <c>in float aIdx</c> without explicit locations,
    /// so the program has to have them bound to 0 and 1 before linking.
    /// </summary>
    public static void BindAttribLocations(GL gl, uint program)
    {
        gl.BindAttribLocation(program, 0, "aSeed");
        gl.BindAttribLocation(program, 1, "aIdx");
    }

    public void Draw(int count)
    {
        if (count <= 0) return;
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)Math.Min(count, MaxParticles));
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_seedBuffer);
        _gl.DeleteBuffer(_indexBuffer);
        _gl.DeleteVertexArray(_vao);
    }
}
