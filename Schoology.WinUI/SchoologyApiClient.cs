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

    internal Task<JsonDocument> GetMessagesAsync(
        SchoologyAuthService.Session session, bool sent, CancellationToken cancellationToken = default) =>
        _auth.GetApiJsonAsync(session, sent ? "messages/sent" : "messages/inbox",
            new Dictionary<string, string> { ["limit"] = "50" }, cancellationToken);

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
            "Materials" => "documents",
            "Grades" => "grades",
            "Members" => "enrollments",
            "Staff" => "users",
            "Discussions" => "discussions",
            "Events" or "Upcoming" => "events",
            "Resources" => "resources",
            "Info" => throw new InvalidOperationException("School information is not available."),
            _ => throw new InvalidOperationException($"The {tab} view is not supported.")
        };
        return await _auth.GetApiJsonAsync(session, $"{realm}/{entityId}/{resource}",
            new Dictionary<string, string> { ["limit"] = "100" }, cancellationToken);
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
                ReadString(item.Data, "picture_url", "pictureUrl", "picture", "image", "profile_url", "profileUrl"),
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
                    "resource", "resources", "message", "messages", "folder", "folders", "user", "users" }
                : collectionKeys;
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out collection) && collection.ValueKind == JsonValueKind.Array)
                {
                    found = true;
                    break;
                }
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
            var title = ReadString(element, "title", "name_display", "nameDisplay", "name",
                "course_title", "courseTitle", "section_title", "sectionTitle", "subject", "message_subject")
                ?? (string.IsNullOrWhiteSpace(personName) ? "Schoology item" : personName);
            var subtitle = ReadString(element, "description", "body", "message", "role", "admin",
                "due", "due_date", "section_title", "sectionTitle", "created", "timestamp") ?? string.Empty;
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
    /// <summary>Execute an OAuth-signed GET against the configured Schoology API host.</summary>
    internal async Task<JsonDocument> GetApiJsonAsync(
        Session session, string path, IReadOnlyDictionary<string, string>? query,
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
            HttpMethod.Get, builder.Uri.AbsoluteUri, () => null,
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
