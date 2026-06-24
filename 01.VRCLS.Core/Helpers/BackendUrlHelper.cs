using System;
using VRCLS.Core.Models;

namespace VRCLS.Core.Helpers
{
    public static class BackendUrlHelper
    {
        public static bool UsesLegacyBackendApi(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return config.UseLegacyBackendApi;
        }

        /// <summary>
        /// Builds the configured backend API base URL and normalizes it to end with "/".
        /// </summary>
        public static string BuildApiBaseUrl(AppConfig config)
        {
            if (UsesLegacyBackendApi(config))
            {
                return BuildLegacyApiBaseUrl(config);
            }

            return $"{BuildServiceRootUrl(config)}/v2/api/";
        }

        public static Uri BuildApiBaseAddress(AppConfig config)
        {
            return new Uri(BuildApiBaseUrl(config), UriKind.Absolute);
        }

        public static string BuildServiceRootUrl(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
                ? $"http://{config.ApiIp}:{config.ApiPort}"
                : config.BaseUrl.Trim();

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                return baseUrl.TrimEnd('/');
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            foreach (var suffix in new[] { "/v2/api", "/v2/auth", "/v1/api", "/v1/manageapi", "/v1/ui", "/api" })
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^suffix.Length];
                    break;
                }
            }

            var builder = new UriBuilder(uri)
            {
                Path = string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd('/') + "/",
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.ToString().TrimEnd('/');
        }

        public static string BuildV2AuthBaseUrl(AppConfig config)
        {
            return $"{BuildServiceRootUrl(config)}/v2/auth/";
        }

        public static string BuildV2AccountBaseUrl(AppConfig config)
        {
            return $"{BuildServiceRootUrl(config)}/v2/api/account/";
        }

        public static string BuildLegacyApiBaseUrl(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
                ? $"http://{config.ApiIp}:{config.ApiPort}"
                : config.BaseUrl.Trim();

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                return EnsureTrailingSlash(baseUrl);
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            };

            builder.Path = EnsureTrailingSlash(builder.Path);

            return builder.Uri.ToString();
        }

        public static string BuildLegacyLoginUrl(AppConfig config)
        {
            return AppendRelativePath(BuildLegacyApiBaseUrl(config), "login");
        }

        public static string BuildPortalBaseUrl(AppConfig config)
        {
            return $"{BuildServiceRootUrl(config)}/app";
        }

        public static string ResolvePortalUrl(AppConfig config, string? url, string fallbackRelativePath = "/app")
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var portalBaseUrl = BuildPortalBaseUrl(config).TrimEnd('/') + "/";
            var portalBaseUri = new Uri(portalBaseUrl, UriKind.Absolute);

            if (string.IsNullOrWhiteSpace(url))
            {
                return new Uri(portalBaseUri, fallbackRelativePath.TrimStart('/')).ToString();
            }

            var candidate = url.Trim();
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri))
            {
                return new Uri(portalBaseUri, candidate.TrimStart('/')).ToString();
            }

            if (!candidateUri.AbsolutePath.StartsWith("/app", StringComparison.OrdinalIgnoreCase))
            {
                return candidateUri.ToString();
            }

            var builder = new UriBuilder(portalBaseUri)
            {
                Path = candidateUri.AbsolutePath,
                Query = candidateUri.Query.TrimStart('?'),
                Fragment = candidateUri.Fragment.TrimStart('#')
            };

            return builder.Uri.ToString();
        }

        private static string AppendRelativePath(string baseUrl, string relativePath)
        {
            var normalizedBase = EnsureTrailingSlash(baseUrl);
            var normalizedRelativePath = string.IsNullOrWhiteSpace(relativePath)
                ? string.Empty
                : relativePath.Trim().TrimStart('/');

            if (string.IsNullOrWhiteSpace(normalizedRelativePath))
            {
                return normalizedBase;
            }

            if (Uri.TryCreate(normalizedBase, UriKind.Absolute, out var baseUri))
            {
                return new Uri(baseUri, normalizedRelativePath).ToString();
            }

            return normalizedBase + normalizedRelativePath;
        }

        private static string EnsureTrailingSlash(string? path)
        {
            var normalized = string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "/";
            }

            return normalized.EndsWith("/", StringComparison.Ordinal)
                ? normalized
                : normalized + "/";
        }
    }
}

