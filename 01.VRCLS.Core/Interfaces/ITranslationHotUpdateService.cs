using System;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface ITranslationHotUpdateService
    {
        event EventHandler<TranslationProfileUpdatedEventArgs>? ProfileUpdated;

        Task<TranslationProfileUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

        Task<bool> SyncLatestProfileAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    }
}
