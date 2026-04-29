using System;

namespace VRCLS.Core.Models
{
    public enum BackendAuthStatus
    {
        NotLoggedIn = 0,
        LoggingIn = 1,
        LoggedIn = 2,
        Failed = 3
    }

    public enum BackendAuthMode
    {
        None = 0,
        AnonymousTest = 1,
        WebsiteAuthorized = 2,
        LegacyCredentials = 3
    }

    public sealed record BackendAuthState
    {
        public BackendAuthStatus Status { get; init; } = BackendAuthStatus.NotLoggedIn;

        public BackendAuthMode AuthMode { get; init; } = BackendAuthMode.None;

        public string? AccessToken { get; init; }

        public string? ErrorMessage { get; init; }

        public bool IsUsingTestAccount { get; init; }

        public string? UserUuid { get; init; }

        public string? UserLabel { get; init; }

        public string? Email { get; init; }

        public bool EmailVerified { get; init; }

        public string? VerificationUri { get; init; }

        public string? VerificationUriComplete { get; init; }

        public string? UserCode { get; init; }

        public DateTimeOffset? ExpiresAtUtc { get; init; }

        public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);

        public bool IsAnonymousMode => AuthMode == BackendAuthMode.AnonymousTest;

        public bool IsWebsiteAuthorized => AuthMode == BackendAuthMode.WebsiteAuthorized;

        public bool IsLegacyCredentials => AuthMode == BackendAuthMode.LegacyCredentials;
    }
}

