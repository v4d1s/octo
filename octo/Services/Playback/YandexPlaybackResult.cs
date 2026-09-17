namespace Octo.Services.Playback;

public enum YandexPlaybackStatus
{
    Matched,
    NotFound,
    Ambiguous,
    RateLimited,
    Unauthorized,
    TemporaryFailure,
}

public sealed record YandexPlaybackResult(
    YandexPlaybackStatus Status,
    string? Path = null,
    string? ContentType = null,
    string? Reason = null)
{
    public bool IsMatched => Status == YandexPlaybackStatus.Matched && Path is not null;
}
