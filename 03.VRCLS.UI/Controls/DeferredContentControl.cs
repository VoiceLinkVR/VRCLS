using System.Windows;
using System.Windows.Controls;

namespace VRCLS.UI.Controls
{
    /// <summary>
    /// Defers heavy content creation until the placeholder itself is first loaded.
    /// In a TabControl this means the selected tab creates its content immediately,
    /// while other tabs wait until the user opens them.
    /// </summary>
    public sealed class DeferredContentControl : ContentControl
    {
        public static readonly DependencyProperty DeferredContentTemplateProperty =
            DependencyProperty.Register(
                nameof(DeferredContentTemplate),
                typeof(DataTemplate),
                typeof(DeferredContentControl),
                new PropertyMetadata(null, OnDeferredContentTemplateChanged));

        public static readonly DependencyProperty IsContentActivatedProperty =
            DependencyProperty.Register(
                nameof(IsContentActivated),
                typeof(bool),
                typeof(DeferredContentControl),
                new PropertyMetadata(false, OnIsContentActivatedChanged));

        private bool _contentCreated;

        public DeferredContentControl()
        {
            Loaded += (_, _) => EnsureContentCreated();
            DataContextChanged += (_, _) =>
            {
                if (_contentCreated)
                {
                    Content = DataContext;
                }
            };
        }

        public DataTemplate? DeferredContentTemplate
        {
            get => (DataTemplate?)GetValue(DeferredContentTemplateProperty);
            set => SetValue(DeferredContentTemplateProperty, value);
        }

        public bool IsContentActivated
        {
            get => (bool)GetValue(IsContentActivatedProperty);
            set => SetValue(IsContentActivatedProperty, value);
        }

        private static void OnDeferredContentTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((DeferredContentControl)d).EnsureContentCreated();
        }

        private static void OnIsContentActivatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((DeferredContentControl)d).EnsureContentCreated();
        }

        private void EnsureContentCreated()
        {
            if (_contentCreated || DeferredContentTemplate == null)
            {
                return;
            }

            // TabControl does not load inactive tab content into the visual tree,
            // so IsLoaded is a reliable lazy-load gate and avoids fragile IsSelected bindings.
            if (!IsLoaded && !IsContentActivated)
            {
                return;
            }

            ContentTemplate = DeferredContentTemplate;
            Content = DataContext;
            _contentCreated = true;
        }
    }
}
