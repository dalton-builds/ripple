using System.Runtime.InteropServices;
using Ripple.Render;

namespace Ripple;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        // The project is a WinExe so the shipped app does not flash a console.
        // The dump and diagnostic paths still need one, so borrow the parent's
        // when there is one — no-op when launched from Explorer.
        AttachConsole(AttachParentProcess);

        RippleOptions opt;
        try
        {
            opt = RippleOptions.Parse(args);
        }
        catch (RippleOptions.HelpRequested)
        {
            Console.WriteLine(RippleOptions.Usage);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ripple: {ex.Message}\n");
            Console.Error.WriteLine(RippleOptions.Usage);
            return 2;
        }

        // Content is copied next to the executable, but running from the repo
        // root is the normal development case. Resolve either way rather than
        // making the caller care.
        ResolveRoots(opt);

        try
        {
            using var host = new GlHost(opt);
            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ripple: {ex}");
            return 1;
        }
    }

    private static void ResolveRoots(RippleOptions opt)
    {
        opt.ShaderRoot = FindRoot(opt.ShaderRoot);
        opt.PresetRoot = FindRoot(opt.PresetRoot);
        opt.DataRoot = FindRoot(opt.DataRoot);
    }

    private static string FindRoot(string relative)
    {
        if (Path.IsPathRooted(relative) && Directory.Exists(relative)) return relative;

        foreach (var b in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(b);
            for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return relative;   // let the loader produce the specific error
    }
}
