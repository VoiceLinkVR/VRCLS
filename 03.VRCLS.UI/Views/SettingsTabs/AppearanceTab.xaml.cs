using System.Windows;
using System.Windows.Controls;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views.SettingsTabs
{
    public partial class AppearanceTab : UserControl
    {
        public AppearanceTab()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.ActivateRuntimeLogView();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.DeactivateRuntimeLogView();
            }
        }
    }
}
