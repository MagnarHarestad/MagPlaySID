using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace MagPlaySid;

/// <summary>
/// The window's threads a step above other programs': the one that handles input and lays out, and the
/// one that draws. Another program spinning every core at its usual priority - a build, an AI agent at
/// work - then no longer holds the window still for a fifth of a second at a time. The sound's own thread
/// stays above both; the work in the background - hearing notes, reading lists - stays at the usual
/// priority, so MagPlaySID never takes from other programs more than its window's share.
/// </summary>
internal static class WindowPriority
{
    /// <summary>The calling thread raised a step - never lowered, as the system may have set it higher.
    /// Where the system will not, it stays as it is.</summary>
    public static void Raise()
    {
        try
        {
            if (Thread.CurrentThread.Priority < ThreadPriority.AboveNormal)
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>The thread that draws <paramref name="window"/> raised from within it: a visual of no
    /// size whose handler runs there is sent a word, and raises the thread it is read on.</summary>
    public static void RaiseDrawing(Window window)
    {
        if (ElementComposition.GetElementVisual(window)?.Compositor is not { } compositor)
            return;
        var visual = compositor.CreateCustomVisual(new Raiser());
        visual.Size = default;
        ElementComposition.SetElementChildVisual(window, visual);
        visual.SendHandlerMessage(Word);
    }

    private static readonly object Word = new();

    private sealed class Raiser : CompositionCustomVisualHandler
    {
        public override void OnMessage(object message) => Raise();

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
        }
    }
}
