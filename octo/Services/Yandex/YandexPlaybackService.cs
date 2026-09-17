using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Playback;

namespace Octo.Services.Yandex;

/// <summary>Small, playback-only Yandex integration. It does not participate in discovery or acquisition.</summary>
// Protocol and AES-CTR details are adapted from V1ck3s/octo-fiesta (GPLv3),
// reduced here to the playback path and kept separate from its catalog/download layer.
public sealed class YandexPlaybackService
{
    private const string SigningSecret = "kzqU4XhfCaY6B6JTHODeq5";
    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<YandexSettings> _options;
    private readonly SingleFlight<string, YandexPlaybackResult> _singleFlight = new();
    private readonly ILogger<YandexPlaybackService> _logger;

    public YandexPlaybackService(IHttpClientFactory clients, IOptionsMonitor<YandexSettings> options,
        ILogger<YandexPlaybackService> logger)
    {
        _clients = clients;
        _options = options;
        _logger = logger;
    }

    public async Task<YandexPlaybackResult> TryPrepareAsync(TrackIdentity identity, CancellationToken cancellationToken)
    {
        var settings = _options.CurrentValue;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OAuthToken))
            return new(YandexPlaybackStatus.Unauthorized, Reason: "Yandex is disabled or OAuth token is missing");

        var key = string.Join("\n", identity.Artist, identity.Title, identity.Album, identity.Duration, identity.Version, identity.IsExplicit);
        try
        {
            var prepared = _singleFlight.RunAsync(key, token => PrepareCoreAsync(identity, settings, token),
                TimeSpan.FromSeconds(Math.Clamp(settings.PlaybackTimeoutSeconds, 1, 300)));
            return await prepared.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(YandexPlaybackStatus.TemporaryFailure, Reason: "Yandex playback timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Yandex playback preparation failed for {Artist} - {Title}", identity.Artist, identity.Title);
            return new(YandexPlaybackStatus.TemporaryFailure, Reason: ex.Message);
        }
    }

    private async Task<YandexPlaybackResult> PrepareCoreAsync(TrackIdentity identity, YandexSettings settings, CancellationToken ct)
    {
        var root = GetRoot(settings);
        Directory.CreateDirectory(root);
        CleanupExpired(root, settings.TemporaryFileTtlHours);

        var candidate = await FindCandidateAsync(identity, settings.OAuthToken!, ct);
        if (candidate.Status != YandexPlaybackStatus.Matched || candidate.TrackId is null)
            return new(candidate.Status, Reason: candidate.Reason);

        var info = await GetFileInfoAsync(candidate.TrackId, settings.OAuthToken!, ct);
        if (info.Status != YandexPlaybackStatus.Matched || info.Download is null)
            return new(info.Status, Reason: info.Reason);

        var extension = ExtensionFor(info.Download.Codec);
        var finalPath = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.TrackId))).ToLowerInvariant() + extension);
        if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            return new(YandexPlaybackStatus.Matched, finalPath, ContentTypeFor(info.Download.Codec));

        // The final name is stable for reuse, while each attempt gets its own part
        // file. This prevents two different identities that happen to resolve to the
        // same Yandex track from deleting or truncating each other's download.
        var partPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var cdn = _clients.CreateClient("YandexCdn");
            var urls = new[] { info.Download.Url }.Concat(info.Download.Urls ?? []).Where(u => !string.IsNullOrWhiteSpace(u));
            HttpStatusCode? lastStatus = null;
            foreach (var directUrl in urls)
            {
                using var response = await cdn.GetAsync(directUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                lastStatus = response.StatusCode;
                if (!response.IsSuccessStatusCode) continue;

                await using (var encrypted = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var cipher = new Org.BouncyCastle.Crypto.BufferedBlockCipher(new Org.BouncyCastle.Crypto.Modes.SicBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine()));
                    cipher.Init(false, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Convert.FromHexString(info.Download.Key)), new byte[16]));
                    await using var decryptor = new Org.BouncyCastle.Crypto.IO.CipherStream(encrypted, cipher, null);
                    await decryptor.CopyToAsync(output, ct);
                }
                break;
            }

            if (!File.Exists(partPath))
                return new(MapHttpStatus(lastStatus ?? HttpStatusCode.BadGateway), Reason: "all Yandex CDN URLs failed");

            if (!IsValidAudio(partPath, info.Download.Codec))
                return new(YandexPlaybackStatus.TemporaryFailure, Reason: "Yandex response is not a valid audio file");

            File.Move(partPath, finalPath, overwrite: true);
            return new(YandexPlaybackStatus.Matched, finalPath, ContentTypeFor(info.Download.Codec));
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    private async Task<(YandexPlaybackStatus Status, string? TrackId, string? Reason)> FindCandidateAsync(TrackIdentity identity, string token, CancellationToken ct)
    {
        var client = _clients.CreateClient("YandexApi");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/search?text={Uri.EscapeDataString($"{identity.Artist} {identity.Title}")}&type=track&page=0&nocorrect=false");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", token);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (MapHttpStatus(response.StatusCode), null, $"search returned {(int)response.StatusCode}");
        var payload = await response.Content.ReadFromJsonAsync<YandexSearchResponse>(cancellationToken: ct);
        if (payload?.Error is not null) return (MapApiError(payload.Error), null, payload.Error.Message ?? payload.Error.Name);
        if (payload?.Result is null) return (YandexPlaybackStatus.TemporaryFailure, null, "search payload is incomplete");
        var tracks = payload?.Result?.Tracks?.Results ?? [];
        var matches = tracks.Where(track => YandexTrackMatcher.IsMatch(new YandexTrackCandidate(track.Title, track.Version, track.DurationMs,
            track.Artists?.Select(a => a.Name).ToArray() ?? [],
            track.Albums?.Select(a => new YandexAlbumCandidate(a.Title, a.Version)).ToArray() ?? [],
            GetExplicitState(track.ContentWarning, track.Disclaimers), track.Available != false), identity)).ToList();
        return matches.Count switch
        {
            0 => (YandexPlaybackStatus.NotFound, null, "no confident Yandex match"),
            1 => (YandexPlaybackStatus.Matched, matches[0].Id.ToString(), null),
            _ => (YandexPlaybackStatus.Ambiguous, null, $"{matches.Count} Yandex candidates matched"),
        };
    }

    private async Task<(YandexPlaybackStatus Status, YandexDownload? Download, string? Reason)> GetFileInfoAsync(string trackId, string token, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var codecs = "flac,flac-mp4";
        var text = ts + trackId + "lossless" + "flacflac-mp4" + "encraw";
        var sign = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningSecret), Encoding.UTF8.GetBytes(text))).TrimEnd('=');
        var client = _clients.CreateClient("YandexApi");
        var url = $"/get-file-info/?ts={Uri.EscapeDataString(ts)}&trackId={Uri.EscapeDataString(trackId)}&quality=lossless&codecs={Uri.EscapeDataString(codecs)}&transports=encraw&sign={Uri.EscapeDataString(sign)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", token);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (MapHttpStatus(response.StatusCode), null, $"file-info returned {(int)response.StatusCode}");
        var payload = await response.Content.ReadFromJsonAsync<YandexFileInfoResponse>(cancellationToken: ct);
        if (payload?.Error is not null)
            return (MapApiError(payload.Error), null, payload.Error.Message ?? payload.Error.Name);
        if (payload?.Result?.DownloadInfo is null)
            return (YandexPlaybackStatus.TemporaryFailure, null, "file-info payload is incomplete");
        return (YandexPlaybackStatus.Matched, payload.Result.DownloadInfo, null);
    }

    private static YandexPlaybackStatus MapHttpStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => YandexPlaybackStatus.Unauthorized,
        (HttpStatusCode)429 => YandexPlaybackStatus.RateLimited,
        HttpStatusCode.NotFound => YandexPlaybackStatus.NotFound,
        _ => YandexPlaybackStatus.TemporaryFailure,
    };
    private static YandexPlaybackStatus MapApiError(YandexError error)
    {
        var text = $"{error.Name} {error.Message}".ToLowerInvariant();
        if (text.Contains("auth") || text.Contains("token") || text.Contains("access")) return YandexPlaybackStatus.Unauthorized;
        if (text.Contains("limit") || text.Contains("429") || text.Contains("rate")) return YandexPlaybackStatus.RateLimited;
        if (text.Contains("not found") || text.Contains("track_not_found")) return YandexPlaybackStatus.NotFound;
        return YandexPlaybackStatus.TemporaryFailure;
    }
    private static string GetRoot(YandexSettings settings) => string.IsNullOrWhiteSpace(settings.TemporaryPlaybackDirectory) ? Path.Combine(Path.GetTempPath(), "octo-yandex-playback") : settings.TemporaryPlaybackDirectory!;
    private static string ExtensionFor(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "flac" => ".flac",
        "mp3" => ".mp3",
        _ => ".m4a",
    };
    private static string ContentTypeFor(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "flac" => "audio/flac",
        "mp3" => "audio/mpeg",
        _ => "audio/mp4",
    };
    private static bool IsValidAudio(string path, string codec)
    {
        try
        {
            if (new FileInfo(path).Length <= 0) return false;
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            var count = stream.Read(header);
            if (count < 4) return false;

            var normalized = codec.Trim().ToLowerInvariant();
            var containerOk = normalized switch
            {
                "flac" => header[..4].SequenceEqual("fLaC"u8),
                "flac-mp4" => count >= 8 && header[4..8].SequenceEqual("ftyp"u8),
                "mp3" => header[..3].SequenceEqual("ID3"u8) || header[0] == 0xFF,
                _ => count >= 8 && header[4..8].SequenceEqual("ftyp"u8),
            };
            if (!containerOk) return false;

            using var tagFile = TagLib.File.Create(path, ContentTypeFor(normalized), TagLib.ReadStyle.Average);
            return tagFile.Properties.Duration > TimeSpan.Zero;
        }
        catch
        {
            return false;
        }
    }
    private static void CleanupExpired(string root, int ttlHours)
    {
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, ttlHours));
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                if (!file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && File.GetLastAccessTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch { }
    }

    private static bool? GetExplicitState(string? contentWarning, IReadOnlyList<string>? disclaimers)
    {
        if (string.Equals(contentWarning, "explicit", StringComparison.OrdinalIgnoreCase)
            || disclaimers?.Any(d => string.Equals(d, "explicit", StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        if (string.Equals(contentWarning, "clean", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private sealed record YandexSearchResponse(YandexSearchResult? Result, YandexError? Error);
    private sealed record YandexSearchResult(YandexTracks? Tracks);
    private sealed record YandexTracks(List<YandexTrack>? Results);
    private sealed record YandexTrack(int Id, string? Title, string? Version, int? DurationMs, List<YandexArtist>? Artists, List<YandexAlbum>? Albums,
        bool? Available = null, string? ContentWarning = null, List<string>? Disclaimers = null);
    private sealed record YandexArtist(string? Name);
    private sealed record YandexAlbum(string? Title, string? Version);
    private sealed record YandexFileInfoResponse(YandexFileInfoResult? Result, YandexError? Error);
    private sealed record YandexFileInfoResult(YandexDownload? DownloadInfo);
    private sealed record YandexDownload(string Url, string Key, string Codec, List<string>? Urls);
    private sealed record YandexError(string? Name, string? Message);
}
