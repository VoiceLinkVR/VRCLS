using System;

namespace VRCLS.Core.Helpers
{
    public static class AudioPlatformHelper
    {
        // OBS win-capture-audio and similar real-world implementations have
        // verified this path on updated Windows 10 2004+ systems, even though
        // Microsoft documents the API more conservatively.
        public const int ApplicationLoopbackMinimumBuild = 19041;

        public static bool IsApplicationLoopbackSupported()
        {
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, ApplicationLoopbackMinimumBuild);
        }

        public static int GetCurrentWindowsBuildNumber()
        {
            var version = Environment.OSVersion.Version;
            return version.Build > 0 ? version.Build : 0;
        }
    }
}
