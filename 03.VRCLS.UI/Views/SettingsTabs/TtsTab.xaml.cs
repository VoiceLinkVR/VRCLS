using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VRCLS.UI.Services;
using VRCLS.UI.ViewModels;
using VRCLS.UI.Views;

namespace VRCLS.UI.Views.SettingsTabs
{
    public partial class TtsTab : UserControl
    {
        public TtsTab()
        {
            InitializeComponent();
        }

        private void OnOpenEdgeTtsVoicePicker(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
            {
                return;
            }

            OpenTtsVoicePicker(
                vm,
                vm.TtsVoiceName,
                value => vm.TtsVoiceName = value,
                () => EdgeTtsVoiceCatalogService.GetVoicesAsync(),
                EdgeTtsVoicePickerWindow.GetEdgeVoiceLanguageLabel);
        }

        private void OnOpenQwenTtsVoicePicker(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
            {
                return;
            }

            OpenTtsVoicePicker(
                vm,
                vm.QwenTtsVoiceName,
                value => vm.QwenTtsVoiceName = value,
                () => vm.GetQwenTtsVoicesAsync(),
                EdgeTtsVoicePickerWindow.GetQwenVoiceLanguageLabel);
        }

        private void OnOpenMossTtsVoicePicker(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
            {
                return;
            }

            OpenTtsVoicePicker(
                vm,
                vm.MossTtsVoiceName,
                value => vm.MossTtsVoiceName = value,
                () => vm.GetMossTtsVoicesAsync(),
                EdgeTtsVoicePickerWindow.GetMossVoiceLanguageLabel);
        }

        private void OnOpenOmniVoiceTtsVoicePicker(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
            {
                return;
            }

            OpenTtsVoicePicker(
                vm,
                vm.OmniVoiceTtsVoiceName,
                value => vm.OmniVoiceTtsVoiceName = value,
                () => vm.GetOmniVoiceTtsVoicesAsync(),
                EdgeTtsVoicePickerWindow.GetOmniVoiceVoiceLanguageLabel);
        }

        private void OnOpenTtsVoiceCloneProfiles(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
            {
                return;
            }

            var window = new TtsVoiceCloneProfilesWindow(
                vm.GetTtsVoiceClonePresetsSnapshot(),
                vm.SelectedTtsVoiceClonePresetId,
                vm.TtsProvider,
                vm.GetTtsVoiceClonePresetMarketAsync,
                vm.DownloadTtsVoiceClonePresetPackageAsync);

            if (NonTopmostPopupHelper.ShowDialog(window, Window.GetWindow(this)) == true)
            {
                vm.ReplaceTtsVoiceClonePresets(window.Presets, window.SelectedPresetId);
            }
        }

        private void OpenTtsVoicePicker(
            SettingsViewModel vm,
            string currentVoiceName,
            Action<string> applyVoiceName,
            Func<Task<IReadOnlyList<string>>> loadVoicesAsync,
            Func<string, string> getVoiceLanguageLabel)
        {
            var picker = new EdgeTtsVoicePickerWindow(
                currentVoiceName,
                vm.PreviewTtsVoiceAsync,
                loadVoicesAsync,
                getVoiceLanguageLabel);

            if (NonTopmostPopupHelper.ShowDialog(picker, Window.GetWindow(this)) == true)
            {
                applyVoiceName(picker.SelectedVoiceName);
            }
        }

    }
}
