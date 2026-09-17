using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;
using YouTubeMusicAPI.Pagination;

namespace Octo.Services.Playback;

public enum YouTubeMusicPlaybackStatus
{
    Matched,
    NotFound,
    Ambiguous,
    TemporaryFailure,
}

public sealed class YouTubeMusicPlaybackResult
{
    public required YouTubeMusicPlaybackStatus Status { get; init; }
    public Stream? AudioStream { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public int StatusCode { get; init; } = 200;
    public string? ContentRange { get; init; }
    public string? Reason { get; init; }

    public bool IsMatched => Status == YouTubeMusicPlaybackStatus.Matched && AudioStream is not null;
}

public interface IYouTubeMusicPlaybackService
{
    Task<YouTubeMusicPlaybackResult> TryOpenStreamAsync(
        TrackIdentity identity,
        string? rangeHeader,
        CancellationToken cancellationToken = default);
}

public sealed class YouTubeMusicVideoTypeCapture
{
    private readonly AsyncLocal<Dictionary<string, string>?> _current = new();

    public IDisposable BeginOperation()
    {
        var previous = _current.Value;
        _current.Value = new Dictionary<string, string>(StringComparer.Ordinal);
        return new OperationScope(() => _current.Value = previous);
    }

    public void Set(string videoId, string? type)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(type)) return;
        var types = _current.Value;
        if (types is null) return;
        if (types.TryGetValue(videoId, out var previous)
            && !string.Equals(previous, type, StringComparison.OrdinalIgnoreCase))
            types[videoId] = "__CONFLICT__";
        else
            types[videoId] = type;
    }

    public void Reset(string videoId) => _current.Value?.Remove(videoId);
    public string? Get(string videoId) => _current.Value is { } types && types.TryGetValue(videoId, out var type) ? type : null;

    private sealed class OperationScope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

internal sealed class YouTubeMusicRawResponseHandler(YouTubeMusicVideoTypeCapture capture) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? videoId = null;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            videoId = TryFindVideoId(bytes);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content.Dispose();
            request.Content = replacement;
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (videoId is null || response.Content is null) return response;

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var oldContent = response.Content;
        var replacementBody = new ByteArrayContent(body);
        foreach (var header in oldContent.Headers)
            replacementBody.Headers.TryAddWithoutValidation(header.Key, header.Value);
        oldContent.Dispose();
        response.Content = replacementBody;

        var type = request.RequestUri?.AbsolutePath.Contains("player", StringComparison.OrdinalIgnoreCase) == true
            ? FindString(body, "videoDetails", "musicVideoType")
            : FindWatchEndpointType(body, videoId);
        capture.Set(videoId, type);
        return response;
    }

    private static string? TryFindVideoId(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return FindString(document.RootElement, "videoId");
        }
        catch (JsonException) { return null; }
    }

    private static string? FindString(byte[] body, params string[] path)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return FindString(document.RootElement, path);
        }
        catch (JsonException) { return null; }
    }

    private static string? FindString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? FindWatchEndpointType(byte[] body, string videoId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return FindWatchEndpointType(document.RootElement, videoId);
        }
        catch (JsonException) { return null; }
    }

    private static string? FindWatchEndpointType(JsonElement element, string videoId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("videoId", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == videoId
                && element.TryGetProperty("watchEndpointMusicSupportedConfigs", out var configs)
                && configs.TryGetProperty("watchEndpointMusicConfig", out var musicConfig)
                && musicConfig.TryGetProperty("musicVideoType", out var type)
                && type.ValueKind == JsonValueKind.String)
                return type.GetString();

            foreach (var property in element.EnumerateObject())
                if (FindWatchEndpointType(property.Value, videoId) is string found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindWatchEndpointType(item, videoId) is string found) return found;
        }
        return null;
    }
}

internal sealed class AnonymousCookieStripHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("Cookie");
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Isolated anonymous YouTube Music catalog and CDN adapter. Public library
/// models remain inside this service; Octo only sees a prepared HTTP stream.
/// </summary>
public sealed class YouTubeMusicPlaybackService : IYouTubeMusicPlaybackService
{
    private const string ApiClientName = "YouTubeMusicApi";
    private const string CdnClientName = "YouTubeMusicCdn";
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<YouTubeMusicPlaybackService> _logger;
    private readonly YouTubeMusicVideoTypeCapture _videoTypes;

