using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Mods;

public sealed record CurseForgeFile(
    int Id,
    int ModId,
    int ClassId,
    string FileName,
    string? DownloadUrl,
    long Length,
    string? Sha1);

public sealed class CurseForgeClient
{
    private const string BaseUrl = "https://api.curseforge.com/v1";

    public const int MaxBatchSize = 50;

    private readonly HttpClient _http;

    public CurseForgeClient(HttpClient http, string? apiKey = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY")
            : apiKey;
    }

    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static string FolderForClassId(int classId) => classId switch
    {
        6 => "mods",
        12 => "resourcepacks",
        6552 => "shaderpacks",
        6945 => "datapacks",
        4471 => string.Empty,
        _ => "mods"
    };

    public async Task<IReadOnlyDictionary<int, CurseForgeFile>> GetFilesAsync(
        IReadOnlyCollection<int> fileIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("CurseForge API key is not configured.");
        }

        var result = new Dictionary<int, CurseForgeFile>();
        var uniqueIds = fileIds.Distinct().ToList();

        if (uniqueIds.Count == 0)
        {
            return result;
        }

        var files = new List<FileDto>();

        foreach (var batch in uniqueIds.Chunk(MaxBatchSize))
        {
            var response = await PostAsync<FilesResponse>("/mods/files", new { fileIds = batch }, cancellationToken)
                .ConfigureAwait(false);

            files.AddRange(response?.Data ?? new List<FileDto>());
        }

        var modIds = files.Select(f => f.ModId).Distinct().ToList();
        var classIds = new Dictionary<int, int>();

        foreach (var batch in modIds.Chunk(MaxBatchSize))
        {
            var response = await PostAsync<ModsResponse>("/mods", new { modIds = batch }, cancellationToken)
                .ConfigureAwait(false);

            foreach (var mod in response?.Data ?? new List<ModDto>())
            {
                classIds[mod.Id] = mod.ClassId;
            }
        }

        foreach (var file in files)
        {
            classIds.TryGetValue(file.ModId, out var classId);
            var sha1 = file.Hashes?
                .FirstOrDefault(h => h.Algo == 1)?
                .Value;

            result[file.Id] = new CurseForgeFile(
                file.Id,
                file.ModId,
                classId,
                file.FileName ?? string.Empty,
                file.DownloadUrl,
                file.FileLength,
                sha1);
        }

        return result;
    }

    private async Task<T?> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"CurseForge {path} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, Json.Options);
    }

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    private sealed class FilesResponse
    {
        [JsonPropertyName("data")]
        public List<FileDto>? Data { get; set; }
    }

    private sealed class ModsResponse
    {
        [JsonPropertyName("data")]
        public List<ModDto>? Data { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("fileLength")]
        public long FileLength { get; set; }

        [JsonPropertyName("hashes")]
        public List<HashDto>? Hashes { get; set; }
    }

    private sealed class HashDto
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("algo")]
        public int Algo { get; set; }
    }

    private sealed class ModDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("classId")]
        public int ClassId { get; set; }
    }
}