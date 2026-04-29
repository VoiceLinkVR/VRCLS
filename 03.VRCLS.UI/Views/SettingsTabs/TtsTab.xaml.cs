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

            var picker = new EdgeTtsVoicePickerWindow(
                vm.TtsVoiceName,
                vm.PreviewTtsVoiceAsync,
                vm.IsQwenTtsProvider
                    ? () => QwenTtsVoiceCatalogService.GetVoicesAsync()
                    : () => EdgeTtsVoiceCatalogService.GetVoicesAsync());

            if (NonTopmostPopupHelper.ShowDialog(picker, Window.GetWindow(this)) == true)
            {
                vm.TtsVoiceName = picker.SelectedVoiceName;
            }
        }
    }
}
