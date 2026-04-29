using System;
using System.Windows;
using System.Windows.Controls;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views.SettingsTabs
{
    public partial class UserCenterTab : UserControl
    {
        private SettingsViewModel? _viewModel;

        public UserCenterTab()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachViewModel(DataContext as SettingsViewModel);
            SyncPasswordFromViewModel();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            AttachViewModel(null);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachViewModel(e.NewValue as SettingsViewModel);
            SyncPasswordFromViewModel();
        }

        private void AttachViewModel(SettingsViewModel? nextViewModel)
        {
            if (ReferenceEquals(_viewModel, nextViewModel))
            {
                return;
            }

            if (_viewModel != null)
            {
                _viewModel.PasswordUpdated -= OnPasswordUpdated;
            }

            _viewModel = nextViewModel;

            if (_viewModel != null)
            {
                _viewModel.PasswordUpdated += OnPasswordUpdated;
            }
        }

        private void OnPasswordUpdated(string password)
        {
            Dispatcher.Invoke(() => PasswordBox.Password = password);
        }

        private void SyncPasswordFromViewModel()
        {
            if (_viewModel != null)
            {
                PasswordBox.Password = _viewModel.Password;
            }
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null && sender is PasswordBox passwordBox)
            {
                _viewModel.Password = passwordBox.Password;
            }
        }
    }
}
