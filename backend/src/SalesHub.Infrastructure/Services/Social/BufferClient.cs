using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Publicador social vía Buffer (API GraphQL nueva, https://api.buffer.com).
/// Porteo de la lógica validada en buffer-mcp: resolver org, listar canales,
/// createPost (drafts primero), deletePost. Media por URL pública (no upload).
/// </summary>
public class BufferClient : ISocialPublisher
{
    private readonly HttpClient _http;
    private readonly BufferOptions _opts;
    private readonly ILogger<BufferClient> _log;
    private string? _orgId;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BufferClient(HttpClient http, IOptions<BufferOptions> opts, ILogger<BufferClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_opts.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.Token);

    private async Task<JsonElement> GqlAsync(string query, object? variables, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { query, variables }, Json);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(_opts.BaseUrl.TrimEnd('/'), content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var msg = string.Join("; ", errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : "?"));
            throw new InvalidOperationException($"Buffer GraphQL error: {msg}");
        }
        if (!root.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"Buffer API sin 'data' ({(int)resp.StatusCode}): {Trunc(text, 300)}");
        return data;
    }

    private async Task<string> ResolveOrgIdAsync(CancellationToken ct)
    {
        if (_orgId != null) return _orgId;
        var data = await GqlAsync("{ account { organizations { id } } }", null, ct);
        var orgs = data.GetProperty("account").GetProperty("organizations");
        if (orgs.GetArrayLength() == 0) throw new InvalidOperationException("Buffer: la cuenta no tiene organizaciones.");
        _orgId = orgs[0].GetProperty("id").GetString()!;
        return _orgId;
    }

    public async Task<IReadOnlyList<SocialChannel>> ListChannelsAsync(CancellationToken ct = default)
    {
        var orgId = await ResolveOrgIdAsync(ct);
        var data = await GqlAsync(
            "query($i:ChannelsInput!){ channels(input:$i){ id name service } }",
            new { i = new { organizationId = orgId } }, ct);
        var list = new List<SocialChannel>();
        foreach (var c in data.GetProperty("channels").EnumerateArray())
        {
            list.Add(new SocialChannel(
                c.GetProperty("id").GetString() ?? "",
                c.GetProperty("name").GetString() ?? "",
                c.GetProperty("service").GetString() ?? ""));
        }
        return list;
    }

    public async Task<PublishResult> CreatePostAsync(PublishRequest req, CancellationToken ct = default)
    {
        if (!IsConfigured) return new PublishResult { Success = false, Error = "Buffer no configurado (falta token)." };
        if (string.IsNullOrWhiteSpace(req.ChannelId)) return new PublishResult { Success = false, Error = "channelId requerido." };

        // assets: imagen o video por URL pública.
        object asset;
        if (!string.IsNullOrWhiteSpace(req.VideoUrl))
        {
            var video = new Dictionary<string, object?> { ["url"] = req.VideoUrl };
            if (!string.IsNullOrWhiteSpace(req.ThumbnailUrl)) video["thumbnailUrl"] = req.ThumbnailUrl;
            asset = new Dictionary<string, object?> { ["video"] = video };
        }
        else if (!string.IsNullOrWhiteSpace(req.ImageUrl))
        {
            asset = new Dictionary<string, object?> { ["image"] = new Dictionary<string, object?> { ["url"] = req.ImageUrl } };
        }
        else
        {
            return new PublishResult { Success = false, Error = "Falta imageUrl o videoUrl." };
        }

        var input = new Dictionary<string, object?>
        {
            ["channelId"] = req.ChannelId,
            ["text"] = req.Caption ?? "",
            ["assets"] = new[] { asset },
            ["schedulingType"] = req.Automatic ? "automatic" : "notification",
            ["mode"] = req.ScheduledAt.HasValue ? "customScheduled" : "shareNow",
        };
        if (req.ScheduledAt.HasValue)
            input["dueAt"] = req.ScheduledAt.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        if (req.SaveAsDraft)
            input["saveToDraft"] = true;
        if (string.Equals(req.Service, "instagram", StringComparison.OrdinalIgnoreCase))
        {
            input["metadata"] = new Dictionary<string, object?>
            {
                ["instagram"] = new Dictionary<string, object?>
                {
                    ["type"] = string.IsNullOrWhiteSpace(req.InstagramType) ? "post" : req.InstagramType,
                    ["shouldShareToFeed"] = req.ShareToFeed,
                }
            };
        }

        const string mutation = @"mutation($input: CreatePostInput!){ createPost(input: $input){
            __typename
            ... on PostActionSuccess { post { id status dueAt channelService } }
            ... on InvalidInputError { message }
            ... on LimitReachedError { message }
            ... on UnauthorizedError { message }
            ... on NotFoundError { message }
            ... on UnexpectedError { message }
            ... on RestProxyError { message code link }
        } }";

        try
        {
            var data = await GqlAsync(mutation, new { input }, ct);
            var payload = data.GetProperty("createPost");
            var typename = payload.GetProperty("__typename").GetString();
            if (typename == "PostActionSuccess")
            {
                var post = payload.GetProperty("post");
                return new PublishResult
                {
                    Success = true,
                    ExternalPostId = post.GetProperty("id").GetString(),
                    Status = post.TryGetProperty("status", out var st) ? st.GetString() : null,
                    RawJson = payload.GetRawText(),
                };
            }
            var err = payload.TryGetProperty("message", out var m) ? m.GetString() : typename;
            return new PublishResult { Success = false, Error = $"{typename}: {err}", RawJson = payload.GetRawText() };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Buffer createPost falló");
            return new PublishResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<bool> DeletePostAsync(string externalPostId, CancellationToken ct = default)
    {
        const string mutation = @"mutation($input: DeletePostInput!){ deletePost(input: $input){
            __typename
            ... on DeletePostSuccess { id }
            ... on VoidMutationError { message }
        } }";
        try
        {
            var data = await GqlAsync(mutation, new { input = new { id = externalPostId } }, ct);
            return data.GetProperty("deletePost").GetProperty("__typename").GetString() == "DeletePostSuccess";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Buffer deletePost falló");
            return false;
        }
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n];
}
