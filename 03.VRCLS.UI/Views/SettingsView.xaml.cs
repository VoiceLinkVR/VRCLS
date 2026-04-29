using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VRCLS.UI.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var comboBox = FindAncestor<ComboBox>(e.OriginalSource as DependencyObject);
            if (comboBox == null || comboBox.IsDropDownOpen)
            {
                return;
            }

            e.Handled = true;

            var scrollViewer = FindAncestor<ScrollViewer>(comboBox);
            if (scrollViewer == null)
            {
                return;
            }

            var forwardedEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = comboBox
            };

            scrollViewer.RaiseEvent(forwardedEvent);
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
