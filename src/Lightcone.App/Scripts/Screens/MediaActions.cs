using Lightcone.App.Playback;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// What opening an item does, shared by both UI modes so they never drift apart: containers and
/// movies open their details; episodes and tracks play; "Continue watching" resumes directly.
/// </summary>
public static class MediaActions
{
    public static void Open(MediaItem item)
    {
        switch (item.Kind)
        {
            case MediaKind.Episode or MediaKind.Track:
                Play(item);
                break;
            default:
                Main.Instance.Push(new DetailPage(item));
                break;
        }
    }

    public static void Resume(MediaItem item) => Play(item);

    public static void Play(MediaItem item, bool fromStart = false)
    {
        if (!PlayerService.EngineAvailable(out var why))
        {
            Main.Instance.Error("Can't play: the playback engine (libmpv) isn't available. " + why);
            return;
        }
        PlayerService.Instance.Play(item, fromStart);
        Main.Instance.ShowPlayer();
    }

    /// <summary>Plays a show's next unwatched episode, or an album from the top.</summary>
    public static void PlayContainer(MediaItem container, bool shuffle = false)
    {
        var lib = Main.Instance.Host!.Library;
        var children = lib.Children(container.Id).Where(c => c.Playable).ToList();
        if (children.Count == 0)
        {
            return;
        }
        var next = shuffle ? children[Random.Shared.Next(children.Count)]
            : children.FirstOrDefault(c => lib.PositionOf(c) != null) ?? children.FirstOrDefault(c => !lib.Progress.IsWatched(c.Id)) ?? children[0];
        Play(next);
    }
}
