using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Closing += SettingsWindow_Closing;
        }

        private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Ensure active TextBox bindings commit before persisting.
            Focus();
            Keyboard.ClearFocus();

            if (DataContext is SettingsViewModel vm)
            {
                vm.PersistConfig();
            }
        }
    }
}
