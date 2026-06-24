using System.Windows;
using System.Windows.Controls;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views.SettingsTabs
{
    public partial class TranslationTab : UserControl
    {
        public TranslationTab()
        {
            InitializeComponent();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
            {
                OpenAiApiKeyBox.Password = vm.OpenAiApiKey;
            }
        }

        private void OnOpenAiApiKeyLostFocus(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && sender is PasswordBox passwordBox)
            {
                vm.OpenAiApiKey = passwordBox.Password;
            }
        }
    }
}
