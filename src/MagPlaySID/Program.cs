using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MagSid.Audio.PortAudio;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>MagPlaySID: a .sid named on the command line - or opened with it - plays at once.</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // No full collection that stops every thread while the music plays, when one can be done
        // alongside: a stop of the sound's thread is a gap in the sound.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        AudioOutput.Open = PortAudioOutput.Open;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = "MagPlaySID";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PlayerWindow(desktop.Args?.FirstOrDefault(File.Exists));

        base.OnFrameworkInitializationCompleted();
    }
}