    public YouTubeMusicPlaybackService(IHttpClientFactory clients, ILogger<YouTubeMusicPlaybackService> logger,
        YouTubeMusicVideoTypeCapture videoTypes)
    {
        _clients = clients;
        _logger = logger;
        _videoTypes = videoTypes;
    }

    public async Task<YouTubeMusicPlaybackResult> TryOpenStreamAsync(
        TrackIdentity identity,
        string? rangeHeader,
        CancellationToken cancellationToken = default)
    {
        using var videoTypeOperation = _videoTypes.BeginOperation();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var operationToken = deadline.Token;
        try
        {
            var client = new YouTubeMusicClient(
                geographicalLocation: "US",
                cookies: null,
                poToken: null,
                httpClient: _clients.CreateClient(ApiClientName));
            var resolved = await ResolveAsync(client, identity, operationToken);
            if (resolved.Status != YouTubeMusicPlaybackStatus.Matched || resolved.VideoId is null)
                return new() { Status = resolved.Status, Reason = resolved.Reason };

            var streaming = await client.GetStreamingDataAsync(resolved.VideoId, operationToken);
            if (streaming.IsLiveContent)
                return new() { Status = YouTubeMusicPlaybackStatus.NotFound, Reason = "live content is not playable" };

            var audio = streaming.StreamInfo
                .OfType<AudioStreamInfo>()
                .Where(s => s.Container.ContainsAudio && !s.Container.ContainsVideo && !string.IsNullOrWhiteSpace(s.Url))
                .OrderByDescending(s => s.Bitrate)
                .ThenByDescending(s => s.SampleRate)
                .ToList();
            if (audio.Count == 0)
                return new() { Status = YouTubeMusicPlaybackStatus.NotFound, Reason = "no audio-only representation" };

            return await OpenCdnAsync(audio[0], rangeHeader, operationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube Music playback preparation failed for {Artist} - {Title}", identity.Artist, identity.Title);
            return new() { Status = YouTubeMusicPlaybackStatus.TemporaryFailure, Reason = ex.Message };
        }
    }

    private async Task<Resolution> ResolveAsync(
        YouTubeMusicClient client,
        TrackIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(identity.Album))
            return await ResolveFromAlbumAsync(client, identity, cancellationToken);
        return await ResolveFromSongSearchAsync(client, identity, cancellationToken);
    }

