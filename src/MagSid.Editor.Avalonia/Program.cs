using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MagSid.Audio.PortAudio;
using MagSid.Editor.Avalonia.Player;
using MagSid.Playback;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// MagSID for Windows, macOS and Linux. For now the SID player; the editor's views follow one by
/// one. The engines play through PortAudio - Core Audio on a Mac - so nothing is Windows-only here.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AudioOutput.Open = PortAudioOutput.Open;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia designer.
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
        Name = "MagSID";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? path = desktop.Args?.FirstOrDefault(arg => File.Exists(arg));
            desktop.MainWindow = new SidPlayerWindow(path);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
