using System.Globalization;
using System.Text;
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

    internal Task<JsonDocument> GetAssignmentsAsync(
        SchoologyAuthService.Session session, long sectionId, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"sections/{sectionId}/assignments",
            new Dictionary<string, string>
            {
                ["start"] = "0", ["limit"] = "100", ["with_attachments"] = "TRUE", ["richtext"] = "1"
            }, cancellationToken);

    internal Task<JsonDocument> GetCourseAppsAsync(
        SchoologyAuthService.Session session, long sectionId, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAtVersionAsync(session, "v2", $"sections/{sectionId}/applications", null, cancellationToken);

    internal Task<JsonDocument> LaunchCourseAppAsync(
        SchoologyAuthService.Session session, long sectionId, long appId, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAtVersionAsync(session, "v2", $"sections/{sectionId}/applications/{appId}/launch", null, cancellationToken);

    internal Task<JsonDocument> GetMessagesAsync(
        SchoologyAuthService.Session session, bool sent, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, sent ? "messages/sent" : "messages/inbox",
            new Dictionary<string, string> { ["limit"] = "50" }, cancellationToken);

    internal Task<JsonDocument> CreateUpdateAsync(
        SchoologyAuthService.Session session, string kind, long entityId, string body,
        CancellationToken cancellationToken = default)
    {
        var realm = kind switch
        {
            "Courses" => "sections",
            "Groups" => "groups",
            _ => throw new InvalidOperationException("Updates can only be posted to a course or group.")
        };
        if (entityId <= 0) throw new InvalidOperationException("Choose a valid Schoology destination.");
        return _auth.SendApiFormAsync(session, HttpMethod.Post, $"{realm}/{entityId}/updates",
            new Dictionary<string, string> { ["body"] = body }, cancellationToken);
    }

    internal Task<JsonDocument> GetMessageAsync(
        SchoologyAuthService.Session session, long messageId, bool sent,
        CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"messages/{(sent ? "sent" : "inbox")}/{messageId}",
            new Dictionary<string, string> { ["with_attachments"] = "TRUE" }, cancellationToken);

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
            "Members" => "enrollments",
            "Staff" => "users",
            "Discussions" => "discussions",
            "Assignments" => "assignments",
            "Events" or "Upcoming" => "events",
            "Resources" => "resources",
            "Info" => throw new InvalidOperationException("School information is not available."),
            _ => throw new InvalidOperationException($"The {tab} view is not supported.")
        };
        var parameters = tab == "Members"
            ? new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200", ["enrollment_status"] = "1" }
            : tab == "Materials"
                ? new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200", ["with_attachments"] = "TRUE" }
                : tab == "Assignments"
                    ? new Dictionary<string, string> { ["start"] = "0", ["limit"] = "100", ["with_attachments"] = "TRUE", ["richtext"] = "1" }
                    : tab == "Upcoming"
                        ? new Dictionary<string, string>
                        {
                            ["start_date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            ["start"] = "0", ["limit"] = "20"
                        }
                        : new Dictionary<string, string> { ["limit"] = "100" };
        if (tab == "Discussions")
        {
            parameters["limit"] = "50";
            for (var attempt = 0; ; attempt++)
            {
                try { return await _auth.GetApiJsonAsync(session, $"{realm}/{entityId}/{resource}", parameters, cancellationToken); }
                catch (InvalidOperationException ex) when (attempt < 2 && ex.Message.Contains("HTTP 429", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(800 * (attempt + 1)), cancellationToken);
                }
            }
        }
        return await _auth.GetApiJsonAsync(session, $"{realm}/{entityId}/{resource}", parameters, cancellationToken);
    }

    internal Task<JsonDocument> GetUpdateCommentsAsync(
        SchoologyAuthService.Session session, string kind, long entityId, long updateId,
        CancellationToken cancellationToken = default)
    {
        var realm = kind == "Groups" ? "groups" : kind == "School Profile" ? "schools" :
            kind == "Profile" ? "users" : "sections";
        return _auth.GetApiJsonAsync(session, $"{realm}/{entityId}/updates/{updateId}/comments",
            new Dictionary<string, string>
            {
                ["start"] = "0", ["limit"] = "100",
                ["with_attachments"] = "TRUE", ["richtext"] = "1"
            }, cancellationToken);
    }

    internal Task<JsonDocument> GetUpdateLikesAsync(
        SchoologyAuthService.Session session, long updateId,
        CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, $"like/{updateId}", null, cancellationToken);

    // Official Android ROPollVote posts the option ID and selection state to poll/{update_id}/vote.
    internal Task<JsonDocument> VotePollAsync(
        SchoologyAuthService.Session session, long updateId, long optionId, bool select,
        CancellationToken cancellationToken = default) =>
        _auth.SendApiFormAsync(session, HttpMethod.Post, $"poll/{updateId}/vote",
            new Dictionary<string, string>
            {
                ["id"] = optionId.ToString(CultureInfo.InvariantCulture),
                ["select"] = select ? "true" : "false"
            }, cancellationToken);

    internal Task<JsonDocument> SetUpdateLikeAsync(
        SchoologyAuthService.Session session, long updateId, bool like,
        CancellationToken cancellationToken = default) =>
        _auth.SendApiFormAsync(session, HttpMethod.Post, $"like/{updateId}",
            new Dictionary<string, string> { ["like_action"] = like ? "true" : "false" }, cancellationToken);

    internal Task<JsonDocument> GetCommentLikesAsync(SchoologyAuthService.Session session, long entityId, long commentId, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"like/{entityId}/comment/{commentId}", null, ct);
    internal Task<JsonDocument> SetCommentLikeAsync(SchoologyAuthService.Session session, long entityId, long commentId, bool like, CancellationToken ct = default) =>
        _auth.SendApiFormAsync(session, HttpMethod.Post, $"like/{entityId}/comment/{commentId}",
            new Dictionary<string, string> { ["like_action"] = like ? "true" : "false" }, ct);

    internal Task<JsonDocument> AddUpdateCommentAsync(
        SchoologyAuthService.Session session, string kind, long entityId, long updateId,
        string comment, CancellationToken cancellationToken = default)
    {
        var realm = kind == "Groups" ? "groups" : kind == "School Profile" ? "schools" :
            kind == "Profile" ? "users" : "sections";
        return _auth.SendApiJsonAsync(session, HttpMethod.Post, $"{realm}/{entityId}/updates/{updateId}/comments",
            new Dictionary<string, string> { ["comment"] = comment, ["parent_id"] = "0" }, cancellationToken);
    }

    internal Task<JsonDocument> GetNotificationsAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, "notifications", null, ct);
    internal Task<JsonDocument> MarkNotificationsReadAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.SendApiJsonAsync(session, HttpMethod.Post, "notifications/read", null, ct);
    internal Task<JsonDocument> GetFriendRequestsAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"users/{session.UserId}/requests/friends", new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200" }, ct);
    internal Task<JsonDocument> GetGroupInvitationsAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"users/{session.UserId}/invites/groups", new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200" }, ct);
    internal Task<JsonDocument> GetSectionInvitationsAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"users/{session.UserId}/invites/sections", new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200" }, ct);
    internal Task<JsonDocument> RespondToRequestAsync(SchoologyAuthService.Session session, string type, long id, bool accept, CancellationToken ct = default)
    {
        if (id <= 0) throw new InvalidOperationException("This request is missing its ID.");
        var action = accept ? "accept" : "deny";
        var (path, payload) = type switch
        {
            "friend" => ($"users/{session.UserId}/requests/friends/{id}", (object)new { request = new { request_action = action } }),
            "group" => ($"users/{session.UserId}/invites/groups/{id}", (object)new { invite = new { invite_action = action } }),
            "section" => ($"users/{session.UserId}/invites/sections/{id}", (object)new { invite = new { invite_action = action } }),
            _ => throw new InvalidOperationException("This request type is not supported.")
        };
        return _auth.SendApiJsonBodyAsync(session, HttpMethod.Put, path, payload, ct);
    }
    internal Task<JsonDocument> GetResourceCollectionsAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, "collections", new Dictionary<string, string> { ["limit"] = "200" }, ct);
    internal Task<JsonDocument> GetResourceAppsAsync(SchoologyAuthService.Session session, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, "resource_apps", new Dictionary<string, string> { ["limit"] = "200" }, ct);
    internal Task<JsonDocument> GetCollectionResourcesAsync(SchoologyAuthService.Session session, long collectionId, long folderId = 0, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["start"] = "0", ["limit"] = "200", ["with_attachments"] = "TRUE" };
        if (folderId > 0) query["f"] = folderId.ToString(CultureInfo.InvariantCulture);
        return _auth.GetApiJsonAsync(session, $"collections/{collectionId}/resources", query, ct);
    }
    internal Task<JsonDocument> LaunchResourceAppAsync(SchoologyAuthService.Session session, long appId, CancellationToken ct = default) =>
        _auth.GetApiJsonAtVersionAsync(session, "v2", $"resources/applications/{appId}/launch", null, ct);

    internal Task<JsonDocument> GetDiscussionAsync(SchoologyAuthService.Session session, string kind, long entityId, long id, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"{EntityRealm(kind)}/{entityId}/discussions/{id}",
            new Dictionary<string, string> { ["with_attachments"] = "TRUE", ["richtext"] = "1" }, ct);
    internal Task<JsonDocument> GetDiscussionCommentsAsync(SchoologyAuthService.Session session, string kind, long entityId, long id, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"{EntityRealm(kind)}/{entityId}/discussions/{id}/comments",
            new Dictionary<string, string> { ["start"] = "0", ["limit"] = "100", ["with_attachments"] = "TRUE", ["richtext"] = "1" }, ct);
    internal Task<JsonDocument> GetEventAsync(SchoologyAuthService.Session session, string kind, long entityId, long id, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"{EntityRealm(kind)}/{entityId}/events/{id}",
            new Dictionary<string, string> { ["with_attachments"] = "TRUE", ["richtext"] = "1" }, ct);
    internal Task<JsonDocument> GetEventCommentsAsync(SchoologyAuthService.Session session, string kind, long entityId, long id, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"{EntityRealm(kind)}/{entityId}/events/{id}/comments",
            new Dictionary<string, string> { ["start"] = "0", ["limit"] = "50", ["with_attachments"] = "TRUE", ["richtext"] = "1" }, ct);
    internal Task<JsonDocument> CreateDiscussionAsync(SchoologyAuthService.Session session, string kind, long entityId, string title, string body, CancellationToken ct = default) =>
        _auth.SendApiFormAsync(session, HttpMethod.Post, $"{EntityRealm(kind)}/{entityId}/discussions",
            new Dictionary<string, string> { ["title"] = title, ["body"] = body, ["published"] = "1" }, ct);
    internal Task<JsonDocument> CreateEventAsync(SchoologyAuthService.Session session, string kind, long entityId, string title, string description, string start, bool allDay, bool commentsEnabled, CancellationToken ct = default) =>
        _auth.SendApiFormAsync(session, HttpMethod.Post, $"{EntityRealm(kind)}/{entityId}/events",
            new Dictionary<string, string> { ["title"] = title, ["description"] = description, ["start"] = start,
                ["is_all_day"] = allDay ? "1" : "0", ["rsvp"] = "0", ["comments_enabled"] = commentsEnabled ? "1" : "0" }, ct);
    private static string EntityRealm(string kind) => kind switch
    {
        "Groups" => "groups", "School Profile" => "schools", "Profile" => "users", _ => "sections"
    };

    internal Task<JsonDocument> GetAssignmentAsync(
        SchoologyAuthService.Session session, long sectionId, long assignmentId, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"sections/{sectionId}/assignments/{assignmentId}",
            new Dictionary<string, string> { ["richtext"] = "1", ["with_attachments"] = "TRUE" }, ct);

    internal Task<JsonDocument> GetAssignmentCommentsAsync(
        SchoologyAuthService.Session session, long sectionId, long assignmentId,
        CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"sections/{sectionId}/assignments/{assignmentId}/comments",
            new Dictionary<string, string>
            {
                ["start"] = "0", ["limit"] = "100", ["with_attachments"] = "TRUE", ["richtext"] = "1"
            }, ct);

    internal Task<JsonDocument> AddAssignmentCommentAsync(
        SchoologyAuthService.Session session, long sectionId, long assignmentId, string comment,
        CancellationToken ct = default) =>
        _auth.SendApiJsonBodyAsync(session, HttpMethod.Post,
            $"course/{sectionId}/materials/assignments/{assignmentId}/create_comment",
            new { comment, parent_id = 0 }, ct);

    // Official Android uses the section realm, grade-item ID, and current user ID.
    internal Task<JsonDocument> GetAssignmentSubmissionAsync(
        SchoologyAuthService.Session session, long sectionId, long gradeItemId, long userId,
        CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"sections/{sectionId}/submissions/{gradeItemId}/{userId}",
            new Dictionary<string, string>
            {
                ["with_attachments"] = "TRUE", ["all_revisions"] = "TRUE", ["start"] = "0", ["limit"] = "200"
            }, ct);
    internal Task<JsonDocument> GetDocumentAsync(
        SchoologyAuthService.Session session, long sectionId, long documentId, CancellationToken ct = default) =>
        _auth.GetApiJsonAsync(session, $"sections/{sectionId}/documents/{documentId}", null, ct);

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
                    "assignment", "assignments", "resource", "resources", "message", "messages", "folder", "folders", "user", "users",
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
        ArgumentNullException.ThrowIfNull(session);
        var normalizedPath = path.TrimStart('/');
        if (normalizedPath.Length == 0 ||
            normalizedPath.Split('/').Any(segment => segment is "." or "..") ||
            normalizedPath.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-')))
            throw new InvalidOperationException("The Schoology API path is invalid.");
        var url = $"https://{ApiHost}/v1/{normalizedPath}";
        using var response = await SendSignedRequestAsync(method, url,
            () => new FormUrlEncodedContent(fields), session.Token, session.Secret, null, cancellationToken);
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
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
        ArgumentNullException.ThrowIfNull(session);
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
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "Schoology could not load account data", response.StatusCode));
        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new InvalidOperationException("Schoology returned an unreadable response for this page.");
        }
    }
}


