using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using VRCLS.Core.Models;
using VRCLS.UI.ViewModels;

namespace VRCLS.UI.Views
{
    public partial class MainWindow : Window
    {
        private static readonly ILogger Logger = Log.ForContext<MainWindow>();
        private static readonly Brush MicGuideBrush = CreateBrush(0x6B, 0xE5, 0xFF);
        private static readonly Brush DesktopGuideBrush = CreateBrush(0xFF, 0xB3, 0x47);
        private static readonly Brush TranscriptGuideBrush = CreateBrush(0x6F, 0xF7, 0xD7);
        private static readonly Brush ManualGuideBrush = CreateBrush(0x88, 0xA9, 0xFF);
        private static readonly Brush SettingsGuideBrush = CreateBrush(0xFF, 0xA6, 0x6B);
        private readonly AppConfig _config;
        private readonly int _startupOverlayWidth;
        private readonly int _startupOverlayHeight;
        private MainViewModel? _viewModel;
        private bool _quickStartPresentationUpdateQueued;
        private QuickStartPresentationSnapshot? _lastQuickStartPresentationSnapshot;

        public MainWindow(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _startupOverlayWidth = _config.OverlayWidth;
            _startupOverlayHeight = _config.OverlayHeight;
            InitializeComponent();
            Loaded += OnWindowLoaded;
            Unloaded += OnWindowUnloaded;
            SizeChanged += OnGuideLayoutChanged;
            LayoutUpdated += OnGuideLayoutUpdated;
            DataContextChanged += OnDataContextChanged;
            ApplyWindowBoundsFromConfig();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveWindowBoundsToConfig();
            Logger.Information(
                "MainWindow Closing. ProcessId={ProcessId}, MainWindowHandle={Handle}, WindowState={WindowState}, IsVisible={IsVisible}",
                Environment.ProcessId,
                new IntPtr(new WindowInteropHelper(this).Handle.ToInt64()),
                WindowState,
                IsVisible);
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            var currentProcess = Process.GetCurrentProcess();
            Logger.Information(
                "MainWindow Closed. ProcessId={ProcessId}, UptimeSeconds={UptimeSeconds:F1}",
                Environment.ProcessId,
                (DateTime.Now - currentProcess.StartTime).TotalSeconds);
            base.OnClosed(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ButtonState == MouseButtonState.Pressed && WindowStyle == WindowStyle.None)
            {
                DragMove();
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && WindowStyle == WindowStyle.None)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void ToggleMaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            AttachViewModel(DataContext as MainViewModel);
            QueueQuickStartGuidePresentationUpdate();
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            AttachViewModel(null);
            Loaded -= OnWindowLoaded;
            Unloaded -= OnWindowUnloaded;
            SizeChanged -= OnGuideLayoutChanged;
            LayoutUpdated -= OnGuideLayoutUpdated;
            DataContextChanged -= OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachViewModel(e.NewValue as MainViewModel);
            QueueQuickStartGuidePresentationUpdate();
        }

        private void AttachViewModel(MainViewModel? viewModel)
        {
            if (ReferenceEquals(_viewModel, viewModel))
            {
                return;
            }

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = viewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) &&
                e.PropertyName != nameof(MainViewModel.IsQuickStartGuideVisible) &&
                e.PropertyName != nameof(MainViewModel.QuickStartStep))
            {
                return;
            }

