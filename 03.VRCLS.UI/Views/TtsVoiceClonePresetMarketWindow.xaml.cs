using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VRCLS.UI.Services;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views
{
    public partial class TtsVoiceClonePresetMarketWindow : Window
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<TtsVoiceClonePresetMarketItem>>> _loadMarketAsync;
        private readonly Func<TtsVoiceClonePresetMarketItem, CancellationToken, Task<string>> _downloadPackageAsync;
        private readonly TtsVoiceClonePresetMarketWindowState _state = new();
        private bool _loaded;

        public string DownloadedPackagePath { get; private set; } = string.Empty;

        public TtsVoiceClonePresetMarketWindow(
            Func<CancellationToken, Task<IReadOnlyList<TtsVoiceClonePresetMarketItem>>> loadMarketAsync,
            Func<TtsVoiceClonePresetMarketItem, CancellationToken, Task<string>> downloadPackageAsync)
        {
            _loadMarketAsync = loadMarketAsync ?? throw new ArgumentNullException(nameof(loadMarketAsync));
            _downloadPackageAsync = downloadPackageAsync ?? throw new ArgumentNullException(nameof(downloadPackageAsync));
            InitializeComponent();
            DataContext = _state;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            await LoadMarketAsync();
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            await LoadMarketAsync();
        }

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            if (_state.SelectedItem == null || _state.IsBusy)
            {
                return;
            }

            try
            {
                _state.IsBusy = true;
                _state.StatusText = $"正在下载 {_state.SelectedItem.DisplayName}...";
                DownloadedPackagePath = await _downloadPackageAsync(_state.SelectedItem, CancellationToken.None);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                _state.StatusText = ex.Message;
            }
            finally
            {
                _state.IsBusy = false;
            }
        }

        private async Task LoadMarketAsync()
        {
            if (_state.IsBusy)
            {
                return;
            }

            try
            {
                _state.IsBusy = true;
                _state.StatusText = "正在加载市场预设...";
                _state.Items.Clear();

                var items = await _loadMarketAsync(CancellationToken.None);
                foreach (var item in items)
                {
                    _state.Items.Add(item);
                }

                _state.SelectedItem = _state.Items.Count > 0 ? _state.Items[0] : null;
                _state.StatusText = _state.Items.Count == 0
                    ? "市场暂时没有可下载的音色预设。"
                    : $"已加载 {_state.Items.Count} 个市场预设。";
            }
            catch (Exception ex)
            {
                _state.StatusText = ex.Message;
            }
            finally
            {
                _state.IsBusy = false;
            }
        }

        private sealed class TtsVoiceClonePresetMarketWindowState : ObservableObject
        {
            private bool _isBusy;
            private string _statusText = string.Empty;
            private TtsVoiceClonePresetMarketItem? _selectedItem;

            public ObservableCollection<TtsVoiceClonePresetMarketItem> Items { get; } = new();

            public TtsVoiceClonePresetMarketItem? SelectedItem
            {
                get => _selectedItem;
                set
                {
                    if (SetField(ref _selectedItem, value))
                    {
                        OnPropertyChanged(nameof(HasSelectedItem));
                        OnPropertyChanged(nameof(CanInstallSelected));
                    }
                }
            }

            public bool IsBusy
            {
                get => _isBusy;
                set
                {
                    if (SetField(ref _isBusy, value))
                    {
                        OnPropertyChanged(nameof(CanRefresh));
                        OnPropertyChanged(nameof(CanInstallSelected));
                    }
                }
            }

            public bool HasSelectedItem => SelectedItem != null;

            public bool CanRefresh => !IsBusy;

            public bool CanInstallSelected => !IsBusy && SelectedItem != null;

            public string StatusText
            {
                get => _statusText;
                set => SetField(ref _statusText, value ?? string.Empty);
            }
        }
    }
}
