using Avalonia.Controls;
using Avalonia.Threading;
using MagSid.Assembler;
using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Editor.App.Player;
using MagSid.Playback;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The toolbar's size and rastertime, as in the Windows editor: the song's length with what it is
/// made of, and the raster lines the player's own 6502 code takes - average and worst over a
/// pass of the song, measured on a copy after each run of edits, and while the song plays the
/// dearest of the frames just heard in place of the average.
/// </summary>
public sealed partial class MainWindow
{
    private const string NoRasterTime = "Rastertime: $-- / $-- (Max)";

    private readonly TextBlock _rasterTime = new() { Text = NoRasterTime };

    /// <summary>Restarted by every edit, so typing a run of notes measures once, after the last.</summary>
    private readonly DispatcherTimer _rasterDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private CancellationTokenSource? _rasterCancel;
    private RasterTimeResult? _rasterResult;
    private LiveRasterTime? _liveRaster;

    private void WireRasterTime() => _rasterDelay.Tick += (_, _) => MeasureRasterTime();

    /// <summary>The length on the toolbar and its parts beside it; the range follows whatever
    /// address the last export chose, since the player relocates without changing size.</summary>
    private void RefreshSongLength()
    {
        try
        {
            var size = SongSizeCalculator.Measure(_session.Song.WithUnusedPatternsRemoved());
            int bytes = size.TotalBytes;
            _songLength.Text = $"Song Length: ${bytes:X4} ({bytes} bytes)";

            int loadAddress = EditorSettings.SidLoadAddress;
            ToolTip.SetTip(_songLength, string.Join(Environment.NewLine,
                $"{bytes} bytes exported, ${loadAddress:X4}-${loadAddress + bytes - 1:X4}",
                $"  player      {size.PlayerBytes,5}",
                $"  patterns    {size.PatternBytes,5}",
                $"  pattern ptr {size.PatternPointerBytes,5}",
                $"  instruments {size.InstrumentTableBytes,5}",
                $"  wavetables  {size.WavetablePoolBytes,5}",
                $"  arpeggios   {size.ArpeggioPoolBytes,5}",
                $"  filters     {size.FilterPoolBytes,5}",
                $"  song order  {size.OrderBytes,5}",
                $"  note table  {size.NoteTableBytes,5}"));
        }
        catch (Exception)
        {
            _songLength.Text = "";
            ToolTip.SetTip(_songLength, null);
        }

        _rasterDelay.Stop();
        _rasterDelay.Start();
    }

    /// <summary>Measures the song as it stands now, on a copy, off the UI thread. A newer edit
    /// cancels a measurement still running, so only the latest one reaches the label.</summary>
    private async void MeasureRasterTime()
    {
        _rasterDelay.Stop();
        _rasterCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _rasterCancel = cancel;

        var song = SongCloner.Clone(_session.Song);
        int loadAddress = EditorSettings.SidLoadAddress;
        try
        {
            var result = await Task.Run(() =>
            {
                int frames = SongCoverageAnalyzer.PassFrames(song);
                return RasterTime.Measure(song, frames, loadAddress, cancel.Token);
            }, cancel.Token);

            if (!cancel.IsCancellationRequested)
                ShowRasterTime(result);
        }
        catch (OperationCanceledException)
        {
            // A newer edit took over.
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            // A song the exporter cannot build has no player to measure either.
            _rasterResult = null;
            _rasterTime.Text = NoRasterTime;
            ToolTip.SetTip(_rasterTime, "Not measured: " + ex.Message);
        }
    }

    /// <summary>Starts the 6502 player alongside a playback starting <paramref name="startFrame"/>
    /// frames into the song. A song that cannot be built simply gets no live figure.</summary>
    private void StartLiveRaster(int startFrame)
    {
        try
        {
            _liveRaster = new LiveRasterTime(SongCloner.Clone(_session.Song), EditorSettings.SidLoadAddress, startFrame);
        }
        catch (Exception)
        {
            _liveRaster = null;
        }
    }

    private void StopLiveRaster()
    {
        _liveRaster = null;
        if (_rasterResult is { } result)
            ShowRasterTime(result);
    }

    /// <summary>The first figure follows the music while it plays: the dearest of the last few
    /// frames heard. The second stays the whole song's worst.</summary>
    private void ShowLiveRaster(int heardFrame)
    {
        if (_liveRaster?.Advance(heardFrame) is not { } cycles)
            return;

        int lines = (int)Math.Ceiling(cycles / (double)RasterTimeResult.CyclesPerLine);
        string max = _rasterResult is { } result ? $"${result.MaxLines:X2}" : "$--";
        _rasterTime.Text = $"Rastertime: ${lines:X2} / {max} (Max)";
    }

    private void ShowRasterTime(RasterTimeResult result)
    {
        _rasterResult = result;
        if (_liveRaster is not null)
            return; // the live figure owns the label until playback stops

        _rasterTime.Text = $"Rastertime: ${result.AverageLines:X2} / ${result.MaxLines:X2} (Max)";

        static string At(int frame) => TimeSpan.FromSeconds(frame / 50.0).ToString(@"m\:ss\.ff");
        const int line = RasterTimeResult.CyclesPerLine;

        ToolTip.SetTip(_rasterTime, string.Join(Environment.NewLine,
            "Raster lines for \"jsr play\" - the player's own 6502 code, run cycle by cycle",
            $"over one pass of the song ({result.Frames} frames, {At(result.Frames)}).",
            $"  average {result.AverageCycles,5:F0} cycles  {result.AverageCycles / line,5:F1} lines",
            $"  max     {result.MaxCycles,5} cycles  {result.MaxCycles / (double)line,5:F1} lines, at {At(result.MaxFrame)}",
            $"  init    {result.InitCycles,5} cycles",
            "PAL: 63 cycles a line, 312 lines a frame. Badlines and sprites on",
            "screen take cycles too, so in the visible area it runs a little longer."));
    }
}
