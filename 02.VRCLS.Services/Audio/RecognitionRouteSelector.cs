using System;
using VRCLS.Core.Models;

namespace VRCLS.Services.Audio
{
    internal enum RecognitionRoute
    {
        Remote,
        LocalSherpa,
        LocalSenseVoice
    }

    internal static class RecognitionRouteSelector
    {
        public static RecognitionRoute Select(
            string source,
            RecognitionBackendMode mode,
            LocalRecognitionModel localModel)
        {
            if (!string.Equals(source, "Mic", StringComparison.OrdinalIgnoreCase)
                || mode != RecognitionBackendMode.Local)
            {
                return RecognitionRoute.Remote;
            }

            return localModel == LocalRecognitionModel.SenseVoice
                ? RecognitionRoute.LocalSenseVoice
                : RecognitionRoute.LocalSherpa;
        }
    }
}
