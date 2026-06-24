using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Audio
{
    public class AudioPipelineService : IAudioService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<AudioPipelineService>();

        private readonly IAudioService _audioService;
        private readonly IVadService? _vadService;
        private readonly IRecognitionService _recognitionService;
        private readonly IRecognitionService? _controlRecognitionService;
        private readonly bool _ownsRecognitionService;
        private readonly bool _ownsControlRecognitionService;
        private readonly string _sourceName;

        private readonly Pipe _audioPipe;
        private readonly Channel<RecognitionWorkItem> _recognitionChannel;

        private Task? _processingTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _recognitionTask;
        private CancellationTokenSource? _recognitionCts;

        private readonly bool _useVad;
        private readonly bool _useOfficialVadPaddingOnly;
        private bool _stopping;
        private bool _disposed;
        private bool _vadMeasurementForwardingAttached;

        private readonly object _processLock = new();
        private readonly object _drainDisableLock = new();
        private readonly object _taskLock = new();
        private readonly object _vadMeasurementLock = new();
        private readonly object _pendingVadSegmentLock = new();

        private CancellationTokenSource? _pendingDrainDisableCts;
        private CancellationTokenSource? _pendingVadSegmentFlushCts;

        private static readonly TimeSpan DrainDisableTimeout = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan StreamingDrainDisableTimeout = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan StreamingStartPreRollDuration = TimeSpan.FromMilliseconds(260);
        private static readonly TimeSpan VadSegmentMergeWindow = TimeSpan.FromMilliseconds(250);
        private static readonly float[] DrainFlushSilence = new float[16000];
        private static readonly TimeSpan DisposeTaskWaitTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan NoFrameUnhealthyThreshold = TimeSpan.FromSeconds(5);
        private static readonly int MaxConsecutiveFrameProcessingFailures = 3;
        private static readonly int MaxConsecutivePipeProcessingFailures = 3;

        private const int StreamingSampleRate = 16000;
        private const int MaxPendingAudioFrames = 24;
        private const int MinVadRecognitionSegmentSamples = (int)(StreamingSampleRate * 0.10);
        private static readonly int StreamingStartPreRollMaxSamples = (int)(StreamingSampleRate * StreamingStartPreRollDuration.TotalSeconds);
        private static readonly float[] VadSegmentMergeBridgeSilence = new float[(int)(StreamingSampleRate * 0.02)];
        private static readonly float[] VadSegmentRecognitionTailPadding = new float[(int)(StreamingSampleRate * 0.12)];

        private int _pendingRecognitionWorkItems;
        private int _pendingAudioFrames;
        private int _audioPipeReadInProgress;
        private int _consecutiveFrameProcessingFailures;
        private int _consecutivePipeProcessingFailures;
        private int _processingStopLossTriggered;
        private readonly object _streamingPreRollLock = new();
        private readonly Queue<float[]> _streamingPreRollFrames = new();
        private int _streamingPreRollSamples;
        private float[]? _pendingVadSegmentAudio;
        private RecognitionSegmentInfo _pendingVadSegmentInfo = RecognitionSegmentInfo.Empty;
        private bool _pendingVadSegmentMergeArmed;

        private long _segmentSequence;
        private long _activeSpeechSegmentId;
        private long _activeSpeechCapturedAtUtcTicks = DateTime.MinValue.Ticks;
        private long _activeStreamingSegmentId;
        private long _activeStreamingCapturedAtUtcTicks = DateTime.MinValue.Ticks;
        private long _lastAudioFrameUtcTicks = DateTime.MinValue.Ticks;

        private int _state = (int)PipelineState.Idle;

        private enum PipelineState
        {
            Idle = 0,
            Capturing = 1,
            SpeechDetected = 2,
            DisablingAfterSpeech = 3,
            Disabled = 4,
            Disposing = 5
        }

        public event EventHandler<TranscriptionResult>? TextRecognized;
        public event EventHandler<VolumeLevelChangedEventArgs>? VolumeLevelChanged;
        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
        public event EventHandler<SpeechStateChangedEventArgs>? SpeechStateChanged;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;
        private event EventHandler<VadConfidenceMeasuredEventArgs>? _vadConfidenceMeasuredHandlers;
        public event EventHandler<VadConfidenceMeasuredEventArgs>? VadConfidenceMeasured
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                lock (_vadMeasurementLock)
                {
                    _vadConfidenceMeasuredHandlers += value;
                    EnsureVadMeasurementForwardingLocked();
                }
            }
            remove
            {
                if (value == null)
                {
                    return;
                }

                lock (_vadMeasurementLock)
                {
                    _vadConfidenceMeasuredHandlers -= value;
                    DisableVadMeasurementForwardingIfUnusedLocked();
                }
            }
        }
        public AudioPipelineService(
            IAudioService audioService,
            IVadService? vadService,
            IRecognitionService recognitionService,
            IRecognitionService? controlRecognitionService = null,
            bool ownsRecognitionService = true,
            bool ownsControlRecognitionService = false,
            string sourceName = "")
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _vadService = vadService;
            _recognitionService = recognitionService ?? throw new ArgumentNullException(nameof(recognitionService));
            _controlRecognitionService = controlRecognitionService;
            _ownsRecognitionService = ownsRecognitionService;
            _ownsControlRecognitionService = ownsControlRecognitionService;
            _sourceName = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : sourceName.Trim();

            _useVad = vadService != null;
            _useOfficialVadPaddingOnly = vadService is OfficialSileroVadService;

            _audioPipe = new Pipe(new PipeOptions(
                minimumSegmentSize: 4096,
                pauseWriterThreshold: 1024 * 256,
                resumeWriterThreshold: 1024 * 128));

            _recognitionChannel = Channel.CreateBounded<RecognitionWorkItem>(new BoundedChannelOptions(6)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });

            SetupEvents();
        }

        public bool IsCapturing => _audioService.IsCapturing;

        public bool UsesVad => _useVad;

        public HealthReport CheckHealth()
        {
            var state = CurrentState;
            var pendingRecognition = Volatile.Read(ref _pendingRecognitionWorkItems);
            var pendingAudio = Volatile.Read(ref _pendingAudioFrames);

            if ((state == PipelineState.Capturing || state == PipelineState.SpeechDetected || state == PipelineState.DisablingAfterSpeech)
                && IsCapturing)
            {
                var lastTicks = Interlocked.Read(ref _lastAudioFrameUtcTicks);
                if (lastTicks > DateTime.MinValue.Ticks)
                {
                    var last = new DateTime(lastTicks, DateTimeKind.Utc);
                    if (DateTime.UtcNow - last > NoFrameUnhealthyThreshold)
                    {
                        return new HealthReport
                        {
                            ServiceName = nameof(AudioPipelineService),
                            Status = HealthStatus.Unhealthy,
                            Message = "No audio frames received for more than 5 seconds."
                        };
                    }
                }
            }

            if (pendingRecognition > 10)
            {
                return new HealthReport
                {
                    ServiceName = nameof(AudioPipelineService),
                    Status = HealthStatus.Degraded,
                    Message = $"Recognition queue backlog: {pendingRecognition}."
                };
            }

            if (pendingAudio > MaxPendingAudioFrames / 2)
            {
                return new HealthReport
                {
                    ServiceName = nameof(AudioPipelineService),
                    Status = HealthStatus.Degraded,
                    Message = $"Audio queue backlog: {pendingAudio}."
                };
            }

            return new HealthReport
            {
                ServiceName = nameof(AudioPipelineService),
                Status = HealthStatus.Healthy
            };
        }

        public void SetRecognitionEnabled(bool enabled, bool drainCurrentSpeech = false)
        {
            var state = CurrentState;

            if (enabled)
            {
                CancelPendingDrainDisable();
                if (IsRecognitionEnabled(state))
                {
                    return;
                }

                SetState(IsCapturing ? PipelineState.Capturing : PipelineState.Idle);
                _activeStreamingSegmentId = 0;
                Interlocked.Exchange(ref _activeStreamingCapturedAtUtcTicks, DateTime.MinValue.Ticks);
                FlushStreamingPreRollFrames();
                return;
            }

            if (!IsRecognitionEnabled(state) && state != PipelineState.DisablingAfterSpeech)
            {
                return;
            }

            if (drainCurrentSpeech)
            {
                SetState(PipelineState.DisablingAfterSpeech);
                DrainAudioPipeFrames(processFrames: true);
                if (_useVad)
                {
                    ProcessAudioFrame(DrainFlushSilence);

                    if (IsSpeechInProgress(CurrentState) || Volatile.Read(ref _pendingRecognitionWorkItems) > 0)
                    {
                        ScheduleDrainDisableFallback();
                        return;
                    }
                }
                else
                {
                    // Streaming mic path has no VAD; keep recognition alive briefly
                    // to absorb tail frames before finalizing segment on disable.
                    ScheduleDrainDisableFallback(StreamingDrainDisableTimeout, requireRecognitionQueueEmpty: false);
                    return;
                }
            }

            DisableRecognitionNow();
        }

        private void DisableRecognitionNow()
        {
            CancelPendingDrainDisable();
            var stateBeforeDisable = CurrentState;
            var wasSpeaking = IsSpeechInProgress(stateBeforeDisable);

            if (!_useVad && stateBeforeDisable == PipelineState.DisablingAfterSpeech)
            {
                DrainAudioPipeFrames(processFrames: true);
            }

            FlushPendingVadSegmentForRecognition();

            SetState(PipelineState.Disabled);

            DrainAudioPipeFrames(processFrames: false);
            ClearRecognitionQueue();
            ClearStreamingPreRollFrames();

            lock (_processLock)
            {
                if (_useVad)
                {
                    _vadService?.Reset();
                }

                if (_useVad)
                {
                    try { _recognitionService.FinishSegment(); } catch { }
                    try { _controlRecognitionService?.FinishSegment(); } catch { }
                }
                else
                {
                    ResetStreamingRecognitionService(_recognitionService);
                    ResetStreamingRecognitionService(_controlRecognitionService);
                }

                ResetSegmentTrackingState();
            }

            if (wasSpeaking)
            {
                SpeechStateChanged?.Invoke(this, new SpeechStateChangedEventArgs(false));
            }
        }

        private void SetupEvents()
        {
            _audioService.AudioDataAvailable += OnAudioDataAvailable;
            _audioService.VolumeLevelChanged += OnVolumeLevelChanged;
            _audioService.ErrorOccurred += OnChildServiceErrorOccurred;

            if (_useVad)
            {
                _vadService!.SpeechEnded += OnSpeechEnded;
                _vadService!.SpeechDetected += OnSpeechDetected;
                _vadService!.ErrorOccurred += OnChildServiceErrorOccurred;
            }

            _recognitionService.TextRecognized += OnRecognitionServiceTextRecognized;
            _recognitionService.ErrorOccurred += OnChildServiceErrorOccurred;
            if (_controlRecognitionService != null)
            {
                _controlRecognitionService.TextRecognized += OnControlRecognitionServiceTextRecognized;
                _controlRecognitionService.ErrorOccurred += OnChildServiceErrorOccurred;
            }
        }

        private void OnChildServiceErrorOccurred(object? sender, ServiceErrorEvent error)
        {
            ForwardServiceError(error);
        }

        private void ForwardServiceError(ServiceErrorEvent error)
        {
            if (error == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_sourceName))
            {
                ErrorOccurred?.Invoke(this, error);
                return;
            }

            var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (error.Context != null)
            {
                foreach (var item in error.Context)
                {
                    context[item.Key] = item.Value;
                }
            }

            if (!context.ContainsKey("Pipeline"))
            {
                context["Pipeline"] = _sourceName;
            }

            if (!context.ContainsKey("Source"))
            {
                context["Source"] = ResolveSourceDisplayName(_sourceName);
            }

            ErrorOccurred?.Invoke(this, new ServiceErrorEvent
            {
                Code = error.Code,
                Context = context,
                Exception = error.Exception
            });
        }

        private static string ResolveSourceDisplayName(string sourceName)
        {
            return string.Equals(sourceName, "Mic", StringComparison.OrdinalIgnoreCase)
                ? "麦克风"
                : "监听音频";
        }

        private void EnsureVadMeasurementForwardingLocked()
        {
            if (!_useVad || _vadService == null || _vadMeasurementForwardingAttached || _vadConfidenceMeasuredHandlers == null)
            {
                return;
            }

            _vadService.ConfidenceMeasured += OnVadConfidenceMeasured;
            _vadMeasurementForwardingAttached = true;
        }

        private void DisableVadMeasurementForwardingIfUnusedLocked()
        {
            if (!_vadMeasurementForwardingAttached || _vadService == null || _vadConfidenceMeasuredHandlers != null)
            {
                return;
            }

            _vadService.ConfidenceMeasured -= OnVadConfidenceMeasured;
            _vadMeasurementForwardingAttached = false;
        }

        private void OnVadConfidenceMeasured(object? sender, VadConfidenceMeasuredEventArgs e)
            => _vadConfidenceMeasuredHandlers?.Invoke(this, e);

        private void OnRecognitionServiceTextRecognized(object? sender, TranscriptionResult result)
        {
            if (!_useVad && result.IsFinal)
            {
                _activeStreamingSegmentId = 0;
                Interlocked.Exchange(ref _activeStreamingCapturedAtUtcTicks, DateTime.MinValue.Ticks);
            }

            TextRecognized?.Invoke(this, result);
        }

        private void OnControlRecognitionServiceTextRecognized(object? sender, TranscriptionResult result)
        {
            TextRecognized?.Invoke(this, result);
        }

        private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            var audioData = e.AudioData;
            if (audioData == null || audioData.Length == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _lastAudioFrameUtcTicks, DateTime.UtcNow.Ticks);
            AudioDataAvailable?.Invoke(this, e);

            if (!IsRecognitionEnabled(CurrentState))
            {
                if (!_useVad)
                {
                    ClearStreamingPreRollFrames();
                }

                return;
            }

            if (Volatile.Read(ref _pendingAudioFrames) >= MaxPendingAudioFrames)
            {
                _logger.Debug("Audio pipe backlog is full; dropping frame.");
                return;
            }

            if (!TryWriteAudioFrame(audioData))
            {
                return;
            }

            Interlocked.Increment(ref _pendingAudioFrames);
        }

        private bool TryWriteAudioFrame(float[] audioData)
        {
            try
            {
                var payloadBytes = audioData.Length * sizeof(float);
                var memory = _audioPipe.Writer.GetMemory(payloadBytes + sizeof(int));
                var span = memory.Span;

                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, sizeof(int)), payloadBytes);
                MemoryMarshal.AsBytes(audioData.AsSpan()).CopyTo(span.Slice(sizeof(int), payloadBytes));

                _audioPipe.Writer.Advance(payloadBytes + sizeof(int));

                var flush = _audioPipe.Writer.FlushAsync();
                if (!flush.IsCompletedSuccessfully)
                {
                    _ = flush.AsTask();
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to write frame into audio pipe.");
                return false;
            }
        }

        private void CaptureStreamingPreRoll(float[] audioData)
        {
            var copy = new float[audioData.Length];
            Array.Copy(audioData, copy, audioData.Length);

            lock (_streamingPreRollLock)
            {
                _streamingPreRollFrames.Enqueue(copy);
                _streamingPreRollSamples += copy.Length;
                TrimStreamingPreRollLocked();
            }
        }

        private void TrimStreamingPreRollLocked()
        {
            while (_streamingPreRollSamples > StreamingStartPreRollMaxSamples && _streamingPreRollFrames.Count > 0)
            {
                var removed = _streamingPreRollFrames.Dequeue();
                _streamingPreRollSamples -= removed.Length;
            }

            if (_streamingPreRollSamples < 0)
            {
                _streamingPreRollSamples = 0;
            }
        }

        private void FlushStreamingPreRollFrames()
        {
            float[][] frames;
            lock (_streamingPreRollLock)
            {
                if (_streamingPreRollFrames.Count == 0)
                {
                    return;
                }

                frames = _streamingPreRollFrames.ToArray();
                _streamingPreRollFrames.Clear();
                _streamingPreRollSamples = 0;
            }

            var written = 0;
            foreach (var frame in frames)
            {
                if (Volatile.Read(ref _pendingAudioFrames) >= MaxPendingAudioFrames)
                {
                    break;
                }

                if (!TryWriteAudioFrame(frame))
                {
                    continue;
                }

                Interlocked.Increment(ref _pendingAudioFrames);
                written++;
            }

            if (written > 0)
            {
                _logger.Debug("Flushed {Count} streaming pre-roll frames.", written);
            }
        }

        private void ClearStreamingPreRollFrames()
        {
            lock (_streamingPreRollLock)
            {
                _streamingPreRollFrames.Clear();
                _streamingPreRollSamples = 0;
            }
        }

        private void ResetStreamingRecognitionService(IRecognitionService? recognitionService)
        {
            if (recognitionService == null)
            {
                return;
            }

            try
            {
                if (recognitionService.IsRunning)
                {
                    recognitionService.Stop();
                }

                recognitionService.Start();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to reset streaming recognition service.");
            }
        }

        private void OnVolumeLevelChanged(object? sender, VolumeLevelChangedEventArgs e)
        {
            VolumeLevelChanged?.Invoke(this, e);
        }

        private void OnSpeechEnded(object? sender, VadSpeechEventArgs e)
        {
            if (!IsRecognitionEnabled(CurrentState))
            {
                return;
            }

            if (e.AudioData != null && e.AudioData.Length > 0)
            {
                Interlocked.Exchange(ref _activeSpeechCapturedAtUtcTicks, DateTime.UtcNow.Ticks);
                var segmentInfo = BuildSegmentInfoForSpeechEnd();
                var flushImmediately = CurrentState == PipelineState.DisablingAfterSpeech || !IsCapturing;
                BufferVadSegmentForRecognition(e.AudioData, segmentInfo, flushImmediately);
            }

            _activeSpeechSegmentId = 0;
            Interlocked.Exchange(ref _activeSpeechCapturedAtUtcTicks, DateTime.MinValue.Ticks);

            if (CurrentState == PipelineState.DisablingAfterSpeech)
            {
                FlushPendingVadSegmentForRecognition();
                SpeechStateChanged?.Invoke(this, new SpeechStateChangedEventArgs(false));
                TryFinalizeDeferredDisable();
            }
            else
            {
                SetState(IsCapturing ? PipelineState.Capturing : PipelineState.Idle);
                SpeechStateChanged?.Invoke(this, new SpeechStateChangedEventArgs(false));
            }
        }

        private bool TryQueueRecognitionWorkItem(float[] audioData, RecognitionSegmentInfo segmentInfo)
        {
            if (audioData == null || audioData.Length == 0)
            {
                return false;
            }

            var workItem = new RecognitionWorkItem(audioData, segmentInfo);
            if (!_recognitionChannel.Writer.TryWrite(workItem))
            {
                return false;
            }

            Interlocked.Increment(ref _pendingRecognitionWorkItems);
            return true;
        }

        private void BufferVadSegmentForRecognition(float[] audioData, RecognitionSegmentInfo segmentInfo, bool flushImmediately)
        {
            List<RecognitionWorkItem>? workItems = null;

            lock (_pendingVadSegmentLock)
            {
                if (_pendingVadSegmentAudio != null && _pendingVadSegmentAudio.Length > 0)
                {
                    if (_pendingVadSegmentMergeArmed)
                    {
                        _pendingVadSegmentAudio = MergeVadSegments(_pendingVadSegmentAudio, audioData);
                        _pendingVadSegmentInfo = MergeSegmentInfo(_pendingVadSegmentInfo, segmentInfo);
                        _pendingVadSegmentMergeArmed = false;
                    }
                    else
                    {
                        workItems = TakePendingVadSegmentsLocked(workItems);
                        _pendingVadSegmentAudio = audioData;
                        _pendingVadSegmentInfo = segmentInfo;
                    }
                }
                else
                {
                    _pendingVadSegmentAudio = audioData;
                    _pendingVadSegmentInfo = segmentInfo;
                }

                CancelPendingVadSegmentFlushLocked();

                if (flushImmediately)
                {
                    workItems = TakePendingVadSegmentsLocked(workItems);
                }
                else
                {
                    SchedulePendingVadSegmentFlushLocked();
                }
            }

            if (workItems == null)
            {
                return;
            }

            foreach (var workItem in workItems)
            {
                QueuePendingVadRecognitionWorkItem(workItem);
            }
        }

        private void QueuePendingVadRecognitionWorkItem(RecognitionWorkItem workItem)
        {
            if (!TryQueueRecognitionWorkItem(workItem.AudioData, workItem.SegmentInfo))
            {
                _logger.Warning("Recognition queue is full; dropping speech segment.");
            }
        }

        private void HoldPendingVadSegmentForPotentialMerge()
        {
            lock (_pendingVadSegmentLock)
            {
                if (_pendingVadSegmentAudio == null || _pendingVadSegmentAudio.Length == 0)
                {
                    return;
                }

                CancelPendingVadSegmentFlushLocked();
                _pendingVadSegmentMergeArmed = true;
            }
        }

        private void FlushPendingVadSegmentForRecognition()
        {
            List<RecognitionWorkItem>? workItems = null;

            lock (_pendingVadSegmentLock)
            {
                CancelPendingVadSegmentFlushLocked();
                workItems = TakePendingVadSegmentsLocked(workItems);
            }

            if (workItems == null)
            {
                return;
            }

            foreach (var workItem in workItems)
            {
                QueuePendingVadRecognitionWorkItem(workItem);
            }
        }

        private void SchedulePendingVadSegmentFlushLocked()
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _pendingVadSegmentFlushCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(VadSegmentMergeWindow, token).ConfigureAwait(false);

                    RecognitionWorkItem? workItem = null;
                    lock (_pendingVadSegmentLock)
                    {
                        if (!ReferenceEquals(_pendingVadSegmentFlushCts, cts) || token.IsCancellationRequested)
                        {
                            return;
                        }

                        _pendingVadSegmentFlushCts = null;
                        workItem = TakePendingVadSegmentLocked();
                    }

                    if (workItem.HasValue)
                    {
                        QueuePendingVadRecognitionWorkItem(workItem.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                finally
                {
                    lock (_pendingVadSegmentLock)
                    {
                        if (ReferenceEquals(_pendingVadSegmentFlushCts, cts))
                        {
                            _pendingVadSegmentFlushCts = null;
                        }
                    }

                    cts.Dispose();
                }
            });
        }

        private void CancelPendingVadSegmentFlushLocked()
        {
            if (_pendingVadSegmentFlushCts == null)
            {
                return;
            }

            _pendingVadSegmentFlushCts.Cancel();
            _pendingVadSegmentFlushCts = null;
            _pendingVadSegmentMergeArmed = false;
        }

        private RecognitionWorkItem? TakePendingVadSegmentLocked()
        {
            if (_pendingVadSegmentAudio == null || _pendingVadSegmentAudio.Length == 0)
            {
                _pendingVadSegmentAudio = null;
                _pendingVadSegmentInfo = RecognitionSegmentInfo.Empty;
                _pendingVadSegmentMergeArmed = false;
                return null;
            }

            var audioData = _pendingVadSegmentAudio;
            var segmentInfo = _pendingVadSegmentInfo;
            _pendingVadSegmentAudio = null;
            _pendingVadSegmentInfo = RecognitionSegmentInfo.Empty;
            _pendingVadSegmentMergeArmed = false;

            if (audioData.Length < MinVadRecognitionSegmentSamples)
            {
                _logger.Debug(
                    "Skip too-short VAD segment. samples={Samples}, segmentId={SegmentId}",
                    audioData.Length,
                    segmentInfo.SegmentId);
                return null;
            }

            var workItem = new RecognitionWorkItem(
                PrepareVadSegmentForRecognition(audioData),
                segmentInfo);
            return workItem;
        }

        private List<RecognitionWorkItem> TakePendingVadSegmentsLocked(List<RecognitionWorkItem>? workItems)
        {
            var workItem = TakePendingVadSegmentLocked();
            if (!workItem.HasValue)
            {
                return workItems ?? new List<RecognitionWorkItem>(0);
            }

            workItems ??= new List<RecognitionWorkItem>(2);
            workItems.Add(workItem.Value);
            return workItems;
        }

        private static float[] MergeVadSegments(float[] leading, float[] trailing)
        {
            var bridgeLength = VadSegmentMergeBridgeSilence.Length;
            var merged = new float[leading.Length + bridgeLength + trailing.Length];
            Array.Copy(leading, 0, merged, 0, leading.Length);
            if (bridgeLength > 0)
            {
                Array.Copy(VadSegmentMergeBridgeSilence, 0, merged, leading.Length, bridgeLength);
            }

            Array.Copy(trailing, 0, merged, leading.Length + bridgeLength, trailing.Length);
            return merged;
        }

        private float[] PrepareVadSegmentForRecognition(float[] audioData)
        {
            if (_useOfficialVadPaddingOnly)
            {
                var passthrough = new float[audioData.Length];
                Array.Copy(audioData, passthrough, audioData.Length);
                return passthrough;
            }

            return AppendVadRecognitionTailPadding(audioData);
        }

        private static float[] AppendVadRecognitionTailPadding(float[] audioData)
        {
            if (audioData.Length == 0)
            {
                return audioData;
            }

            var tailLength = VadSegmentRecognitionTailPadding.Length;
            if (tailLength == 0)
            {
                var passthrough = new float[audioData.Length];
                Array.Copy(audioData, passthrough, audioData.Length);
                return passthrough;
            }

            var output = new float[audioData.Length + tailLength];
            Array.Copy(audioData, 0, output, 0, audioData.Length);
            Array.Copy(VadSegmentRecognitionTailPadding, 0, output, audioData.Length, tailLength);
            return output;
        }

        private static RecognitionSegmentInfo MergeSegmentInfo(RecognitionSegmentInfo existing, RecognitionSegmentInfo incoming)
        {
            var capturedAtUtc = existing.CapturedAtUtc != DateTime.MinValue
                ? existing.CapturedAtUtc
                : incoming.CapturedAtUtc;

            if (incoming.CapturedAtUtc != DateTime.MinValue
                && (capturedAtUtc == DateTime.MinValue || incoming.CapturedAtUtc < capturedAtUtc))
            {
                capturedAtUtc = incoming.CapturedAtUtc;
            }

            return new RecognitionSegmentInfo
            {
                SegmentId = existing.SegmentId > 0 ? existing.SegmentId : incoming.SegmentId,
                CapturedAtUtc = capturedAtUtc == DateTime.MinValue ? DateTime.UtcNow : capturedAtUtc
            };
        }

        private void ProcessRecognitionWorkItem(RecognitionWorkItem workItem)
        {
            try
            {
                lock (_processLock)
                {
                    if (!IsRecognitionEnabled(CurrentState))
                    {
                        return;
                    }

                    _recognitionService.FinishSegment();
                    _recognitionService.ProcessAudio(workItem.AudioData, workItem.SegmentInfo);

                    if (_controlRecognitionService != null)
                    {
                        try
                        {
                            _controlRecognitionService.FinishSegment();
                            _controlRecognitionService.ProcessAudio(workItem.AudioData, workItem.SegmentInfo);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Control recognition failed.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process speech segment.");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingRecognitionWorkItems);
                TryFinalizeDeferredDisable();
            }
        }

        private async Task ProcessRecognitionQueue(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (await _recognitionChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                    {
                        while (_recognitionChannel.Reader.TryRead(out var workItem))
                        {
                            ProcessRecognitionWorkItem(workItem);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Recognition queue processing failed.");
                }
            }
        }

        private void TryFinalizeDeferredDisable()
        {
            if (CurrentState != PipelineState.DisablingAfterSpeech)
            {
                return;
            }

            if (Volatile.Read(ref _pendingRecognitionWorkItems) > 0)
            {
                ScheduleDrainDisableFallback();
                return;
            }

            DisableRecognitionNow();
        }

        private static DateTime ResolveCapturedAtUtc(long capturedAtTicks)
        {
            if (capturedAtTicks <= DateTime.MinValue.Ticks)
            {
                return DateTime.UtcNow;
            }

            return new DateTime(capturedAtTicks, DateTimeKind.Utc);
        }

        private RecognitionSegmentInfo BuildSegmentInfoForSpeechEnd()
        {
            if (_activeSpeechSegmentId > 0)
            {
                return new RecognitionSegmentInfo
                {
                    SegmentId = _activeSpeechSegmentId,
                    CapturedAtUtc = ResolveCapturedAtUtc(Interlocked.Read(ref _activeSpeechCapturedAtUtcTicks))
                };
            }

            return new RecognitionSegmentInfo
            {
                SegmentId = Interlocked.Increment(ref _segmentSequence),
                CapturedAtUtc = DateTime.UtcNow
            };
        }

        private RecognitionSegmentInfo GetOrCreateStreamingSegmentInfo()
        {
            if (_activeStreamingSegmentId == 0)
            {
                _activeStreamingSegmentId = Interlocked.Increment(ref _segmentSequence);
            }

            Interlocked.Exchange(ref _activeStreamingCapturedAtUtcTicks, DateTime.UtcNow.Ticks);

            return new RecognitionSegmentInfo
            {
                SegmentId = _activeStreamingSegmentId,
                CapturedAtUtc = ResolveCapturedAtUtc(Interlocked.Read(ref _activeStreamingCapturedAtUtcTicks))
            };
        }

        private void ResetSegmentTrackingState()
        {
            _activeSpeechSegmentId = 0;
            Interlocked.Exchange(ref _activeSpeechCapturedAtUtcTicks, DateTime.MinValue.Ticks);
            _activeStreamingSegmentId = 0;
            Interlocked.Exchange(ref _activeStreamingCapturedAtUtcTicks, DateTime.MinValue.Ticks);
        }

        private void ClearRecognitionQueue()
        {
            while (_recognitionChannel.Reader.TryRead(out _))
            {
                Interlocked.Decrement(ref _pendingRecognitionWorkItems);
            }

            if (Volatile.Read(ref _pendingRecognitionWorkItems) < 0)
            {
                Interlocked.Exchange(ref _pendingRecognitionWorkItems, 0);
            }
        }

        private void OnSpeechDetected(object? sender, VadSpeechEventArgs e)
        {
            if (!IsRecognitionEnabled(CurrentState))
            {
                return;
            }

            HoldPendingVadSegmentForPotentialMerge();

            _activeSpeechSegmentId = Interlocked.Increment(ref _segmentSequence);
            Interlocked.Exchange(ref _activeSpeechCapturedAtUtcTicks, DateTime.UtcNow.Ticks);

            if (CurrentState != PipelineState.DisablingAfterSpeech)
            {
                SetState(PipelineState.SpeechDetected);
            }

            SpeechStateChanged?.Invoke(this, new SpeechStateChangedEventArgs(true));
        }

        private void EnsureProcessingTask()
        {
            lock (_taskLock)
            {
                if (_cancellationTokenSource == null || _processingTask == null || _processingTask.IsCompleted)
                {
                    try { _cancellationTokenSource?.Dispose(); } catch { }

                    _cancellationTokenSource = new CancellationTokenSource();
                    var token = _cancellationTokenSource.Token;
                    _processingTask = Task.Run(async () => await ProcessAudioPipe(token).ConfigureAwait(false), token);
                }

                EnsureRecognitionTask();
            }
        }

        private void EnsureRecognitionTask()
        {
            lock (_taskLock)
            {
                if (_recognitionCts == null || _recognitionTask == null || _recognitionTask.IsCompleted)
                {
                    try { _recognitionCts?.Dispose(); } catch { }

                    _recognitionCts = new CancellationTokenSource();
                    var token = _recognitionCts.Token;
                    _recognitionTask = Task.Run(async () => await ProcessRecognitionQueue(token).ConfigureAwait(false), token);
                }
            }
        }

        public void StartCapture(AudioCaptureRequest request)
        {
            ResetProcessingFailureState();
            EnsureProcessingTask();

            _audioService.StartCapture(request);
            lock (_processLock)
            {
                _recognitionService.Start();
                _controlRecognitionService?.Start();

                if (_useVad)
                {
                    _vadService?.Reset();
                }
            }

            if (CurrentState != PipelineState.Disabled)
            {
                SetState(PipelineState.Capturing);
            }
        }

        public void StopCapture()
        {
            if (_disposed || _stopping)
            {
                return;
            }

            _stopping = true;
            try
            {
                CancelPendingDrainDisable();
                FlushPendingVadSegmentForRecognition();
                SetState(PipelineState.Disabled);

                _audioService.StopCapture();
                DrainAudioPipeFrames(processFrames: false);
                ClearRecognitionQueue();

                lock (_processLock)
                {
                    _recognitionService.Stop();
                    _controlRecognitionService?.Stop();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to stop audio pipeline.");
            }
            finally
            {
                _stopping = false;
            }
        }

        private void ProcessAudioFrame(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                return;
            }

            if (!IsRecognitionEnabled(CurrentState))
            {
                return;
            }

            lock (_processLock)
            {
                if (_useVad && _vadService != null)
                {
                    _vadService.ProcessAudio(audioData);
                }
                else
                {
                    _recognitionService.ProcessAudio(audioData, GetOrCreateStreamingSegmentInfo());
                }
            }
        }

        private async Task ProcessAudioPipe(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Interlocked.Exchange(ref _audioPipeReadInProgress, 1);
                    var result = await _audioPipe.Reader.ReadAsync(token).ConfigureAwait(false);
                    var buffer = result.Buffer;
                    try
                    {
                        while (TryReadAudioFrame(ref buffer, out var audioData))
                        {
                            try
                            {
                                ProcessAudioFrame(audioData);
                                ResetFrameProcessingFailureCount();
                            }
                            catch (Exception ex)
                            {
                                if (RegisterFrameProcessingFailure(ex))
                                {
                                    buffer = buffer.Slice(buffer.End);
                                    break;
                                }
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _pendingAudioFrames);
                            }
                        }
                    }
                    finally
                    {
                        _audioPipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                    }

                    if (result.IsCompleted && buffer.Length == 0)
                    {
                        break;
                    }

                    ResetPipeProcessingFailureCount();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (RegisterPipeProcessingFailure(ex))
                    {
                        break;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _audioPipeReadInProgress, 0);
                }
            }
        }

        private void ResetProcessingFailureState()
        {
            Interlocked.Exchange(ref _consecutiveFrameProcessingFailures, 0);
            Interlocked.Exchange(ref _consecutivePipeProcessingFailures, 0);
            Interlocked.Exchange(ref _processingStopLossTriggered, 0);
        }

        private void ResetFrameProcessingFailureCount()
        {
            Interlocked.Exchange(ref _consecutiveFrameProcessingFailures, 0);
        }

        private void ResetPipeProcessingFailureCount()
        {
            Interlocked.Exchange(ref _consecutivePipeProcessingFailures, 0);
        }

        private bool RegisterFrameProcessingFailure(Exception ex)
        {
            var count = Interlocked.Increment(ref _consecutiveFrameProcessingFailures);
            _logger.Debug(ex, "Audio frame processing failed. consecutiveFailures={ConsecutiveFailures}", count);
            return TripProcessingStopLossIfNeeded("frame-processing", count, MaxConsecutiveFrameProcessingFailures, ex);
        }

        private bool RegisterPipeProcessingFailure(Exception ex)
        {
            var count = Interlocked.Increment(ref _consecutivePipeProcessingFailures);
            _logger.Debug(ex, "Audio pipe processing failed. consecutiveFailures={ConsecutiveFailures}", count);
            return TripProcessingStopLossIfNeeded("pipe-processing", count, MaxConsecutivePipeProcessingFailures, ex);
        }

        private bool TripProcessingStopLossIfNeeded(string stage, int consecutiveFailures, int threshold, Exception ex)
        {
            if (consecutiveFailures < threshold)
            {
                return false;
            }

            if (Interlocked.Exchange(ref _processingStopLossTriggered, 1) != 0)
            {
                return true;
            }

            _logger.Error(
                ex,
                "Audio pipeline stop-loss triggered after repeated {Stage} failures. consecutiveFailures={ConsecutiveFailures}, threshold={Threshold}",
                stage,
                consecutiveFailures,
                threshold);

            ForwardServiceError(new ServiceErrorEvent
            {
                Code = ServiceErrorCode.AudioProcessingRepeatedFailures,
                Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Source"] = ResolveSourceDisplayName(_sourceName),
                    ["Pipeline"] = _sourceName,
                    ["Stage"] = stage,
                    ["Reason"] = $"音频处理连续失败 {consecutiveFailures} 次，正在尝试自动恢复。",
                    ["ConsecutiveFailures"] = consecutiveFailures.ToString(),
                    ["Threshold"] = threshold.ToString()
                },
                Exception = ex
            });

            try
            {
                StopCapture();
            }
            catch (Exception stopEx)
            {
                _logger.Error(stopEx, "Failed to stop audio pipeline after stop-loss trigger.");
            }

            return true;
        }

        private bool TryReadAudioFrame(ref ReadOnlySequence<byte> buffer, out float[] audioData)
        {
            audioData = Array.Empty<float>();

            if (buffer.Length < sizeof(int))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[sizeof(int)];
            buffer.Slice(0, sizeof(int)).CopyTo(header);
            var payloadBytes = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (payloadBytes <= 0 || payloadBytes % sizeof(float) != 0)
            {
                buffer = buffer.Slice(sizeof(int));
                return false;
            }

            var messageSize = sizeof(int) + payloadBytes;
            if (buffer.Length < messageSize)
            {
                return false;
            }

            audioData = new float[payloadBytes / sizeof(float)];
            buffer.Slice(sizeof(int), payloadBytes).CopyTo(MemoryMarshal.AsBytes(audioData.AsSpan()));
            buffer = buffer.Slice(messageSize);
            return true;
        }

        private void DrainAudioPipeFrames(bool processFrames)
        {
            if (Volatile.Read(ref _audioPipeReadInProgress) != 0)
            {
                _logger.Debug("Skip draining audio pipe because a read is already in progress.");
                return;
            }

            try
            {
                while (_audioPipe.Reader.TryRead(out var result))
                {
                    var buffer = result.Buffer;
                    while (TryReadAudioFrame(ref buffer, out var audioData))
                    {
                        if (processFrames)
                        {
                            ProcessAudioFrame(audioData);
                        }

                        Interlocked.Decrement(ref _pendingAudioFrames);
                    }

                    _audioPipe.Reader.AdvanceTo(buffer.Start, buffer.End);

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Reading is already in progress.", StringComparison.Ordinal))
            {
                _logger.Debug("Skip draining audio pipe because a read is already in progress.");
                return;
            }

            if (Volatile.Read(ref _pendingAudioFrames) < 0)
            {
                Interlocked.Exchange(ref _pendingAudioFrames, 0);
            }
        }

        private void ScheduleDrainDisableFallback(TimeSpan? delay = null, bool requireRecognitionQueueEmpty = true)
        {
            CancellationTokenSource cts;
            CancellationToken token;
            lock (_drainDisableLock)
            {
                _pendingDrainDisableCts?.Cancel();

                cts = new CancellationTokenSource();
                token = cts.Token;
                _pendingDrainDisableCts = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay ?? DrainDisableTimeout, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || CurrentState != PipelineState.DisablingAfterSpeech)
                    {
                        return;
                    }

                    if (requireRecognitionQueueEmpty && Volatile.Read(ref _pendingRecognitionWorkItems) > 0)
                    {
                        return;
                    }

                    DisableRecognitionNow();
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                finally
                {
                    lock (_drainDisableLock)
                    {
                        if (ReferenceEquals(_pendingDrainDisableCts, cts))
                        {
                            _pendingDrainDisableCts = null;
                        }
                    }

                    cts.Dispose();
                }
            });
        }

        private void CancelPendingDrainDisable()
        {
            lock (_drainDisableLock)
            {
                if (_pendingDrainDisableCts == null)
                {
                    return;
                }

                _pendingDrainDisableCts.Cancel();
                _pendingDrainDisableCts = null;
            }
        }

        private PipelineState CurrentState => (PipelineState)Volatile.Read(ref _state);

        private bool IsRecognitionEnabled(PipelineState state)
            => state != PipelineState.Disabled && state != PipelineState.Disposing;

        private static bool IsSpeechInProgress(PipelineState state)
            => state == PipelineState.SpeechDetected || state == PipelineState.DisablingAfterSpeech;

        private void SetState(PipelineState state)
        {
            var previous = (PipelineState)Interlocked.Exchange(ref _state, (int)state);
            if (previous != state)
            {
                _logger.Debug("Audio pipeline state: {From} -> {To}", previous, state);
            }
        }

        private readonly record struct RecognitionWorkItem(float[] AudioData, RecognitionSegmentInfo SegmentInfo);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            var disposeStopwatch = Stopwatch.StartNew();
            _disposed = true;
            SetState(PipelineState.Disposing);
            _logger.Information("开始释放音频管道。AudioServiceType={AudioServiceType}, CurrentState={CurrentState}", _audioService.GetType().Name, CurrentState);

            try
            {
                StopCapture();
                _logger.Debug("音频管道 StopCapture 完成，累计耗时 {ElapsedMilliseconds} ms", disposeStopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to stop pipeline while disposing.");
            }

            try
            {
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }

                if (_recognitionCts != null && !_recognitionCts.IsCancellationRequested)
                {
                    _recognitionCts.Cancel();
                }

                try { _audioPipe.Writer.Complete(); } catch { }
                try { _audioPipe.Reader.Complete(); } catch { }

                if (_processingTask != null && !_processingTask.Wait(DisposeTaskWaitTimeout))
                {
                    _logger.Warning("Audio processing task did not stop within timeout.");
                }
                else if (_processingTask != null)
                {
                    _logger.Debug("音频处理任务已停止，累计耗时 {ElapsedMilliseconds} ms", disposeStopwatch.ElapsedMilliseconds);
                }

                if (_recognitionTask != null && !_recognitionTask.Wait(DisposeTaskWaitTimeout))
                {
                    _logger.Warning("Recognition queue task did not stop within timeout.");
                }
                else if (_recognitionTask != null)
                {
                    _logger.Debug("识别队列任务已停止，累计耗时 {ElapsedMilliseconds} ms", disposeStopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed while waiting background tasks during dispose.");
            }

            try
            {
                _audioService.AudioDataAvailable -= OnAudioDataAvailable;
                _audioService.VolumeLevelChanged -= OnVolumeLevelChanged;
                _audioService.ErrorOccurred -= OnChildServiceErrorOccurred;
                _recognitionService.TextRecognized -= OnRecognitionServiceTextRecognized;
                _recognitionService.ErrorOccurred -= OnChildServiceErrorOccurred;

                if (_controlRecognitionService != null)
                {
                    _controlRecognitionService.TextRecognized -= OnControlRecognitionServiceTextRecognized;
                    _controlRecognitionService.ErrorOccurred -= OnChildServiceErrorOccurred;
                }

                if (_useVad && _vadService != null)
                {
                    _vadService.SpeechEnded -= OnSpeechEnded;
                    _vadService.SpeechDetected -= OnSpeechDetected;
                    _vadService.ErrorOccurred -= OnChildServiceErrorOccurred;
                    if (_vadMeasurementForwardingAttached)
                    {
                        _vadService.ConfidenceMeasured -= OnVadConfidenceMeasured;
                        _vadMeasurementForwardingAttached = false;
                    }
                }

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _recognitionCts?.Dispose();
                _recognitionCts = null;
                lock (_pendingVadSegmentLock)
                {
                    CancelPendingVadSegmentFlushLocked();
                    _pendingVadSegmentAudio = null;
                    _pendingVadSegmentInfo = RecognitionSegmentInfo.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to detach handlers while disposing pipeline.");
            }

            try
            {
                if (_audioService is IDisposable disposableAudio)
                {
                    disposableAudio.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose audio service.");
            }

            try
            {
                if (_vadService is IDisposable disposableVad)
                {
                    lock (_processLock)
                    {
                        disposableVad.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose VAD service.");
            }

            try
            {
                if (_ownsRecognitionService && _recognitionService is IDisposable disposableRecognition)
                {
                    lock (_processLock)
                    {
                        disposableRecognition.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose recognition service.");
            }

            try
            {
                if (_ownsControlRecognitionService && _controlRecognitionService is IDisposable disposableControlRecognition)
                {
                    lock (_processLock)
                    {
                        disposableControlRecognition.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose control recognition service.");
            }

            SetState(PipelineState.Disabled);
            _logger.Information("音频管道释放完成。AudioServiceType={AudioServiceType}, ElapsedMilliseconds={ElapsedMilliseconds}", _audioService.GetType().Name, disposeStopwatch.ElapsedMilliseconds);
        }
    }
}

