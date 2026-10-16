using System.Reflection;
using System.Runtime.Loader;

namespace MagSid.Editor.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Every dependency lives in Addons\ so the folder is not twenty DLLs deep (see the
        // csproj target that puts them there). The runtime only probes beside the .exe, so this
        // has to be registered before the first type from any of them is touched - which means
        // before MainForm is even named, since naming it would load MagSid.Core with it.
        AssemblyLoadContext.Default.Resolving += ResolveFromAddons;

        ApplicationConfiguration.Initialize();
        Run();
    }

    // Kept out of Main so that JITting Main cannot pull MainForm - and everything it references -
    // in before the resolver above is in place.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Run()
    {
        // The engines in MagSid.Playback play through whatever the host gives them; here, waveOut.
        MagSid.Playback.AudioOutput.Open = WaveOutAudioOutput.Open;
        Application.Run(new MainForm());
    }

    private static Assembly? ResolveFromAddons(AssemblyLoadContext context, AssemblyName name)
    {
        string candidate = Path.Combine(AppContext.BaseDirectory, "Addons", name.Name + ".dll");
        return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
    }
}