using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.UI.Views
{
    public partial class ModelDownloadWindow : Window
    {
        private readonly IModelUpdateService _modelUpdateService;
        private readonly IReadOnlyList<ModelPackageInfo> _models;
        private readonly bool _allowCancel;
        private bool _isBusy;

        public ModelDownloadWindow(
            IModelUpdateService modelUpdateService,
            IReadOnlyList<ModelPackageInfo> models,
            string title,
            string message,
            string primaryButtonText,
            string secondaryButtonText,
            bool allowCancel)
        {
            _modelUpdateService = modelUpdateService ?? throw new ArgumentNullException(nameof(modelUpdateService));
            _models = models ?? throw new ArgumentNullException(nameof(models));
            _allowCancel = allowCancel;

            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "\u6A21\u578B\u4E0B\u8F7D" : title.Trim();
            TitleTextBlock.Text = Title;
            MessageTextBlock.Text = message ?? string.Empty;
            ModelListTextBlock.Text = BuildModelListText(_models);
            PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryButtonText) ? "\u4E0B\u8F7D" : primaryButtonText.Trim();
            SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryButtonText) ? "\u53D6\u6D88" : secondaryButtonText.Trim();
            StatusTextBlock.Text = "\u7B49\u5F85\u5F00\u59CB\u4E0B\u8F7D\u3002";
        }

        public bool DownloadSucceeded { get; private set; }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isBusy)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            await DownloadModelsAsync();
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            DownloadSucceeded = false;
            DialogResult = false;
            Close();
        }

        private async Task DownloadModelsAsync()
        {
            if (_isBusy)
            {
                return;
            }

            try
            {
                SetBusy(true);
                DownloadProgressBar.Value = 0;

                for (var index = 0; index < _models.Count; index++)
                {
                    var model = _models[index];
                    if (model == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(model.DownloadUrl))
                    {
                        StatusTextBlock.Text = $"\u66F4\u65B0\u76EE\u5F55\u4E2D\u672A\u627E\u5230\u6A21\u578B\u5305\uFF1A{GetModelDisplayName(model.Type)}\u3002";
                        return;
                    }

                    StatusTextBlock.Text = _models.Count == 1
                        ? $"\u6B63\u5728\u4E0B\u8F7D\u5E76\u5B89\u88C5 {GetModelDisplayName(model.Type)}..."
                        : $"\u6B63\u5728\u4E0B\u8F7D\u5E76\u5B89\u88C5 {index + 1}/{_models.Count}\uFF1A{GetModelDisplayName(model.Type)}...";

                    var currentIndex = index;
                    var progress = new Progress<double>(value =>
                    {
                        var baseProgress = currentIndex * 100d / _models.Count;
                        var span = 100d / _models.Count;
                        DownloadProgressBar.Value = Math.Min(100d, baseProgress + value * span / 100d);
                    });

                    var ok = await _modelUpdateService.DownloadAndInstallModelAsync(model, progress);
                    if (!ok)
                    {
                        StatusTextBlock.Text = $"\u6A21\u578B\u4E0B\u8F7D\u6216\u5B89\u88C5\u5931\u8D25\uFF1A{GetModelDisplayName(model.Type)}\u3002";
                        return;
                    }
                }

                DownloadProgressBar.Value = 100;
                StatusTextBlock.Text = "\u6A21\u578B\u4E0B\u8F7D\u5B8C\u6210\u3002";
                DownloadSucceeded = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"\u6A21\u578B\u4E0B\u8F7D\u5931\u8D25\uFF1A{ex.Message}";
            }
            finally
            {
                if (!DownloadSucceeded)
                {
                    SetBusy(false);
                }
            }
        }

        private void SetBusy(bool isBusy)
        {
            _isBusy = isBusy;
            PrimaryButton.IsEnabled = !isBusy;
            SecondaryButton.IsEnabled = !isBusy && _allowCancel;
            if (!_allowCancel)
            {
                SecondaryButton.IsEnabled = !isBusy;
            }
        }

        private static string BuildModelListText(IEnumerable<ModelPackageInfo> models)
        {
            var names = models
                .Where(model => model != null)
                .Select(model => GetModelDisplayName(model.Type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return names.Count == 0
                ? "\u672A\u6307\u5B9A\u6A21\u578B\u3002"
                : "\u9700\u8981\u51C6\u5907\u7684\u6A21\u578B\uFF1A" + string.Join("\u3001", names);
        }

        private static string GetModelDisplayName(string? modelType)
        {
            var normalized = (modelType ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "vad" => "VAD",
                "sensevoice" => "SenseVoice",
                "sherpa" => "Sherpa",
                _ => string.IsNullOrWhiteSpace(modelType) ? "\u6A21\u578B" : modelType.Trim()
            };
        }
    }
}
