using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IModelUpdateService : IDisposable
    {
        event EventHandler<ModelHotReloadEventArgs>? ModelsChanged;

        Task<IReadOnlyList<ModelPackageInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ModelPackageInfo>> GetMissingRequiredModelsAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ModelPackageInfo>> GetMissingModelsAsync(IEnumerable<string> modelTypes, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ModelPackageInfo>> EnsureRequiredModelsAsync(CancellationToken cancellationToken = default);

        Task<bool> DownloadAndInstallModelAsync(ModelPackageInfo model, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

        void StartWatching();

        void StopWatching();
    }
}
