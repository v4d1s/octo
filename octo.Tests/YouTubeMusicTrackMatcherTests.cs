using Octo.Services.Playback;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Info;

namespace Octo.Tests;

public sealed class YouTubeMusicTrackMatcherTests
{
    [Fact]
    public void ExactAlbumTrackAndDurationAreAccepted()
    {
        var identity = new TrackIdentity("Artist", "Song", "Album", 180, null, null);
        var song = new AlbumSong("Song", "video", false, null, TimeSpan.FromSeconds(181), 1);

        Assert.True(YouTubeMusicTrackMatcher.MatchesAlbum("Album", [new NamedEntity("Artist", "artist")], identity));
        Assert.True(YouTubeMusicTrackMatcher.MatchesTitleAndDuration(song, identity));
    }

    [Theory]
    [InlineData("Song (Live)")]
    [InlineData("Song - Remix")]
    [InlineData("Song (Acoustic)")]
    [InlineData("Song (Karaoke)")]
    public void RecordingVariantsAreRejectedWithoutVersion(string title)
    {
        var identity = new TrackIdentity("Artist", "Song", "Album", 180, null, null);
        var song = new AlbumSong(title, "video", false, null, TimeSpan.FromSeconds(180), 1);

        Assert.False(YouTubeMusicTrackMatcher.MatchesTitleAndDuration(song, identity));
    }

    [Fact]
    public void ExplicitEditionIsPreferredWhenIdentityIsUnknown()
    {
        var candidates = new[]
        {
            new EditionCandidate("clean", false),
            new EditionCandidate("explicit", true),
        };

        Assert.Equal("explicit", YouTubeMusicTrackMatcher.SelectEdition(candidates, null)?.Id);
    }

    [Fact]
    public void NaturallyCleanTrackCanComeFromExplicitEdition()
    {
        var candidates = new[] { new EditionCandidate("explicit", true) };

        Assert.NotNull(YouTubeMusicTrackMatcher.SelectEdition(candidates, null));
    }

    [Fact]
    public void CleanRequestDoesNotUseExplicitEdition()
    {
        var candidates = new[]
        {
            new EditionCandidate("clean", false),
            new EditionCandidate("explicit", true),
        };

        Assert.Equal("clean", YouTubeMusicTrackMatcher.SelectEdition(candidates, false)?.Id);
    }

    [Fact]
    public void UnknownEditionDoesNotAcceptOnlyUnbadgedCandidate()
    {
        var candidates = new[] { new EditionCandidate("unknown", false) };

        Assert.Empty(YouTubeMusicTrackMatcher.FilterEditions(candidates, null));
    }

    private sealed record EditionCandidate(string Id, bool ExplicitEdition)
        : YouTubeMusicTrackMatcher.IYouTubeMusicEditionCandidate;
}
