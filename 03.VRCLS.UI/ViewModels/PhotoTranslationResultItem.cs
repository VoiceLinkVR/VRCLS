using System;
using System.Linq;
using VRCLS.Core.Models;

namespace VRCLS.UI.ViewModels
{
    public sealed class PhotoTranslationResultItem : ObservableObject
    {
        private string _original = string.Empty;
        private string _translation = string.Empty;
        private string _translation2 = string.Empty;
        private string _translation3 = string.Empty;
        private string _sourceImagePath = string.Empty;
        private string _resultImagePath = string.Empty;
        private string _displayMode = AppConfig.DefaultPhotoTranslationMode;
        private DateTime _timestamp = DateTime.Now;

        public string Original
        {
            get => _original;
            set
            {
                if (SetField(ref _original, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Translation
        {
            get => _translation;
            set
            {
                if (SetField(ref _translation, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Translation2
        {
            get => _translation2;
            set
            {
                if (SetField(ref _translation2, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Translation3
        {
            get => _translation3;
            set
            {
                if (SetField(ref _translation3, value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string SourceImagePath
        {
            get => _sourceImagePath;
            set => SetField(ref _sourceImagePath, value);
        }

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                if (SetField(ref _resultImagePath, value))
                {
                    OnPropertyChanged(nameof(HasResultImage));
                    OnPropertyChanged(nameof(IsImageMode));
                    OnPropertyChanged(nameof(IsTextMode));
                }
            }
        }

        public string DisplayMode
        {
            get => _displayMode;
            set
            {
                var normalized = AppConfig.NormalizePhotoTranslationMode(value);
                if (SetField(ref _displayMode, normalized))
                {
                    OnPropertyChanged(nameof(IsImageMode));
                    OnPropertyChanged(nameof(IsTextMode));
                }
            }
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set => SetField(ref _timestamp, value);
        }

        public bool HasResultImage => !string.IsNullOrWhiteSpace(ResultImagePath);

        public bool IsImageMode => HasResultImage
            && string.Equals(DisplayMode, AppConfig.PhotoTranslationModeImage, StringComparison.OrdinalIgnoreCase);

        public bool IsTextMode => !IsImageMode;

        public string DisplayText
        {
            get
            {
                return string.Join(Environment.NewLine, new[]
                {
                    Translation,
                    Translation2,
                    Translation3,
                    Original
                }.Where(text => !string.IsNullOrWhiteSpace(text)));
            }
        }
    }
}
