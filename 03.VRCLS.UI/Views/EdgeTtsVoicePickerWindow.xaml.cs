using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VRCLS.Core.Models;
using VRCLS.UI.Services;

namespace VRCLS.UI.Views
{
    public partial class EdgeTtsVoicePickerWindow : Window
    {
        private readonly Func<string, Task> _previewVoiceAsync;
        private readonly Func<Task<IReadOnlyList<string>>> _loadVoicesAsync;
        private readonly Func<string, string> _getVoiceLanguageLabel;
        private List<string> _allVoices = new();

        public EdgeTtsVoicePickerWindow(
            string currentVoiceName,
            Func<string, Task> previewVoiceAsync,
            Func<Task<IReadOnlyList<string>>>? loadVoicesAsync = null,
            Func<string, string>? getVoiceLanguageLabel = null)
        {
            InitializeComponent();
            _previewVoiceAsync = previewVoiceAsync ?? throw new ArgumentNullException(nameof(previewVoiceAsync));
            _loadVoicesAsync = loadVoicesAsync ?? (() => EdgeTtsVoiceCatalogService.GetVoicesAsync());
            _getVoiceLanguageLabel = getVoiceLanguageLabel ?? GetEdgeVoiceLanguageLabel;

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
                    var matched = VoiceListBox.Items
                        .OfType<VoiceListItem>()
                        .FirstOrDefault(v => string.Equals(v.Name, current, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
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
                    .Where(v =>
                        v.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || GetVoiceLanguageLabel(v).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            VoiceListBox.ItemsSource = filtered
                .Select(v => new VoiceListItem(v, GetVoiceLanguageLabel(v)))
                .ToList();
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
            if (VoiceListBox.SelectedItem is VoiceListItem voice)
            {
                VoiceInputBox.Text = voice.Name;
            }
        }

        private async void OnPreviewClicked(object sender, RoutedEventArgs e)
        {
            var voice = VoiceInputBox.Text?.Trim() ?? string.Empty;
            var displayVoice = string.IsNullOrWhiteSpace(voice)
                ? L("EdgeTtsVoicePickerUseDefault", "Use Default (Auto)")
                : voice;

            try
            {
                SetBusyState(true, F("EdgeTtsVoicePickerPreviewing", "Previewing: {0}", displayVoice));
                await _previewVoiceAsync(voice);
                StatusText.Text = F("EdgeTtsVoicePickerPreviewFinished", "Preview finished: {0}", displayVoice);
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

        private string GetVoiceLanguageLabel(string voiceName)
        {
            var language = _getVoiceLanguageLabel(voiceName);
            return string.IsNullOrWhiteSpace(language)
                ? "默认适配：自动"
                : language;
        }

        public static string GetEdgeVoiceLanguageLabel(string voiceName)
        {
            var locale = TryGetEdgeLocale(voiceName);
            if (string.IsNullOrWhiteSpace(locale))
            {
                return "默认适配：自动";
            }

            var language = locale.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            return $"默认适配：{GetLanguageDisplayName(language, locale)}";
        }

        public static string GetQwenVoiceLanguageLabel(string voiceName)
        {
            return QwenTtsSpeakerCatalog.TryGetLanguageCode(voiceName, out var language)
                ? BuildLanguageLabel(language)
                : BuildLanguageLabel(string.Empty);
        }

        public static string GetMossVoiceLanguageLabel(string voiceName)
        {
            var normalized = (voiceName ?? string.Empty).Trim().ToLowerInvariant();
            var language = normalized switch
            {
                "trump" or "ava" or "bella" or "adam" or "nathan" => "en",
                "soyo" or "saki" or "mortis" or "umiri" or "mei" or "anon" or "arisa" => "ja",
                "sakura" or "yui" or "aoi" or "hina" => "ja",
                "junhao" or "zhiming" or "weiguo" or "xiaoyu" or "yuewen" or "lingyu" => "zh",
                _ => string.Empty
            };

            return BuildLanguageLabel(language);
        }

        public static string GetOmniVoiceVoiceLanguageLabel(string voiceName)
        {
            return !string.IsNullOrWhiteSpace(voiceName)
                ? "声音设计提示词"
                : BuildLanguageLabel(string.Empty);
        }

        private static string BuildLanguageLabel(string language)
        {
            return string.IsNullOrWhiteSpace(language)
                ? "默认适配：自动"
                : $"默认适配：{GetLanguageDisplayName(language)}";
        }

        private static string GetLanguageDisplayName(string language, string? locale = null)
        {
            var displayName = language.ToLowerInvariant() switch
            {
                "zh" => "中文",
                "en" => "英语",
                "ja" => "日语",
                "ko" => "韩语",
                "fr" => "法语",
                "de" => "德语",
                "es" => "西语",
                "pt" => "葡语",
                "ru" => "俄语",
                _ => language
            };

            return string.IsNullOrWhiteSpace(locale)
                ? displayName
                : $"{displayName} ({locale})";
        }

        private static string TryGetEdgeLocale(string voiceName)
        {
            var segments = (voiceName ?? string.Empty).Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2
                ? $"{segments[0]}-{segments[1]}"
                : string.Empty;
        }

        public sealed class VoiceListItem
        {
            public VoiceListItem(string name, string languageLabel)
            {
                Name = name;
                LanguageLabel = languageLabel;
            }

            public string Name { get; }

            public string LanguageLabel { get; }
        }
    }
}