            QueueQuickStartGuidePresentationUpdate();
        }

        private void OnGuideLayoutChanged(object? sender, EventArgs e)
        {
            if (_viewModel?.IsQuickStartGuideVisible == true)
            {
                QueueQuickStartGuidePresentationUpdate();
            }
        }

        private void OnGuideLayoutUpdated(object? sender, EventArgs e)
        {
            if (_viewModel?.IsQuickStartGuideVisible == true)
            {
                QueueQuickStartGuidePresentationUpdate();
            }
        }

        private void QueueQuickStartGuidePresentationUpdate()
        {
            if (_quickStartPresentationUpdateQueued)
            {
                return;
            }

            _quickStartPresentationUpdateQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _quickStartPresentationUpdateQueued = false;
                UpdateQuickStartGuidePresentation();
            }), DispatcherPriority.Render);
        }

        private void UpdateQuickStartGuidePresentation()
        {
            if (!IsLoaded || MainLayoutRoot == null || QuickStartMaskPath == null)
            {
                return;
            }

            var rootRect = new Rect(0, 0, MainLayoutRoot.ActualWidth, MainLayoutRoot.ActualHeight);
            if (rootRect.Width <= 0 || rootRect.Height <= 0)
            {
                return;
            }

            var highlights = GetQuickStartHighlights(rootRect);
            var snapshot = new QuickStartPresentationSnapshot(
                rootRect.Size,
                _viewModel?.IsQuickStartGuideVisible == true ? _viewModel.QuickStartStep : 0,
                highlights);

            if (_lastQuickStartPresentationSnapshot?.Matches(snapshot) == true)
            {
                return;
            }

            _lastQuickStartPresentationSnapshot = snapshot;

            var maskGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            maskGeometry.Children.Add(new RectangleGeometry(rootRect));

            foreach (var highlight in highlights)
            {
                maskGeometry.Children.Add(new RectangleGeometry(highlight.Bounds, highlight.RadiusX, highlight.RadiusY));
            }

            QuickStartMaskPath.Data = maskGeometry;
            ApplyHighlightBorder(QuickStartPrimaryHighlight, highlights.Count > 0 ? highlights[0] : null);
            ApplyHighlightBorder(QuickStartSecondaryHighlight, highlights.Count > 1 ? highlights[1] : null);
        }

        private void ApplyHighlightBorder(Border border, GuideHighlight? highlight)
        {
            if (highlight == null)
            {
                border.Visibility = Visibility.Collapsed;
                border.Width = 0;
                border.Height = 0;
                return;
            }

            border.Visibility = Visibility.Visible;
            border.BorderBrush = highlight.BorderBrush;
            border.CornerRadius = highlight.CornerRadius;
            border.Width = Math.Max(0, highlight.Bounds.Width);
            border.Height = Math.Max(0, highlight.Bounds.Height);
            Canvas.SetLeft(border, highlight.Bounds.Left);
            Canvas.SetTop(border, highlight.Bounds.Top);
        }

        private List<GuideHighlight> GetQuickStartHighlights(Rect rootRect)
        {
            var highlights = new List<GuideHighlight>();
            if (_viewModel?.IsQuickStartGuideVisible != true)
            {
                return highlights;
            }

            switch (_viewModel.QuickStartStep)
            {
                case 2:
                    AddHighlight(highlights, MicGuideAnchor, rootRect, 6, MicGuideBrush, new CornerRadius(18), 18, 18);
                    AddHighlight(highlights, DesktopGuideAnchor, rootRect, 6, DesktopGuideBrush, new CornerRadius(18), 18, 18);
                    break;
                case 3:
                    AddHighlight(highlights, TranscriptGuideAnchor, rootRect, 6, TranscriptGuideBrush, new CornerRadius(20), 20, 20);
                    break;
                case 4:
                    AddHighlight(highlights, ManualGuideAnchor, rootRect, 5, ManualGuideBrush, new CornerRadius(10), 10, 10);
                    break;
                case 5:
                    AddHighlight(highlights, SettingsGuideAnchor, rootRect, 6, SettingsGuideBrush, new CornerRadius(14), 14, 14);
                    break;
            }

            return highlights;
        }

        private void AddHighlight(
            ICollection<GuideHighlight> highlights,
            FrameworkElement element,
            Rect rootRect,
            double inflate,
            Brush borderBrush,
            CornerRadius cornerRadius,
            double radiusX,
            double radiusY)
        {
            if (element == null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return;
            }

            try
            {
                var topLeft = element.TransformToAncestor(MainLayoutRoot).Transform(new Point(0, 0));
                var bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
                bounds.Inflate(inflate, inflate);
                bounds = Rect.Intersect(bounds, rootRect);
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                {
                    highlights.Add(new GuideHighlight(bounds, borderBrush, cornerRadius, radiusX, radiusY));
                }
            }
            catch (InvalidOperationException)
            {
                // Layout can transiently be unavailable while visual tree is updating.
            }
        }

        private void ApplyWindowBoundsFromConfig()
        {
            if (_config.OverlayWidth > 0)
            {
                Width = Math.Max(MinWidth, _config.OverlayWidth);
            }

            if (_config.OverlayHeight > 0)
            {
                Height = Math.Max(MinHeight, _config.OverlayHeight);
            }

            if (!IsFinite(_config.MainWindowLeft) || !IsFinite(_config.MainWindowTop))
            {
                return;
            }

            var left = _config.MainWindowLeft!.Value;
            var top = _config.MainWindowTop!.Value;
            if (!IsVisibleOnAnyScreen(left, top))
            {
                return;
            }

            Left = left;
            Top = top;
        }

        private void SaveWindowBoundsToConfig()
        {
            try
            {
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

                // If the user changed the configured default size in Settings during this run,
                // preserve that explicit value so it can take effect on the next startup.
                var configuredStartupSizeChanged =
                    _config.OverlayWidth != _startupOverlayWidth ||
                    _config.OverlayHeight != _startupOverlayHeight;

                if (!configuredStartupSizeChanged && IsFinite(bounds.Width) && bounds.Width > 0)
                {
                    _config.OverlayWidth = (int)Math.Round(Math.Max(MinWidth, bounds.Width));
                }

                if (!configuredStartupSizeChanged && IsFinite(bounds.Height) && bounds.Height > 0)
                {
                    _config.OverlayHeight = (int)Math.Round(Math.Max(MinHeight, bounds.Height));
                }

                if (IsFinite(bounds.Left) && IsFinite(bounds.Top))
                {
                    _config.MainWindowLeft = bounds.Left;
                    _config.MainWindowTop = bounds.Top;
                }

                App.SaveConfigToDisk(_config);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to persist main window bounds.");
            }
        }

        private static bool IsVisibleOnAnyScreen(double left, double top)
        {
            const double tolerance = 64;
            var minLeft = SystemParameters.VirtualScreenLeft - tolerance;
            var minTop = SystemParameters.VirtualScreenTop - tolerance;
            var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - tolerance;
            var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - tolerance;

            return left >= minLeft
                && left <= maxLeft
                && top >= minTop
                && top <= maxTop;
        }

        private static bool IsFinite(double? value)
        {
            return value.HasValue && IsFinite(value.Value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private sealed class GuideHighlight
        {
            public GuideHighlight(Rect bounds, Brush borderBrush, CornerRadius cornerRadius, double radiusX, double radiusY)
            {
                Bounds = bounds;
                BorderBrush = borderBrush;
                CornerRadius = cornerRadius;
                RadiusX = radiusX;
                RadiusY = radiusY;
            }

            public Rect Bounds { get; }

            public Brush BorderBrush { get; }

            public CornerRadius CornerRadius { get; }

            public double RadiusX { get; }

            public double RadiusY { get; }
        }

        private sealed class QuickStartPresentationSnapshot
        {
            private readonly Size _rootSize;
            private readonly int _step;
            private readonly Rect[] _highlightBounds;

            public QuickStartPresentationSnapshot(Size rootSize, int step, IReadOnlyList<GuideHighlight> highlights)
            {
                _rootSize = rootSize;
                _step = step;
                _highlightBounds = new Rect[highlights.Count];
                for (var i = 0; i < highlights.Count; i++)
                {
                    _highlightBounds[i] = highlights[i].Bounds;
                }
            }

            public bool Matches(QuickStartPresentationSnapshot other)
            {
                if (other == null ||
                    _step != other._step ||
                    !AreClose(_rootSize.Width, other._rootSize.Width) ||
                    !AreClose(_rootSize.Height, other._rootSize.Height) ||
                    _highlightBounds.Length != other._highlightBounds.Length)
                {
                    return false;
                }

                for (var i = 0; i < _highlightBounds.Length; i++)
                {
                    if (!AreClose(_highlightBounds[i].Left, other._highlightBounds[i].Left) ||
                        !AreClose(_highlightBounds[i].Top, other._highlightBounds[i].Top) ||
                        !AreClose(_highlightBounds[i].Width, other._highlightBounds[i].Width) ||
                        !AreClose(_highlightBounds[i].Height, other._highlightBounds[i].Height))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool AreClose(double left, double right)
            {
                return Math.Abs(left - right) < 0.5;
            }
        }
    }
}
