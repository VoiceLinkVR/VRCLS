using System;
using System.Collections.Generic;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Recognition
{
    public sealed partial class SenseVoiceAsrService : IRecognitionService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<SenseVoiceAsrService>();
        private readonly object _recognizerLock = new();
        private readonly IOfflineSenseVoiceRecognizer _recognizer;
        private readonly string _source;
        private bool _isRunning;
        private bool _disposed;

        public event EventHandler<TranscriptionResult>? TextRecognized;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        internal SenseVoiceAsrService(
            IOfflineSenseVoiceRecognizer recognizer,
            string source = "Mic")
        {
            _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
            _source = source;
        }

        public bool IsRunning
        {
            get
            {
                lock (_recognizerLock)
                {
                    return !_disposed && _isRunning;
                }
            }
        }

        public void Start()
        {
            lock (_recognizerLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _isRunning = true;
            }
        }

        public void Stop()
        {
            lock (_recognizerLock)
            {
                _isRunning = false;
            }
        }

        public void ProcessAudio(
            float[] audioData,
            RecognitionSegmentInfo? segmentInfo = null)
        {
            lock (_recognizerLock)
            {
                if (_disposed || !_isRunning || audioData == null || audioData.Length == 0)
                {
                    return;
                }

                try
                {
                    var text = _recognizer.Decode(audioData).Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    var metadata = segmentInfo ?? RecognitionSegmentInfo.Empty;
                    TextRecognized?.Invoke(this, new TranscriptionResult
                    {
                        Text = text,
                        Original = text,
                        IsFinal = true,
                        Confidence = 1.0f,
                        Source = _source,
                        SegmentId = metadata.SegmentId,
                        CapturedAtUtc = metadata.CapturedAtUtc
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "SenseVoice recognition failed.");
                    ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                    {
                        Code = ServiceErrorCode.RecognitionFailed,
                        Context = new Dictionary<string, string>
                        {
                            ["Reason"] = ex.Message
                        },
                        Exception = ex
                    });
                }
            }
        }

        public void FinishSegment()
        {
            // ProcessAudio receives complete VAD segments for offline recognition.
        }

        public void Dispose()
        {
            lock (_recognizerLock)
            {
                if (_disposed)
                {
                    return;
                }

                _isRunning = false;
                _recognizer.Dispose();
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}
