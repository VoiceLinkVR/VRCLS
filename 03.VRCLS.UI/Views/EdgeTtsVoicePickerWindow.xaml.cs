using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VRCLS.UI.Services;

namespace VRCLS.UI.Views
{
    public partial class EdgeTtsVoicePickerWindow : Window
    {
        private readonly Func<string, Task> _previewVoiceAsync;
        private readonly Func<Task<IReadOnlyList<string>>> _loadVoicesAsync;
        private List<string> _allVoices = new();

        public EdgeTtsVoicePickerWindow(
            string currentVoiceName,
            Func<string, Task> previewVoiceAsync,
            Func<Task<IReadOnlyList<string>>>? loadVoicesAsync = null)
        {
            InitializeComponent();
            _previewVoiceAsync = previewVoiceAsync ?? throw new ArgumentNullException(nameof(previewVoiceAsync));
            _loadVoicesAsync = loadVoicesAsync ?? (() => EdgeTtsVoiceCatalogService.GetVoicesAsync());

            VoiceInputBox.Text = currentVoiceName ?? string.Empty;
            Loaded += OnLoaded;
        }

        public string SelectedVoiceName { get; private set; } = string.Empty;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await ReloadVoicesAsync();
        }

        private async Task ReloadVoicesAsync()
        {
            try
            {
                SetBusyState(true, L("EdgeTtsVoicePickerLoading", "Loading voice list..."));

                var voices = await _loadVoicesAsync();
                _allVoices = voices
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ApplyFilter();

                var current = VoiceInputBox.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var matched = _allVoices.FirstOrDefault(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(matched))
                    {
                        VoiceListBox.SelectedItem = matched;
                        VoiceListBox.ScrollIntoView(matched);
                    }
                }

                StatusText.Text = F("EdgeTtsVoicePickerLoaded", "Loaded {0} voices", _allVoices.Count);
            }
            catch (Exception ex)
            {
                StatusText.Text = F("EdgeTtsVoicePickerLoadFailed", "Load failed: {0}", ex.Message);
            }
            finally
            {
                SetBusyState(false, StatusText.Text);
            }
        }

        private void ApplyFilter()
        {
            var keyword = SearchBox.Text?.Trim() ?? string.Empty;
            var filtered = string.IsNullOrWhiteSpace(keyword)
                ? _allVoices
                : _allVoices
                    .Where(v => v.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            VoiceListBox.ItemsSource = filtered;
        }

        private void SetBusyState(bool isBusy, string statusText)
        {
            SearchBox.IsEnabled = !isBusy;
            VoiceListBox.IsEnabled = !isBusy;
            VoiceInputBox.IsEnabled = !isBusy;
            StatusText.Text = statusText;
        }

        private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            await ReloadVoicesAsync();
        }

        private void OnVoiceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoiceListBox.SelectedItem is string voice)
            {
                VoiceInputBox.Text = voice;
            }
        }

        private async void OnPreviewClicked(object sender, RoutedEventArgs e)
        {
            var voice = VoiceInputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(voice))
            {
                MessageBox.Show(
                    this,
                    L("EdgeTtsVoicePickerSelectFirst", "Please select or input a voice first."),
                    L("EdgeTtsVoicePickerInfoTitle", "Info"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                SetBusyState(true, F("EdgeTtsVoicePickerPreviewing", "Previewing: {0}", voice));
                await _previewVoiceAsync(voice);
                StatusText.Text = F("EdgeTtsVoicePickerPreviewFinished", "Preview finished: {0}", voice);
            }
            catch (Exception ex)
            {
                StatusText.Text = F("EdgeTtsVoicePickerPreviewFailed", "Preview failed: {0}", ex.Message);
                MessageBox.Show(
                    this,
                    StatusText.Text,
                    L("EdgeTtsVoicePickerErrorTitle", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusyState(false, StatusText.Text);
            }
        }

        private static string L(string key, string fallback)
        {
            var text = Application.Current?.TryFindResource(key) as string;
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static string F(string key, string fallbackTemplate, params object[] args)
        {
            var template = L(key, fallbackTemplate);
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return fallbackTemplate;
            }
        }

        private void OnUseDefaultClicked(object sender, RoutedEventArgs e)
        {
            SelectedVoiceName = string.Empty;
            DialogResult = true;
        }

        private void OnConfirmClicked(object sender, RoutedEventArgs e)
        {
            SelectedVoiceName = VoiceInputBox.Text?.Trim() ?? string.Empty;
            DialogResult = true;
        }
    }
}
