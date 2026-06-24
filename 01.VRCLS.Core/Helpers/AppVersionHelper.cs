using System;
using System.Reflection;

namespace VRCLS.Core.Helpers
{
    public static class AppVersionHelper
    {
        public static Version GetCurrentVersion(Assembly? assembly = null)
        {
            var resolved = ResolveAssembly(assembly);
            return resolved.GetName().Version ?? new Version(1, 0, 0, 0);
        }

        public static string GetDisplayVersion(Assembly? assembly = null)
        {
            var resolved = ResolveAssembly(assembly);
            var informational = resolved.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var normalized = informational.Trim();
                var metadataSeparator = normalized.IndexOf('+');
                if (metadataSeparator >= 0)
                {
                    normalized = normalized.Substring(0, metadataSeparator);
                }

                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            return GetCurrentVersion(resolved).ToString();
        }

        private static Assembly ResolveAssembly(Assembly? assembly)
        {
            return assembly
                ?? Assembly.GetEntryAssembly()
                ?? Assembly.GetExecutingAssembly();
        }
    }
}
