namespace Octo.Services.Playback;

public sealed record TrackIdentity(
    string Artist,
    string Title,
    string? Album,
    int? Duration,
    string? Version,
    bool? IsExplicit);
