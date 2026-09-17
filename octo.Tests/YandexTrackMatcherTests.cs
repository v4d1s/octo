using Octo.Services.Playback;
using Octo.Services.Yandex;

namespace Octo.Tests;

public sealed class YandexTrackMatcherTests
{
    [Fact]
    public void ExactMetadataMatchIsAccepted()
    {
        var candidate = new YandexTrackCandidate("Song!", null, 180000, ["Artist"], [new YandexAlbumCandidate("Album", null)], null);
        Assert.True(YandexTrackMatcher.IsMatch(candidate, new("artist", "song", "album", 180, null, null)));
    }

    [Fact]
    public void RecordingVariantIsRejected()
    {
        var candidate = new YandexTrackCandidate("Song", "Live", 180000, ["Artist"], [new YandexAlbumCandidate("Album", null)], null);
        Assert.False(YandexTrackMatcher.IsMatch(candidate, new("Artist", "Song", "Album", 180, null, null)));
    }

    [Fact]
    public void DurationOutsideToleranceIsRejected()
    {
        var candidate = new YandexTrackCandidate("Song", null, 260000, ["Artist"], [], null);
        Assert.False(YandexTrackMatcher.IsMatch(candidate, new("Artist", "Song", null, 180, null, null)));
    }

    [Fact]
    public void KnownCleanEditionIsNotAcceptedForUnknownIdentity()
    {
        var candidate = new YandexTrackCandidate("Song", null, 180000, ["Artist"],
            [new YandexAlbumCandidate("Album", null)], false);
        Assert.False(YandexTrackMatcher.IsMatch(candidate, new("Artist", "Song", "Album", 180, null, null)));
    }

    [Fact]
    public void ExplicitEditionMayBeUsedWhenIdentityIsUnknown()
    {
        var candidate = new YandexTrackCandidate("Song", null, 180000, ["Artist"],
            [new YandexAlbumCandidate("Album", null)], true);
        Assert.True(YandexTrackMatcher.IsMatch(candidate, new("Artist", "Song", "Album", 180, null, null)));
    }

    [Fact]
    public void PunctuationAndUnicodeWhitespaceAreNormalized()
    {
        var candidate = new YandexTrackCandidate("Song / Title", null, null, ["Artist"], [], null);
        Assert.True(YandexTrackMatcher.IsMatch(candidate, new("Artist", "Song Title", null, null, null, null)));
    }

    [Fact]
    public void VersionFromAnotherAlbumDoesNotRejectExactAlbum()
    {
        var candidate = new YandexTrackCandidate("Song", null, 180000, ["Artist"],
            [new YandexAlbumCandidate("Album", null), new YandexAlbumCandidate("Other Album", "Remastered")], null);
        Assert.True(YandexTrackMatcher.IsMatch(candidate, new("Artist", "Song", "Album", 180, null, null)));
    }
}
