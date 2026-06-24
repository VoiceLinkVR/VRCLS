using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VRCLS.UI.Services
{
    public static class OmniVoiceTtsVoiceCatalogService
    {
        private static readonly string[] _voiceDesignPresets =
        {
            "female, young adult, moderate pitch",
            "female, young adult, high pitch",
            "female, young adult, high pitch, american accent",
            "female, young adult, high pitch, british accent",
            "female, young adult, moderate pitch, whisper",
            "male, young adult, moderate pitch",
            "male, middle-aged, low pitch",
            "male, elderly, low pitch",
            "male, young adult, moderate pitch, american accent",
            "male, young adult, moderate pitch, british accent"
        };

        public static Task<IReadOnlyList<string>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(
                _voiceDesignPresets
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
    }
}
