using System;
using System.Windows.Media;

namespace VRCLS.UI.ViewModels
{
    public class TranscriptItem : ObservableObject
    {
        private string _source = "Mic";
        private string _original = string.Empty;
        private string _translation = string.Empty;
        private string _translation2 = string.Empty;
        private string _translation3 = string.Empty;
        private string _statusTag = string.Empty;
        private string _requestStage = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isRateLimited;
        private bool _isIncomplete;

        public string Source
        {
            get => _source;
            set => SetField(ref _source, value);
        }

        public string Original
        {
            get => _original;
            set => SetField(ref _original, value);
        }

        public string Translation
        {
            get => _translation;
            set => SetField(ref _translation, value);
        }

        public string Translation2
        {
            get => _translation2;
            set => SetField(ref _translation2, value);
        }

        public string Translation3
        {
            get => _translation3;
            set => SetField(ref _translation3, value);
        }

        public string StatusTag
        {
            get => _statusTag;
            set => SetField(ref _statusTag, value);
        }

        public string RequestStage
        {
            get => _requestStage;
            set => SetField(ref _requestStage, value);
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set => SetField(ref _timestamp, value);
        }

        public bool IsRateLimited
        {
            get => _isRateLimited;
            set => SetField(ref _isRateLimited, value);
        }

        public bool IsIncomplete
        {
            get => _isIncomplete;
            set => SetField(ref _isIncomplete, value);
        }
    }
}
