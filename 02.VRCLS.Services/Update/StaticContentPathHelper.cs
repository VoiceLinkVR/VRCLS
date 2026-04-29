using System;
using VRCLS.Core.Models;

namespace VRCLS.Services.Update
{
    internal static class StaticContentPathHelper
    {
        internal const string ModelCatalogRelativePath = "manifests/models/list.json";
        internal const string TranslationProfileRelativePath = "manifests/translations/profile.json";

        internal static Uri ResolveStaticBaseUri(AppConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (string.IsNullOrWhiteSpace(config.UpdateServerUrl))
            {
                throw new InvalidOperationException("UpdateServerUrl is required for static content mode.");
            }

            var raw = config.UpdateServerUrl.Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"Invalid update server url: '{raw}'");
            }

            return raw.EndsWith("/", StringComparison.Ordinal)
                ? uri
                : new Uri(raw + "/", UriKind.Absolute);
        }

        internal static Uri ResolveStaticContentUri(AppConfig config, string relativePath)
        {
            var baseUri = ResolveStaticBaseUri(config);
            var normalizedPath = (relativePath ?? string.Empty).Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new InvalidOperationException("Static content relative path is required.");
            }

            return new Uri(baseUri, normalizedPath);
        }
    }
}
