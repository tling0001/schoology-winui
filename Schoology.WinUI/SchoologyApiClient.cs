using System.Globalization;
using System.Text.Json;

namespace Schoology.WinUI;

/// <summary>
/// Typed entry point for Schoology's OAuth-signed v1 API. Endpoint paths and
/// collection names follow the supplied official Schoology desktop port.28.
/// </summary>
internal sealed class SchoologyApiClient
{
    private readonly SchoologyAuthService _auth;

    internal SchoologyApiClient(SchoologyAuthService auth) => _auth = auth;

    internal Task<IReadOnlyList<SchoologyEntity>> GetCoursesAsync(
        SchoologyAuthService.Session session, CancellationToken cancellationToken = default) =>
        GetEntitiesAsync(session, $"users/{session.UserId}/sections", ["section", "sections"], cancellationToken);

    internal Task<IReadOnlyList<SchoologyEntity>> GetGroupsAsync(
        SchoologyAuthService.Session session, CancellationToken cancellationToken = default) =>
        GetEntitiesAsync(session, $"users/{session.UserId}/groups", ["group", "groups"], cancellationToken);

    internal Task<JsonDocument> GetProfileAsync(
        SchoologyAuthService.Session session, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, "users/me", null, cancellationToken);

    internal Task<JsonDocument> GetUserGradesAsync(
        SchoologyAuthService.Session session, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"users/{session.UserId}/grades",
            new Dictionary<string, string> { ["limit"] = "100" }, cancellationToken);

    internal Task<JsonDocument> GetUserAsync(
        SchoologyAuthService.Session session, long userId, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"users/{userId}", null, cancellationToken);

    internal Task<JsonDocument> GetCourseFolderAsync(
        SchoologyAuthService.Session session, long sectionId, long folderId, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"courses/{sectionId}/folder/{folderId}", null, cancellationToken);

    internal Task<JsonDocument> GetMessagesAsync(
        SchoologyAuthService.Session session, bool sent, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, sent ? "messages/sent" : "messages/inbox",
            new Dictionary<string, string> { ["limit"] = "50" }, cancellationToken);

    internal Task<JsonDocument> GetMessageAsync(SchoologyAuthService.Session session, long messageId, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"messages/{messageId}", null, cancellationToken);

    internal async Task<JsonDocument> GetEntityTabAsync(
        SchoologyAuthService.Session session, string kind, long entityId, string tab,
        CancellationToken cancellationToken = default)
    {
        var realm = kind switch { "Groups" => "groups", "School Profile" => "schools", _ => "sections" };
        if (kind == "School Profile" && tab == "Info")
            return await _auth.GetApiJsonAsync(session, $"schools/{entityId}", null, cancellationToken);
        var resource = tab switch
        {
            "Updates" => "updates",
            "Materials" => "materials_hierarchy",
            "Grades" => "grades",
            "Members" => kind == "Groups" ? "members" : "enrollments",
            "Staff" => "users",
            "Discussions" => "discussions",
            "Events" or "Upcoming" => "events",
            "Resources" => "resources",
            "Info" => throw new InvalidOperationException("School information is not available."),
            _ => throw new InvalidOperationException($"The {tab} view is not supported.")
        };
        var parameters = tab == "Members"
            ? kind == "Groups"
                ? new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200" }
                : new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200", ["enrollment_status"] = "1" }
            : tab == "Materials"
                ? new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200", ["with_attachments"] = "TRUE" }
                : new Dictionary<string, string> { ["limit"] = "100" };
        return await _auth.GetApiJsonAsync(session, $"{realm}/{entityId}/{resource}", parameters, cancellationToken);
    }

    internal Task<JsonDocument> GetUpdateCommentsAsync(
        SchoologyAuthService.Session session, string kind, long entityId, long updateId,
        CancellationToken cancellationToken = default)
    {
        var realm = kind == "Groups" ? "groups" : kind == "School Profile" ? "schools" : "sections";
        return _auth.GetApiJsonAsync(session, $"{realm}/{entityId}/updates/{updateId}/comments",
            new Dictionary<string, string>
            {
                ["start"] = "0", ["limit"] = "100",
                ["with_attachments"] = "TRUE", ["richtext"] = "1"
            }, cancellationToken);
    }

    internal Task<JsonDocument> SetUpdateLikeAsync(
        SchoologyAuthService.Session session, long updateId, bool like,
        CancellationToken cancellationToken = default) =>
        _auth.SendApiFormAsync(session, HttpMethod.Post, $"like/{updateId}",
            new Dictionary<string, string> { ["like_action"] = like ? "true" : "false" }, cancellationToken);

    internal Task<JsonDocument> AddUpdateCommentAsync(
        SchoologyAuthService.Session session, string kind, long entityId, long updateId,
        string comment, CancellationToken cancellationToken = default)
    {
        var realm = kind == "Groups" ? "groups" : kind == "School Profile" ? "schools" : "sections";
        return _auth.SendApiJsonAsync(session, HttpMethod.Post, $"{realm}/{entityId}/updates/{updateId}/comments",
            new Dictionary<string, string> { ["comment"] = comment, ["parent_id"] = "0" }, cancellationToken);
    }