internal sealed partial class SchoologyAuthService
{
    internal string WebSessionUrl => $"https://{WebHost}/";

    internal sealed record WebSessionCookie(string Name, string Value, string Domain, string Path, bool HttpOnly, bool Secure);

    internal async Task<IReadOnlyList<WebSessionCookie>> GetWebSessionCookiesAsync(Session session, CancellationToken ct = default)
    {
        var defaultDomain = WebHost.EndsWith("schoologyca.com", StringComparison.OrdinalIgnoreCase) ? ".schoologyca.com" : ".schoology.com";
        // Android CSOAssignment.startSession(domain) sends the bare app host (for example app.schoology.com), not a URL with a scheme.
        var url = $"https://{ApiHost}/v1/sessionstart?domain={Uri.EscapeDataString(WebHost)}";
        var redirectCookies = new List<string>();
        using var response = await SendSignedRequestAsync(HttpMethod.Get, url, () => null,
            session.Token, session.Secret, null, ct, redirectSetCookieHeaders: redirectCookies);
        var body = await ReadResponseTextAsync(response.Content, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "Schoology could not prepare the browser session", response.StatusCode));
        var cookies = new Dictionary<string, WebSessionCookie>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string value, string domain, string path, bool httpOnly, bool secure)
        {
            domain = string.IsNullOrWhiteSpace(domain) ? defaultDomain : domain.Trim();
            path = string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') ? "/" : path;
            var host = domain.TrimStart('.').ToLowerInvariant();
            var targetHost = WebHost.ToLowerInvariant();
            var validDomain = host == "schoology.com" || host.EndsWith(".schoology.com", StringComparison.Ordinal) ||
                host == "schoologyca.com" || host.EndsWith(".schoologyca.com", StringComparison.Ordinal);
            var appliesToTarget = targetHost == host || targetHost.EndsWith("." + host, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value) ||
                name.Any(char.IsControl) || name.IndexOfAny([';', '=', ',']) >= 0 ||
                value.Any(char.IsControl) || value.IndexOfAny([';', ',']) >= 0 ||
                !validDomain || !appliesToTarget) return;
            // Tauri explicitly scopes the session cookie to Schoology sibling hosts.
            // Use the canonical host form expected by WebView2; a parent-domain
            // cookie still applies to its Schoology app subdomains.
            domain = host;
            cookies[name] = new(name, value, domain, path, httpOnly, true);
        }
        var allCookieHeaders = redirectCookies.ToList();
        if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            allCookieHeaders.AddRange(headers);
        foreach (var header in allCookieHeaders)
        {
            var parts = header.Split(';', StringSplitOptions.TrimEntries);
            var pair = parts.FirstOrDefault();
            var split = pair?.IndexOf('=') ?? -1;
            if (split <= 0) continue;
            var domain = defaultDomain; var path = "/"; var httpOnly = false; var secure = false;
            foreach (var attr in parts.Skip(1))
            {
                var eq = attr.IndexOf('='); var key = (eq >= 0 ? attr[..eq] : attr).Trim();
                var value = eq >= 0 ? attr[(eq + 1)..].Trim() : "";
                if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase) && value.Length > 0) domain = value;
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase) && value.StartsWith('/')) path = value;
                else if (key.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase)) httpOnly = true;
                else if (key.Equals("Secure", StringComparison.OrdinalIgnoreCase)) secure = true;
            }
            Add(pair![..split], pair[(split + 1)..], domain, path, httpOnly, secure);
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement; JsonElement array = default;
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("cookie", out array))
                root.TryGetProperty("cookies", out array);
            if (array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
            {
                string? Get(params string[] keys)
                {
                    foreach (var key in keys)
                        if (item.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number) return v.ToString();
                    return null;
                }
                Add(Get("key", "name") ?? "", Get("value", "val") ?? "", Get("domain") ?? defaultDomain,
                    Get("path") ?? "/", Get("httponly", "http_only") == "true", Get("secure") == "true");
            }
        }
        catch (JsonException) { }
        var mobilePair = MobileCookie.Split('=', 2);
        if (mobilePair.Length == 2) Add(mobilePair[0], mobilePair[1], defaultDomain, "/", false, true);
        if (!cookies.TryGetValue("SESS", out var sessCookie) || string.IsNullOrWhiteSpace(sessCookie.Value))
            throw new InvalidOperationException("Schoology did not return a usable SESS browser cookie. The session-start response contained no SESS Set-Cookie header or cookie object.");
        if (cookies.Count == 0) throw new InvalidOperationException("Schoology did not return browser session cookies.");
        return cookies.Values.ToArray();
    }

    internal async Task<JsonDocument> GetApiJsonAtVersionAsync(
        Session session, string version, string path, IReadOnlyDictionary<string, string>? query,
        CancellationToken ct = default)
    {
        if (version is not ("v1" or "v2")) throw new InvalidOperationException("The Schoology API version is invalid.");
        var normalized = path.TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "." or "..") ||
            normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-')))
            throw new InvalidOperationException("The Schoology API path is invalid.");
        var builder = new UriBuilder($"https://{ApiHost}/{version}/{normalized}");
        if (query is { Count: > 0 })
            builder.Query = string.Join("&", query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var response = await SendSignedRequestAsync(HttpMethod.Get, builder.Uri.AbsoluteUri, () => null,
            session.Token, session.Secret, null, ct);
        var body = await ReadResponseTextAsync(response.Content, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "Schoology could not load this resource", response.StatusCode));
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException("Schoology returned an unreadable response for this resource."); }
    }

    internal async Task<JsonDocument> SendApiJsonBodyAsync(Session session, HttpMethod method, string path, object payload, CancellationToken ct = default)
    {
        var normalized = path.TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "." or "..") ||
            normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-')))
            throw new InvalidOperationException("The Schoology API path is invalid.");
        using var response = await SendSignedRequestAsync(method, $"https://{ApiHost}/v1/{normalized}",
            () => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            session.Token, session.Secret, null, ct);
        var body = await ReadResponseTextAsync(response.Content, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "Schoology could not update this request", response.StatusCode));
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }
}
