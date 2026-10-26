using System.Runtime.InteropServices;
using System.Text;

namespace MagSid.Editor.App;

/// <summary>
/// The editor run from its MSIX package - the Microsoft Store's - rather than the zip. Windows
/// then keeps what the editor writes under AppData in a folder of the package's own, and the
/// editor reads and writes there without noticing; but Explorer runs outside the package and
/// would open the real, empty folder instead. <see cref="Visible"/> is the folder to show it.
/// </summary>
internal static class PackagedApp
{
    private const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    /// <summary>The package's family name, or null when the editor runs from the zip.</summary>
    public static string? FamilyName { get; } = ReadFamilyName();

    private static string? ReadFamilyName()
    {
        try
        {
            int length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) == NoPackage || length == 0)
                return null;
            var name = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Where a folder under the roaming AppData really is - the package's own copy when
    /// it has one - for opening in Explorer. Anything else, or the editor run from the zip, is
    /// the path as it is.</summary>
    public static string Visible(string path)
    {
        if (FamilyName is not { } family)
            return path;

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!path.StartsWith(roaming, StringComparison.OrdinalIgnoreCase))
            return path;

        string redirected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", family, "LocalCache", "Roaming", path[roaming.Length..].TrimStart('\\'));
        return Directory.Exists(redirected) ? redirected : path;
    }
}