    private async Task<IReadOnlyList<SchoologyEntity>> GetEntitiesAsync(
        SchoologyAuthService.Session session, string path, string[] collectionKeys,
        CancellationToken cancellationToken)
    {
        using var document = await _auth.GetApiJsonAsync(session, path,
            new Dictionary<string, string> { ["limit"] = "100" }, cancellationToken);
        return ReadRecords(document.RootElement, collectionKeys)
            .Select(item => new SchoologyEntity(
                ReadLong(item.Data, "id", "section_id", "group_id"),
                item.Title,
                item.Subtitle,
                ReadString(item.Data, "picture_url", "pictureUrl", "picture", "image", "profile_url", "profileUrl",
                    "course_profile_url", "courseProfileUrl", "course_theme", "courseTheme", "course_image", "courseImage"),
                item.Data.Clone()))
            .Where(item => item.Id > 0)
            .ToArray();
    }

    internal static IReadOnlyList<SchoologyApiRecord> ReadRecords(
        JsonElement root, params string[] collectionKeys)
    {
        JsonElement collection = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var found = false;
            var keys = collectionKeys.Length == 0
                ? new[] { "section", "sections", "group", "groups", "update", "updates", "document", "documents",
                    "grade", "grades", "member", "members", "discussion", "discussions", "event", "events",
                    "resource", "resources", "message", "messages", "folder", "folders", "user", "users",
                    "enrollment", "enrollments", "event", "events", "update", "updates" }
                : collectionKeys;
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out collection))
                {
                    if (collection.ValueKind == JsonValueKind.Array) { found = true; break; }
                    if (collection.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var nestedKey in keys)
                            if (collection.TryGetProperty(nestedKey, out var nested) && nested.ValueKind == JsonValueKind.Array)
                            { collection = nested; found = true; break; }
                        if (found) break;
                    }
                }
            }
            if (!found && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in keys)
                    if (data.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Array)
                    { collection = nested; found = true; break; }
            }
            if (!found) return [];
        }

        if (collection.ValueKind != JsonValueKind.Array) return [];
        var records = new List<SchoologyApiRecord>();
        foreach (var element in collection.EnumerateArray())
        {
            var personName = string.Join(" ", new[]
            {
                ReadString(element, "name_first", "first_name"),
                ReadString(element, "name_last", "last_name")
            }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            var title = ReadString(element, "course_title", "courseTitle", "title", "name_display", "nameDisplay", "name",
                "user_name", "author_name", "created_by", "section_title", "sectionTitle", "subject", "message_subject")
                ?? (string.IsNullOrWhiteSpace(personName) ? "Schoology item" : personName);
            var subtitle = ReadString(element, "section_title", "sectionTitle", "description", "body", "comment", "text", "message", "role", "admin",
                "due", "due_date", "created", "timestamp") ?? string.Empty;
            records.Add(new SchoologyApiRecord(title, subtitle, element.Clone()));
        }
        return records;
    }

    private static string? ReadString(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) &&
                value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return value.ToString();
        }
        return null;
    }

    private static long ReadLong(JsonElement element, params string[] keys)
    {
        var value = ReadString(element, keys);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }
}

internal sealed record SchoologyEntity(long Id, string Name, string Subtitle, string? ImageUrl, JsonElement Data)
{
    public override string ToString() => Name;
}

internal sealed record SchoologyApiRecord(string Title, string Subtitle, JsonElement Data);

internal sealed partial class SchoologyAuthService
{
    /// <summary>Execute an OAuth-signed form POST. Schoology's like endpoint
    /// expects like_action in the request body, matching Android ROLike.</summary>
    internal async Task<JsonDocument> SendApiFormAsync(
        Session session, HttpMethod method, string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        var normalizedPath = path.TrimStart('/');
        if (normalizedPath.Length == 0 ||
            normalizedPath.Split('/').Any(segment => segment is "." or "..") ||
            normalizedPath.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-')))
            throw new InvalidOperationException("The Schoology API path is invalid.");
        var url = $"https://{ApiHost}/v1/{normalizedPath}";
        using var response = await SendSignedRequestAsync(method, url,
            () => new FormUrlEncodedContent(fields), session.Token, session.Secret, null, cancellationToken, fields);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "Schoology could not update this item", response.StatusCode));
        if (string.IsNullOrWhiteSpace(body)) return JsonDocument.Parse("{}");
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    /// <summary>Execute an OAuth-signed GET against the configured Schoology API host.</summary>
    internal async Task<JsonDocument> GetApiJsonAsync(
        Session session, string path, IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken = default) =>
        await SendApiJsonAsync(session, HttpMethod.Get, path, query, cancellationToken);

    /// <summary>Execute an OAuth-signed API mutation. Parameters are URL-encoded
    /// into the request URI so OAuth 1.0a signs the complete parameter set.</summary>
    internal async Task<JsonDocument> SendApiJsonAsync(
        Session session, HttpMethod method, string path,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken = default)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        var normalizedPath = path.TrimStart('/');
        if (normalizedPath.Length == 0 ||
            normalizedPath.Split('/').Any(segment => segment is "." or "..") ||
            normalizedPath.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-')))
            throw new InvalidOperationException("The Schoology API path is invalid.");

        var builder = new UriBuilder($"https://{ApiHost}/v1/{normalizedPath}");
        if (query is { Count: > 0 })
            builder.Query = string.Join("&", query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using var response = await SendSignedRequestAsync(
            method, builder.Uri.AbsoluteUri, () => null,
            session.Token, session.Secret, null, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "Schoology could not load account data", response.StatusCode));
        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new InvalidOperationException("Schoology returned an unreadable response for this page.");
        }
    }
}
