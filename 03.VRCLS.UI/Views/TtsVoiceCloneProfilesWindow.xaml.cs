using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using VRCLS.Core.Models;
using VRCLS.UI.Services;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views
{
    public partial class TtsVoiceCloneProfilesWindow : Window
    {
        private readonly TtsVoiceClonePresetsEditorState _state;
        private readonly Func<CancellationToken, Task<IReadOnlyList<TtsVoiceClonePresetMarketItem>>>? _loadMarketAsync;
        private readonly Func<TtsVoiceClonePresetMarketItem, CancellationToken, Task<string>>? _downloadMarketPackageAsync;

        public IReadOnlyList<TtsVoiceClonePresetConfig> Presets { get; private set; } =
            Array.Empty<TtsVoiceClonePresetConfig>();

        public string SelectedPresetId { get; private set; } = string.Empty;

        public TtsVoiceCloneProfilesWindow(
            IEnumerable<TtsVoiceClonePresetConfig> presets,
            string selectedPresetId,
            string activeProvider,
            Func<CancellationToken, Task<IReadOnlyList<TtsVoiceClonePresetMarketItem>>>? loadMarketAsync = null,
            Func<TtsVoiceClonePresetMarketItem, CancellationToken, Task<string>>? downloadMarketPackageAsync = null)
        {
            InitializeComponent();
            _loadMarketAsync = loadMarketAsync;
            _downloadMarketPackageAsync = downloadMarketPackageAsync;
            _state = new TtsVoiceClonePresetsEditorState(presets, selectedPresetId, activeProvider);
            DataContext = _state;
        }

        private void OnAddPreset(object sender, RoutedEventArgs e)
        {
            _state.AddPreset();
        }

        private void OnDuplicatePreset(object sender, RoutedEventArgs e)
        {
            _state.DuplicateSelectedPreset();
        }

        private void OnRemovePreset(object sender, RoutedEventArgs e)
        {
            _state.RemoveSelectedPreset();
        }

        private void OnImportPreset(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入音色预设包",
                Filter = "VRCLS preset packages (*.zip;*.vrclspreset)|*.zip;*.vrclspreset|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                var preset = TtsVoiceClonePresetPackageService.ImportPresetPackage(
                    dialog.FileName,
                    _state.Presets.Select(presetItem => presetItem.Id));
                _state.ImportPreset(preset);
                _state.ValidationMessage = $"已导入音色预设：{preset.Name}";
            }
            catch (Exception ex)
            {
                _state.ValidationMessage = ex.Message;
            }
        }

        private void OnExportPreset(object sender, RoutedEventArgs e)
        {
            var preset = _state.SelectedPreset?.ToConfig();
            if (preset == null)
            {
                return;
            }

            var safeFileName = TtsVoiceClonePresetPackageService.NormalizePresetId(
                string.IsNullOrWhiteSpace(preset.Name) ? preset.Id : preset.Name);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                safeFileName = "voice-clone-preset";
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出音色预设包",
                Filter = "VRCLS preset package (*.zip)|*.zip|All files (*.*)|*.*",
                FileName = $"{safeFileName}.zip",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                TtsVoiceClonePresetPackageService.ExportPreset(preset, dialog.FileName);
                _state.ValidationMessage = $"已导出音色预设：{dialog.FileName}";
            }
            catch (Exception ex)
            {
                _state.ValidationMessage = ex.Message;
            }
        }

        private void OnOpenPresetMarket(object sender, RoutedEventArgs e)
        {
            if (_loadMarketAsync == null || _downloadMarketPackageAsync == null)
            {
                _state.ValidationMessage = "当前窗口没有连接预设市场服务。";
                return;
            }

            var window = new TtsVoiceClonePresetMarketWindow(_loadMarketAsync, _downloadMarketPackageAsync);
            if (NonTopmostPopupHelper.ShowDialog(window, this) != true
                || string.IsNullOrWhiteSpace(window.DownloadedPackagePath))
            {
                return;
            }

            try
            {
                var preset = TtsVoiceClonePresetPackageService.ImportPresetPackage(
                    window.DownloadedPackagePath,
                    _state.Presets.Select(presetItem => presetItem.Id));
                _state.ImportPreset(preset);
                _state.ValidationMessage = $"已安装市场音色预设：{preset.Name}";
            }
            catch (Exception ex)
            {
                _state.ValidationMessage = ex.Message;
            }
        }

        private void OnAddProfile(object sender, RoutedEventArgs e)
        {
            _state.SelectedPreset?.AddProfile(_state.ActiveProvider);
        }

        private void OnDuplicateProfile(object sender, RoutedEventArgs e)
        {
            _state.SelectedPreset?.DuplicateSelectedProfile();
        }

        private void OnRemoveProfile(object sender, RoutedEventArgs e)
        {
            _state.SelectedPreset?.RemoveSelectedProfile();
        }

        private void OnBrowseReferenceAudio(object sender, RoutedEventArgs e)
        {
            var profile = _state.SelectedPreset?.SelectedProfile;
            if (profile == null)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "选择参考音频文件",
                Filter = "Audio files (*.wav;*.mp3;*.flac;*.ogg;*.m4a;*.aac)|*.wav;*.mp3;*.flac;*.ogg;*.m4a;*.aac|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (!string.IsNullOrWhiteSpace(profile.ReferenceAudioPath))
            {
                try
                {
                    var fullPath = ResolveReferenceAudioPath(profile.ReferenceAudioPath);
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        dialog.InitialDirectory = directory;
                    }
                }
                catch
                {
                    // Ignore invalid persisted paths.
                }
            }

            if (dialog.ShowDialog(this) == true)
            {
                profile.ReferenceAudioPath = dialog.FileName;
            }
        }

        private void OnConfirm(object sender, RoutedEventArgs e)
        {
            if (!_state.TryValidate(out var errorMessage))
            {
                _state.ValidationMessage = errorMessage;
                return;
            }

            Presets = _state.BuildPresets();
            SelectedPresetId = _state.ResolveSelectedPresetId();
            DialogResult = true;
        }

        private sealed class TtsVoiceClonePresetsEditorState : ObservableObject
        {
            private int _nextPresetNumber = 1;
            private VoiceClonePresetEditorItem? _selectedPreset;
            private string _selectedPresetId;
            private string _validationMessage = string.Empty;

            public TtsVoiceClonePresetsEditorState(
                IEnumerable<TtsVoiceClonePresetConfig> presets,
                string selectedPresetId,
                string activeProvider)
            {
                ActiveProvider = NormalizeProvider(activeProvider);
                _selectedPresetId = selectedPresetId?.Trim() ?? string.Empty;
                LanguageOptions = new ObservableCollection<LanguageOption>(BuildLanguageOptions());
                Presets = new ObservableCollection<VoiceClonePresetEditorItem>(
                    (presets ?? Enumerable.Empty<TtsVoiceClonePresetConfig>())
                    .Select(preset => VoiceClonePresetEditorItem.FromConfig(preset, ActiveProvider)));

                _nextPresetNumber = Presets.Count + 1;
                if (Presets.Count == 0)
                {
                    AddPreset();
                }
                else
                {
                    SelectedPreset = Presets.FirstOrDefault(preset =>
                        string.Equals(preset.Id, _selectedPresetId, StringComparison.OrdinalIgnoreCase))
                        ?? Presets[0];
                }
            }

            public string ActiveProvider { get; }
            public ObservableCollection<LanguageOption> LanguageOptions { get; }
            public ObservableCollection<VoiceClonePresetEditorItem> Presets { get; }

            public VoiceClonePresetEditorItem? SelectedPreset
            {
                get => _selectedPreset;
                set
                {
                    if (SetField(ref _selectedPreset, value))
                    {
                        OnPropertyChanged(nameof(HasSelectedPreset));
                    }
                }
            }

            public bool HasSelectedPreset => SelectedPreset != null;

            public string ValidationMessage
            {
                get => _validationMessage;
                set => SetField(ref _validationMessage, value ?? string.Empty);
            }

            public void AddPreset()
            {
                var name = $"音色预设 {_nextPresetNumber++}";
                var item = new VoiceClonePresetEditorItem
                {
                    Id = CreateUniquePresetId(name, Presets.Select(preset => preset.Id)),
                    Enabled = true,
                    Name = name
                };

                item.AddProfile(ActiveProvider);
                Presets.Add(item);
                SelectedPreset = item;
                if (string.IsNullOrWhiteSpace(_selectedPresetId))
                {
                    _selectedPresetId = item.Id;
                }

                ValidationMessage = string.Empty;
            }

            public void DuplicateSelectedPreset()
            {
                if (SelectedPreset == null)
                {
                    return;
                }

                var clone = VoiceClonePresetEditorItem.FromConfig(SelectedPreset.ToConfig(), ActiveProvider);
                clone.Id = CreateUniquePresetId(clone.Name, Presets.Select(preset => preset.Id));
                clone.Name = string.IsNullOrWhiteSpace(clone.Name)
                    ? $"音色预设 {_nextPresetNumber++}"
                    : $"{clone.Name} 副本";
                Presets.Add(clone);
                SelectedPreset = clone;
                ValidationMessage = string.Empty;
            }

            public void RemoveSelectedPreset()
            {
                if (SelectedPreset == null)
                {
                    return;
                }

                var removeIndex = Presets.IndexOf(SelectedPreset);
                if (string.Equals(_selectedPresetId, SelectedPreset.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedPresetId = string.Empty;
                }

                Presets.Remove(SelectedPreset);
                SelectedPreset = Presets.Count == 0
                    ? null
                    : Presets[Math.Clamp(removeIndex, 0, Presets.Count - 1)];
                ValidationMessage = string.Empty;
            }

            public void ImportPreset(TtsVoiceClonePresetConfig preset)
            {
                var item = VoiceClonePresetEditorItem.FromConfig(preset, ActiveProvider);
                Presets.Add(item);
                SelectedPreset = item;
                _selectedPresetId = item.Id;
                ValidationMessage = string.Empty;
            }

            public bool TryValidate(out string errorMessage)
            {
                for (var presetIndex = 0; presetIndex < Presets.Count; presetIndex++)
                {
                    var preset = Presets[presetIndex];
                    var presetName = string.IsNullOrWhiteSpace(preset.Name)
                        ? $"第 {presetIndex + 1} 个预设"
                        : preset.Name.Trim();

                    if (!preset.Enabled)
                    {
                        continue;
                    }

                    if (preset.Profiles.Count == 0)
                    {
                        errorMessage = $"{presetName} 至少需要一条参考音频配置。";
                        return false;
                    }

                    if (!preset.Profiles.Any(profile => profile.Enabled))
                    {
                        errorMessage = $"{presetName} 至少需要一条启用的参考音频配置。";
                        return false;
                    }

                    if (!preset.Profiles.Any(profile => profile.Enabled && IsAutoTargetLanguage(profile.TargetLanguage)))
                    {
                        errorMessage = $"{presetName} 至少需要一条适用语言为“自动语言”的启用参考配置，作为默认声音。";
                        return false;
                    }

                    for (var profileIndex = 0; profileIndex < preset.Profiles.Count; profileIndex++)
                    {
                        var profile = preset.Profiles[profileIndex];
                        if (!profile.Enabled)
                        {
                            continue;
                        }

                        var displayName = $"{presetName} / 第 {profileIndex + 1} 条参考配置";
                        if (string.IsNullOrWhiteSpace(profile.TargetLanguage))
                        {
                            errorMessage = $"{displayName} 需要选择适用语言。";
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(profile.ReferenceAudioPath))
                        {
                            errorMessage = $"{displayName} 需要选择参考音频文件。";
                            return false;
                        }

                        var fullPath = ResolveReferenceAudioPath(profile.ReferenceAudioPath);
                        if (!File.Exists(fullPath))
                        {
                            errorMessage = $"{displayName} 的参考音频文件不存在。";
                            return false;
                        }

                        if (RequiresReferenceText(profile.Provider)
                            && string.IsNullOrWhiteSpace(profile.ReferenceText))
                        {
                            errorMessage = $"{displayName} 使用 Qwen-TTS 声音克隆时必须填写参考音频文本，保存前请补全。";
                            return false;
                        }
                    }
                }

                errorMessage = string.Empty;
                return true;
            }

            public IReadOnlyList<TtsVoiceClonePresetConfig> BuildPresets()
            {
                return Presets.Select(preset => preset.ToConfig()).ToList();
            }

            public string ResolveSelectedPresetId()
            {
                var selectedPresetId = SelectedPreset?.Id?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(selectedPresetId)
                    && Presets.Any(preset =>
                        preset.Enabled
                        && string.Equals(preset.Id, selectedPresetId, StringComparison.OrdinalIgnoreCase)))
                {
                    return selectedPresetId;
                }

                if (!string.IsNullOrWhiteSpace(_selectedPresetId)
                    && Presets.Any(preset =>
                        preset.Enabled
                        && string.Equals(preset.Id, _selectedPresetId, StringComparison.OrdinalIgnoreCase)))
                {
                    return _selectedPresetId;
                }

                return string.Empty;
            }

            private bool RequiresReferenceText(string provider)
            {
                var normalized = NormalizeProvider(provider);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    normalized = ActiveProvider;
                }

                return string.Equals(normalized, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsAutoTargetLanguage(string? language)
            {
                return string.Equals(NormalizeUiLanguage(language), "auto", StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class VoiceClonePresetEditorItem : ObservableObject
        {
            private string _id = string.Empty;
            private bool _enabled = true;
            private string _name = string.Empty;
            private VoiceCloneProfileEditorItem? _selectedProfile;

            public VoiceClonePresetEditorItem()
            {
                Profiles.CollectionChanged += OnProfilesCollectionChanged;
            }

            public string Id
            {
                get => _id;
                set => SetField(ref _id, value ?? string.Empty);
            }

            public bool Enabled
            {
                get => _enabled;
                set
                {
                    if (SetField(ref _enabled, value))
                    {
                        RefreshDisplayProperties();
                    }
                }
            }

            public string Name
            {
                get => _name;
                set
                {
                    if (SetField(ref _name, value ?? string.Empty))
                    {
                        RefreshDisplayProperties();
                    }
                }
            }

            public ObservableCollection<VoiceCloneProfileEditorItem> Profiles { get; } = new();

            public VoiceCloneProfileEditorItem? SelectedProfile
            {
                get => _selectedProfile;
                set
                {
                    if (SetField(ref _selectedProfile, value))
                    {
                        OnPropertyChanged(nameof(HasSelectedProfile));
                    }
                }
            }

            public bool HasSelectedProfile => SelectedProfile != null;

            public string DisplayName =>
                string.IsNullOrWhiteSpace(Name) ? "未命名预设" : Name.Trim();

            public string Summary
            {
                get
                {
                    var enabledText = Enabled ? "启用" : "停用";
                    return $"{enabledText} · {Profiles.Count} 条参考配置";
                }
            }

            public void AddProfile(string activeProvider)
            {
                var profile = new VoiceCloneProfileEditorItem
                {
                    Enabled = true,
                    Provider = string.IsNullOrWhiteSpace(activeProvider) ? string.Empty : activeProvider,
                    TargetLanguage = "auto",
                    CloneLanguage = "auto"
                };

                AddProfile(profile);
            }

            public void DuplicateSelectedProfile()
            {
                if (SelectedProfile == null)
                {
                    return;
                }

                AddProfile(VoiceCloneProfileEditorItem.FromConfig(SelectedProfile.ToConfig()).First());
            }

            public void RemoveSelectedProfile()
            {
                if (SelectedProfile == null)
                {
                    return;
                }

                var removeIndex = Profiles.IndexOf(SelectedProfile);
                Profiles.Remove(SelectedProfile);
                SelectedProfile = Profiles.Count == 0
                    ? null
                    : Profiles[Math.Clamp(removeIndex, 0, Profiles.Count - 1)];
                RefreshDisplayProperties();
            }

            public static VoiceClonePresetEditorItem FromConfig(TtsVoiceClonePresetConfig config, string activeProvider)
            {
                var item = new VoiceClonePresetEditorItem
                {
                    Id = config.Id ?? string.Empty,
                    Enabled = config.Enabled,
                    Name = config.Name ?? string.Empty
                };

                foreach (var profile in (config.Profiles ?? new List<TtsVoiceCloneProfileConfig>())
                    .SelectMany(VoiceCloneProfileEditorItem.FromConfig))
                {
                    item.AddProfile(profile);
                }

                if (item.Profiles.Count == 0)
                {
                    item.AddProfile(activeProvider);
                }
                else
                {
                    item.SelectedProfile = item.Profiles[0];
                }

                item.RefreshDisplayProperties();
                return item;
            }

            public TtsVoiceClonePresetConfig ToConfig()
            {
                return new TtsVoiceClonePresetConfig
                {
                    Id = Id.Trim(),
                    Enabled = Enabled,
                    Name = Name.Trim(),
                    Profiles = Profiles.Select(profile => profile.ToConfig()).ToList()
                };
            }

            private void AddProfile(VoiceCloneProfileEditorItem profile)
            {
                profile.PropertyChanged += OnProfilePropertyChanged;
                Profiles.Add(profile);
                SelectedProfile = profile;
                RefreshDisplayProperties();
            }

            private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.OldItems != null)
                {
                    foreach (VoiceCloneProfileEditorItem profile in e.OldItems)
                    {
                        profile.PropertyChanged -= OnProfilePropertyChanged;
                    }
                }

                RefreshDisplayProperties();
            }

            private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                RefreshDisplayProperties();
            }

            private void RefreshDisplayProperties()
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Summary));
            }
        }

        private sealed class VoiceCloneProfileEditorItem : ObservableObject
        {
            private bool _enabled = true;
            private string _provider = string.Empty;
            private string _targetLanguage = "auto";
            private string _referenceAudioPath = string.Empty;
            private string _referenceText = string.Empty;
            private string _cloneLanguage = "auto";

            public bool Enabled
            {
                get => _enabled;
                set
                {
                    if (SetField(ref _enabled, value))
                    {
                        RefreshDisplayProperties();
                    }
                }
            }

            public string Provider
            {
                get => _provider;
                set
                {
                    if (SetField(ref _provider, NormalizeProvider(value)))
                    {
                        RefreshDisplayProperties();
                    }
                }
            }

            public string TargetLanguage
            {
                get => _targetLanguage;
                set
                {
                    if (SetField(ref _targetLanguage, NormalizeUiLanguage(value)))
                    {
                        RefreshDisplayProperties();
                    }
                }
            }

            public string ReferenceAudioPath
            {
                get => _referenceAudioPath;
                set
                {
                    if (SetField(ref _referenceAudioPath, value ?? string.Empty))
                    {
                        RefreshDisplayProperties();
                    }
                }
            }

            public string ReferenceText
            {
                get => _referenceText;
                set => SetField(ref _referenceText, value ?? string.Empty);
            }

            public string CloneLanguage
            {
                get => _cloneLanguage;
                set => SetField(ref _cloneLanguage, NormalizeUiLanguage(value));
            }

            public string DisplayName =>
                $"{GetLanguageDisplayName(TargetLanguage)} · {GetProviderDisplayName(Provider)}";

            public string Summary
            {
                get
                {
                    var enabledText = Enabled ? "启用" : "停用";
                    var fileName = string.IsNullOrWhiteSpace(ReferenceAudioPath)
                        ? "未选择音频"
                        : Path.GetFileName(ReferenceAudioPath);
                    return $"{enabledText} · {fileName}";
                }
            }

            public static IReadOnlyList<VoiceCloneProfileEditorItem> FromConfig(TtsVoiceCloneProfileConfig config)
            {
                var targetLanguages = config.TargetLanguages == null || config.TargetLanguages.Count == 0
                    ? new List<string> { "auto" }
                    : config.TargetLanguages;

                var items = new List<VoiceCloneProfileEditorItem>();
                foreach (var targetLanguage in targetLanguages)
                {
                    items.Add(new VoiceCloneProfileEditorItem
                    {
                        Enabled = config.Enabled,
                        Provider = config.Provider ?? string.Empty,
                        TargetLanguage = NormalizeUiLanguage(targetLanguage),
                        ReferenceAudioPath = config.ReferenceAudioPath ?? string.Empty,
                        ReferenceText = config.ReferenceText ?? string.Empty,
                        CloneLanguage = string.IsNullOrWhiteSpace(config.CloneLanguage)
                            ? "auto"
                            : NormalizeUiLanguage(config.CloneLanguage)
                    });
                }

                return items;
            }

            public TtsVoiceCloneProfileConfig ToConfig()
            {
                return new TtsVoiceCloneProfileConfig
                {
                    Enabled = Enabled,
                    Provider = NormalizeProvider(Provider),
                    TargetLanguages = new List<string> { NormalizeConfigLanguage(TargetLanguage) },
                    ReferenceAudioPath = ReferenceAudioPath.Trim(),
                    ReferenceText = ReferenceText.Trim(),
                    CloneLanguage = NormalizeUiLanguage(CloneLanguage)
                };
            }

            private void RefreshDisplayProperties()
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Summary));
            }
        }

        private static IReadOnlyList<LanguageOption> BuildLanguageOptions()
        {
            var options = new List<LanguageOption>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string code, string displayName, bool appendCode = true)
            {
                var normalized = NormalizeUiLanguage(code);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    return;
                }

                var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
                    ? normalized
                    : displayName.Trim();
                if (appendCode
                    && !resolvedDisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedDisplayName = $"{resolvedDisplayName}（{normalized}）";
                }

                options.Add(new LanguageOption(normalized, resolvedDisplayName));
            }

            Add("auto", "自动语言", appendCode: false);
            Add("zh", "中文");
            Add("zh-cn", "中文（简体，中国）");
            Add("zh-tw", "中文（繁体，台湾）");
            Add("en", "英语");
            Add("en-us", "英语（美国）");
            Add("en-gb", "英语（英国）");
            Add("ja", "日语");
            Add("ja-jp", "日语（日本）");
            Add("ko", "韩语");
            Add("ko-kr", "韩语（韩国）");
            Add("yue", "粤语");
            Add("fr", "法语");
            Add("de", "德语");
            Add("es", "西班牙语");
            Add("pt", "葡萄牙语");
            Add("ru", "俄语");
            Add("it", "意大利语");
            Add("ar", "阿拉伯语");
            Add("hi", "印地语");
            Add("id", "印度尼西亚语");
            Add("th", "泰语");
            Add("vi", "越南语");
            Add("tr", "土耳其语");
            Add("pl", "波兰语");
            Add("nl", "荷兰语");
            Add("sv", "瑞典语");
            Add("uk", "乌克兰语");

            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
                .Where(culture => !string.IsNullOrWhiteSpace(culture.Name))
                .OrderBy(culture => culture.Name.Contains('-') ? 1 : 0)
                .ThenBy(culture => culture.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase))
            {
                Add(culture.Name, culture.DisplayName);
            }

            return options;
        }

        private static string NormalizeProvider(string? value)
        {
            var provider = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(provider)
                || string.Equals(provider, "*", StringComparison.Ordinal)
                || string.Equals(provider, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider, "default", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return provider.ToLowerInvariant() switch
            {
                AppConfig.TtsProviderQwen => AppConfig.TtsProviderQwen,
                AppConfig.TtsProviderMoss or "moss-tts" or "smoss" or "smoss-tts" => AppConfig.TtsProviderMoss,
                AppConfig.TtsProviderOmniVoice or "omni" or "omni-voice" or "omni_voice" => AppConfig.TtsProviderOmniVoice,
                _ => string.Empty
            };
        }

        private static string NormalizeUiLanguage(string? language)
        {
            var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "*", StringComparison.Ordinal)
                || string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "auto";
            }

            return normalized switch
            {
                "jp" => "ja",
                "kr" => "ko",
                "cn" => "zh",
                _ => normalized
            };
        }

        private static string NormalizeConfigLanguage(string? language)
        {
            var normalized = NormalizeUiLanguage(language);
            return string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
                ? "*"
                : normalized;
        }

        private static string ResolveReferenceAudioPath(string path)
        {
            var trimmed = path.Trim();
            return Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
        }

        private static string GetProviderDisplayName(string provider)
        {
            return NormalizeProvider(provider) switch
            {
                AppConfig.TtsProviderQwen => "Qwen-TTS",
                AppConfig.TtsProviderMoss => "MOSS-TTS",
                AppConfig.TtsProviderOmniVoice => "OmniVoice-TTS",
                _ => "当前服务商"
            };
        }

        private static string GetLanguageDisplayName(string language)
        {
            var normalized = NormalizeUiLanguage(language);
            if (string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "自动语言";
            }

            return BuildLanguageOptions().FirstOrDefault(option =>
                    string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? normalized;
        }

        private static string CreateUniquePresetId(string name, IEnumerable<string> usedIds)
        {
            var normalized = NormalizePresetId(name);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "preset";
            }

            var used = new HashSet<string>(usedIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var candidate = normalized;
            var index = 2;
            while (used.Contains(candidate))
            {
                candidate = $"{normalized}-{index++}";
            }

            return candidate;
        }

        private static string NormalizePresetId(string value)
        {
            var builder = new StringBuilder();
            foreach (var ch in (value ?? string.Empty).Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else if (ch == '-' || ch == '_' || ch == '.')
                {
                    builder.Append(ch);
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    builder.Append('-');
                }
            }

            return builder.ToString().Trim('-', '_', '.');
        }
    }
}
