using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace MagPlaySid;

/// <summary>
/// Whether a newer MagPlaySID is out: the version this one is against the latest published
/// release - the same one the website shows and offers for download. Drafts are not seen.
/// </summary>
internal static class UpdateCheck
{
    public const string Website = "https://magnarharestad.github.io/MagPlaySID/";

    private const string LatestRelease = "https://api.github.com/repos/MagnarHarestad/MagPlaySID/releases/latest";

    /// <summary>The version this build was stamped with - packaging/magplaysid passes the tag's.</summary>
    public static Version Current { get; } = Normalise(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    // After Current, which the client's User-Agent names: static fields start in the order written.
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The latest release's version when it is newer than this one; null when it is not,
    /// or when there is no answer - offline, or GitHub busy - which is no reason to bother anyone.</summary>
    public static async Task<Version?> NewerVersionAsync(CancellationToken cancel = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestRelease, cancel);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            return Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest) && Normalise(latest) > Current
                ? Normalise(latest)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"0.2" and "0.2.0.0" are the same version; <see cref="Version"/> says otherwise.</summary>
    private static Version Normalise(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>A version as the releases name it: v0.2, not 0.2.0.0.</summary>
    public static string Describe(Version v) =>
        "v" + (v.Revision > 0 ? v.ToString(4) : v.Build > 0 ? v.ToString(3) : v.ToString(2));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API turns away requests that do not say who they are.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MagPlaySID", Describe(Current)[1..]));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
