
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Conflux.Services;

public sealed class ChatGptAuthService
{
    private const string Issuer = "https://auth.openai.com";
    private const string AuthorizeEndpoint =
        "https://auth.openai.com/api/accounts/authorize";
    private const string TokenEndpoint =
        "https://auth.openai.com/api/accounts/oauth/token";
    private const string Resource = "https://api.openai.com/v1";
    private const string RequiredScope = "chatgpt.tokens.use.direct";
    private const string InitialClientId = "dynamic_agent_client";
    private const int CallbackPort = 1455;

    private const string RequestedScopes =
        "openid profile email offline_access resource.invoke " +
        "chatgpt.tokens.use.direct";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private static readonly byte[] CredentialEntropy =
        Encoding.UTF8.GetBytes("Conflux.ChatGPT.Credentials.v1");

    private readonly string _directory;
    private readonly string _accountsDirectory;
    private readonly string _legacyCredentialPath;
    private readonly string _hostIdPath;
    private readonly string _legacyClientIdPath;
    private readonly string _pendingClientIdPath;
    private readonly string _selectedAccountPath;
    private readonly string _diagnosticPath;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private sealed class CredentialRecord
    {
        public string Email { get; set; } = "";
        public string Subject { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string HostId { get; set; } = "";
        public string IdToken { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string TokenType { get; set; } = "Bearer";
        public string[] Scopes { get; set; } = Array.Empty<string>();
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset SavedAt { get; set; }
    }

    public sealed record ConnectionStatus(
        bool IsConnected,
        string? Email,
        bool CanUseChatGptPlan,
        DateTimeOffset? ExpiresAt,
        string Message);

    public sealed record ChatGptAccountInfo(
        string Subject,
        string Email,
        string DisplayName,
        bool CanUseChatGptPlan,
        DateTimeOffset ExpiresAt,
        bool IsSelected);

    private sealed record AuthorizationResult(
        string Code,
        string ClientId);

    private sealed class TokenResponse
    {
        public string access_token { get; set; } = "";
        public string refresh_token { get; set; } = "";
        public string id_token { get; set; } = "";
        public string token_type { get; set; } = "Bearer";
        public int expires_in { get; set; }
        public string scope { get; set; } = "";
    }

    private sealed class TokenExchangeException : InvalidOperationException
    {
        public int StatusCode { get; }
        public string ErrorCode { get; }

        public TokenExchangeException(
            int statusCode,
            string errorCode,
            string message)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }

    public ChatGptAuthService()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "Conflux");

        _accountsDirectory = Path.Combine(
            _directory,
            "chatgpt.accounts");

        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_accountsDirectory);

        _legacyCredentialPath = Path.Combine(
            _directory,
            "chatgpt.credentials");

        _legacyClientIdPath = Path.Combine(
            _directory,
            "chatgpt.client-id");

        _pendingClientIdPath = Path.Combine(
            _directory,
            "chatgpt.pending-client-id");

        _hostIdPath = Path.Combine(
            _directory,
            "chatgpt.host-id");

        _selectedAccountPath = Path.Combine(
            _directory,
            "chatgpt.selected-account");

