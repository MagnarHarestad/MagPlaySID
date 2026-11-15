using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MagPlaySid;

/// <summary>
/// Where the window opens and how big: as it was left when the program was closed, or else its
/// usual size centred - either way made to fit the screen it opens on, title bar and all, so a small
/// or scaled-up screen still shows the buttons to move, maximize and close it.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>The window's usual size, and the least it is let shrink to, in device-independent
    /// pixels - smaller on a screen that has no room for them.</summary>
    public const double UsualWidth = 1180, UsualHeight = 900, LeastWidth = 900, LeastHeight = 600;

    /// <summary>Room left for the window's frame around what it shows: its edges, and the title bar
    /// above. The window is set right once it is open and its frame is known.</summary>
    public static readonly Size Frame = new(16, 40);

    /// <summary>Its size and place to open with on <paramref name="workingArea"/> - the screen's
    /// pixels less its taskbar, at <paramref name="scaling"/> - from the size kept, or the usual
    /// size, no bigger than fits; at <paramref name="kept"/> when the window is wholly on the
    /// screen there, else centred. Also the least size the screen has room for.</summary>
    public static (Size Size, PixelPoint Position, Size Least) Fit(PixelRect workingArea, double scaling,
                                                                   Size? keptSize, PixelPoint? kept)
    {
        double roomWidth = (workingArea.Width / scaling) - Frame.Width;
        double roomHeight = (workingArea.Height / scaling) - Frame.Height;
        var least = new Size(Math.Max(200, Math.Min(LeastWidth, roomWidth)), Math.Max(200, Math.Min(LeastHeight, roomHeight)));
        var want = keptSize is { Width: > 0, Height: > 0 } size ? size : new Size(UsualWidth, UsualHeight);
        var fits = new Size(Math.Max(least.Width, Math.Min(want.Width, roomWidth)),
                            Math.Max(least.Height, Math.Min(want.Height, roomHeight)));

        var outer = new PixelSize((int)Math.Ceiling((fits.Width + Frame.Width) * scaling),
                                  (int)Math.Ceiling((fits.Height + Frame.Height) * scaling));
        // A little past the edges allowed: Windows counts the window's unseen resize borders in its
        // place, so one snapped to the screen's side starts a few pixels off it.
        int slack = (int)Math.Ceiling(8 * scaling);
        var room = new PixelRect(workingArea.X - slack, workingArea.Y - slack, workingArea.Width + (2 * slack), workingArea.Height + (2 * slack));
        if (kept is { } at && room.Contains(new PixelRect(at, outer)))
            return (fits, at, least);
        var centred = new PixelPoint(workingArea.X + Math.Max(0, (workingArea.Width - outer.Width) / 2),
                                     workingArea.Y + Math.Max(0, (workingArea.Height - outer.Height) / 2));
        return (fits, centred, least);
    }

    /// <summary>The screen to open on: the one the window was left on, while it is there, else the
    /// main one.</summary>
    public static Screen? ScreenFor(Screens screens, PixelPoint? kept) =>
        (kept is { } at ? screens.All.FirstOrDefault(s => s.WorkingArea.Contains(at)) : null) ?? screens.Primary ?? screens.All.FirstOrDefault();

    /// <summary>The window, open and its frame known, pulled onto <paramref name="workingArea"/>:
    /// shorter and narrower when it does not fit, and moved so its title bar is on the screen.</summary>
    public static (Size Size, PixelPoint Position)? Correct(PixelRect workingArea, double scaling, Size client, Size frame, PixelPoint position)
    {
        double roomWidth = workingArea.Width / scaling, roomHeight = workingArea.Height / scaling;
        double width = client.Width - Math.Max(0, frame.Width - roomWidth);
        double height = client.Height - Math.Max(0, frame.Height - roomHeight);
        var outer = new PixelSize((int)Math.Ceiling((frame.Width - (client.Width - width)) * scaling),
                                  (int)Math.Ceiling((frame.Height - (client.Height - height)) * scaling));
        // The unseen resize borders let past the sides and the bottom, as a window snapped there has
        // them; the title bar kept wholly on the screen.
        int slack = (int)Math.Ceiling(8 * scaling);
        int x = Math.Clamp(position.X, workingArea.X - slack, Math.Max(workingArea.X - slack, workingArea.Right + slack - outer.Width));
        int y = Math.Clamp(position.Y, workingArea.Y, Math.Max(workingArea.Y, workingArea.Bottom + slack - outer.Height));
        if (width == client.Width && height == client.Height && x == position.X && y == position.Y)
            return null;
        return (new Size(width, height), new PixelPoint(x, y));
    }
}
