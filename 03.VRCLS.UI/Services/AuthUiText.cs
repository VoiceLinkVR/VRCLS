using System;
using System.Windows;

namespace VRCLS.UI.Services
{
    internal static class AuthUiText
    {
        public static string LocalizeErrorMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            const string startFailedPrefix = "Failed to start website authorization: ";
            const string refreshFailedPrefix = "Website refresh failed: ";
            const string unexpectedStatusPrefix = "Unexpected website authorization status: ";

            if (message.StartsWith(startFailedPrefix, StringComparison.Ordinal))
            {
                return string.Format(
                    Get("AuthErrorStartWebsiteAuthorizationFailed", "Failed to start website authorization: {0}"),
                    message[startFailedPrefix.Length..]);
            }

            if (message.StartsWith(refreshFailedPrefix, StringComparison.Ordinal))
            {
                return string.Format(
                    Get("AuthErrorWebsiteRefreshFailed", "Website refresh failed: {0}"),
                    message[refreshFailedPrefix.Length..]);
            }

            if (message.StartsWith(unexpectedStatusPrefix, StringComparison.Ordinal))
            {
                return string.Format(
                    Get("AuthErrorWebsiteAuthorizationUnexpectedStatus", "Unexpected website authorization status: {0}"),
                    message[unexpectedStatusPrefix.Length..]);
            }

            return message switch
            {
                "Website authorization start response did not include device_code."
                    => Get("AuthErrorMissingDeviceCode", "Website authorization start response did not include device_code."),
                "Website authorization was approved, but the backend did not return access_token."
                    => Get("AuthErrorWebsiteAuthorizationApprovedMissingToken", "Website authorization was approved, but the backend did not return access_token."),
                "Website authorization was denied."
                    => Get("AuthErrorWebsiteAuthorizationDenied", "Website authorization was denied."),
                "Website authorization request expired. Please start login again."
                    => Get("AuthErrorWebsiteAuthorizationExpired", "Website authorization request expired. Please start login again."),
                "This website authorization request has already completed. Please start login again."
                    => Get("AuthErrorWebsiteAuthorizationCompleted", "This website authorization request has already completed. Please start login again."),
                "Website authorization request timed out. Please start login again."
                    => Get("AuthErrorWebsiteAuthorizationTimedOut", "Website authorization request timed out. Please start login again."),
                "Cancelled"
                    => Get("AuthErrorCancelled", "Cancelled"),
                "Website auth state is missing refresh_token. Please authorize again."
                    => Get("AuthErrorWebsiteStateMissingRefreshToken", "Website auth state is missing refresh_token. Please authorize again."),
                "Website refresh response did not return access_token."
                    => Get("AuthErrorWebsiteRefreshMissingAccessToken", "Website refresh response did not return access_token."),
                "Password login is no longer available for the modern backend. Please use website authorization."
                    => Get("AuthErrorPasswordLoginDeprecated", "Password login is no longer available for the modern backend. Please use website authorization."),
                "Legacy backend mode requires username and password."
                    => Get("AuthErrorLegacyRequiresUsernamePassword", "Legacy backend mode requires username and password."),
                "Legacy backend did not return access_token."
                    => Get("AuthErrorLegacyMissingAccessToken", "Legacy backend did not return access_token."),
                "Website login succeeded but the backend did not return refresh_token. Please authorize again."
                    => Get("AuthErrorWebsiteLoginMissingRefreshToken", "Website login succeeded but the backend did not return refresh_token. Please authorize again."),
                "Guest trial login did not return access_token."
                    => Get("AuthErrorGuestMissingAccessToken", "Guest trial login did not return access_token."),
                _ => message
            };
        }

        public static string Get(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key) as string ?? fallback;
        }
    }
}
