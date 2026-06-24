using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views
{
    public partial class UserCenterDonationWizardWindow : Window
    {
        private readonly SettingsViewModel _viewModel;

        public UserCenterDonationWizardWindow(SettingsViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            DataContext = viewModel;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            DonationWizardPasswordBox.Password = _viewModel.DonationPassword;
        }

        private void OnDonationPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                _viewModel.DonationPassword = passwordBox.Password;
            }
        }

        private void OnCloseWindow(object sender, RoutedEventArgs e)
        {
            if (_viewModel.CanCloseDonationWizard)
            {
                Close();
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!_viewModel.CanCloseDonationWizard)
            {
                e.Cancel = true;
            }
        }
    }
}
