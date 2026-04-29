using System;
using System.Windows;
using System.Windows.Controls;
using VRCLS.UI.Services;
using VRCLS.UI.ViewModels;
using VRCLS.UI.Views;

namespace VRCLS.UI.Views.SettingsTabs
{
    public partial class RecognitionTab : UserControl
    {
        public RecognitionTab()
        {
            InitializeComponent();
        }

        private void OnOpenMicVadCalibration(object sender, RoutedEventArgs e)
        {
            OpenVadCalibrationWindow("Microphone", "Mic", vm => vm.MicVadMinSilenceDuration, (vm, value) => vm.MicVadMinSilenceDuration = value, vm => vm.MicVadThreshold, (vm, value) => vm.MicVadThreshold = value);
        }

        private void OnOpenDesktopVadCalibration(object sender, RoutedEventArgs e)
        {
            OpenVadCalibrationWindow("Desktop Audio", "Desktop", vm => vm.DesktopVadMinSilenceDuration, (vm, value) => vm.DesktopVadMinSilenceDuration = value, vm => vm.DesktopVadThreshold, (vm, value) => vm.DesktopVadThreshold = value);
        }

        private void OpenVadCalibrationWindow(string routeDisplayName, string source, Func<SettingsViewModel, double> getMinSilenceDuration, Action<SettingsViewModel, double> setMinSilenceDuration, Func<SettingsViewModel, double> getThreshold, Action<SettingsViewModel, double> setThreshold)
        {
            if (DataContext is not SettingsViewModel vm)
            {
                return;
            }

            var calibrationService = App.GetRequiredService<VadCalibrationService>();
            var window = new VadCalibrationWindow(routeDisplayName, source, getMinSilenceDuration(vm), getThreshold(vm), calibrationService);

            if (NonTopmostPopupHelper.ShowDialog(window, Window.GetWindow(this)) == true)
            {
                setMinSilenceDuration(vm, window.SelectedMinSilenceDuration);
                setThreshold(vm, window.SelectedThreshold);
            }
        }
    }
}
