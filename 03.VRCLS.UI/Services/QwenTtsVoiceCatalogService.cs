using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VRCLS.UI.Services
{
    public static class QwenTtsVoiceCatalogService
    {
        private static readonly string[] _voices =
        {
            "alloy",
            "echo",
            "fable",
            "onyx",
            "nova",
            "shimmer",
            "vivian",
            "aiden"
        };

        public static Task<IReadOnlyList<string>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(
                _voices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