        _diagnosticPath = Path.Combine(
            _directory,
            "chatgpt-auth-diagnostics.log");
    }

    public async Task<ConnectionStatus> GetStatusAsync()
    {
        await MigrateLegacyCredentialsAsync();

        var subject = await GetSelectedSubjectAsync();
        CredentialRecord? record = null;

        if (!string.IsNullOrWhiteSpace(subject))
            record = await LoadCredentialsForSubjectAsync(subject);

        if (record is null)
        {
            var accounts = await GetAllAccountRecordsAsync();

            if (accounts.Count > 0)
            {
                record = accounts[0];
                await SaveSelectedSubjectAsync(record.Subject);
            }
        }

        if (record is null)
        {
            var hasLegacyRegistration =
                File.Exists(_legacyClientIdPath);

            var hasPendingRegistration =
                await GetPendingClientIdAsync() is not null;

            return new ConnectionStatus(
                false,
                null,
                false,
                null,
                hasPendingRegistration
                    ? "Not connected. A previous sign-in registration " +
                      "can be resumed by trying again."
                    : hasLegacyRegistration
                        ? "Not connected. A previous registration exists, " +
                          "but a new sign-in will create an isolated account registration."
                        : "Not connected to ChatGPT.");
        }

        var canUsePlan = HasRequiredScope(record);

        return new ConnectionStatus(
            true,
            record.Email,
            canUsePlan,
            record.ExpiresAt,
            canUsePlan
                ? "Connected. ChatGPT-plan permission was granted."
                : "Signed in, but ChatGPT-plan permission is not enabled.");
    }

    public async Task<IReadOnlyList<ChatGptAccountInfo>>
        GetAccountsAsync()
    {
        await _operationLock.WaitAsync();

        try
        {
            await MigrateLegacyCredentialsAsync();

            var records = await GetAllAccountRecordsAsync();
            var selectedSubject = await GetSelectedSubjectAsync();

            if (records.Count > 0 &&
                (string.IsNullOrWhiteSpace(selectedSubject) ||
                 records.All(record =>
                     !string.Equals(
                         record.Subject,
                         selectedSubject,
                         StringComparison.Ordinal))))
            {
                selectedSubject = records[0].Subject;
                await SaveSelectedSubjectAsync(selectedSubject);
            }

            return records
                .OrderBy(
                    record => record.Email,
                    StringComparer.OrdinalIgnoreCase)
                .Select(record => new ChatGptAccountInfo(
                    record.Subject,
                    record.Email,
                    string.IsNullOrWhiteSpace(record.Email)
                        ? "ChatGPT account"
                        : record.Email,
                    HasRequiredScope(record),
                    record.ExpiresAt,
                    string.Equals(
                        record.Subject,
                        selectedSubject,
                        StringComparison.Ordinal)))
                .ToArray();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SelectAccountAsync(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException(
                "An account subject is required.",
                nameof(subject));
        }

        await _operationLock.WaitAsync();

        try
        {
            await MigrateLegacyCredentialsAsync();

            var record =
                await LoadCredentialsForSubjectAsync(subject);

            if (record is null)
            {
                throw new InvalidOperationException(
                    "The selected ChatGPT account could not be found. " +
                    "Refresh the account list and try again.");
            }

            await SaveSelectedSubjectAsync(record.Subject);

            await WriteAuthDiagnosticAsync(
                "Account selected",
                $"email_present={!string.IsNullOrWhiteSpace(record.Email)}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<ConnectionStatus> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hostId = await GetOrCreateHostIdAsync();
            var redirectUri =
                $"http://127.0.0.1:{CallbackPort}/auth/callback";

            using var listener = new HttpListener();

            listener.Prefixes.Add(
                $"http://127.0.0.1:{CallbackPort}/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                await WriteAuthDiagnosticAsync(
                    "Callback listener failed",
                    $"exception_type={ex.GetType().Name}; " +
                    $"message={ex.Message}");

                throw new InvalidOperationException(
                    $"Conflux could not open its local sign-in callback " +
                    $"on port {CallbackPort}. Close any application " +
                    $"already using that port and try again. Details: " +
                    ex.Message,
                    ex);
            }

            // Resume a registration previously issued by OpenAI if an
            // earlier attempt failed before credentials were saved.
            var pendingClientId = await GetPendingClientIdAsync();
            var isResumingRegistration =
                !string.IsNullOrWhiteSpace(pendingClientId);

            var authorizationClientId = isResumingRegistration
                ? pendingClientId!
                : InitialClientId;

            var firstRegistration = !isResumingRegistration;

            var verifier = CreateRandomUrlSafeValue(32);
            var challenge = CreateCodeChallenge(verifier);
            var state = CreateRandomUrlSafeValue(32);
            var nonce = CreateRandomUrlSafeValue(32);

            await WriteAuthDiagnosticAsync(
                "Authorization starting",
                $"registration_mode={(firstRegistration ? "new_account" : "resume_pending")}; " +
                $"client_id={authorizationClientId}; " +
                $"redirect_uri={redirectUri}; " +
                $"host_id_present={!string.IsNullOrWhiteSpace(hostId)}; " +
                $"pkce_challenge_length={challenge.Length}");

            var authorizationUrl = BuildAuthorizationUrl(
                authorizationClientId,
                firstRegistration,
                hostId,
                redirectUri,
                state,
                nonce,
                challenge);

            OpenAuthorizationPage(authorizationUrl);

            var callback = await ReceiveCallbackAsync(
                listener,
                state,
                expectedClientId: isResumingRegistration
                    ? authorizationClientId
                    : null,
                cancellationToken);

            // Persist the issued registration ID before token exchange.
            // A failed exchange must not force another dynamic registration.
            await SavePendingClientIdAsync(callback.ClientId);

            await WriteAuthDiagnosticAsync(
                "Authorization callback received",
                $"registration_mode={(firstRegistration ? "new_account" : "resume_pending")}; " +
                $"client_id={callback.ClientId}; " +
                $"code_length={callback.Code.Length}");

            TokenResponse token;

            try
            {
                token = await ExchangeCodeAsync(
                    callback.Code,
                    callback.ClientId,
                    verifier,
                    redirectUri,
                    cancellationToken);
            }
            catch (TokenExchangeException ex) when (
                ex.StatusCode == (int)HttpStatusCode.BadRequest &&
                string.Equals(
                    ex.ErrorCode,
                    "invalid_grant",
                    StringComparison.Ordinal))
            {
                // The rejected authorization code and verifier are never
                // reused. Resume with the issued client ID and fresh PKCE.
                await SavePendingClientIdAsync(callback.ClientId);

                await WriteAuthDiagnosticAsync(
                    "Retrying authorization after invalid_grant",
                    $"client_id={callback.ClientId}; " +
                    "retry_count=1; " +
                    "registration_mode=resume_pending; " +
                    "agent_name_hint_included=false");

                verifier = CreateRandomUrlSafeValue(32);
                challenge = CreateCodeChallenge(verifier);
                state = CreateRandomUrlSafeValue(32);
                nonce = CreateRandomUrlSafeValue(32);

                var retryAuthorizationUrl = BuildAuthorizationUrl(
                    callback.ClientId,
                    firstRegistration: false,
                    hostId,
                    redirectUri,
                    state,
                    nonce,
                    challenge);

                OpenAuthorizationPage(retryAuthorizationUrl);

                var retryCallback = await ReceiveCallbackAsync(
                    listener,
                    state,
                    expectedClientId: callback.ClientId,
                    cancellationToken);

                // Save the registration ID again in case the callback
                // supplied it explicitly during reauthorization.
                await SavePendingClientIdAsync(retryCallback.ClientId);

                await WriteAuthDiagnosticAsync(
                    "Retry authorization callback received",
                    $"client_id={retryCallback.ClientId}; " +
                    $"code_length={retryCallback.Code.Length}");

                token = await ExchangeCodeAsync(
                    retryCallback.Code,
                    retryCallback.ClientId,
                    verifier,
                    redirectUri,
                    cancellationToken);

                callback = retryCallback;
            }

            if (string.IsNullOrWhiteSpace(token.access_token) ||
                string.IsNullOrWhiteSpace(token.id_token) ||
                string.IsNullOrWhiteSpace(token.refresh_token))
            {
                await WriteAuthDiagnosticAsync(
                    "Token response incomplete",
                    $"access_token_present={!string.IsNullOrWhiteSpace(token.access_token)}; " +
                    $"id_token_present={!string.IsNullOrWhiteSpace(token.id_token)}; " +
                    $"refresh_token_present={!string.IsNullOrWhiteSpace(token.refresh_token)}");

                throw new InvalidOperationException(
                    "OpenAI's token response did not include all " +
                    "required credentials. No account credentials were saved.");
            }

            var principal = await ValidateIdTokenAsync(
                token.id_token,
                callback.ClientId,
                nonce,
                cancellationToken);

            var subject = principal.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new InvalidOperationException(
                    "The validated ID token did not contain an account " +
                    "identifier. No credentials were saved.");
            }

            var scopes = token.scope
                .Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var record = new CredentialRecord
            {
                Email = principal.FindFirst("email")?.Value ?? "",
                Subject = subject,
                ClientId = callback.ClientId,
                HostId = hostId,
                IdToken = token.id_token,
                AccessToken = token.access_token,
                RefreshToken = token.refresh_token,
                TokenType = token.token_type,
                Scopes = scopes,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Max(0, token.expires_in)),
                SavedAt = DateTimeOffset.UtcNow
            };

            // Save only after successful token exchange and ID-token
            // validation. Existing saved accounts remain untouched.
            await SaveCredentialsAsync(record);
            await SaveSelectedSubjectAsync(record.Subject);

            // Clear only the dedicated pending-registration file.
            await ClearPendingClientIdAsync();

            var canUsePlan = HasRequiredScope(record);

            await WriteAuthDiagnosticAsync(
                "Authentication completed",
                $"plan_scope_granted={canUsePlan}; " +
                $"expires_at={record.ExpiresAt:O}");

            return new ConnectionStatus(
                true,
                record.Email,
                canUsePlan,
                record.ExpiresAt,
                canUsePlan
                    ? "ChatGPT connected and plan-use permission granted."
                    : "Signed in successfully, but ChatGPT-plan access " +
                      "was not granted. The account remains saved.");
        }
        catch (OperationCanceledException)
        {
            await WriteAuthDiagnosticAsync(
                "Authentication cancelled",
                "The operation was cancelled.");

            throw;
        }
        catch (Exception ex)
        {
            await WriteAuthDiagnosticAsync(
                "Authentication failed",
                $"exception_type={ex.GetType().Name}; " +
                $"message={ex.Message}");

            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _operationLock.WaitAsync();

        try
        {
            await MigrateLegacyCredentialsAsync();

            var selectedSubject = await GetSelectedSubjectAsync();

            if (string.IsNullOrWhiteSpace(selectedSubject))
            {
                var records = await GetAllAccountRecordsAsync();

                if (records.Count > 0)
                    selectedSubject = records[0].Subject;
            }

            if (string.IsNullOrWhiteSpace(selectedSubject))
                return;

            await DisconnectAccountInternalAsync(selectedSubject);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectAsync(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException(
                "An account subject is required.",
                nameof(subject));
        }

        await _operationLock.WaitAsync();

        try
        {
            await MigrateLegacyCredentialsAsync();
            await DisconnectAccountInternalAsync(subject);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task DisconnectAccountInternalAsync(string subject)
    {
        var accountPath = GetAccountCredentialPath(subject);

        if (File.Exists(accountPath))
            File.Delete(accountPath);

        if (File.Exists(_legacyCredentialPath))
        {
            var legacy = await ReadLegacyCredentialsAsync();

            if (legacy is not null &&
                string.Equals(
                    legacy.Subject,
                    subject,
                    StringComparison.Ordinal))
            {
                File.Delete(_legacyCredentialPath);
            }
        }

        var records = await GetAllAccountRecordsAsync();

        if (records.Count == 0)
        {
            if (File.Exists(_selectedAccountPath))
                File.Delete(_selectedAccountPath);
        }
        else
        {
            var selectedSubject = await GetSelectedSubjectAsync();

            if (string.Equals(
                    selectedSubject,
                    subject,
                    StringComparison.Ordinal) ||
                records.All(record =>
                    !string.Equals(
                        record.Subject,
                        selectedSubject,
                        StringComparison.Ordinal)))
            {
                await SaveSelectedSubjectAsync(records[0].Subject);
            }
        }

        await WriteAuthDiagnosticAsync(
            "Account disconnected",
            $"remaining_accounts={records.Count}");
    }

    private async Task<string?> GetPendingClientIdAsync()
    {
        if (!File.Exists(_pendingClientIdPath))
            return null;

        var clientId = (
            await File.ReadAllTextAsync(_pendingClientIdPath)).Trim();

        if (string.IsNullOrWhiteSpace(clientId) ||
            !clientId.StartsWith(
                "oaiapp_",
                StringComparison.Ordinal))
        {
            await WriteAuthDiagnosticAsync(
                "Pending registration ignored",
                "The pending client ID was missing or invalid.");

            return null;
        }

        return clientId;
    }

    private async Task SavePendingClientIdAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) ||
            !clientId.StartsWith(
                "oaiapp_",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "OpenAI did not return a valid issued registration ID.");
        }

        var temporaryPath = _pendingClientIdPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, clientId);

            File.Move(
                temporaryPath,
                _pendingClientIdPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private Task ClearPendingClientIdAsync()
    {
        if (File.Exists(_pendingClientIdPath))
            File.Delete(_pendingClientIdPath);

        return Task.CompletedTask;
    }

    private static string BuildAuthorizationUrl(
        string clientId,
        bool firstRegistration,
        string hostId,
        string redirectUri,
        string state,
        string nonce,
        string challenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = RequestedScopes,
            ["resource"] = Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge
        };

        if (firstRegistration)
            parameters["agent_name_hint"] = "Conflux";

        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}=" +
                $"{Uri.EscapeDataString(pair.Value)}"));

        return $"{AuthorizeEndpoint}?{query}";
    }

    private static void OpenAuthorizationPage(string authorizationUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Conflux could not open your default browser. " +
                "Check your Windows browser settings and try again.",
                ex);
        }
    }

    private static async Task<AuthorizationResult> ReceiveCallbackAsync(
        HttpListener listener,
        string expectedState,
        string? expectedClientId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpListenerContext context;

            try
            {
                context = await listener
                    .GetContextAsync()
                    .WaitAsync(
                        TimeSpan.FromMinutes(3),
                        cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "Conflux did not receive the ChatGPT sign-in callback " +
                    "within three minutes. Return to Conflux and try again. " +
                    "If the browser showed an account-upgrade message, note " +
                    "that message before retrying.",
                    ex);
            }

            var request = context.Request;
            var response = context.Response;

            if (!string.Equals(
                request.Url?.AbsolutePath,
                "/auth/callback",
                StringComparison.Ordinal))
            {
                response.StatusCode = 404;
                response.Close();
                continue;
            }

            var query = ParseQuery(request.Url?.Query ?? "");

            if (!query.TryGetValue("state", out var returnedState) ||
                !FixedTimeEquals(expectedState, returnedState))
            {
                WriteBrowserResponse(
                    response,
                    400,
                    "Sign-in could not be verified",
                    "The security state did not match. Return to Conflux " +
                    "and start sign-in again.");

                throw new InvalidOperationException(
                    "The sign-in callback state did not match.");
            }

            if (query.TryGetValue("error", out var error))
            {
                var description = query.TryGetValue(
                    "error_description",
                    out var detail)
                    ? detail
                    : error;

                WriteBrowserResponse(
                    response,
                    400,
                    "Sign-in was not completed",
                    "Authorization was cancelled or declined. You can " +
                    "close this page and return to Conflux.");

                throw new InvalidOperationException(
                    $"OpenAI sign-in was not completed: {description}");
            }

            if (!query.TryGetValue("code", out var code) ||
                string.IsNullOrWhiteSpace(code))
            {
                WriteBrowserResponse(
                    response,
                    400,
                    "Authorization code missing",
                    "Return to Conflux and try signing in again.");

                throw new InvalidOperationException(
                    "The sign-in callback did not contain an authorization code.");
            }

            string clientId;

            if (query.TryGetValue("client_id", out var returnedClientId) &&
                !string.IsNullOrWhiteSpace(returnedClientId))
            {
                clientId = returnedClientId;

                if (string.Equals(
                    clientId,
                    InitialClientId,
                    StringComparison.Ordinal))
                {
                    WriteBrowserResponse(
                        response,
                        400,
                        "Registration was incomplete",
                        "OpenAI did not return an issued client ID. " +
                        "Return to Conflux and try again.");

                    throw new InvalidOperationException(
                        "The callback returned the initial dynamic client ID instead of an issued client ID.");
                }

                if (!string.IsNullOrWhiteSpace(expectedClientId) &&
                    !string.Equals(
                        expectedClientId,
                        clientId,
                        StringComparison.Ordinal))
                {
                    WriteBrowserResponse(
                        response,
                        400,
                        "Client registration mismatch",
                        "The returned registration did not match this " +
                        "sign-in attempt. Return to Conflux and try again.");

                    throw new InvalidOperationException(
                        "OpenAI returned a client ID that differs from the expected registration.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(expectedClientId))
            {
                clientId = expectedClientId;
            }
            else
            {
                WriteBrowserResponse(
                    response,
                    400,
                    "Registration was incomplete",
                    "OpenAI did not return the issued client ID. " +
                    "Return to Conflux and try again.");

                throw new InvalidOperationException(
                    "The first-registration callback did not contain an issued client ID.");
            }

            WriteBrowserResponse(
                response,
                200,
                "Authorization received",
                "Return to Conflux. It is validating your sign-in.");

            return new AuthorizationResult(code, clientId);
        }
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        string code,
        string clientId,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        await WriteAuthDiagnosticAsync(
            "Starting token exchange",
            $"client_id={clientId}; " +
            $"redirect_uri={redirectUri}; " +
            $"code_length={code.Length}; " +
            $"verifier_length={verifier.Length}; " +
            $"resource={Resource}");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            TokenEndpoint);

        request.Headers.Accept.ParseAdd("application/json");

        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["resource"] = Resource
            });

        using var response = await Http.SendAsync(
            request,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken);

        var requestId =
            response.Headers.TryGetValues(
                "openai-request-id",
                out var openAiIds)
                ? openAiIds.FirstOrDefault()
                : null;

        requestId ??=
            response.Headers.TryGetValues(
                "x-request-id",
                out var requestIds)
                ? requestIds.FirstOrDefault()
                : null;

        if (!response.IsSuccessStatusCode)
        {
            var safeError = GetSafeError(body);
            var errorCode = GetErrorCode(body);

            await WriteAuthDiagnosticAsync(
                "Token exchange rejected",
                $"http_status={(int)response.StatusCode}; " +
                $"reason={response.ReasonPhrase}; " +
                $"request_id={requestId ?? "(not provided)"}; " +
                $"server_date={response.Headers.Date?.ToString("O") ?? "(not provided)"}; " +
                $"error={safeError}");

            throw new TokenExchangeException(
                (int)response.StatusCode,
                errorCode,
                $"OpenAI token exchange failed with HTTP " +
                $"{(int)response.StatusCode}: {safeError}. " +
                $"Request ID: {requestId ?? "(not provided)"}. " +
                $"Diagnostics saved to: {_diagnosticPath}");
        }

        await WriteAuthDiagnosticAsync(
            "Token exchange succeeded",
            $"http_status={(int)response.StatusCode}; " +
            $"request_id={requestId ?? "(not provided)"}");

        var token = JsonSerializer.Deserialize<TokenResponse>(body);

        return token ?? throw new InvalidOperationException(
            "OpenAI returned an invalid token response.");
    }

    private static async Task<ClaimsPrincipal> ValidateIdTokenAsync(
        string idToken,
        string clientId,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        using var discoveryResponse = await Http.GetAsync(
            $"{Issuer}/.well-known/openid-configuration",
            cancellationToken);

        discoveryResponse.EnsureSuccessStatusCode();

        using var discoveryDocument = JsonDocument.Parse(
            await discoveryResponse.Content.ReadAsStringAsync(
                cancellationToken));

        var jwksUri = discoveryDocument.RootElement
            .GetProperty("jwks_uri")
            .GetString();

        if (string.IsNullOrWhiteSpace(jwksUri))
        {
            throw new InvalidOperationException(
                "OpenAI's identity configuration did not provide a signing-key URL.");
        }

        using var keysResponse = await Http.GetAsync(
            jwksUri,
            cancellationToken);

        keysResponse.EnsureSuccessStatusCode();

        var keysJson = await keysResponse.Content.ReadAsStringAsync(
            cancellationToken);

        var signingKeys = new JsonWebKeySet(keysJson)
            .GetSigningKeys();

        if (signingKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "No OpenAI ID-token signing keys were available.");
        }

        var handler = new JsonWebTokenHandler
        {
            MapInboundClaims = false
        };

        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        var result = await handler.ValidateTokenAsync(
            idToken,
            validation);

        if (!result.IsValid || result.ClaimsIdentity is null)
        {
            throw new InvalidOperationException(
                "OpenAI's ID token could not be validated. " +
                "No credentials were saved.",
                result.Exception);
        }

        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        var actualNonce = principal.FindFirst("nonce")?.Value;

        if (string.IsNullOrWhiteSpace(actualNonce) ||
            !FixedTimeEquals(expectedNonce, actualNonce))
        {
            throw new InvalidOperationException(
                "The ID-token nonce did not match this sign-in attempt. " +
                "No credentials were saved.");
        }

        return principal;
    }

    private async Task<string> GetOrCreateHostIdAsync()
    {
        if (File.Exists(_hostIdPath))
        {
            var existing = (
                await File.ReadAllTextAsync(_hostIdPath)).Trim();

            if (existing.StartsWith(
                "urn:uuid:",
                StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        var hostId = $"urn:uuid:{Guid.NewGuid():D}";
        var temporaryPath = _hostIdPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, hostId);

            File.Move(
                temporaryPath,
                _hostIdPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return hostId;
    }

    private async Task<string?> GetSelectedSubjectAsync()
    {
        if (!File.Exists(_selectedAccountPath))
            return null;

        var subject = (
            await File.ReadAllTextAsync(_selectedAccountPath)).Trim();

        return string.IsNullOrWhiteSpace(subject)
            ? null
            : subject;
    }

    private async Task SaveSelectedSubjectAsync(string subject)
    {
        var temporaryPath = _selectedAccountPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, subject);

            File.Move(
                temporaryPath,
                _selectedAccountPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private string GetAccountCredentialPath(string subject)
    {
        var subjectBytes = Encoding.UTF8.GetBytes(subject);
        var hash = Convert.ToHexString(
            SHA256.HashData(subjectBytes));

        return Path.Combine(
            _accountsDirectory,
            $"{hash}.credentials");
    }

    private async Task<CredentialRecord?> LoadCredentialsForSubjectAsync(
        string subject)
    {
        var path = GetAccountCredentialPath(subject);

        if (!File.Exists(path))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(path);

            var plaintext = ProtectedData.Unprotect(
                encrypted,
                CredentialEntropy,
                DataProtectionScope.CurrentUser);

            var record =
                JsonSerializer.Deserialize<CredentialRecord>(plaintext);

            if (record is null ||
                !string.Equals(
                    record.Subject,
                    subject,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The saved account identifier does not match its credential file.");
            }

            return record;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is IOException)
        {
            throw new InvalidOperationException(
                "Conflux could not read a protected ChatGPT account. " +
                "Its file may be damaged or belong to another Windows user.",
                ex);
        }
    }

    private async Task<List<CredentialRecord>> GetAllAccountRecordsAsync()
    {
        var records = new List<CredentialRecord>();

        foreach (var path in Directory.EnumerateFiles(
            _accountsDirectory,
            "*.credentials",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                var encrypted = await File.ReadAllBytesAsync(path);

                var plaintext = ProtectedData.Unprotect(
                    encrypted,
                    CredentialEntropy,
                    DataProtectionScope.CurrentUser);

                var record =
                    JsonSerializer.Deserialize<CredentialRecord>(plaintext);

                if (record is not null &&
                    !string.IsNullOrWhiteSpace(record.Subject))
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (
                ex is CryptographicException ||
                ex is JsonException ||
                ex is IOException)
            {
                await WriteAuthDiagnosticAsync(
                    "Saved account could not be loaded",
                    $"file_name={Path.GetFileName(path)}; " +
                    $"exception_type={ex.GetType().Name}");
            }
        }

        return records
            .GroupBy(
                record => record.Subject,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(record => record.SavedAt)
                .First())
            .ToList();
    }

    private async Task MigrateLegacyCredentialsAsync()
    {
        if (!File.Exists(_legacyCredentialPath))
            return;

        var legacy = await ReadLegacyCredentialsAsync();

        if (legacy is null ||
            string.IsNullOrWhiteSpace(legacy.Subject))
        {
            return;
        }

        var accountPath = GetAccountCredentialPath(legacy.Subject);

        if (!File.Exists(accountPath))
            await SaveCredentialsAsync(legacy);

        if (File.Exists(accountPath))
        {
            var migrated =
                await LoadCredentialsForSubjectAsync(legacy.Subject);

            if (migrated is not null &&
                string.Equals(
                    migrated.Subject,
                    legacy.Subject,
                    StringComparison.Ordinal))
            {
                File.Delete(_legacyCredentialPath);

                await WriteAuthDiagnosticAsync(
                    "Legacy credentials migrated",
                    "The previous single-account credentials were moved " +
                    "to account-specific protected storage.");
            }
        }

        // Existing global client-ID data is preserved. Pending registration
        // state is stored separately in chatgpt.pending-client-id.
    }

    private async Task<CredentialRecord?> ReadLegacyCredentialsAsync()
    {
        if (!File.Exists(_legacyCredentialPath))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(
                _legacyCredentialPath);

            var plaintext = ProtectedData.Unprotect(
                encrypted,
                CredentialEntropy,
                DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<CredentialRecord>(plaintext);
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is IOException)
        {
            throw new InvalidOperationException(
                "Conflux could not read its previous protected ChatGPT " +
                "credentials. The original file has been left in place. " +
                "Back it up before attempting any manual recovery.",
                ex);
        }
    }

    private async Task SaveCredentialsAsync(CredentialRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Subject))
        {
            throw new InvalidOperationException(
                "Cannot save ChatGPT credentials without a validated account subject.");
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record);

        var encrypted = ProtectedData.Protect(
            plaintext,
            CredentialEntropy,
            DataProtectionScope.CurrentUser);

        var path = GetAccountCredentialPath(record.Subject);
        var temporaryPath = path + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, encrypted);

            File.Move(
                temporaryPath,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool HasRequiredScope(CredentialRecord record)
    {
        return record.Scopes.Contains(
            RequiredScope,
            StringComparer.Ordinal);
    }

    private async Task WriteAuthDiagnosticAsync(
        string eventName,
        string details)
    {
        try
        {
            var line =
                $"[{DateTimeOffset.UtcNow:O}] {eventName}: " +
                $"{details}{Environment.NewLine}";

            await File.AppendAllTextAsync(
                _diagnosticPath,
                line);
        }
        catch
        {
            // Diagnostics must never interrupt authentication.
        }
    }

    private static string CreateRandomUrlSafeValue(int byteCount)
    {
        return Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var digest = SHA256.HashData(
            Encoding.ASCII.GetBytes(verifier));

        return Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool FixedTimeEquals(
        string expected,
        string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                actualBytes);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(
            StringComparer.Ordinal);

        foreach (var part in query.TrimStart('?').Split(
            '&',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');

            var key = separator < 0
                ? part
                : part[..separator];

            var value = separator < 0
                ? ""
                : part[(separator + 1)..];

            key = Uri.UnescapeDataString(key.Replace('+', ' '));
            value = Uri.UnescapeDataString(value.Replace('+', ' '));

            result[key] = value;
        }

        return result;
    }

    private static string GetErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty(
                    "error",
                    out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // The server may return a non-JSON error response.
        }

        return "";
    }

    private static string GetSafeError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var error = root.TryGetProperty(
                "error",
                out var errorElement)
                ? errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString() ?? "Unknown OAuth error."
                    : errorElement.ToString()
                : "Unknown OAuth error.";

            var description = root.TryGetProperty(
                "error_description",
                out var descriptionElement) &&
                descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

            var parameter = root.TryGetProperty(
                "error_param",
                out var parameterElement) &&
                parameterElement.ValueKind == JsonValueKind.String
                ? parameterElement.GetString()
                : null;

            var parts = new List<string> { error };

            if (!string.IsNullOrWhiteSpace(description))
                parts.Add(description);

            if (!string.IsNullOrWhiteSpace(parameter))
                parts.Add($"Parameter: {parameter}");

            return string.Join(" | ", parts);
        }
        catch (JsonException)
        {
            // Never include arbitrary response bodies in error messages.
        }

        return "The authorization server rejected the token exchange.";
    }

    private static void WriteBrowserResponse(
        HttpListenerResponse response,
        int statusCode,
        string heading,
        string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";

        var safeHeading = WebUtility.HtmlEncode(heading);
        var safeMessage = WebUtility.HtmlEncode(message);

        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width\">" +
            "<title>Conflux sign-in</title></head>" +
            "<body style=\"font:16px system-ui;max-width:600px;" +
            "margin:12vh auto;padding:24px;background:#17181d;" +
            "color:#f2f2f7\">" +
            $"<h1>{safeHeading}</h1><p>{safeMessage}</p>" +
            "<p>You may close this browser tab.</p></body></html>";

        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;

        try
        {
            response.OutputStream.Write(bytes);
        }
        finally
        {
            response.Close();
        }
    }
}