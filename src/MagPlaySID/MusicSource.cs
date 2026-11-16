namespace MagPlaySid;

/// <summary>
/// A collection MagPlaySID browses above the playlists - HVSC, Modland - as the strip and the player see
/// it: its list of tunes, where each is kept, and its fetch when it is first played.
/// </summary>
internal abstract class MusicSource
{
    public static readonly MusicSource Hvsc = new HvscSource();
    public static readonly MusicSource Modland = new ModlandSource();
    public static readonly IReadOnlyList<MusicSource> All = [Hvsc, Modland];

    public abstract Collection Kind { get; }

    /// <summary>Its short name: "HVSC".</summary>
    public abstract string Name { get; }

    /// <summary>Its full name: "The High Voltage SID Collection".</summary>
    public abstract string FullName { get; }

    /// <summary>What its entry above the playlists tells with the mouse over it.</summary>
    public abstract string Tip { get; }

    /// <summary>Whether its list and tunes come from a copy on this computer, not online.</summary>
    public abstract bool OnThisPc { get; }

    /// <summary>What is shown while its list is read.</summary>
    public abstract string Loading { get; }

    /// <summary>Its list, once it has been read; null before.</summary>
    public abstract MusicCatalog? Catalog { get; }

    public abstract Task<MusicCatalog> CatalogAsync();

    /// <summary>Where one of its tunes - by its path in it - is kept on this computer.</summary>
    public abstract string LocalPath(string path);

    /// <summary>A kept tune's path in it; null for a file that is not one of its.</summary>
    public abstract string? PathOf(string localPath);

    public abstract bool NeedsFetch(string localPath);

    public abstract Task FetchAsync(string localPath);

    /// <summary>Whether its own names for its tunes are kept over the ones in their files: Modland's
    /// "crosswind" by 4-Mat over a module's own "4mat - x-wind".</summary>
    public virtual bool KeepsNames => false;

    /// <summary>The collection a kept file is one of; null for none.</summary>
    public static MusicSource? Of(string localPath) => All.FirstOrDefault(s => s.PathOf(localPath) is not null);

    /// <summary>Whether a tune is one of a collection's that is to be fetched before it plays.</summary>
    public static bool NeedsFetchAny(string localPath) => Of(localPath)?.NeedsFetch(localPath) == true;

    /// <summary>A tune of a collection as its list has it, when the list has been read; null else.</summary>
    public static MusicTune? Listed(string localPath) =>
        Of(localPath) is { } source && source.PathOf(localPath) is { } path ? source.Catalog?.Tune(path) : null;

    private sealed class HvscSource : MusicSource
    {
        public override Collection Kind => Collection.Hvsc;
        public override string Name => "HVSC";
        public override string FullName => "The High Voltage SID Collection";
        public override string Tip => "The High Voltage SID Collection: every C64 tune, fetched as it is played";
        public override bool OnThisPc => HvscArchive.Folder is not null;
        public override string Loading => HvscArchive.Folder is not null
            ? "Reading the list of HVSC's tunes from your copy..."
            : "Getting the list of HVSC's tunes - 5 MB, fetched again once a week...";
        public override MusicCatalog? Catalog => MagPlaySid.Hvsc.Catalog;
        public override Task<MusicCatalog> CatalogAsync() => MagPlaySid.Hvsc.CatalogAsync();
        public override string LocalPath(string path) => MagPlaySid.Hvsc.LocalPath(path);
        public override string? PathOf(string localPath) => MagPlaySid.Hvsc.HvscPath(localPath);
        public override bool NeedsFetch(string localPath) => MagPlaySid.Hvsc.NeedsFetch(localPath);
        public override Task FetchAsync(string localPath) => MagPlaySid.Hvsc.FetchAsync(localPath);
    }

    private sealed class ModlandSource : MusicSource
    {
        public override Collection Kind => Collection.Modland;
        public override string Name => "Modland";
        public override string FullName => "Modland's modules";
        public override string Tip => "Modland: the Amiga's and the PC's modules - Protracker, Fasttracker, Screamtracker, Impulse Tracker - fetched as they are played";
        public override bool OnThisPc => false;
        public override string Loading => File.Exists(MagPlaySid.Modland.ListPath)
            ? "Reading the list of Modland's modules..."
            : "Getting the list of Modland's modules - 6 MB, fetched again once a week...";
        public override MusicCatalog? Catalog => MagPlaySid.Modland.Catalog;
        public override Task<MusicCatalog> CatalogAsync() => MagPlaySid.Modland.CatalogAsync();
        public override string LocalPath(string path) => MagPlaySid.Modland.LocalPath(path);
        public override string? PathOf(string localPath) => MagPlaySid.Modland.ModlandPath(localPath);
        public override bool NeedsFetch(string localPath) => MagPlaySid.Modland.NeedsFetch(localPath);
        public override Task FetchAsync(string localPath) => MagPlaySid.Modland.FetchAsync(localPath);
        public override bool KeepsNames => true;
    }
}
