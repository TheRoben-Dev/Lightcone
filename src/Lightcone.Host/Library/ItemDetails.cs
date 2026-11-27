namespace Lightcone.Host.Library;

/// <summary>Someone in a movie or show: an actor (with the part they play) or crew (director, writer).</summary>
/// <param name="Key">The source's id for the person ("actor:1234" on Plex, a person id on Jellyfin).</param>
public sealed record Person(string Name, string? Role, string Job, string? PhotoUrl, string SourceId, string Key)
{
    public const string Actor = "Actor", Director = "Director", Writer = "Writer";

    /// <summary>"as Ellen Ripley", "Director".</summary>
    public string Credit => Job == Actor ? (Role is { Length: > 0 } r ? r : "Cast") : Job;
}

/// <summary>A trailer, featurette, deleted scene or other extra the server keeps with a title.</summary>
/// <param name="Kind">"Trailer", "Behind the scenes", "Deleted scene", "Featurette", "Interview", "Short", "Extra".</param>
public sealed record Extra(string Title, string Kind, double? Duration, string PlayUrl, string? ThumbUrl)
{
    public bool IsTrailer => Kind == "Trailer";
}

/// <summary>What a detail page loads on demand.</summary>
public sealed record ItemDetails(IReadOnlyList<Person> People, IReadOnlyList<Extra> Extras)
{
    public Extra? Trailer => Extras.FirstOrDefault(e => e.IsTrailer);

    public static string ExtraKind(string? plexSubtype) => plexSubtype switch
    {
        "trailer" or "Trailer" => "Trailer",
        "behindTheScenes" or "BehindTheScenes" => "Behind the scenes",
        "deleted" or "DeletedScene" => "Deleted scene",
        "featurette" or "Featurette" => "Featurette",
        "interview" or "Interview" => "Interview",
        "short" or "Short" => "Short",
        "sceneOrSample" or "Scene" or "Sample" => "Scene",
        "Clip" => "Clip",
        _ => "Extra",
    };
}
