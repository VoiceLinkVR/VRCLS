using System;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IAppUpdateService
    {
        Version CurrentVersion { get; }

        AppUpdateState State { get; }

        AppUpdateInfo? PendingUpdate { get; }

        Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

        Task<bool> DownloadUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

        Task<bool> ApplyUpdateAndRestartAsync(CancellationToken cancellationToken = default);
    }
}
