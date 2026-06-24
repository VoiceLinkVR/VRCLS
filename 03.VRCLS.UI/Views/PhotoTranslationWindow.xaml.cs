using System.Windows;
using System.Windows.Input;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views
{
    public partial class PhotoTranslationWindow : Window
    {
        public PhotoTranslationWindow()
        {
            InitializeComponent();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is MainViewModel viewModel
                && TryHandlePhotoNavigationKey(viewModel, e.Key))
            {
                e.Handled = true;
                return;
            }

            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (DataContext is not MainViewModel pasteViewModel
                || !pasteViewModel.PastePhotoTranslationImageCommand.CanExecute(null))
            {
                return;
            }

            pasteViewModel.PastePhotoTranslationImageCommand.Execute(null);
            e.Handled = true;
        }

        private static bool TryHandlePhotoNavigationKey(MainViewModel viewModel, Key key)
        {
            var command = key switch
            {
                Key.Left => viewModel.PreviousPhotoTranslationResultCommand,
                Key.Right => viewModel.NextPhotoTranslationResultCommand,
                _ => null
            };

            if (command == null || !command.CanExecute(null))
            {
                return false;
            }

            command.Execute(null);
            return true;
        }
    }
}
