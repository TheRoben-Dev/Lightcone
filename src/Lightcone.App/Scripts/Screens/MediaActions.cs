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

    /// <summary>Plays an item; an episode after a break may show the "Previously" recap first (<paramref name="recap"/>).</summary>
    public static void Play(MediaItem item, bool fromStart = false, bool recap = true)
    {
        if (!PlayerService.EngineAvailable(out var why))
        {
            Main.Instance.Error("Can't play: the playback engine (libmpv) isn't available. " + why);
            return;
        }
        if (recap && RecapPage.RecapFor(item, fromStart) is { } previous)
        {
            Main.Instance.Push(new RecapPage(item, previous));
            return;
        }
        PlayerService.Instance.Play(item, fromStart);
        Main.Instance.ShowPlayer();
    }

    /// <summary>Plays a trailer or other extra of <paramref name="owner"/> (no progress or history kept).</summary>
    public static void PlayExtra(MediaItem owner, Extra extra)
    {
        if (!PlayerService.EngineAvailable(out var why))
        {
            Main.Instance.Error("Can't play: the playback engine (libmpv) isn't available. " + why);
            return;
        }
        var item = new MediaItem
        {
            Id = $"extra:{owner.Id}:{extra.Title}",
            Title = extra.Title,
            Subtitle = $"{owner.Title}  ·  {extra.Kind}",
            Kind = MediaKind.Movie,
            Duration = extra.Duration,
            PosterUrl = extra.ThumbUrl,
            PlayUrl = extra.PlayUrl,
            SourceId = PlayerService.ExtraSource,
            SourceName = owner.SourceName,
        };
        PlayerService.Instance.Play(item, fromStart: true);
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
        if (shuffle && container.Kind == MediaKind.Show)
        {
            if (!PlayerService.EngineAvailable(out var why))
            {
                Main.Instance.Error("Can't play: the playback engine (libmpv) isn't available. " + why);
                return;
            }
            PlayerService.Instance.Shuffle(container);
            Main.Instance.ShowPlayer();
            Main.Instance.Toast($"Shuffling {container.Title}");
            return;
        }
        var next = shuffle ? children[Random.Shared.Next(children.Count)] : lib.NextToPlay(container) ?? children[0];
        Play(next);
    }
}