    private async Task<Resolution> ResolveFromAlbumAsync(
        YouTubeMusicClient client,
        TrackIdentity identity,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SearchResult> search;
        try
        {
            search = await client.SearchAsync(identity.Album!, SearchCategory.Albums)
                .FetchItemsAsync(0, 20, cancellationToken);
        }
        catch (ArgumentNullException)
        {
            return new(YouTubeMusicPlaybackStatus.TemporaryFailure, null, "album search response could not be parsed");
        }
        var candidates = new List<AlbumTrackCandidate>();
        foreach (var result in search.OfType<AlbumSearchResult>())
        {
            if (!YouTubeMusicTrackMatcher.MatchesAlbum(result.Name, result.Artists, identity)) continue;
            if (result.IsSingle || result.IsEp) continue;

            AlbumInfo album;
            try
            {
                var browseId = await client.GetAlbumBrowseIdAsync(result.Id, cancellationToken);
                album = await client.GetAlbumInfoAsync(browseId, cancellationToken);
            }
            catch (ArgumentNullException)
            {
                return new(YouTubeMusicPlaybackStatus.TemporaryFailure, null,
                    "album response could not be parsed");
            }

            if (!YouTubeMusicTrackMatcher.MatchesAlbum(album.Name, album.Artists, identity)
                || album.IsSingle || album.IsEp || album.Songs.Length != album.SongCount)
                continue;
            // The public model exposes only per-track badges. An all-clean tracklist
            // does not prove that this is a clean edition, so do not guess its edition.
            if (!album.Songs.Any(s => s.IsExplicit)) continue;

            foreach (var song in album.Songs.Where(s => YouTubeMusicTrackMatcher.MatchesTitleAndDuration(s, identity)))
            {
                if (string.IsNullOrWhiteSpace(song.Id)) continue;
                _videoTypes.Reset(song.Id);
                SongVideoInfo info;
                try { info = await client.GetSongVideoInfoAsync(song.Id, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { continue; }
                if (!YouTubeMusicTrackMatcher.MatchesVideoInfo(info, identity, albumProvenance: true,
                    _videoTypes.Get(song.Id))) continue;
                candidates.Add(new AlbumTrackCandidate(song.Id, album.Songs.Any(s => s.IsExplicit), song.IsExplicit));
            }
        }

        var selected = YouTubeMusicTrackMatcher.FilterEditions(candidates, identity.IsExplicit);
        return selected.Count switch
        {
            0 => new(YouTubeMusicPlaybackStatus.NotFound, null, "no exact track in a matching album edition"),
            1 => new(YouTubeMusicPlaybackStatus.Matched, selected[0].VideoId, null),
            _ => new(YouTubeMusicPlaybackStatus.Ambiguous, null, $"{selected.Count} album editions matched"),
        };
    }

    private async Task<Resolution> ResolveFromSongSearchAsync(
        YouTubeMusicClient client,
        TrackIdentity identity,
        CancellationToken cancellationToken)
    {
        if (identity.IsExplicit == false)
            return new(YouTubeMusicPlaybackStatus.NotFound, null, "song search cannot prove a clean album edition");
        var query = $"{identity.Artist} {identity.Title}";
        var search = await client.SearchAsync(query, SearchCategory.Songs)
            .FetchItemsAsync(0, 10, cancellationToken);
        var matches = new List<SongSearchCandidate>();
        foreach (var song in search.OfType<SongSearchResult>())
        {
            if (!YouTubeMusicTrackMatcher.MatchesSongSearch(song, identity)) continue;
            _videoTypes.Reset(song.Id);
            SongVideoInfo info;
            try { info = await client.GetSongVideoInfoAsync(song.Id, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { continue; }
            if (YouTubeMusicTrackMatcher.MatchesVideoInfo(info, identity, albumProvenance: false,
                _videoTypes.Get(song.Id)))
                matches.Add(new(song.Id, song.IsExplicit));
        }
        var selected = YouTubeMusicTrackMatcher.FilterEditions(matches, identity.IsExplicit);
        return selected.Count switch
        {
            0 => new(YouTubeMusicPlaybackStatus.NotFound, null, "song search found no confident match"),
            1 => new(YouTubeMusicPlaybackStatus.Matched, selected[0].VideoId, null),
            _ => new(YouTubeMusicPlaybackStatus.Ambiguous, null, $"{selected.Count} song candidates matched"),
        };
    }

    private async Task<YouTubeMusicPlaybackResult> OpenCdnAsync(
        AudioStreamInfo stream,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(CdnClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        if (!string.IsNullOrWhiteSpace(rangeHeader))
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
        {
            response.Dispose();
            return new() { Status = YouTubeMusicPlaybackStatus.TemporaryFailure, Reason = $"CDN returned {(int)response.StatusCode}" };
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new()
        {
            Status = YouTubeMusicPlaybackStatus.Matched,
            AudioStream = new ResponseOwnedStream(body, response),
            ContentType = response.Content.Headers.ContentType?.ToString() ?? ContentTypeFor(stream.Container.Format),
            ContentLength = response.Content.Headers.ContentLength,
            StatusCode = (int)response.StatusCode,
            ContentRange = response.Content.Headers.ContentRange?.ToString(),
        };
    }

    private static string ContentTypeFor(string format) => format.ToLowerInvariant() switch
    {
        "webm" => "audio/webm",
        "mp4" => "audio/mp4",
        _ => "application/octet-stream",
    };

    private sealed record Resolution(YouTubeMusicPlaybackStatus Status, string? VideoId, string? Reason);
    private sealed record AlbumTrackCandidate(string VideoId, bool ExplicitEdition, bool TrackExplicit) : YouTubeMusicTrackMatcher.IYouTubeMusicEditionCandidate;
    private sealed record SongSearchCandidate(string VideoId, bool ExplicitEdition) : YouTubeMusicTrackMatcher.IYouTubeMusicEditionCandidate;

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly HttpResponseMessage _response = response;
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); _response.Dispose(); } base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { _inner.Dispose(); _response.Dispose(); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => _inner.CanSeek; public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush(); public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin); public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}

internal static class YouTubeMusicTrackMatcher
{
    internal interface IYouTubeMusicEditionCandidate
    {
        bool ExplicitEdition { get; }
    }
    internal static bool MatchesAlbum(string title, IEnumerable<YouTubeMusicAPI.Models.NamedEntity> artists, TrackIdentity identity) =>
        Equal(title, identity.Album) && artists.Any(a => Equal(a.Name, identity.Artist));

    internal static bool MatchesTitleAndDuration(AlbumSong song, TrackIdentity identity)
    {
        if (!MatchesTitle(song.Name, identity.Title, identity.Version)) return false;
        return identity.Duration is not int duration || song.Duration <= TimeSpan.Zero
            || Math.Abs(song.Duration.TotalSeconds - duration) <= Math.Max(10, duration * .05);
    }

    internal static bool MatchesSongSearch(SongSearchResult song, TrackIdentity identity) =>
        MatchesTitle(song.Name, identity.Title, identity.Version)
        && song.Artists.Any(a => Equal(a.Name, identity.Artist))
        && (identity.Duration is not int duration || song.Duration <= TimeSpan.Zero
            || Math.Abs(song.Duration.TotalSeconds - duration) <= Math.Max(10, duration * .05))
        && (identity.Album is null || Equal(song.Album?.Name, identity.Album));

    internal static bool MatchesVideoInfo(SongVideoInfo info, TrackIdentity identity, bool albumProvenance,
        string? musicVideoType = null)
    {
        if (info.IsLiveContent || info.IsPrivate || info.IsUnlisted) return false;
        var isAtv = string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_ATV", StringComparison.OrdinalIgnoreCase);
        var isOmv = string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase);
        if (!isAtv && !(albumProvenance && isOmv))
            return false;
        if (!albumProvenance && !info.PlayabilityStatus.IsOkay) return false;
        if (!MatchesTitle(info.Name, identity.Title, identity.Version)) return false;
        if (!info.Artists.Any(a => Equal(a.Name, identity.Artist))) return false;
        if (!albumProvenance && identity.Album is not null && !Equal(info.Album?.Name, identity.Album)) return false;
        if (identity.Duration is int duration && info.Duration > TimeSpan.Zero
            && Math.Abs(info.Duration.TotalSeconds - duration) > Math.Max(10, duration * .05)) return false;
        return true;
    }

    internal static T? SelectEdition<T>(IEnumerable<T> candidates, bool? requestedExplicit)
        where T : class, IYouTubeMusicEditionCandidate
    {
        var list = candidates.ToList();
        if (list.Count == 0) return default;
        if (requestedExplicit == false)
            list = list.Where(x => !x.ExplicitEdition).ToList();
        else if (requestedExplicit == true)
            list = list.Where(x => x.ExplicitEdition).ToList();
        else
        {
            var explicitList = list.Where(x => x.ExplicitEdition).ToList();
            if (explicitList.Count > 0) list = explicitList;
        }
        return list.Count == 1 ? list[0] : default;
    }

    internal static List<T> FilterEditions<T>(IEnumerable<T> candidates, bool? requestedExplicit)
        where T : class, IYouTubeMusicEditionCandidate
    {
        var list = candidates.ToList();
        if (requestedExplicit == false)
            return list.Where(x => !x.ExplicitEdition).ToList();
        if (requestedExplicit == true)
            return list.Where(x => x.ExplicitEdition).ToList();
        var explicitList = list.Where(x => x.ExplicitEdition).ToList();
        return explicitList;
    }

    private static bool MatchesTitle(string candidate, string expected, string? version)
    {
        if (Equal(candidate, expected)) return true;
        return !string.IsNullOrWhiteSpace(version)
            && Equal(candidate, $"{expected} {version}");
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
