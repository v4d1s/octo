using System.Text;
using Octo.Services.Playback;

namespace Octo.Services.Yandex;

internal sealed record YandexTrackCandidate(
    string? Title,
    string? Version,
    int? DurationMs,
    IReadOnlyList<string?> Artists,
    IReadOnlyList<YandexAlbumCandidate> Albums,
    bool? IsExplicit,
    bool IsAvailable = true);

internal sealed record YandexAlbumCandidate(string? Title, string? Version);

internal static class YandexTrackMatcher
{
    internal static bool IsMatch(YandexTrackCandidate track, TrackIdentity identity)
    {
        if (!track.IsAvailable || !Equal(track.Title, identity.Title) || !track.Artists.Any(a => Equal(a, identity.Artist))) return false;
        if (identity.Album is not null && !track.Albums.Any(a => Equal(a.Title, identity.Album))) return false;
        if (identity.Duration is int duration && track.DurationMs is int ms
            && Math.Abs(ms - duration * 1000) > Math.Max(10_000, duration * 50)) return false;

        var versionAlbums = identity.Album is null
            ? track.Albums
            : track.Albums.Where(a => Equal(a.Title, identity.Album)).ToList();
        if (identity.Album is not null && versionAlbums.Count == 0) return false;
        var albumVersion = versionAlbums.Select(a => a.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (!string.IsNullOrWhiteSpace(identity.Version))
        {
            if (!Equal(track.Version, identity.Version) && !Equal(albumVersion, identity.Version)) return false;
        }
        else if (!string.IsNullOrWhiteSpace(track.Version) || !string.IsNullOrWhiteSpace(albumVersion)) return false;

        if (identity.IsExplicit is bool explicitIdentity)
        {
            if (track.IsExplicit is not bool candidateExplicit || candidateExplicit != explicitIdentity) return false;
        }
        else if (track.IsExplicit == false)
        {
            // Prefer the normal/explicit edition when the source identity does not
            // carry an explicitness declaration. A known clean edition is a semantic
            // variant and must not be silently substituted.
            return false;
        }
        return true;
    }

    private static bool Equal(string? left, string? right) => Normalize(left) == Normalize(right);
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
            builder.Append(char.IsPunctuation(c) ? ' ' : c);
        return string.Join(' ', builder.ToString().Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }
}
