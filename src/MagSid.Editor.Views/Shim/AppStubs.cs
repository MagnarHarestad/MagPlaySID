// The two pieces of the Windows editor the DAW calls into that are about Windows rather than the
// DAW: the dark theme (Avalonia's is dark already) and the bank-folder helpers of the Retro view's
// list header, here without the header.
using System.Diagnostics;
using MagSid.Core.Midi;

namespace MagSid.Editor.App
{
    internal static class DarkTheme
    {
        public static void Apply(object control)
        {
        }

        public static void ApplyTitleBar(object form)
        {
        }
    }
}

namespace MagSid.Editor.App.Controls
{
    public static class InstrumentListHeader
    {
        public static string SoundLabel(string key) => GmBank.Label(key);

        /// <summary>Opens the folder of the user's own bank sounds in the system's file browser.</summary>
        public static void OpenBankFolder()
        {
            Directory.CreateDirectory(EditorSettings.BankDirectory);
            string opener = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsLinux() ? "xdg-open" : EditorSettings.BankDirectory;
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo { FileName = EditorSettings.BankDirectory, UseShellExecute = true }
                : new ProcessStartInfo { FileName = opener, ArgumentList = { EditorSettings.BankDirectory }, UseShellExecute = false };
            try
            {
                Process.Start(start);
            }
            catch (Exception)
            {
            }
        }
    }
}
