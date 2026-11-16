using System.Net.Http.Headers;

namespace MagPlaySid;

/// <summary>A music source - HVSC, or a tune of it - could not be had: the message says why, for the user.</summary>
internal sealed class MusicSourceException(string message) : Exception(message);

/// <summary>
/// What the music sources share on the web: files fetched from the first mirror that answers, written
/// whole or not at all, and large archives downloaded to disk with their progress told.
/// </summary>
internal static class MusicWeb
{
    private static readonly HttpClient Http = Client(TimeSpan.FromSeconds(30));

    /// <summary>For an archive of a hundred megabytes or so: time to come down a slow line.</summary>
    private static readonly HttpClient Downloads = Client(TimeSpan.FromHours(2));

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MagPlaySID", UpdateCheck.Describe(UpdateCheck.Current)[1..]));
        return http;
    }

    /// <summary>A file of a source - <paramref name="path"/> from its top, "/" between folders - from
    /// the first of its mirrors that has it. Throws <see cref="MusicSourceException"/> saying why
    /// when none does.</summary>
    public static async Task<byte[]> GetAsync(string source, IReadOnlyList<string> mirrors, string path, CancellationToken cancel)
    {
        string url = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        Exception? last = null;
        foreach (var mirror in mirrors)
        {
            try
            {
                using var response = await Http.GetAsync(mirror + url, cancel);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsByteArrayAsync(cancel);
                last = new MusicSourceException($"{new Uri(mirror).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last is MusicSourceException ? last
            : new MusicSourceException(last is TaskCanceledException ? $"{source} did not answer in time" : $"{source} could not be reached: {last?.Message}");
    }

    /// <summary>A small text from the web - an API's answer.</summary>
    public static async Task<string> GetTextAsync(string source, string url, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode)
                throw new MusicSourceException($"{new Uri(url).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            return await response.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new MusicSourceException(ex is TaskCanceledException ? $"{source} did not answer in time" : $"{source} could not be reached: {ex.Message}");
        }
    }

    /// <summary>How large a file on the web is, without fetching it; null when it does not say.</summary>
    public static async Task<long?> SizeAsync(string url, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(request, cancel);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A large file downloaded to <paramref name="file"/> - to a file beside it first, so a
    /// download cut short leaves nothing that looks whole - telling how much is in, 0 to 1.</summary>
    public static async Task DownloadAsync(string source, string url, string file, IProgress<double>? progress, CancellationToken cancel)
    {
        string part = file + ".part";
        try
        {
            using var response = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw new MusicSourceException($"{new Uri(url).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            long? total = response.Content.Headers.ContentLength;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await using (var from = await response.Content.ReadAsStreamAsync(cancel))
            await using (var to = File.Create(part))
            {
                var buffer = new byte[1 << 16];
                long got = 0;
                int told = -1;
                for (int read; (read = await from.ReadAsync(buffer, cancel)) > 0;)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), cancel);
                    got += read;
                    if (total is > 0 && (int)(got * 500 / total.Value) is var now && now != told)
                    {
                        told = now;
                        progress?.Report((double)got / total.Value);
                    }
                }
                if (total is { } expected && got != expected)
                    throw new MusicSourceException($"{source}'s download stopped at {got:N0} of {expected:N0} bytes");
            }
            File.Move(part, file, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new MusicSourceException(ex is TaskCanceledException ? $"{source} did not answer in time" : $"{source} could not be reached: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Written whole, or not at all: to a file beside it first.</summary>
    public static async Task SaveAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string part = path + ".part";
        await File.WriteAllBytesAsync(part, bytes);
        File.Move(part, path, overwrite: true);
    }

    /// <summary>What a source's failure is: told to the user, never thrown on.</summary>
    public static bool Failed(Exception ex) => ex is MusicSourceException or IOException or UnauthorizedAccessException;
}
