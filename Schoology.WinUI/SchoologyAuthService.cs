using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials;

namespace Schoology.WinUI;

/// <summary>Schoology OAuth 1.0a login and protected local session storage.
/// Protocol details mirror the supplied official Schoology 2026.10.0 port.28 source.</summary>
internal sealed partial class SchoologyAuthService
{
    private static async Task<string> ReadResponseTextAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    internal async Task<byte[]> GetSchoologyFileBytesAsync(Session session, string rawUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(rawUrl))
            throw new InvalidOperationException("Schoology did not provide an attachment URL.");

        var candidate = rawUrl.Trim();
        Uri uri;
        if (candidate.StartsWith("/", StringComparison.Ordinal))
            uri = new Uri($"https://{ApiHost}{candidate}");
        else if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri!))
            throw new InvalidOperationException("Schoology returned an invalid attachment URL.");

        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Schoology attachment URLs must use HTTPS.");

        // File URLs may be emitted on the web host; Tauri's binary request signs
        // the matching API-host path instead, preserving the same route/query.
        if (uri.Host.Equals(WebHost, StringComparison.OrdinalIgnoreCase))
            uri = new UriBuilder(uri) { Host = ApiHost }.Uri;

        var isApi = uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase);
        var isStorage = IsTrustedSignedStorageUrl(uri);
        if (!isApi && !isStorage)
            throw new InvalidOperationException("Schoology returned an attachment URL on an untrusted host.");

        HttpResponseMessage response;
        if (isApi)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddOAuth(request, HttpMethod.Get, uri.AbsoluteUri, session.Token, session.Secret, null);
            AddClientIdentity(request);
            response = await OAuthHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.8.0");
            response = await OAuthHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        try
        {
            var currentUri = uri;
            for (var redirects = 0; response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect; redirects++)
            {
                if (redirects >= 5 || response.Headers.Location is null)
                    throw new InvalidOperationException("Schoology's attachment file used too many redirects.");
                var target = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location : new Uri(currentUri, response.Headers.Location);
                if (target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo) ||
                    !(IsSchoologyOrLausdHost(target.Host) || IsTrustedSignedStorageUrl(target)))
                    throw new InvalidOperationException("Schoology redirected the attachment to an untrusted host.");

                response.Dispose();
                currentUri = target;
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                // Never forward OAuth credentials or cookies beyond the API host.
                request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.8.0");
                response = await OAuthHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Schoology attachment download failed (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is long length && length > 60 * 1024 * 1024)
                throw new InvalidOperationException("The attachment exceeds the 60 MB safety limit.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > 60 * 1024 * 1024)
                throw new InvalidOperationException("The attachment exceeds the 60 MB safety limit.");
            return bytes;
        }
        finally { response.Dispose(); }
    }

    private static bool IsSchoologyOrLausdHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host == "schoology.com" || host.EndsWith(".schoology.com", StringComparison.Ordinal) ||
            host == "schoologyca.com" || host.EndsWith(".schoologyca.com", StringComparison.Ordinal) ||
            host == "lms.lausd.net" || host.EndsWith(".lms.lausd.net", StringComparison.Ordinal);
    }

    private static bool IsTrustedSignedStorageUrl(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var allowedHost = host == "s3.amazonaws.com" ||
            (host.StartsWith("s3.", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal)) ||
            (host.StartsWith("s3-", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal)) ||
            host.EndsWith(".s3.amazonaws.com", StringComparison.Ordinal) ||
            (host.Contains(".s3.", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal)) ||
            host.EndsWith(".cloudfront.net", StringComparison.Ordinal) ||
            host == "storage.googleapis.com" || host.EndsWith(".storage.googleapis.com", StringComparison.Ordinal);
        var query = uri.Query.ToLowerInvariant();
        var signed = new[] { "signature=", "awsaccesskeyid=", "x-amz-signature=", "x-goog-signature=", "key-pair-id=" }
            .Any(key => query.Contains(key, StringComparison.Ordinal));
        return allowedHost && signed;
    }

    private const string ConsumerKey = "998121f221d5dc0f4956ee6946d27d5e04e55635e";
    private const string ConsumerSecret = "5cff9d3780d64695d762ba8e05a34e9e";
    private const string MobileCookie = "s_mobile=03447c0175ac0c7299a5508fde9569fc";
    private const string VaultResource = "Schoology.WinUI.OAuth";
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
    private static readonly HttpClient OAuthHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        AllowAutoRedirect = false
    });
    private static long _serverTimeOffset;
    private readonly bool _canadian;
    private (string Token, string Secret)? _pendingSchoolRequest;
    private string ApiHost => _canadian ? "api.schoologyca.com" : "api.schoology.com";
    private string WebHost => _canadian ? "app.schoologyca.com" : "app.schoology.com";

    internal sealed record School(string Name, string Id, string? LoginType, string? LoginUrl, string? Domain)
    {
        public override string ToString() => Name;
    }
    internal sealed record Session(string Token, string Secret, long UserId, JsonElement User);
    internal sealed record SchoolBrowserRequest(string AuthorizationUrl, string RequestToken);

    public SchoologyAuthService(bool canadian) => _canadian = canadian;

    public async Task<IReadOnlyList<School>> SearchSchoolsAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"https://{ApiHost}/v1/login/school_search?query={Uri.EscapeDataString(query.Trim())}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddClientIdentity(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
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
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "Schoology rejected the sign-in", response.StatusCode));
        var session = await ExchangeAndVerifyAsync(temporary, cancellationToken);
        SaveSession(session);
        return session;
    }

    public async Task<Session> SignInWithQrAsync(string scannedQrData, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scannedQrData))
            throw new InvalidOperationException("The camera did not detect a valid Schoology QR code.");
        await SyncServerTimeAsync(cancellationToken);
        var temporary = await GetRequestTokenAsync(cancellationToken);
        var url = $"https://{ApiHost}/v1/oauth/qr_code_authorize_auto";
        var fields = new Dictionary<string, string>
        {
            ["scanned_qr_data"] = scannedQrData,
            ["oauth_token"] = temporary.Token
        };
        using var response = await SendSignedRequestAsync(
            HttpMethod.Post, url, () => new FormUrlEncodedContent(fields),
            temporary.Token, temporary.Secret, scannedQrData, cancellationToken);
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadableError(body, "QR authorization failed", response.StatusCode));
        var session = await ExchangeAndVerifyAsync(temporary, cancellationToken);
        SaveSession(session);
        return session;
    }

    public async Task<SchoolBrowserRequest> BeginSchoolBrowserLoginAsync(School school, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(school);
        var rawDomain = (school.Domain ?? school.LoginUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawDomain))
            throw new InvalidOperationException("This school did not provide a secure sign-in domain.");
        var candidate = rawDomain.Contains("://", StringComparison.Ordinal) ? rawDomain : "https://" + rawDomain;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var domainUri) || domainUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("This school provided an invalid sign-in domain.");
        var host = domainUri.Host;
        await SyncServerTimeAsync(cancellationToken);
        var temporary = await GetRequestTokenAsync(cancellationToken);
        _pendingSchoolRequest = temporary;
        var callback = $"https://{host}/mobile_login_success?os=android&os_version=34&app_version=600000478";
        var oauthPath = $"oauth/authorize?oauth_token={Uri.EscapeDataString(temporary.Token)}&oauth_callback={Uri.EscapeDataString(callback)}";
        var loginUrl = $"https://{host}/{oauthPath}&login_landing_dest={Uri.EscapeDataString(oauthPath)}";
        return new SchoolBrowserRequest(loginUrl, temporary.Token);
    }

    public async Task<Session> CompleteSchoolBrowserSignInAsync(string callbackRequestToken, CancellationToken cancellationToken = default)
    {
        var pending = _pendingSchoolRequest;
        if (pending is null || string.IsNullOrWhiteSpace(callbackRequestToken) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pending.Value.Token), Encoding.UTF8.GetBytes(callbackRequestToken)))
            throw new InvalidOperationException("The school sign-in callback did not match the pending request. Please start again.");
        _pendingSchoolRequest = null;
        var session = await ExchangeAndVerifyAsync(pending.Value, cancellationToken);
        SaveSession(session);
        return session;
    }

    private static IReadOnlyList<PasswordCredential> FindVaultCredentials(PasswordVault vault)
    {
        try
        {
            return vault.FindAllByResource(VaultResource);
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x80070490)
        {
            // PasswordVault uses HRESULT 0x80070490 (Element not found) when
            // the resource has no saved credentials. An empty vault is normal
            // on first sign-in and must not prevent adding the first session.
            return Array.Empty<PasswordCredential>();
        }
    }

    public async Task<Session?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        App.WriteStartupLog("RestoreSessionAsync entered.");
        try
        {
            App.WriteStartupLog("Opening Windows Credential Locker.");
            var vault = new PasswordVault();
            var credentials = FindVaultCredentials(vault);
            var credential = credentials.Count > 0 ? credentials[0] : null;
            if (credential is null)
            {
                App.WriteStartupLog("No Schoology credential exists in Windows Credential Locker.");
                return null;
            }

            App.WriteStartupLog("Found a saved Schoology credential; retrieving its protected password.");
            credential.RetrievePassword();
            using var document = JsonDocument.Parse(credential.Password);
            var root = document.RootElement;
            var token = root.GetProperty("token").GetString();
            var secret = root.GetProperty("secret").GetString();
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
            {
                App.WriteStartupLog("Saved Schoology credential did not contain a usable OAuth token and secret.");
                return null;
            }

            App.WriteStartupLog("Synchronizing Schoology server time before OAuth profile verification.");
            await SyncServerTimeAsync(cancellationToken);
            App.WriteStartupLog("Requesting the current Schoology user profile to validate the saved session.");
            var user = await GetCurrentUserAsync(token, secret, cancellationToken);
            var id = ReadLong(user, "id");
            if (id <= 0)
            {
                App.WriteStartupLog("Schoology profile validation returned an invalid user ID.");
                return null;
            }

            var session = new Session(token, secret, id, user);
            App.WriteStartupLog($"Saved Schoology session validated for user ID {id}; refreshing credential metadata.");
            SaveSession(session);
            App.WriteStartupLog("Saved Schoology session restoration completed successfully.");
            return session;
        }
        catch (OperationCanceledException)
        {
            App.WriteStartupLog("Saved Schoology session restoration was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            App.WriteStartupLog("Saved Schoology session restoration failed inside the authentication service.", ex);
            return null;
        }
    }

    public static void SignOut()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in FindVaultCredentials(vault)) vault.Remove(credential);
        }
        catch { }
    }

    // HttpClient's automatic redirect handling can drop OAuth Authorization
    // headers. Follow Schoology API redirects explicitly and generate a fresh
    // OAuth signature for every hop, matching the official port.28 request flow.
    private static async Task<HttpResponseMessage> SendSignedRequestAsync(
        HttpMethod method,
        string rawUrl,
        Func<HttpContent?> contentFactory,
        string token,
        string secret,
        string? scannedQrData,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? formParameters = null,
        ICollection<string>? redirectSetCookieHeaders = null)
    {
        var currentUri = new Uri(rawUrl, UriKind.Absolute);
        var expectedHost = currentUri.IdnHost;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            using var request = new HttpRequestMessage(method, currentUri)
            {
                Content = contentFactory()
            };
            AddOAuth(request, method, currentUri.AbsoluteUri, token, secret, scannedQrData, formParameters);
            AddClientIdentity(request);
            var response = await OAuthHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (redirectSetCookieHeaders is not null && response.Headers.TryGetValues("Set-Cookie", out var redirectCookies))
                foreach (var cookieHeader in redirectCookies) redirectSetCookieHeaders.Add(cookieHeader);
            var status = (int)response.StatusCode;
            var location = response.Headers.Location;
            if (status is not (301 or 302 or 303 or 307 or 308) || location is null)
                return response;

            if (redirectCount == 5)
            {
                response.Dispose();
                throw new InvalidOperationException("Schoology returned too many redirects during OAuth sign-in.");
            }

            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (nextUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(nextUri.IdnHost, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(nextUri.UserInfo))
            {
                response.Dispose();
                throw new InvalidOperationException("Schoology returned an unsafe OAuth redirect. Sign-in was stopped.");
            }

            response.Dispose();
            if (status == 303 ||
                ((status == 301 || status == 302) && method != HttpMethod.Get && method != HttpMethod.Head))
            {
                method = HttpMethod.Get;
                contentFactory = () => null;
                scannedQrData = null;
            }
            currentUri = nextUri;
        }

        throw new InvalidOperationException("Schoology OAuth sign-in could not complete.");
    }

    private static string SafeDiagnosticBody(string body)
    {
        var compact = string.Join(" ", body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 1200 ? compact : compact[..1200];
    }

    private async Task<(string Token, string Secret)> GetRequestTokenAsync(CancellationToken cancellationToken)
    {
        var url = $"https://{ApiHost}/v1/oauth/request_token";
        using var response = await SendSignedRequestAsync(
            HttpMethod.Get, url, () => null, "", "", null, cancellationToken);
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadableError(body, "Could not start Schoology OAuth", response.StatusCode));
        var values = ParseForm(body);
        if (!values.TryGetValue("oauth_token", out var token) || !values.TryGetValue("oauth_token_secret", out var secret))
            throw new InvalidOperationException("Schoology returned an invalid OAuth request token.");
        return (token, secret);
    }

    private async Task<Session> ExchangeAndVerifyAsync((string Token, string Secret) temporary, CancellationToken cancellationToken)
    {
        var url = $"https://{ApiHost}/v1/oauth/access_token";
        using var response = await SendSignedRequestAsync(
            HttpMethod.Get, url, () => null, temporary.Token, temporary.Secret, null, cancellationToken);
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
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
        using var response = await SendSignedRequestAsync(
            HttpMethod.Get, url, () => null, token, secret, null, cancellationToken);
        var body = await ReadResponseTextAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            App.WriteStartupLog($"Schoology OAuth profile request failed: HTTP {(int)response.StatusCode}; final URL: {response.RequestMessage?.RequestUri}; response: {SafeDiagnosticBody(body)}");
            throw new InvalidOperationException(ReadableError(body, "Schoology could not verify your signed-in profile", response.StatusCode));
        }
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void SaveSession(Session session)
    {
        var vault = new PasswordVault();
        foreach (var old in FindVaultCredentials(vault)) vault.Remove(old);
        var json = JsonSerializer.Serialize(new { token = session.Token, secret = session.Secret, userId = session.UserId });
        vault.Add(new PasswordCredential(VaultResource, session.UserId.ToString(CultureInfo.InvariantCulture), json));
    }

    private async Task SyncServerTimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"https://{WebHost}/oauth/timestamp", cancellationToken);
            if (response.IsSuccessStatusCode && long.TryParse(await ReadResponseTextAsync(response.Content, cancellationToken), out var timestamp))
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

    private static void AddOAuth(HttpRequestMessage request, HttpMethod method, string rawUrl, string token, string secret,
        string? scannedQrData = null, IReadOnlyDictionary<string, string>? formParameters = null)
    {
        var uri = new Uri(rawUrl);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Interlocked.Read(ref _serverTimeOffset);
        var parameters = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(uri.Query))
            parameters.AddRange(ParseQuery(uri.Query).Select(pair => (pair.Key, pair.Value)));
        if (!string.IsNullOrEmpty(scannedQrData)) parameters.Add(("scanned_qr_data", JavaEncode(scannedQrData)));
        if (formParameters is not null)
            parameters.AddRange(formParameters.Select(pair => (pair.Key, pair.Value)));
        parameters.AddRange(new[]
        {
            ("oauth_consumer_key", ConsumerKey), ("oauth_nonce", nonce), ("oauth_signature_method", "HMAC-SHA1"),
            ("oauth_timestamp", timestamp.ToString(CultureInfo.InvariantCulture)), ("oauth_version", "1.0"), ("oauth_token", token)
        });
        parameters.Sort((a, b) => { var key = string.CompareOrdinal(a.Key, b.Key); return key != 0 ? key : string.CompareOrdinal(a.Value, b.Value); });
        var normalized = string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"));
        var baseUri = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        var signatureBase = $"{method.Method.ToUpperInvariant()}&{JavaEncode(baseUri)}&{JavaEncode(normalized)}";
#pragma warning disable CA5350 // Schoology OAuth 1.0a explicitly requires HMAC-SHA1.
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes($"{ConsumerSecret}&{secret}"));
#pragma warning restore CA5350
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
