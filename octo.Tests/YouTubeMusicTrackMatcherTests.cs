using Octo.Services.Playback;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;

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
    public void CatalogRejectionExplainsTitleMismatch()
    {
        var identity = new TrackIdentity("Artist", "Song", "Album", 180, null, null);
        var song = new AlbumSong("Song (Live)", "video", false, null, TimeSpan.FromSeconds(180), 1);

        Assert.Equal("title/version mismatch",
            YouTubeMusicTrackMatcher.GetAlbumTrackRejectionReason(song, identity));
    }

    [Fact]
    public void SongSearchRequiresExplicitCandidateWhenRequested()
    {
        var identity = new TrackIdentity("Artist", "Song", null, null, null, true);
        var song = new SongSearchResult("Song", "video",
            [new NamedEntity("Artist", "artist")], null, TimeSpan.Zero, false, "plays", null, []);

        Assert.Equal("explicit candidate required",
            YouTubeMusicTrackMatcher.GetSongSearchRejectionReason(song, identity));
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

    [Fact]
    public void PremiumUpsellParserFailureIsRecognizedByParameterPath()
    {
        var exception = new ArgumentNullException(
            "overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint.watchEndpoint.playlistId");

        Assert.True(YouTubeMusicPlaybackService.IsPremiumUpsellParserFailure(exception));
    }

    [Fact]
    public void UnrelatedParserFailureIsNotRecognizedAsPremiumUpsell()
    {
        var exception = new ArgumentNullException("album.title");

        Assert.False(YouTubeMusicPlaybackService.IsPremiumUpsellParserFailure(exception));
    }

    [Fact]
    public async Task RawAlbumSearchCapturesBrowseIdWithoutPlaylistRedirect()
    {
        var albums = new YouTubeMusicAlbumCapture();
        using var operation = albums.BeginOperation();
        var videos = new YouTubeMusicVideoTypeCapture();
        using var handler = new YouTubeMusicRawResponseHandler(videos, albums)
        {
            InnerHandler = new FixedResponseHandler("""
                {"contents":[{"musicResponsiveListItemRenderer":{"navigationEndpoint":{"browseEndpoint":{"browseId":"MPREabc","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}},"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"We Don't Trust You"}]}}},{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Album"},{"text":"Future","navigationEndpoint":{"browseEndpoint":{"browseId":"UCfuture"}}}]}}}]}}]}
                """)
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search")
        {
            Content = new StringContent("{}")
        };

        using var response = await client.SendAsync(request);

        var album = Assert.Single(albums.Get());
        Assert.Equal("MPREabc", album.BrowseId);
        Assert.Equal("We Don't Trust You", album.Name);
    }

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed record EditionCandidate(string Id, bool ExplicitEdition)
        : YouTubeMusicTrackMatcher.IYouTubeMusicEditionCandidate;
}
