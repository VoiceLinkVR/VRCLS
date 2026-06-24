using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Auth
{
    public sealed class BackendAuthService : IBackendAuthService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<BackendAuthService>();
        private const string RateLimitNotificationPrefix = "[RATE_LIMIT]";
        private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromSeconds(60);

        private readonly AppConfig _config;
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly SemaphoreSlim _loginLock = new(1, 1);
        private readonly object _websiteLoginSync = new();
        private BackendAuthState _state;
        private CancellationTokenSource? _pendingWebsiteLoginCts;
        private string _lastRejectedRefreshedAccessToken = string.Empty;

        public BackendAuthService(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _state = CreateState(BackendAuthStatus.NotLoggedIn);
        }

        public BackendAuthState State => _state;
        public event EventHandler<BackendAuthState>? StateChanged;
        public event EventHandler<string>? UserNotification;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;
        public event EventHandler<AccountStatusInfo>? AccountStatusUpdated;

        public async Task<BackendAuthState> LoginAsync(CancellationToken cancellationToken = default)
        {
            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                var userInfo = EnsureUserInfo();
                _logger.Debug(
                    "Starting auth restore. configuredMode={AuthMode}, hasAccessToken={HasAccessToken}, hasRefreshToken={HasRefreshToken}, expiresAt={ExpiresAt}",
                    userInfo.AuthMode,
                    !string.IsNullOrWhiteSpace(userInfo.AccessToken),
                    !string.IsNullOrWhiteSpace(userInfo.RefreshToken),
                    userInfo.ExpiresAt);

                return GetConfiguredAuthMode() switch
                {
                    BackendAuthMode.AnonymousTest => await LoginAnonymousInternalAsync(cancellationToken),
                    BackendAuthMode.WebsiteAuthorized => await RestoreWebsiteSessionInternalAsync(cancellationToken),
                    BackendAuthMode.LegacyCredentials => await RestoreLegacySessionInternalAsync(cancellationToken, allowCredentialLogin: true),
                    _ => UpdateState(CreateState(BackendAuthStatus.NotLoggedIn))
                };
            }
            finally
            {
                _loginLock.Release();
            }
        }

        public async Task<BackendAuthState> StartWebsiteLoginAsync(CancellationToken cancellationToken = default)
        {
            CancelPendingWebsiteLogin("starting a new website authorization flow");
            await _loginLock.WaitAsync(cancellationToken);
            CancellationTokenSource? websiteLoginCts = null;
            try
            {
                websiteLoginCts = CreatePendingWebsiteLoginTokenSource(cancellationToken);
                var effectiveCancellationToken = websiteLoginCts.Token;
                UpdateState(CreateState(BackendAuthStatus.LoggingIn, authMode: BackendAuthMode.WebsiteAuthorized, accessToken: null));

                var startUrl = $"{BackendUrlHelper.BuildV2AuthBaseUrl(_config)}device/start";
                using var startResponse = await _httpClient.PostAsJsonAsync(
                    startUrl,
                    new { client_name = "VRCLS Desktop", scope = "vrcls.client" },
                    effectiveCancellationToken);

                var startRaw = await startResponse.Content.ReadAsStringAsync(effectiveCancellationToken);
                if (!startResponse.IsSuccessStatusCode)
                {
                    return UpdateState(CreateState(BackendAuthStatus.Failed, $"Failed to start website authorization: {ExtractErrorMessage(startRaw, startResponse)}", BackendAuthMode.WebsiteAuthorized, accessToken: null));
                }

                var start = JsonSerializer.Deserialize<DeviceStartResponse>(startRaw);
                if (start == null || string.IsNullOrWhiteSpace(start.DeviceCode))
                {
                    return UpdateState(CreateState(
                        BackendAuthStatus.Failed,
                        "Website authorization start response did not include device_code.",
                        BackendAuthMode.WebsiteAuthorized,
                        accessToken: null));
                }

                var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, start.ExpiresIn));
                UpdateState(new BackendAuthState
                {
                    Status = BackendAuthStatus.LoggingIn,
                    AuthMode = BackendAuthMode.WebsiteAuthorized,
                    VerificationUri = start.VerificationUri,
                    VerificationUriComplete = start.VerificationUriComplete,
                    UserCode = start.UserCode,
                    ExpiresAtUtc = expiresAt
                });

                var pollInterval = Math.Clamp(start.Interval <= 0 ? 5 : start.Interval, 2, 30);
                while (DateTimeOffset.UtcNow < expiresAt)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollInterval), effectiveCancellationToken);
                    var token = await PollDeviceTokenAsync(start.DeviceCode, effectiveCancellationToken);
                    pollInterval = Math.Clamp(token.PollInterval ?? pollInterval, 2, 30);
                    var status = (token.Status ?? string.Empty).Trim().ToLowerInvariant();

                    if (status == "pending")
                    {
                        continue;
                    }

                    if (status == "approved" && !string.IsNullOrWhiteSpace(token.AccessToken))
                    {
                        return await ApplyTokenResponseAsync(
                            token,
                            BackendAuthMode.WebsiteAuthorized,
                            isAnonymous: false,
                            effectiveCancellationToken,
                            allowStoredRefreshTokenFallback: false);
                    }

                    var message = status switch
                    {
                        "approved" => "Website authorization was approved, but the backend did not return access_token.",
                        "denied" => "Website authorization was denied.",
                        "expired" => "Website authorization request expired. Please start login again.",
                        "completed" => "This website authorization request has already completed. Please start login again.",
                        _ => $"Unexpected website authorization status: {token.Status}"
                    };
                    return UpdateState(CreateState(BackendAuthStatus.Failed, message, BackendAuthMode.WebsiteAuthorized, accessToken: null));
                }

                return UpdateState(CreateState(BackendAuthStatus.Failed, "Website authorization request timed out. Please start login again.", BackendAuthMode.WebsiteAuthorized, accessToken: null));
            }
            catch (OperationCanceledException) when (websiteLoginCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                _logger.Information("Website authorization flow was cancelled because a newer auth action superseded it.");
                return _state;
            }
            catch (OperationCanceledException)
            {
                return UpdateState(CreateState(BackendAuthStatus.Failed, "Cancelled", BackendAuthMode.WebsiteAuthorized, accessToken: null));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Website authorization failed.");
                return UpdateState(CreateState(BackendAuthStatus.Failed, ex.Message, BackendAuthMode.WebsiteAuthorized, accessToken: null));
            }
            finally
            {
                ClearPendingWebsiteLoginTokenSource(websiteLoginCts);
                _loginLock.Release();
            }
        }

        public async Task<BackendAuthState> LogoutAsync(CancellationToken cancellationToken = default)
        {
            CancelPendingWebsiteLogin("logout requested");
            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                if (BackendUrlHelper.UsesLegacyBackendApi(_config))
                {
                    var legacyUserInfo = EnsureUserInfo();
                    legacyUserInfo.AuthMode = UserInfo.AuthModeLegacyCredentials;
                    legacyUserInfo.Username = string.Empty;
                    legacyUserInfo.Password = string.Empty;
                    ClearFormalIdentity(legacyUserInfo);
                    ClearTokens(legacyUserInfo);
                    SaveConfigSnapshot("clear legacy backend login state");
                    return UpdateState(CreateState(BackendAuthStatus.NotLoggedIn, authMode: BackendAuthMode.LegacyCredentials, accessToken: null));
                }

                var userInfo = EnsureUserInfo();
                var accessToken = string.IsNullOrWhiteSpace(userInfo.AccessToken) ? null : userInfo.AccessToken.Trim();
                var refreshToken = string.IsNullOrWhiteSpace(userInfo.RefreshToken) ? null : userInfo.RefreshToken.Trim();

                await TryNotifyLogoutAsync(accessToken, refreshToken, cancellationToken);

                userInfo.AuthMode = UserInfo.AuthModeAnonymousTest;
                userInfo.Username = string.Empty;
                userInfo.Password = string.Empty;
                ClearFormalIdentity(userInfo);
                ClearTokens(userInfo);
                SaveConfigSnapshot("logout to anonymous trial mode");

                return await LoginAnonymousInternalAsync(cancellationToken);
            }
            finally
            {
                _loginLock.Release();
            }
        }

        public async Task<BackendAuthState> SwitchToAnonymousTestModeAsync(CancellationToken cancellationToken = default)
        {
            CancelPendingWebsiteLogin("switch to anonymous trial mode requested");
            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                var userInfo = EnsureUserInfo();
                userInfo.AuthMode = UserInfo.AuthModeAnonymousTest;
                userInfo.Username = string.Empty;
                userInfo.Password = string.Empty;
                ClearFormalIdentity(userInfo);
                ClearTokens(userInfo);
                SaveConfigSnapshot("switch to anonymous trial mode");
                return await LoginAnonymousInternalAsync(cancellationToken);
            }
            finally
            {
                _loginLock.Release();
            }
        }

        public async Task<BackendAuthState> LoginWithCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                if (BackendUrlHelper.UsesLegacyBackendApi(_config))
                {
                    return await LoginWithLegacyCredentialsInternalAsync(username, password, cancellationToken);
                }

                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Password login is no longer available for the modern backend. Please use website authorization or anonymous trial mode.",
                    BackendAuthMode.WebsiteAuthorized,
                    accessToken: null));
            }
            finally
            {
                _loginLock.Release();
            }
        }

        public async Task<BackendAuthState> LoginWithLegacyCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                if (BackendUrlHelper.UsesLegacyBackendApi(_config))
                {
                    return await LoginWithLegacyCredentialsInternalAsync(username, password, cancellationToken);
                }

                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Legacy backend compatibility mode is disabled. Enable the switch first.",
                    BackendAuthMode.WebsiteAuthorized,
                    accessToken: null));
            }
            finally
            {
                _loginLock.Release();
            }
        }

        public Task ResetToDefaultAccountAsync(CancellationToken cancellationToken = default)
        {
            return SwitchToAnonymousTestModeAsync(cancellationToken);
        }

        public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var userInfo = EnsureUserInfo();
            if (IsAccessTokenUsable(userInfo.AccessToken, userInfo.ExpiresAt))
            {
                return userInfo.AccessToken;
            }

            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                userInfo = EnsureUserInfo();
                if (IsAccessTokenUsable(userInfo.AccessToken, userInfo.ExpiresAt))
                {
                    return userInfo.AccessToken;
                }

                var state = GetConfiguredAuthMode() switch
                {
                    BackendAuthMode.AnonymousTest => await LoginAnonymousInternalAsync(cancellationToken),
                    BackendAuthMode.WebsiteAuthorized => await RestoreWebsiteSessionInternalAsync(cancellationToken, silentRefresh: true),
                    BackendAuthMode.LegacyCredentials => await RestoreLegacySessionInternalAsync(cancellationToken, allowCredentialLogin: true),
                    _ => CreateState(BackendAuthStatus.NotLoggedIn)
                };
                return state.AccessToken;
            }
            finally
            {
                _loginLock.Release();
            }
        }

        public async Task<HttpResponseMessage?> SendAuthorizedAsync(
            string operation,
            Func<HttpRequestMessage> createRequest,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
            CancellationToken cancellationToken = default)
        {
            var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.Warning("{Operation} skipped because no access token is available.", operation);
                return null;
            }

            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await sendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            var retryToken = await GetRetryAccessTokenAfterUnauthorizedAsync(accessToken, operation, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(retryToken))
            {
                return response;
            }

            response.Dispose();

            using var retryRequest = createRequest();
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", retryToken);
            var retryResponse = await sendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
            if (retryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                _lastRejectedRefreshedAccessToken = retryToken;
                _logger.Warning(
                    "{Operation} still returned 401 after centralized refresh. Keeping the current session state unchanged and skipping further refresh loops for this token.",
                    operation);
            }
            else if (!string.IsNullOrWhiteSpace(retryToken))
            {
                _lastRejectedRefreshedAccessToken = string.Empty;
            }

            return retryResponse;
        }

        private void InvalidateToken(string? reason = null)
        {
            CancelPendingWebsiteLogin(reason ?? "token invalidated");
            var userInfo = EnsureUserInfo();
            userInfo.AccessToken = string.Empty;
            userInfo.ExpiresAt = null;
            SaveConfigSnapshot("invalidate token");
            UpdateState(CreateState(BackendAuthStatus.NotLoggedIn, reason, accessToken: null));
        }

        private void ExpireAccessToken(string? reason = null)
        {
            ExpireAccessTokenCore(reason);
        }

        private void ExpireAccessTokenCore(string? reason)
        {
            var userInfo = EnsureUserInfo();
            userInfo.AccessToken = string.Empty;
            userInfo.ExpiresAt = null;
            SaveConfigSnapshot("expire access token");
            _logger.Information(
                "Marked cached access token as expired without changing login state. reason={Reason}, authMode={AuthMode}, userUuid={UserUuid}, hasRefreshToken={HasRefreshToken}",
                reason ?? string.Empty,
                userInfo.AuthMode,
                userInfo.UserUuid,
                !string.IsNullOrWhiteSpace(userInfo.RefreshToken));
        }

        public void ResetForBackendModeSwitch(bool useLegacyBackendApi)
        {
            CancelPendingWebsiteLogin("backend compatibility mode switched");
            var userInfo = EnsureUserInfo();
            userInfo.AuthMode = useLegacyBackendApi
                ? UserInfo.AuthModeLegacyCredentials
                : UserInfo.AuthModeAnonymousTest;
            userInfo.Username = string.Empty;
            userInfo.Password = string.Empty;
            ClearFormalIdentity(userInfo);
            ClearTokens(userInfo);

            SaveConfigSnapshot(useLegacyBackendApi
                ? "switch to legacy backend compatibility mode"
                : "switch to modern backend mode");

            UpdateState(CreateState(
                BackendAuthStatus.NotLoggedIn,
                authMode: useLegacyBackendApi
                    ? BackendAuthMode.LegacyCredentials
                    : BackendAuthMode.AnonymousTest,
                accessToken: null));
        }

        public void ResetForBaseUrlChange(bool useLegacyBackendApi)
        {
            CancelPendingWebsiteLogin("backend base url changed");

            var userInfo = EnsureUserInfo();
            var configuredAuthMode = useLegacyBackendApi
                ? BackendAuthMode.LegacyCredentials
                : GetConfiguredAuthMode();

            if (useLegacyBackendApi)
            {
                userInfo.AccessToken = string.Empty;
                userInfo.ExpiresAt = null;
                SaveConfigSnapshot("invalidate legacy token because base url changed");
                UpdateState(CreateState(
                    BackendAuthStatus.NotLoggedIn,
                    authMode: BackendAuthMode.LegacyCredentials,
                    accessToken: null));
                return;
            }

            if (configuredAuthMode == BackendAuthMode.WebsiteAuthorized)
            {
                userInfo.AuthMode = UserInfo.AuthModeWebsiteAuthorized;
                userInfo.Username = string.Empty;
                userInfo.Password = string.Empty;
                ClearFormalIdentity(userInfo);
                ClearTokens(userInfo);
                SaveConfigSnapshot("clear website auth session because base url changed");
                UpdateState(CreateState(
                    BackendAuthStatus.NotLoggedIn,
                    authMode: BackendAuthMode.WebsiteAuthorized,
                    accessToken: null));
                return;
            }

            userInfo.AccessToken = string.Empty;
            userInfo.ExpiresAt = null;
            SaveConfigSnapshot("invalidate token because base url changed");
            UpdateState(CreateState(
                BackendAuthStatus.NotLoggedIn,
                authMode: configuredAuthMode,
                accessToken: null));
        }

        public void ResetForCredentialChange(bool useLegacyBackendApi)
        {
            CancelPendingWebsiteLogin("connection credentials changed");

            var userInfo = EnsureUserInfo();
            if (useLegacyBackendApi)
            {
                userInfo.AccessToken = string.Empty;
                userInfo.ExpiresAt = null;
                SaveConfigSnapshot("invalidate legacy token because credentials changed");
                UpdateState(CreateState(
                    BackendAuthStatus.NotLoggedIn,
                    authMode: BackendAuthMode.LegacyCredentials,
                    accessToken: null));
                return;
            }

            userInfo.AuthMode = UserInfo.AuthModeAnonymousTest;
            userInfo.Username = string.Empty;
            userInfo.Password = string.Empty;
            ClearFormalIdentity(userInfo);
            ClearTokens(userInfo);
            SaveConfigSnapshot("clear website auth session because credentials changed");
            UpdateState(CreateState(
                BackendAuthStatus.NotLoggedIn,
                authMode: BackendAuthMode.AnonymousTest,
                accessToken: null));
        }

        public void NotifyTooManyRequests(
            string? source = null,
            string? backendDetail = null,
            string? channel = null,
            string? stage = null,
            string? originalText = null,
            long? segmentId = null)
        {
            var prefix = string.IsNullOrWhiteSpace(source) ? "backend" : source.Trim();
            var detail = TryExtractErrorDetail(backendDetail ?? string.Empty) ?? backendDetail ?? string.Empty;
            var parts = new List<string>
            {
                $"source={Uri.EscapeDataString(prefix)}",
                $"rules={Uri.EscapeDataString(detail)}"
            };

            if (!string.IsNullOrWhiteSpace(channel)) parts.Add($"channel={Uri.EscapeDataString(channel.Trim())}");
            if (!string.IsNullOrWhiteSpace(stage)) parts.Add($"stage={Uri.EscapeDataString(stage.Trim())}");
            if (!string.IsNullOrWhiteSpace(originalText)) parts.Add($"original={Uri.EscapeDataString(originalText)}");
            if (segmentId.HasValue && segmentId.Value > 0) parts.Add($"segmentId={segmentId.Value.ToString(CultureInfo.InvariantCulture)}");

            ErrorOccurred?.Invoke(this, new ServiceErrorEvent
            {
                Code = ServiceErrorCode.RateLimit,
                Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Source"] = prefix,
                    ["Rules"] = detail,
                    ["BackendDetail"] = backendDetail ?? string.Empty
                }
            });

            UserNotification?.Invoke(this, $"{RateLimitNotificationPrefix}|{string.Join("|", parts)}");
        }

        public async Task<AccountStatusInfo?> GetAccountStatusAsync(CancellationToken cancellationToken = default)
        {
            if (BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                _logger.Debug("Skip v2 account status lookup because legacy backend compatibility mode is enabled.");
                return null;
            }

            if (GetConfiguredAuthMode() == BackendAuthMode.AnonymousTest)
            {
                var anonymous = new AccountStatusInfo
                {
                    AuthMode = BackendAuthMode.AnonymousTest,
                    IsAnonymousMode = true,
                    UserLabel = "Guest Trial",
                    IsActive = true
                };
                AccountStatusUpdated?.Invoke(this, anonymous);
                return anonymous;
            }

            var token = await GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            try
            {
                var user = await FetchCurrentUserAsync(token, cancellationToken);
                var usage = await FetchAccountUsageAsync(token, cancellationToken);
                var userInfo = EnsureUserInfo();
                if (PersistWebsiteUser(user, token, userInfo.RefreshToken, userInfo.ExpiresAt))
                {
                    SaveConfigSnapshot("refresh account status");
                }
                UpdateState(CreateState(BackendAuthStatus.LoggedIn, accessToken: token));

                var info = new AccountStatusInfo
                {
                    AuthMode = BackendAuthMode.WebsiteAuthorized,
                    UserUuid = user.UserUuid,
                    Email = user.Email ?? string.Empty,
                    EmailVerified = user.EmailVerified,
                    UserLabel = ResolveUserLabel(user),
                    IsActive = usage?.IsActive ?? true,
                    IsExpired = usage?.IsExpired ?? false,
                    ExpirationUtc = usage?.ExpirationUtc,
                    RemainingSeconds = usage?.RemainingSeconds,
                    DailyLimit = usage?.DailyLimit,
                    RemainingDaily = usage?.TodayRemainingDaily ?? usage?.RemainingDaily,
                    PlanName = usage?.PlanName ?? user.PlanName,
                    PlanPrice = usage?.PlanPrice,
                    PlanRef = usage?.PlanRef,
                    PricingMode = usage?.PricingMode,
                    AddonResourceBalance = usage?.AddonResourceBalance,
                    AddonResourceTotal = usage?.AddonResourceTotal
                };

                AccountStatusUpdated?.Invoke(this, info);
                return info;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.Warning("Account status lookup returned 401. Keeping the current login session unchanged because this endpoint is informational only.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to get v2 account status.");
                return null;
            }
        }

        public void Dispose()
        {
            CancelPendingWebsiteLogin("disposing auth service");
            _httpClient.Dispose();
            _loginLock.Dispose();
        }

        private async Task<BackendAuthState> RestoreWebsiteSessionInternalAsync(CancellationToken cancellationToken, bool silentRefresh = false)
        {
            var userInfo = EnsureUserInfo();
            if (IsAccessTokenUsable(userInfo.AccessToken, userInfo.ExpiresAt))
            {
                if (silentRefresh)
                {
                    _logger.Debug("Restored website session directly from persisted access token.");
                    var restoredState = CreateState(BackendAuthStatus.LoggedIn, accessToken: userInfo.AccessToken);
                    return restoredState;
                }

                try
                {
                    var user = await FetchCurrentUserAsync(userInfo.AccessToken, cancellationToken).ConfigureAwait(false);
                    if (PersistWebsiteUser(user, userInfo.AccessToken, userInfo.RefreshToken, userInfo.ExpiresAt))
                    {
                        SaveConfigSnapshot("validate persisted website access token");
                    }

                    _logger.Debug("Validated persisted website access token during startup restore.");
                    return UpdateState(CreateState(BackendAuthStatus.LoggedIn, accessToken: userInfo.AccessToken));
                }
                catch (UnauthorizedAccessException)
                {
                    userInfo.AccessToken = string.Empty;
                    userInfo.ExpiresAt = null;
                    SaveConfigSnapshot("persisted website access token rejected during startup restore");
                    _logger.Warning("Persisted website access token was rejected during startup restore. Falling back to refresh_token.");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to validate persisted website access token during startup restore. Keeping current token until a request forces refresh.");
                    return UpdateState(CreateState(BackendAuthStatus.LoggedIn, accessToken: userInfo.AccessToken));
                }
            }

            if (string.IsNullOrWhiteSpace(userInfo.RefreshToken))
            {
                _logger.Warning(
                    "Website auth state is missing refresh_token. userUuid={UserUuid}, expiresAt={ExpiresAt}",
                    userInfo.UserUuid,
                    userInfo.ExpiresAt);
                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Website auth state is missing refresh_token. Please authorize again.",
                    BackendAuthMode.WebsiteAuthorized,
                    accessToken: null));
            }

            var refreshUrl = $"{BackendUrlHelper.BuildV2AuthBaseUrl(_config)}refresh";
            try
            {
                if (!silentRefresh)
                {
                    UpdateState(CreateState(BackendAuthStatus.LoggingIn, accessToken: null));
                }

                using var response = await _httpClient.PostAsJsonAsync(refreshUrl, new { refresh_token = userInfo.RefreshToken }, cancellationToken);
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = ExtractErrorMessage(raw, response);
                    if (ShouldDiscardPersistedWebsiteSession(response.StatusCode, raw))
                    {
                        ClearTokens(userInfo);
                        SaveConfigSnapshot("refresh website session failed");
                        _logger.Warning(
                            "Website refresh rejected persisted credentials. status={StatusCode}, detail={Detail}. Cleared cached website session.",
                            (int)response.StatusCode,
                            detail);
                    }
                    else
                    {
                        userInfo.AccessToken = string.Empty;
                        userInfo.ExpiresAt = null;
                        SaveConfigSnapshot("refresh website session failed but refresh token preserved");
                        _logger.Warning(
                            "Website refresh failed with a non-auth error. status={StatusCode}, detail={Detail}. Preserving refresh token for retry.",
                            (int)response.StatusCode,
                            detail);
                    }

                    var failedState = CreateState(
                        BackendAuthStatus.Failed,
                        $"Website refresh failed: {detail}",
                        accessToken: null);
                    return silentRefresh && !ShouldDiscardPersistedWebsiteSession(response.StatusCode, raw)
                        ? failedState
                        : UpdateState(failedState);
                }

                var payload = JsonSerializer.Deserialize<AuthTokenResponse>(raw);
                if (string.IsNullOrWhiteSpace(payload?.AccessToken))
                {
                    var failedState = CreateState(
                        BackendAuthStatus.Failed,
                        "Website refresh response did not return access_token.",
                        accessToken: null);
                    return silentRefresh ? failedState : UpdateState(failedState);
                }

                try
                {
                    payload.User = await FetchCurrentUserAsync(payload.AccessToken.Trim(), cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.Warning("Website refresh returned an access token, but /me rejected it. Persisting the refreshed session and deferring profile hydration.");
                    payload.User = CreateUserResponseFromPersistedIdentity(userInfo);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Website refresh returned an access token, but /me validation failed. Persisting the refreshed session and deferring profile hydration.");
                    payload.User = CreateUserResponseFromPersistedIdentity(userInfo);
                }

                return await ApplyTokenResponseAsync(
                    payload,
                    BackendAuthMode.WebsiteAuthorized,
                    isAnonymous: false,
                    cancellationToken,
                    allowStoredRefreshTokenFallback: true,
                    emitStateChange: !silentRefresh,
                    runPostLoginTasks: !silentRefresh);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                userInfo.AccessToken = string.Empty;
                userInfo.ExpiresAt = null;
                SaveConfigSnapshot("refresh website session threw exception but refresh token preserved");
                _logger.Warning(ex, "Website refresh threw an exception. Preserving refresh token for retry. refreshUrl={RefreshUrl}", refreshUrl);
                var failedState = CreateState(
                    BackendAuthStatus.Failed,
                    $"Website refresh failed: {ex.Message}",
                    accessToken: null);
                return silentRefresh ? failedState : UpdateState(failedState);
            }
        }

        private Task<BackendAuthState> RestoreLegacySessionInternalAsync(CancellationToken cancellationToken, bool allowCredentialLogin)
        {
            var userInfo = EnsureUserInfo();
            if (IsAccessTokenUsable(userInfo.AccessToken, userInfo.ExpiresAt))
            {
                _logger.Debug("Restored legacy backend session directly from persisted access token.");
                return Task.FromResult(UpdateState(CreateState(BackendAuthStatus.LoggedIn, authMode: BackendAuthMode.LegacyCredentials, accessToken: userInfo.AccessToken)));
            }

            if (allowCredentialLogin
                && !string.IsNullOrWhiteSpace(userInfo.Username)
                && !string.IsNullOrWhiteSpace(userInfo.Password))
            {
                _logger.Debug(
                    "Restoring legacy backend session by stored credentials during startup. username={Username}",
                    userInfo.Username);
                return LoginWithLegacyCredentialsInternalAsync(userInfo.Username, userInfo.Password, cancellationToken);
            }

            _logger.Debug(
                "Legacy backend session restore skipped because no usable persisted token is available. username={Username}, hasPassword={HasPassword}, allowCredentialLogin={AllowCredentialLogin}",
                userInfo.Username,
                !string.IsNullOrWhiteSpace(userInfo.Password),
                allowCredentialLogin);

            return Task.FromResult(UpdateState(CreateState(
                BackendAuthStatus.NotLoggedIn,
                authMode: BackendAuthMode.LegacyCredentials,
                accessToken: null)));
        }

        private async Task<BackendAuthState> LoginAnonymousInternalAsync(CancellationToken cancellationToken)
        {
            var userInfo = EnsureUserInfo();
            if (userInfo.IsAnonymousTest && IsAccessTokenUsable(userInfo.AccessToken, userInfo.ExpiresAt))
            {
                return UpdateState(CreateState(BackendAuthStatus.LoggedIn, authMode: BackendAuthMode.AnonymousTest, accessToken: userInfo.AccessToken));
            }

            userInfo.AuthMode = UserInfo.AuthModeAnonymousTest;
            userInfo.Username = string.Empty;
            userInfo.Password = string.Empty;
            ClearFormalIdentity(userInfo);
            ClearTokens(userInfo);
            SaveConfigSnapshot("prepare anonymous trial login");

            UpdateState(CreateState(BackendAuthStatus.LoggingIn, authMode: BackendAuthMode.AnonymousTest, accessToken: null));
            var guestUrl = $"{BackendUrlHelper.BuildV2AuthBaseUrl(_config)}guest";
            using var response = await _httpClient.PostAsync(guestUrl, content: null, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 430)
                {
                    NotifyTooManyRequests("guest login", raw);
                }

                SaveConfigSnapshot("anonymous trial login failed");
                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    ExtractErrorMessage(raw, response),
                    BackendAuthMode.AnonymousTest,
                    accessToken: null));
            }

            var payload = JsonSerializer.Deserialize<AuthTokenResponse>(raw);
            if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            {
                SaveConfigSnapshot("anonymous trial login missing token");
                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Guest trial login did not return access_token.",
                    BackendAuthMode.AnonymousTest,
                    accessToken: null));
            }

            return await ApplyTokenResponseAsync(payload, BackendAuthMode.AnonymousTest, isAnonymous: true, cancellationToken);
        }

        private async Task<BackendAuthState> LoginWithLegacyCredentialsInternalAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Legacy backend mode requires username and password.",
                    BackendAuthMode.LegacyCredentials,
                    accessToken: null));
            }

            UpdateState(CreateState(BackendAuthStatus.LoggingIn, authMode: BackendAuthMode.LegacyCredentials, accessToken: null));
            var loginUrl = BackendUrlHelper.BuildLegacyLoginUrl(_config);
            _logger.Debug(
                "Attempting legacy backend login. url={LoginUrl}, username={Username}, hasPassword={HasPassword}",
                loginUrl,
                username,
                !string.IsNullOrWhiteSpace(password));
            using var response = await _httpClient.PostAsJsonAsync(loginUrl, new { username, password }, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 430)
                {
                    NotifyTooManyRequests("login", raw);
                }

                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    ExtractErrorMessage(raw, response),
                    BackendAuthMode.LegacyCredentials,
                    accessToken: null));
            }

            var payload = JsonSerializer.Deserialize<AuthTokenResponse>(raw);
            if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            {
                return UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Legacy backend did not return access_token.",
                    BackendAuthMode.LegacyCredentials,
                    accessToken: null));
            }

            return ApplyLegacyTokenResponse(payload.AccessToken, username);
        }

        private async Task<DeviceTokenResponse> PollDeviceTokenAsync(string deviceCode, CancellationToken cancellationToken)
        {
            var tokenUrl = $"{BackendUrlHelper.BuildV2AuthBaseUrl(_config)}device/token";
            using var response = await _httpClient.PostAsJsonAsync(tokenUrl, new { device_code = deviceCode }, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtractErrorMessage(raw, response));
            }

            return JsonSerializer.Deserialize<DeviceTokenResponse>(raw) ?? new DeviceTokenResponse { Status = "unknown" };
        }

        private Task<BackendAuthState> ApplyTokenResponseAsync(
            AuthTokenResponse payload,
            BackendAuthMode authMode,
            bool isAnonymous,
            CancellationToken cancellationToken,
            bool allowStoredRefreshTokenFallback = false,
            bool emitStateChange = true,
            bool runPostLoginTasks = true)
        {
            var userInfo = EnsureUserInfo();
            var accessToken = payload.AccessToken.Trim();
            var refreshToken = isAnonymous
                ? string.Empty
                : string.IsNullOrWhiteSpace(payload.RefreshToken)
                    ? allowStoredRefreshTokenFallback
                        ? userInfo.RefreshToken.Trim()
                        : string.Empty
                    : payload.RefreshToken.Trim();
            var expiresAt = ResolveExpiresAt(accessToken, payload.ExpiresIn);

            if (!isAnonymous && string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.Warning("Website authorization completed without refresh_token. userUuid={UserUuid}", userInfo.UserUuid);
                return Task.FromResult(UpdateState(CreateState(
                    BackendAuthStatus.Failed,
                    "Website login succeeded but the backend did not return refresh_token. Please authorize again.",
                    BackendAuthMode.WebsiteAuthorized,
                    accessToken: null)));
            }

            if (isAnonymous)
            {
                userInfo.AuthMode = UserInfo.AuthModeAnonymousTest;
                userInfo.Username = string.Empty;
                userInfo.Password = string.Empty;
                ClearFormalIdentity(userInfo);
            }
            else
            {
                PersistWebsiteSession(userInfo, accessToken, refreshToken, expiresAt);
                if (payload.User != null)
                {
                    PersistWebsiteUser(payload.User, accessToken, refreshToken, expiresAt);
                }
            }

            userInfo.AccessToken = accessToken;
            userInfo.RefreshToken = refreshToken;
            userInfo.ExpiresAt = expiresAt;
            SaveConfigSnapshot("apply token response");

            _logger.Information(
                "Persisted auth tokens. mode={AuthMode}, anonymous={IsAnonymous}, hasRefreshToken={HasRefreshToken}, expiresAt={ExpiresAt}, userUuid={UserUuid}",
                authMode,
                isAnonymous,
                !string.IsNullOrWhiteSpace(refreshToken),
                expiresAt,
                userInfo.UserUuid);

            var state = emitStateChange
                ? UpdateState(CreateState(BackendAuthStatus.LoggedIn, authMode: authMode, accessToken: accessToken))
                : CreateState(BackendAuthStatus.LoggedIn, authMode: authMode, accessToken: accessToken);
            _logger.Information("v2 auth succeeded. mode={AuthMode}, anonymous={IsAnonymous}", authMode, isAnonymous);
            if (runPostLoginTasks && !isAnonymous && payload.User == null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TryHydrateWebsiteUserAsync(accessToken, refreshToken, expiresAt, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }, cancellationToken);
            }

            if (runPostLoginTasks)
            {
                _ = Task.Run(async () =>
                {
                    try { await GetAccountStatusAsync(cancellationToken); }
                    catch { }
                }, cancellationToken);
            }

            return Task.FromResult(state);
        }

        private async Task TryHydrateWebsiteUserAsync(
            string accessToken,
            string refreshToken,
            DateTimeOffset? expiresAt,
            CancellationToken cancellationToken)
        {
            try
            {
                var user = await FetchCurrentUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
                await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var userInfo = EnsureUserInfo();
                    if (!string.Equals(userInfo.AuthMode, UserInfo.AuthModeWebsiteAuthorized, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(userInfo.AccessToken, accessToken, StringComparison.Ordinal)
                        || !string.Equals(userInfo.RefreshToken, refreshToken, StringComparison.Ordinal)
                        || userInfo.ExpiresAt != expiresAt)
                    {
                        _logger.Debug("Skip website user profile hydration because auth state changed before hydration completed.");
                        return;
                    }

                    if (!PersistWebsiteUser(user, accessToken, refreshToken, expiresAt))
                    {
                        return;
                    }

                    SaveConfigSnapshot("hydrate website user profile");
                    UpdateState(CreateState(
                        BackendAuthStatus.LoggedIn,
                        authMode: BackendAuthMode.WebsiteAuthorized,
                        accessToken: accessToken));
                }
                finally
                {
                    _loginLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                _logger.Warning("Failed to hydrate website user profile from /me after token response. Keeping login state with token only.");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to hydrate website user profile after token response.");
            }
        }

        private BackendAuthState ApplyLegacyTokenResponse(string accessToken, string username)
        {
            var userInfo = EnsureUserInfo();
            var trimmedToken = accessToken.Trim();
            var expiresAt = ResolveExpiresAt(trimmedToken, expiresIn: null);

            userInfo.AuthMode = UserInfo.AuthModeLegacyCredentials;
            userInfo.Username = username?.Trim() ?? string.Empty;
            userInfo.AccessToken = trimmedToken;
            userInfo.RefreshToken = string.Empty;
            userInfo.ExpiresAt = expiresAt;
            userInfo.UserUuid = string.Empty;
            userInfo.Email = string.Empty;
            userInfo.EmailVerified = false;
            SaveConfigSnapshot("apply legacy token response");

            _logger.Information(
                "Legacy backend auth succeeded. username={Username}, expiresAt={ExpiresAt}",
                userInfo.Username,
                expiresAt);

            return UpdateState(CreateState(
                BackendAuthStatus.LoggedIn,
                authMode: BackendAuthMode.LegacyCredentials,
                accessToken: trimmedToken));
        }

        private async Task<AuthUserResponse> FetchCurrentUserAsync(string accessToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BackendUrlHelper.BuildV2AuthBaseUrl(_config)}me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.Warning("FetchCurrentUserAsync returned 401. detail={Detail}", TryExtractErrorDetail(raw) ?? raw);
                throw new UnauthorizedAccessException();
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractErrorMessage(raw, response));
            return JsonSerializer.Deserialize<AuthUserResponse>(raw) ?? new AuthUserResponse();
        }

        private async Task<AccountUsageResponse?> FetchAccountUsageAsync(string accessToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BackendUrlHelper.BuildV2AccountBaseUrl(_config)}usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.Warning("FetchAccountUsageAsync returned 401. detail={Detail}", TryExtractErrorDetail(raw) ?? raw);
                throw new UnauthorizedAccessException();
            }
            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<AccountUsageResponse>(raw);
        }

        private async Task TryNotifyLogoutAsync(string? accessToken, string? refreshToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
            {
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BackendUrlHelper.BuildV2AuthBaseUrl(_config)}logout");
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }

                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    request.Content = JsonContent.Create(new { refresh_token = refreshToken });
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.Warning(
                    "Backend logout returned non-success. status={StatusCode}, detail={Detail}",
                    (int)response.StatusCode,
                    ExtractErrorMessage(raw, response));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to notify backend logout endpoint.");
            }
        }

        private CancellationTokenSource CreatePendingWebsiteLoginTokenSource(CancellationToken cancellationToken)
        {
            var websiteLoginCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_websiteLoginSync)
            {
                _pendingWebsiteLoginCts = websiteLoginCts;
            }

            return websiteLoginCts;
        }

        private void CancelPendingWebsiteLogin(string reason)
        {
            CancellationTokenSource? pendingWebsiteLoginCts;
            lock (_websiteLoginSync)
            {
                pendingWebsiteLoginCts = _pendingWebsiteLoginCts;
            }

            if (pendingWebsiteLoginCts == null || pendingWebsiteLoginCts.IsCancellationRequested)
            {
                return;
            }

            _logger.Information("Cancelling pending website authorization flow. reason={Reason}", reason);
            try
            {
                pendingWebsiteLoginCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore races with cleanup from the finishing login flow.
            }
        }

        private void ClearPendingWebsiteLoginTokenSource(CancellationTokenSource? websiteLoginCts)
        {
            if (websiteLoginCts == null)
            {
                return;
            }

            lock (_websiteLoginSync)
            {
                if (ReferenceEquals(_pendingWebsiteLoginCts, websiteLoginCts))
                {
                    _pendingWebsiteLoginCts = null;
                }
            }

            websiteLoginCts.Dispose();
        }

        private async Task<string?> GetRetryAccessTokenAfterUnauthorizedAsync(
            string rejectedAccessToken,
            string operation,
            CancellationToken cancellationToken)
        {
            await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var userInfo = EnsureUserInfo();
                if (IsAccessTokenUsable(userInfo.AccessToken, userInfo.ExpiresAt)
                    && !string.Equals(userInfo.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
                {
                    _logger.Debug(
                        "{Operation} will retry with a newer access token that was already refreshed by another request.",
                        operation);
                    return userInfo.AccessToken;
                }

                if (string.Equals(_lastRejectedRefreshedAccessToken, rejectedAccessToken, StringComparison.Ordinal))
                {
                    _logger.Warning(
                        "{Operation} returned 401 for an access token that was already refreshed and rejected. Skip another refresh cycle.",
                        operation);
                    return null;
                }

                ExpireAccessTokenCore($"{operation} returned 401");

                var state = GetConfiguredAuthMode() switch
                {
                    BackendAuthMode.AnonymousTest => await LoginAnonymousInternalAsync(cancellationToken).ConfigureAwait(false),
                    BackendAuthMode.WebsiteAuthorized => await RestoreWebsiteSessionInternalAsync(cancellationToken, silentRefresh: true).ConfigureAwait(false),
                    BackendAuthMode.LegacyCredentials => await RestoreLegacySessionInternalAsync(cancellationToken, allowCredentialLogin: true).ConfigureAwait(false),
                    _ => CreateState(BackendAuthStatus.NotLoggedIn)
                };

                if (!string.IsNullOrWhiteSpace(state.AccessToken)
                    && !string.Equals(state.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
                {
                    _lastRejectedRefreshedAccessToken = string.Empty;
                }

                return state.AccessToken;
            }
            finally
            {
                _loginLock.Release();
            }
        }

        private BackendAuthState CreateState(BackendAuthStatus status, string? errorMessage = null, BackendAuthMode? authMode = null, string? accessToken = "__CONFIG__")
        {
            var userInfo = EnsureUserInfo();
            var mode = authMode ?? GetConfiguredAuthMode();
            var isAnonymous = mode == BackendAuthMode.AnonymousTest;
            var token = accessToken == "__CONFIG__" ? userInfo.AccessToken : accessToken;
            return new BackendAuthState
            {
                Status = status,
                AuthMode = mode,
                AccessToken = token,
                ErrorMessage = errorMessage,
                IsUsingTestAccount = isAnonymous,
                UserUuid = isAnonymous || mode == BackendAuthMode.LegacyCredentials ? string.Empty : userInfo.UserUuid,
                UserLabel = ResolveStateUserLabel(userInfo, mode, isAnonymous),
                Email = isAnonymous || mode == BackendAuthMode.LegacyCredentials ? string.Empty : userInfo.Email,
                EmailVerified = !isAnonymous && mode != BackendAuthMode.LegacyCredentials && userInfo.EmailVerified,
                ExpiresAtUtc = userInfo.ExpiresAt
            };
        }

        private BackendAuthState UpdateState(BackendAuthState newState)
        {
            if (EqualityComparer<BackendAuthState>.Default.Equals(_state, newState))
            {
                return _state;
            }

            _state = newState;
            StateChanged?.Invoke(this, newState);
            return newState;
        }

        private UserInfo EnsureUserInfo()
        {
            _config.UserInfo ??= new UserInfo();
            return _config.UserInfo;
        }

        private BackendAuthMode GetConfiguredAuthMode()
        {
            if (BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return BackendAuthMode.LegacyCredentials;
            }

            var userInfo = EnsureUserInfo();
            if (string.Equals(userInfo.AuthMode, UserInfo.AuthModeWebsiteAuthorized, StringComparison.OrdinalIgnoreCase))
            {
                return BackendAuthMode.WebsiteAuthorized;
            }
            if (string.Equals(userInfo.AuthMode, UserInfo.AuthModeAnonymousTest, StringComparison.OrdinalIgnoreCase))
            {
                return BackendAuthMode.AnonymousTest;
            }
            if (!string.IsNullOrWhiteSpace(userInfo.RefreshToken)
                || !string.IsNullOrWhiteSpace(userInfo.UserUuid)
                || !string.IsNullOrWhiteSpace(userInfo.Email))
            {
                return BackendAuthMode.WebsiteAuthorized;
            }
            return BackendAuthMode.AnonymousTest;
        }

        private static bool IsAccessTokenUsable(string? accessToken, DateTimeOffset? expiresAt)
        {
            return !string.IsNullOrWhiteSpace(accessToken)
                && (!expiresAt.HasValue || expiresAt.Value > DateTimeOffset.UtcNow.Add(AccessTokenRefreshSkew));
        }

        private static DateTimeOffset? ResolveExpiresAt(string accessToken, int? expiresIn)
        {
            if (expiresIn.HasValue && expiresIn.Value > 0)
            {
                return DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value);
            }
            return TryReadJwtExpiration(accessToken);
        }

        private static DateTimeOffset? TryReadJwtExpiration(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private bool PersistWebsiteUser(AuthUserResponse user, string accessToken, string refreshToken, DateTimeOffset? expiresAt)
        {
            var userInfo = EnsureUserInfo();
            var changed =
                !string.Equals(userInfo.AuthMode, UserInfo.AuthModeWebsiteAuthorized, StringComparison.Ordinal) ||
                !string.Equals(userInfo.AccessToken, accessToken, StringComparison.Ordinal) ||
                !string.Equals(userInfo.RefreshToken, refreshToken, StringComparison.Ordinal) ||
                userInfo.ExpiresAt != expiresAt ||
                !string.Equals(userInfo.UserUuid, user.UserUuid ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(userInfo.Username, user.Username ?? string.Empty, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(userInfo.Password) ||
                !string.Equals(userInfo.Email, user.Email ?? string.Empty, StringComparison.Ordinal) ||
                userInfo.EmailVerified != user.EmailVerified;

            userInfo.AuthMode = UserInfo.AuthModeWebsiteAuthorized;
            userInfo.AccessToken = accessToken;
            userInfo.RefreshToken = refreshToken;
            userInfo.ExpiresAt = expiresAt;
            userInfo.UserUuid = user.UserUuid ?? string.Empty;
            userInfo.Username = user.Username ?? string.Empty;
            userInfo.Password = string.Empty;
            userInfo.Email = user.Email ?? string.Empty;
            userInfo.EmailVerified = user.EmailVerified;
            return changed;
        }

        private static AuthUserResponse CreateUserResponseFromPersistedIdentity(UserInfo userInfo)
        {
            return new AuthUserResponse
            {
                UserUuid = userInfo.UserUuid,
                Username = userInfo.Username,
                Email = userInfo.Email,
                EmailVerified = userInfo.EmailVerified
            };
        }

        private static void PersistWebsiteSession(UserInfo userInfo, string accessToken, string refreshToken, DateTimeOffset? expiresAt)
        {
            userInfo.AuthMode = UserInfo.AuthModeWebsiteAuthorized;
            userInfo.Username = string.Empty;
            userInfo.Password = string.Empty;
            userInfo.AccessToken = accessToken;
            userInfo.RefreshToken = refreshToken;
            userInfo.ExpiresAt = expiresAt;
            ClearFormalIdentity(userInfo);
        }

        private static string ResolveUserLabel(AuthUserResponse user)
        {
            if (!string.IsNullOrWhiteSpace(user.Username)) return user.Username.Trim();
            if (!string.IsNullOrWhiteSpace(user.UserUuid)) return $"User {(user.UserUuid.Length <= 8 ? user.UserUuid : user.UserUuid[..8])}";
            return "VoiceLinkVR User";
        }

        private static string ResolveStateUserLabel(UserInfo userInfo, BackendAuthMode mode, bool isAnonymous)
        {
            if (isAnonymous)
            {
                return "Guest trial";
            }

            if (mode == BackendAuthMode.LegacyCredentials)
            {
                return ResolveLegacyUserLabel(userInfo);
            }

            if (!string.IsNullOrWhiteSpace(userInfo.Username))
            {
                return userInfo.Username.Trim();
            }

            if (!string.IsNullOrWhiteSpace(userInfo.UserUuid))
            {
                return $"User {(userInfo.UserUuid.Length <= 8 ? userInfo.UserUuid : userInfo.UserUuid[..8])}";
            }

            return "VoiceLinkVR User";
        }

        private static void ClearFormalIdentity(UserInfo userInfo)
        {
            userInfo.UserUuid = string.Empty;
            userInfo.Email = string.Empty;
            userInfo.EmailVerified = false;
        }

        private static void ClearTokens(UserInfo userInfo)
        {
            userInfo.AccessToken = string.Empty;
            userInfo.RefreshToken = string.Empty;
            userInfo.ExpiresAt = null;
        }
        private static string ResolveLegacyUserLabel(UserInfo userInfo)
        {
            if (!string.IsNullOrWhiteSpace(userInfo.Username))
            {
                return userInfo.Username.Trim();
            }
            return "Legacy backend account";
        }

        private static string ExtractErrorMessage(string raw, HttpResponseMessage response)
        {
            return TryExtractErrorDetail(raw) ?? $"HTTP {(int)response.StatusCode} {response.StatusCode}";
        }

        private static bool ShouldDiscardPersistedWebsiteSession(HttpStatusCode statusCode, string raw)
        {
            if (statusCode == HttpStatusCode.Unauthorized)
            {
                return true;
            }

            var code = TryExtractErrorCode(raw);
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            return code.Trim().ToLowerInvariant() switch
            {
                "invalid_token" => true,
                "token_expired" => true,
                "expired_token" => true,
                "invalid_grant" => true,
                "invalid_refresh_token" => true,
                "refresh_token_invalid" => true,
                "refresh_token_expired" => true,
                "refresh_token_revoked" => true,
                "token_revoked" => true,
                "session_not_found" => true,
                "session_revoked" => true,
                _ => false
            };
        }

        private static string? TryExtractErrorCode(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("detail", out var detail))
                {
                    return null;
                }

                if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var codeElement))
                {
                    return codeElement.ValueKind == JsonValueKind.String
                        ? codeElement.GetString()
                        : codeElement.ToString();
                }

                if (detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? TryExtractErrorDetail(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("detail", out var detail)) return null;
                return detail.ValueKind switch
                {
                    JsonValueKind.String => detail.GetString(),
                    JsonValueKind.Object when detail.TryGetProperty("message", out var message) => message.GetString(),
                    _ => detail.ToString()
                };
            }
            catch
            {
                return content;
            }
        }

        private void SaveConfigSnapshot(string reason)
        {
            if (AppConfigStorage.TrySave(_config, out var errorMessage))
            {
                var userInfo = EnsureUserInfo();
                _logger.Debug(
                    "Persisted auth config snapshot. reason={Reason}, authMode={AuthMode}, userUuid={UserUuid}, hasAccessToken={HasAccessToken}, hasRefreshToken={HasRefreshToken}, expiresAt={ExpiresAt}",
                    reason,
                    userInfo.AuthMode,
                    string.IsNullOrWhiteSpace(userInfo.UserUuid) ? "(empty)" : userInfo.UserUuid,
                    !string.IsNullOrWhiteSpace(userInfo.AccessToken),
                    !string.IsNullOrWhiteSpace(userInfo.RefreshToken),
                    userInfo.ExpiresAt);
                return;
            }

            _logger.Warning(
                "Failed to persist auth config snapshot. reason={Reason}, error={Error}",
                reason,
                errorMessage ?? "(unknown)");
        }

        private class AuthTokenResponse
        {
            [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
            [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
            [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
            [JsonPropertyName("user")] public AuthUserResponse? User { get; set; }
        }

        private sealed class DeviceStartResponse
        {
            [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
            [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
            [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = string.Empty;
            [JsonPropertyName("verification_uri_complete")] public string VerificationUriComplete { get; set; } = string.Empty;
            [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
            [JsonPropertyName("interval")] public int Interval { get; set; }
        }

        private sealed class DeviceTokenResponse : AuthTokenResponse
        {
            [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
            [JsonPropertyName("poll_interval")] public int? PollInterval { get; set; }
        }

        private sealed class AuthUserResponse
        {
            [JsonPropertyName("user_uuid")] public string? UserUuid { get; set; }
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("email")] public string? Email { get; set; }
            [JsonPropertyName("email_verified")] public bool EmailVerified { get; set; }
            [JsonPropertyName("plan_name")] public string? PlanName { get; set; }
        }

        private sealed class AccountUsageResponse
        {
            [JsonPropertyName("is_active")] public bool IsActive { get; set; } = true;
            [JsonPropertyName("is_expired")] public bool IsExpired { get; set; }
            [JsonPropertyName("expiration_utc")] public DateTimeOffset? ExpirationUtc { get; set; }
            [JsonPropertyName("remaining_seconds")] public int? RemainingSeconds { get; set; }
            [JsonPropertyName("daily_limit")] public int? DailyLimit { get; set; }
            [JsonPropertyName("remaining_daily")] public int? RemainingDaily { get; set; }
            [JsonPropertyName("today_remaining_daily")] public int? TodayRemainingDaily { get; set; }
            [JsonPropertyName("plan_name")] public string? PlanName { get; set; }
            [JsonPropertyName("plan_price")] public decimal? PlanPrice { get; set; }
            [JsonPropertyName("plan_ref")] public string? PlanRef { get; set; }
            [JsonPropertyName("pricing_mode")] public string? PricingMode { get; set; }
            [JsonPropertyName("addon_resource_balance")] public int? AddonResourceBalance { get; set; }
            [JsonPropertyName("addon_resource_total")] public int? AddonResourceTotal { get; set; }
        }
    }
}



