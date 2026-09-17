namespace Octo.Models.Settings;

public sealed class YandexSettings
{
    public bool Enabled { get; set; }
    public string? OAuthToken { get; set; }
    public int PlaybackTimeoutSeconds { get; set; } = 20;
    public string? TemporaryPlaybackDirectory { get; set; }
    public int TemporaryFileTtlHours { get; set; } = 6;
}
