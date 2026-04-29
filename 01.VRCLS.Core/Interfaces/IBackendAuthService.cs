using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IBackendAuthService
    {
        BackendAuthState State { get; }
        event EventHandler<BackendAuthState>? StateChanged;
        event EventHandler<string>? UserNotification;
        event EventHandler<ServiceErrorEvent>? ErrorOccurred;
        event EventHandler<AccountStatusInfo>? AccountStatusUpdated;

        /// <summary>
        /// Restores the configured auth mode and caches an access token when possible.
        /// Website login is never started silently from this method.
        /// </summary>
        Task<BackendAuthState> LoginAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts website/device authorization login and polls until approved, denied, or expired.
        /// </summary>
        Task<BackendAuthState> StartWebsiteLoginAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Logs out the current website-authorized session and returns to anonymous trial mode.
        /// </summary>
        Task<BackendAuthState> LogoutAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Switches to the anonymous trial mode backed by the server guest authorization endpoint.
        /// </summary>
        Task<BackendAuthState> SwitchToAnonymousTestModeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to login with specific credentials (used for manual login).
        /// </summary>
        Task<BackendAuthState> LoginWithCredentialsAsync(string username, string password, CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to login against the legacy backend using username/password.
        /// </summary>
        Task<BackendAuthState> LoginWithLegacyCredentialsAsync(string username, string password, CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures a token is available (may trigger login). Returns null if login failed.
        /// </summary>
        Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends an authorized HTTP request with centralized token refresh/retry handling.
        /// </summary>
        Task<HttpResponseMessage?> SendAuthorizedAsync(
            string operation,
            Func<HttpRequestMessage> createRequest,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears persisted auth/session fields when switching between legacy and modern backend modes.
        /// </summary>
        void ResetForBackendModeSwitch(bool useLegacyBackendApi);

        /// <summary>
        /// Clears auth/session fields that should not survive a backend base URL change.
        /// </summary>
        void ResetForBaseUrlChange(bool useLegacyBackendApi);

        /// <summary>
        /// Clears auth/session fields that should not survive a username/password change.
        /// </summary>
        void ResetForCredentialChange(bool useLegacyBackendApi);

        /// <summary>
        /// Raises a user-facing warning that backend requests are being rate limited.
        /// </summary>
        void NotifyTooManyRequests(
            string? source = null,
            string? backendDetail = null,
            string? channel = null,
            string? stage = null,
            string? originalText = null,
            long? segmentId = null);

        /// <summary>
        /// Fetches current account expiration status from backend.
        /// </summary>
        Task<AccountStatusInfo?> GetAccountStatusAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Compatibility alias for SwitchToAnonymousTestModeAsync.
        /// </summary>
        Task ResetToDefaultAccountAsync(CancellationToken cancellationToken = default);
    }
}
