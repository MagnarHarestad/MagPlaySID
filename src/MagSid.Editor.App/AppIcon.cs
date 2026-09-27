using System.Reflection;

namespace MagSid.Editor.App;

/// <summary>
/// The MagSID window icon, loaded once from the embedded MagSID.ico and shared by every
/// window - a form left with WinForms' generic default icon sitting next to one carrying the
/// real icon reads as a bug rather than a style choice.
///
/// The .ico is embedded as a resource *and* set as the csproj's ApplicationIcon: the latter
/// is what Explorer, the taskbar and Alt-Tab read off the .exe itself, but it does not
/// populate Form.Icon, so the windows still have to be told explicitly.
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon?> Instance = new(Load);

    public static Icon? Value => Instance.Value;

    private static Icon? Load()
    {
        Assembly assembly = typeof(AppIcon).Assembly;

        // Matched by suffix rather than a hardcoded "MagSid.Editor.App.MagSID.ico": the
        // manifest name is built from the root namespace, so a project rename would silently
        // break a literal and leave every window on the default icon.
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using Stream? stream = assembly.GetManifestResourceStream(name);
        return stream is null ? null : new Icon(stream);
    }
}
