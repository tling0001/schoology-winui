using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials;

namespace Schoology.WinUI;

/// <summary>Schoology OAuth 1.0a login and protected local session storage.
/// Protocol details mirror the supplied official Schoology 2026.10.0 port.28 source.</summary>
internal sealed class SchoologyAuthService
{
    private const string ConsumerKey = "998121f221d5dc0f4956ee6946d27d5e04e55635e";
    private const string ConsumerSecret = "5cff9d3780d64695d762ba8e05a34e9e";
    private const string MobileCookie = "s_mobile=03447c0175ac0c7299a5508fde9569fc";
    private const string VaultResource = "Schoology.WinUI.OAuth";
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
    private static long _serverTimeOffset;
    private readonly bool _canadian;
    private string ApiHost => _canadian ? "api.schoologyca.com" : "api.schoology.com";
    private string WebHost => _canadian ? "app.schoologyca.com" : "app.schoology.com";

    internal sealed record School(string Name, string Id, string? LoginType, string? LoginUrl, string? Domain)
    {
        public override string ToString() => Name;
    }
    internal sealed record Session(string Token, string Secret, long UserId, JsonElement User);

    public SchoologyAuthService(bool canadian) => _canadian = canadian;

    public async Task<IReadOnlyList<School>> SearchSchoolsAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"https://{ApiHost}/v1/login/school_search?query={Uri.EscapeDataString(query.Trim())}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddClientIdentity(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "School search failed", response.StatusCode));
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("school", out var schools) || schools.ValueKind != JsonValueKind.Array) return [];
        return schools.EnumerateArray().Select(item => new School(
            Read(item, "title", "name", "school_name") ?? "School",
            Read(item, "id", "school_id") ?? string.Empty,
            Read(item, "login_type", "loginType"),
            Read(item, "login_url", "loginUrl"),
            Read(item, "domain"))).Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToArray();
    }

    public async Task<Session> SignInAsync(string username, string password, string? schoolId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Enter your Schoology username and password.");
        await SyncServerTimeAsync(cancellationToken);
        var temporary = await GetRequestTokenAsync(cancellationToken);
        var fields = new Dictionary<string, string> { ["user"] = username.Trim(), ["password"] = password, ["oauth_token"] = temporary.Token };
        if (!string.IsNullOrWhiteSpace(schoolId)) fields["school_id"] = schoolId;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{WebHost}/oauth/authorize_auto") { Content = new FormUrlEncodedContent(fields) };
        AddClientIdentity(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "Schoology rejected the sign-in", response.StatusCode));
        var session = await ExchangeAndVerifyAsync(temporary, cancellationToken);
        SaveSession(session);
        return session;
    }

    public async Task<Session?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.FindAllByResource(VaultResource).FirstOrDefault();
            if (credential is null) return null;
            credential.RetrievePassword();
            using var document = JsonDocument.Parse(credential.Password);
            var root = document.RootElement;
            var token = root.GetProperty("token").GetString();
            var secret = root.GetProperty("secret").GetString();
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret)) return null;
            await SyncServerTimeAsync(cancellationToken);
            var user = await GetCurrentUserAsync(token, secret, cancellationToken);
            var id = ReadLong(user, "id");
            if (id <= 0) return null;
            var session = new Session(token, secret, id, user);
            SaveSession(session);
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public void SignOut()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.FindAllByResource(VaultResource)) vault.Remove(credential);
        }
        catch { }
    }

    private async Task<(string Token, string Secret)> GetRequestTokenAsync(CancellationToken cancellationToken)
    {
        var url = $"https://{ApiHost}/v1/oauth/request_token";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOAuth(request, HttpMethod.Get, url, "", "");
        AddClientIdentity(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "Could not start Schoology OAuth", response.StatusCode));
        var values = ParseForm(body);
        if (!values.TryGetValue("oauth_token", out var token) || !values.TryGetValue("oauth_token_secret", out var secret))
            throw new InvalidOperationException("Schoology returned an invalid OAuth request token.");
        return (token, secret);
    }

    private async Task<Session> ExchangeAndVerifyAsync((string Token, string Secret) temporary, CancellationToken cancellationToken)
    {
        var url = $"https://{ApiHost}/v1/oauth/access_token";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOAuth(request, HttpMethod.Get, url, temporary.Token, temporary.Secret);
        AddClientIdentity(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "Schoology could not exchange the OAuth token", response.StatusCode));
        var values = ParseForm(body);
        if (!values.TryGetValue("oauth_token", out var token) || !values.TryGetValue("oauth_token_secret", out var secret))
            throw new InvalidOperationException("Schoology returned an invalid access token.");
        var user = await GetCurrentUserAsync(token, secret, cancellationToken);
        var userId = ReadLong(user, "id");
        if (userId <= 0) throw new InvalidOperationException("Schoology did not return a valid user profile.");
        return new Session(token, secret, userId, user);
    }

    private async Task<JsonElement> GetCurrentUserAsync(string token, string secret, CancellationToken cancellationToken)
    {
        var url = $"https://{ApiHost}/v1/users/me";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOAuth(request, HttpMethod.Get, url, token, secret);
        AddClientIdentity(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "Schoology could not verify your signed-in profile", response.StatusCode));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void SaveSession(Session session)
    {
        var vault = new PasswordVault();
        foreach (var old in vault.FindAllByResource(VaultResource)) vault.Remove(old);
        var json = JsonSerializer.Serialize(new { token = session.Token, secret = session.Secret, userId = session.UserId });
        vault.Add(new PasswordCredential(VaultResource, session.UserId.ToString(CultureInfo.InvariantCulture), json));
    }

    private async Task SyncServerTimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"https://{WebHost}/oauth/timestamp", cancellationToken);
            if (response.IsSuccessStatusCode && long.TryParse(await response.Content.ReadAsStringAsync(cancellationToken), out var timestamp))
                Interlocked.Exchange(ref _serverTimeOffset, timestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private static void AddClientIdentity(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.8.0");
        request.Headers.TryAddWithoutValidation("Cookie", MobileCookie);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    private static void AddOAuth(HttpRequestMessage request, HttpMethod method, string rawUrl, string token, string secret)
    {
        var uri = new Uri(rawUrl);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Interlocked.Read(ref _serverTimeOffset);
        var parameters = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(uri.Query))
            parameters.AddRange(ParseQuery(uri.Query).Select(pair => (pair.Key, pair.Value)));
        parameters.AddRange(new[]
        {
            ("oauth_consumer_key", ConsumerKey), ("oauth_nonce", nonce), ("oauth_signature_method", "HMAC-SHA1"),
            ("oauth_timestamp", timestamp.ToString(CultureInfo.InvariantCulture)), ("oauth_version", "1.0"), ("oauth_token", token)
        });
        parameters.Sort((a, b) => { var key = string.CompareOrdinal(a.Key, b.Key); return key != 0 ? key : string.CompareOrdinal(a.Value, b.Value); });
        var normalized = string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"));
        var baseUri = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        var signatureBase = $"{method.Method.ToUpperInvariant()}&{JavaEncode(baseUri)}&{JavaEncode(normalized)}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes($"{ConsumerSecret}&{secret}"));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));
        var header = $"OAuth oauth_consumer_key=\"{ConsumerKey}\", oauth_nonce=\"{nonce}\", oauth_signature_method=\"HMAC-SHA1\", oauth_timestamp=\"{timestamp}\", oauth_token=\"{token}\", oauth_version=\"1.0\", oauth_signature=\"{JavaEncode(signature)}\"";
        request.Headers.TryAddWithoutValidation("Authorization", header);
    }

    // Mirrors Java URLEncoder: spaces '+', '*' literal, '~' escaped.
    private static string JavaEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var output = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            if ((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'0' && b <= (byte)'9') || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'*') output.Append((char)b);
            else if (b == (byte)' ') output.Append('+');
            else output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    private static Dictionary<string, string> ParseForm(string value) => ParseQuery(value);
    private static Dictionary<string, string> ParseQuery(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            result[WebUtility.UrlDecode(split[0])] = split.Length > 1 ? WebUtility.UrlDecode(split[1]) : string.Empty;
        }
        return result;
    }
    private static string? Read(JsonElement item, params string[] keys)
    {
        foreach (var key in keys) if (item.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        return null;
    }
    private static long ReadLong(JsonElement item, string key) => item.TryGetProperty(key, out var value) && long.TryParse(value.ToString(), out var number) ? number : 0;
    private static string ReadableError(string body, string fallback, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = Read(document.RootElement, "message", "error_description", "error");
            if (!string.IsNullOrWhiteSpace(message)) return $"{fallback}: {message} (HTTP {(int)status}).";
        }
        catch { }
        return $"{fallback} (HTTP {(int)status}). Check your credentials and try again.";
    }
}
