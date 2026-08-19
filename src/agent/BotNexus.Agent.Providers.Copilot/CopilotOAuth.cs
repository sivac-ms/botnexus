using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// OAuth credentials for GitHub Copilot.
/// AccessToken holds the current usable token (Copilot session token after exchange).
/// RefreshToken holds the long-lived GitHub OAuth token for re-exchange.
/// ExpiresAt is Unix epoch seconds.
/// ApiEndpoint is the Copilot API base URL (enterprise vs individual).
/// </summary>
public record OAuthCredentials(string AccessToken, string RefreshToken, long ExpiresAt, string? ApiEndpoint = null);

/// <summary>
/// GitHub Copilot OAuth device code flow and token management.
/// </summary>
public static class CopilotOAuth
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";

    // Cap for the peer-controlled OAuth token-exchange JSON bodies (device-code, access-token,
    // copilot-token). A GitHub OAuth device/token response is a few hundred bytes; 256 KiB is
    // generous headroom yet small enough that a hostile or malfunctioning endpoint cannot force the
    // runtime to buffer an unbounded body before JsonDocument.Parse (OOM-DoS hardening, #1772).
    // Matches the OpenClaw Google-Meet OAuth token cap the issue cites. Kept far below the shared
    // BoundedHttpContent.DefaultMaxResponseBytes (16 MiB) used for larger model/search payloads.
    private const long OAuthResponseMaxBytes = 256 * 1024;

    /// <summary>
    /// Number of attempts made against the Copilot token-exchange endpoint before giving up.
    /// A single transient 502 from <c>api.github.com</c> previously failed the whole refresh and
    /// left the gateway with no usable API key for the turn (#2977).
    /// </summary>
    internal const int TokenExchangeMaxAttempts = 3;

    /// <summary>
    /// Base delay for the token-exchange retry backoff. Attempt N waits
    /// <c>Base * 2^(N-1)</c> plus jitter, so three attempts span roughly 0.5s-2.3s.
    /// </summary>
    internal static readonly TimeSpan TokenExchangeBaseRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound applied to a server-supplied <c>Retry-After</c>. GitHub can advertise a very long
    /// cool-off; waiting it out inside a turn would stall the caller far longer than simply failing
    /// the refresh, so the honoured delay is clamped.
    /// </summary>
    internal static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Refresh margin applied by <see cref="GetApiKeyAsync"/>. Credentials are re-exchanged once
    /// they are within this many seconds of expiry. Deliberately wide (10 minutes rather than the
    /// original 60 seconds): a partial GitHub outage then has a large window in which the retrying
    /// exchange can succeed before the credential actually lapses, instead of the original cliff
    /// where the first failed refresh immediately left the caller with no key (#2977).
    /// Public because the gateway's own auth-entry refresh check mirrors this same margin.
    /// </summary>
    public const long RefreshMarginSeconds = 600;

    /// <summary>
    /// Maximum valid Unix timestamp (seconds) for an expiry claim.
    /// Values beyond this exceed <see cref="DateTimeOffset.MaxValue"/> and will cause
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds"/> to throw.
    /// A crafted JWT with an out-of-range <c>exp</c> is treated as invalid and forces a refresh.
    /// </summary>
    public static readonly long MaxValidExpiresAt = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Perform GitHub device code OAuth flow for Copilot access.
    /// Returns credentials with the GitHub OAuth token (requires exchange before API use).
    /// </summary>
    public static async Task<OAuthCredentials> LoginAsync(
        Func<string, string?, Task> onAuth,
        Func<string, Task<string>>? onPrompt = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // Step 1: Request device code
        using var codeResponse = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user"
        }, ct);

        var codeJson = await ReadJsonAsync(codeResponse, ct);
        // The envelope is already known to be a JSON object (ReadBoundedJsonAsync enforces that),
        // but the individual fields are still peer-controlled: a well-formed object missing
        // device_code used to slip a null past ! and surface as a NullReferenceException far
        // downstream. Fail here, by field name, with no body text. (#2894)
        var deviceCode = RequireStringProperty(codeJson, "device_code", DeviceCodeUrl);
        var userCode = RequireStringProperty(codeJson, "user_code", DeviceCodeUrl);
        var verificationUri = RequireStringProperty(codeJson, "verification_uri", DeviceCodeUrl);
        var interval = codeJson.TryGetProperty("interval", out var intervalEl) ? intervalEl.GetInt32() : 5;
        var expiresIn = codeJson.TryGetProperty("expires_in", out var expiresEl) ? expiresEl.GetInt32() : 900;

        // Step 2: Present auth URL and code to user
        await onAuth(verificationUri, userCode);

        // Step 3: Poll for authorization
        var pollInterval = Math.Max(interval, 1);
        // #3738: the device-code window is bounded monotonically. A backwards host clock step would
        // otherwise extend polling past the code's real expiry (the server rejects the code anyway, so
        // the loop would spin until cancelled); a forward step would abandon a still-valid code early.
        var expiry = TimeSpan.FromSeconds(expiresIn);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < expiry)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct);
            onProgress?.Invoke("Waiting for authorization...");

            using var tokenResponse = await PostFormAsync(AccessTokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, ct);

            var tokenJson = await ReadJsonAsync(tokenResponse, ct);

            if (tokenJson.TryGetProperty("access_token", out var accessTokenEl))
            {
                var accessToken = accessTokenEl.GetString()!;
                // Return credentials with ExpiresAt = 0 to force Copilot token exchange on first use
                return new OAuthCredentials(accessToken, "", 0);
            }

            var error = tokenJson.TryGetProperty("error", out var errorEl) ? errorEl.GetString() : null;

            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    pollInterval += 5;
                    continue;
                case "access_denied":
                    throw new InvalidOperationException("GitHub device authorization was denied by the user.");
                case "expired_token":
                    throw new TimeoutException("GitHub device code expired before authorization was completed.");
            }

            // Do NOT embed the peer-supplied error_description: GitHub's OAuth error bodies
            // can reflect the just-submitted credential (refresh_token / device_code) back inside
            // error_description, which would then leak into the exception text and any log/telemetry
            // sink that captures it. Throw with the machine error code only. (#1884)
            throw new InvalidOperationException(BuildOAuthErrorMessage(error));
        }

        throw new TimeoutException("Timed out waiting for GitHub device code authorization.");
    }

    /// <summary>
    /// Refresh expired Copilot credentials by re-exchanging the GitHub token.
    /// </summary>
    public static async Task<OAuthCredentials> RefreshAsync(
        OAuthCredentials credentials, CancellationToken ct = default)
    {
        // Use RefreshToken (GitHub OAuth token) if available, otherwise AccessToken is the GitHub token
        var githubToken = !string.IsNullOrEmpty(credentials.RefreshToken)
            ? credentials.RefreshToken
            : credentials.AccessToken;

        var (copilotToken, expiresAt, apiEndpoint) = await ExchangeForCopilotTokenAsync(githubToken, ct);
        return new OAuthCredentials(copilotToken, githubToken, expiresAt, apiEndpoint);
    }

    /// <summary>
    /// Get a usable Copilot API key, auto-refreshing if expired.
    /// Returns null if the provider has no credentials.
    /// </summary>
    public static async Task<(OAuthCredentials NewCredentials, string ApiKey)?> GetApiKeyAsync(
        string provider, Dictionary<string, OAuthCredentials> credentialsMap, CancellationToken ct = default)
    {
        if (!credentialsMap.TryGetValue(provider, out var credentials))
            return null;

        // Refresh if expired, within RefreshMarginSeconds of expiry, or if ExpiresAt is out of the
        // valid range. An out-of-range ExpiresAt (e.g. from a crafted JWT with a huge exp claim)
        // would cause DateTimeOffset.FromUnixTimeSeconds to throw - treat it as invalid and force a
        // refresh.
        if (!IsExpiresAtInRange(credentials.ExpiresAt) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= credentials.ExpiresAt - RefreshMarginSeconds)
        {
            credentials = await RefreshAsync(credentials, ct);
        }

        return (credentials, credentials.AccessToken);
    }

    /// <summary>
    /// Returns true when <paramref name="expiresAt"/> is within the range accepted by
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds"/>: strictly positive and not greater than
    /// <see cref="MaxValidExpiresAt"/>.  Values outside this range indicate a malformed or
    /// deliberately crafted token and must be treated as immediately expired.
    /// </summary>
    public static bool IsExpiresAtInRange(long expiresAt)
        => expiresAt > 0 && expiresAt <= MaxValidExpiresAt;

    /// <summary>
    /// Exchanges the long-lived GitHub OAuth token for a short-lived Copilot session token,
    /// retrying transient upstream failures.
    /// </summary>
    /// <remarks>
    /// Retries up to <see cref="TokenExchangeMaxAttempts"/> times on transient conditions only -
    /// HTTP 5xx, HTTP 429 and network-level faults (connect/TLS/read/timeouts) - with exponential
    /// backoff plus jitter. 401/403 are terminal: the credential is revoked or expired, retrying it
    /// cannot succeed and repeated attempts risk tripping GitHub's abuse detection.
    /// No response body or error text is ever logged or embedded in the thrown exception: GitHub's
    /// OAuth error bodies can reflect the just-submitted credential back inside error_description
    /// (#1884). Only the numeric status code is surfaced.
    /// </remarks>
    private static async Task<(string Token, long ExpiresAt, string? ApiEndpoint)> ExchangeForCopilotTokenAsync(
        string githubToken, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var isLastAttempt = attempt >= TokenExchangeMaxAttempts;
            TimeSpan? retryAfter = null;
            HttpResponseMessage? response = null;
            try
            {
                response = await SendCopilotTokenRequestAsync(githubToken, ct);

                if (response.IsSuccessStatusCode)
                {
                    return await ParseCopilotTokenResponseAsync(response, ct);
                }

                var status = response.StatusCode;
                if (isLastAttempt || !IsTransientTokenExchangeStatus(status))
                {
                    // Status code only - never the body or any peer-supplied error text (#1884).
                    throw new HttpRequestException(
                        $"Copilot token exchange failed with status {(int)status} ({status}).",
                        inner: null,
                        statusCode: status);
                }

                // A throttled or shedding GitHub tells us when to come back; obeying it recovers
                // sooner than our own backoff and avoids deepening the outage (#2977).
                retryAfter = GetRetryAfterDelay(response.Headers.RetryAfter);
            }
            // Network-level faults surface before a response exists; a fault raised after the
            // response was received (including the terminal throw above) is not retried here.
            catch (HttpRequestException) when (!isLastAttempt && response is null)
            {
            }
            catch (IOException) when (!isLastAttempt && response is null)
            {
            }
            // HttpClient surfaces its own timeout as TaskCanceledException with no caller cancellation.
            catch (TaskCanceledException) when (!isLastAttempt && response is null && !ct.IsCancellationRequested)
            {
            }
            finally
            {
                response?.Dispose();
            }

            await Task.Delay(retryAfter ?? ComputeTokenExchangeRetryDelay(attempt), ct);
        }
    }

    /// <summary>
    /// Translates a <c>Retry-After</c> header (delta-seconds or HTTP-date form) into a wait, or
    /// null when absent. A hostile or clock-skewed value is clamped to
    /// <see cref="MaxRetryAfterDelay"/> and a past date collapses to zero, so a peer can never park
    /// the refresh indefinitely.
    /// </summary>
    /// <param name="retryAfter">The parsed <c>Retry-After</c> header value, if any.</param>
    /// <param name="utcNow">The current time, injectable for tests.</param>
    /// <returns>The honoured delay, or null when the header is absent or unusable.</returns>
    internal static TimeSpan? GetRetryAfterDelay(RetryConditionHeaderValue? retryAfter, DateTimeOffset? utcNow = null)
    {
        if (retryAfter is null)
        {
            return null;
        }

        TimeSpan delay;
        if (retryAfter.Delta is { } delta)
        {
            delay = delta;
        }
        else if (retryAfter.Date is { } date)
        {
            delay = date - (utcNow ?? DateTimeOffset.UtcNow);
        }
        else
        {
            return null;
        }

        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaxRetryAfterDelay ? MaxRetryAfterDelay : delay;
    }

    /// <summary>
    /// Returns true when a token-exchange response status is worth retrying: any 5xx (the 502 that
    /// caused the outage), 429 or 408. 401/403 are deliberately excluded - a revoked or expired
    /// credential will not start working, and hammering GitHub with it invites abuse throttling.
    /// </summary>
    /// <param name="statusCode">The observed response status code.</param>
    /// <returns><c>true</c> when the failure is transient and should be retried.</returns>
    internal static bool IsTransientTokenExchangeStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            || (int)statusCode >= 500;

    /// <summary>
    /// Computes the backoff delay before the retry that follows <paramref name="attempt"/>:
    /// <c>TokenExchangeBaseRetryDelay * 2^(attempt-1)</c> plus up to 50% jitter, so concurrent
    /// agents recovering from the same upstream outage do not retry in lockstep.
    /// </summary>
    /// <param name="attempt">The 1-based attempt that just failed.</param>
    /// <param name="jitter">Jitter fraction in [0,1); defaults to a random value.</param>
    /// <returns>The delay to wait before the next attempt.</returns>
    internal static TimeSpan ComputeTokenExchangeRetryDelay(int attempt, double? jitter = null)
    {
        var exponent = Math.Max(attempt, 1) - 1;
        var baseMs = TokenExchangeBaseRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var jitterFraction = jitter ?? Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(baseMs * (1 + (0.5 * jitterFraction)));
    }

    private static async Task<HttpResponseMessage> SendCopilotTokenRequestAsync(string githubToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {githubToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "BotNexus/0.1");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.0");

        return await SharedClient.SendAsync(request, ct);
    }

    private static async Task<(string Token, long ExpiresAt, string? ApiEndpoint)> ParseCopilotTokenResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var json = await ReadJsonAsync(response, ct);
        var token = json.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Copilot token exchange returned no token.");

        string? apiEndpoint = null;
        if (json.TryGetProperty("endpoints", out var endpoints) &&
            endpoints.TryGetProperty("api", out var apiEl))
        {
            // #2006: the advertised endpoints.api host is peer-controlled. Gate it through the
            // https-only host allowlist before it is allowed to flow onto LlmModel.BaseUrl and
            // carry the bearer token. A rejected value drops to null so the caller falls back to
            // the default individual host rather than routing the token to an attacker-chosen host.
            apiEndpoint = CopilotEndpointAllowlist.SanitiseApiEndpoint(apiEl.GetString());
        }

        long expiresAt;
        if (json.TryGetProperty("expires_at", out var expiresAtEl) && expiresAtEl.ValueKind == JsonValueKind.Number)
        {
            var rawExpiresAt = expiresAtEl.GetInt64();
            // Reject out-of-range exp claims: a value <= 0 or beyond DateTimeOffset.MaxValue indicates a
            // malformed or crafted response. Fall back to the refresh_in-derived expiry instead.
            if (IsExpiresAtInRange(rawExpiresAt))
            {
                expiresAt = rawExpiresAt;
            }
            else
            {
                var refreshIn = json.TryGetProperty("refresh_in", out var refreshInEl) ? refreshInEl.GetInt32() : 1500;
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshIn).ToUnixTimeSeconds();
            }
        }
        else
        {
            var refreshIn = json.TryGetProperty("refresh_in", out var refreshInEl) ? refreshInEl.GetInt32() : 1500;
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshIn).ToUnixTimeSeconds();
        }

        return (token, expiresAt, apiEndpoint);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        string url, IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SharedClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    // All three OAuth reads funnel through this choke-point, so capping here bounds every
    // peer-controlled token-exchange body in the file.
    private static Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
        => ReadBoundedJsonAsync(response, OAuthResponseMaxBytes, ct);

    /// <summary>
    /// Reads an OAuth JSON response through <see cref="BoundedHttpContent"/> so a hostile or
    /// malfunctioning endpoint cannot stream an unbounded body into <see cref="JsonDocument.Parse(string)"/>.
    /// The decoded body is parsed exactly as the previous unbounded read did; the only added behaviour
    /// is a <see cref="ResponseContentTooLargeException"/> when the body exceeds <paramref name="maxBytes"/>
    /// (including a cheap up-front rejection of an over-cap declared <c>Content-Length</c>).
    /// Exposed as an internal seam so the cap is unit-testable without the static <see cref="SharedClient"/>.
    /// </summary>
    internal static async Task<JsonElement> ReadBoundedJsonAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        var json = await BoundedHttpContent.ReadStringWithLimitAsync(response.Content, maxBytes, ct);
        using var doc = JsonDocument.Parse(json);

        // Every OAuth caller indexes the result with GetProperty / TryGetProperty, which requires a
        // JSON object. A hostile or rewriting endpoint returning a JSON null, an array or a scalar
        // otherwise produced a raw System.Text.Json InvalidOperationException instead of a
        // controlled authentication failure. Validate once here so all three call sites are covered.
        // The diagnostic names the endpoint and the observed kind only - never any body text, which
        // can reflect the just-submitted credential back at us (see #1884). (#2894)
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            var endpoint = response.RequestMessage?.RequestUri?.ToString() ?? "the GitHub OAuth endpoint";
            throw new InvalidOperationException(
                $"GitHub returned an unexpected response from {endpoint}: expected a JSON object " +
                $"but the response root was {doc.RootElement.ValueKind}.");
        }

        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Reads a required string field out of a validated OAuth response object, failing by field
    /// name when it is absent or not a JSON string. Replaces the null-forgiving
    /// <c>GetProperty(...).GetString()!</c> reads so a well-formed but incomplete envelope produces
    /// an actionable authentication error rather than a NullReferenceException later in the flow.
    /// The message carries the field name and the endpoint only - never the value of any field,
    /// because siblings in the envelope can be credentials. (#2894)
    /// </summary>
    /// <param name="root">A JSON object element previously validated by <see cref="ReadBoundedJsonAsync"/>.</param>
    /// <param name="propertyName">The required property name.</param>
    /// <param name="endpoint">The endpoint URL to name in the failure diagnostic.</param>
    /// <returns>The property's string value.</returns>
    internal static string RequireStringProperty(JsonElement root, string propertyName, string endpoint)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"GitHub returned an unexpected response from {endpoint}: " +
                $"required field '{propertyName}' was missing or was not a JSON string.");
        }

        return element.GetString()
            ?? throw new InvalidOperationException(
                $"GitHub returned an unexpected response from {endpoint}: " +
                $"required field '{propertyName}' was missing or was not a JSON string.");
    }

    /// <summary>
    /// Builds a safe exception message for a failed GitHub OAuth token exchange.
    /// Only the machine-readable <paramref name="error"/> code is included; the peer-supplied
    /// <c>error_description</c> is deliberately omitted because GitHub can reflect the just-submitted
    /// credential (refresh_token / device_code) back inside it, which would then leak into the
    /// exception text and any log/telemetry sink that captures it. (#1884)
    /// </summary>
    /// <param name="error">The machine-readable OAuth <c>error</c> code from the response, if any.</param>
    /// <returns>A message containing only the error code, never any peer-supplied description.</returns>
    internal static string BuildOAuthErrorMessage(string? error)
    {
        var code = string.IsNullOrWhiteSpace(error) ? "unknown_error" : error;
        return $"GitHub OAuth error: {code}";
    }
}
