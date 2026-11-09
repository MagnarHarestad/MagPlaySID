using System.Text.Json.Serialization;

namespace MagPlaySid;

/// <summary>
/// The app's own files as JSON, the code to read and write them made when the app is built: read at
/// start, JSON worked out by reflection took a tenth of a second before the window could show.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(Favourites.Kept))]
[JsonSerializable(typeof(List<Favourites.Stored>))]
[JsonSerializable(typeof(Dictionary<string, SongLengthEntry>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
[JsonSerializable(typeof(Dictionary<string, TuneRecord>))]
internal sealed partial class AppJson : JsonSerializerContext;
