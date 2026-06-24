using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Serilog;
using VRCLS.Core.Helpers;

namespace VRCLS.Services.Audio
{
    internal sealed class ProcessLoopbackCapture : IWaveIn
    {
        private static readonly ILogger Logger = Log.ForContext<ProcessLoopbackCapture>();

        private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
        private const int AudioBufferMillisecondsLength = 100;
        private const int DefaultBufferFrameCount = 2000;
        private const int CaptureThreadJoinTimeoutMilliseconds = 5000;
        private const long ReftimesPerSecond = 10000000;
        private const long ReftimesPerMillisecond = 10000;

        private readonly uint _targetProcessId;
        private readonly string _targetProcessName;
        private readonly SynchronizationContext? _syncContext;

        private volatile CaptureState _captureState;
        private byte[] _recordBuffer = Array.Empty<byte>();
        private Thread? _captureThread;
        private AudioClient? _audioClient;
        private EventWaitHandle? _frameEventWaitHandle;
        private WaveFormat _waveFormat = new WaveFormat();
        private int _bufferFrameCount = DefaultBufferFrameCount;
        private int _bytesPerFrame;
        private bool _initialized;
        private readonly bool _isUsingEventSync = true;
        private AudioClientStreamFlags _audioClientStreamFlags =
            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;

        public ProcessLoopbackCapture(uint targetProcessId, string targetProcessName)
        {
            if (targetProcessId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetProcessId));
            }

            _targetProcessId = targetProcessId;
            _targetProcessName = targetProcessName ?? string.Empty;
            _syncContext = SynchronizationContext.Current;
            ShareMode = AudioClientShareMode.Shared;
        }

        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public AudioClientShareMode ShareMode { get; set; }

        public CaptureState CaptureState => _captureState;

        public bool IsRecording => _captureState != CaptureState.Stopped;

        public WaveFormat WaveFormat
        {
            get => _waveFormat.AsStandardWaveFormat();
            set => _waveFormat = value;
        }

        public void StartRecording()
        {
            if (_captureState != CaptureState.Stopped)
            {
                throw new InvalidOperationException("Previous recording still in progress");
            }

            EnsureApplicationLoopbackSupported();

            Logger.Information(
                "启动应用监听音频捕获。Pid={ProcessId}, Name={ProcessName}",
                _targetProcessId,
                string.IsNullOrWhiteSpace(_targetProcessName) ? "(unknown)" : _targetProcessName);

            _captureState = CaptureState.Starting;
            RunInMta(() =>
            {
                EnsureAudioClientCreated();
                InitializeCaptureDevice();
            });

            _captureThread = new Thread(() => CaptureThread(_audioClient!))
            {
                IsBackground = true,
                Name = $"ProcessLoopbackCapture-{_targetProcessId}"
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }

        public void StopRecording()
        {
            if (_captureState != CaptureState.Stopped)
            {
                Logger.Information(
                    "停止应用监听音频捕获请求。Pid={ProcessId}, CaptureState={CaptureState}",
                    _targetProcessId,
                    _captureState);
                _captureState = CaptureState.Stopping;
                _frameEventWaitHandle?.Set();
            }
        }

        public void Dispose()
        {
            Logger.Information(
                "开始释放 ProcessLoopbackCapture。Pid={ProcessId}, ThreadAlive={ThreadAlive}, CaptureState={CaptureState}",
                _targetProcessId,
                _captureThread?.IsAlive == true,
                _captureState);
            StopRecording();

            try
            {
                _audioClient?.Stop();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "释放 ProcessLoopbackCapture 前调用 AudioClient.Stop 失败。Pid={ProcessId}", _targetProcessId);
            }

            _frameEventWaitHandle?.Set();

            var captureThreadStopped = true;
            if (_captureThread != null)
            {
                captureThreadStopped = _captureThread.Join(CaptureThreadJoinTimeoutMilliseconds);
                if (!captureThreadStopped)
                {
                    Logger.Warning(
                        "ProcessLoopbackCapture 采集线程在 {TimeoutMilliseconds} ms 内未退出。Pid={ProcessId}, ThreadState={ThreadState}",
                        CaptureThreadJoinTimeoutMilliseconds,
                        _targetProcessId,
                        _captureThread.ThreadState);
                }
                else
                {
                    Logger.Information("ProcessLoopbackCapture 采集线程已退出。Pid={ProcessId}", _targetProcessId);
                    _captureThread = null;
                }
            }

            if (captureThreadStopped && _audioClient != null)
            {
                _audioClient.Dispose();
                _audioClient = null;
            }

            if (captureThreadStopped && _frameEventWaitHandle != null)
            {
                _frameEventWaitHandle.Dispose();
                _frameEventWaitHandle = null;
            }

            _initialized = false;
            _recordBuffer = Array.Empty<byte>();
            Logger.Information(
                "ProcessLoopbackCapture 释放结束。Pid={ProcessId}, CaptureThreadStopped={CaptureThreadStopped}",
                _targetProcessId,
                captureThreadStopped);
        }

        private void EnsureAudioClientCreated()
        {
            if (_audioClient != null)
            {
                return;
            }

            _audioClient = ActivateAudioClientAsync().GetAwaiter().GetResult();
            _audioClientStreamFlags |= AudioClientStreamFlags.Loopback;
            WaveFormat = new WaveFormat();
        }

        private async Task<AudioClient> ActivateAudioClientAsync()
        {
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree,
                    TargetProcessId = _targetProcessId
                }
            };

            var blobData = Marshal.AllocHGlobal(Marshal.SizeOf(activationParams));
            try
            {
                Marshal.StructureToPtr(activationParams, blobData, false);

                var activateParams = new PropVariant
                {
                    vt = (short)VarEnum.VT_BLOB,
                    blobVal = new Blob
                    {
                        Length = Marshal.SizeOf(activationParams),
                        Data = blobData
                    }
                };

                var propVariant = Marshal.AllocHGlobal(Marshal.SizeOf(activateParams));
                try
                {
                    Marshal.StructureToPtr(activateParams, propVariant, false);

                    AudioClient? activatedAudioClient = null;
                    var completionHandler = new ActivateAudioInterfaceCompletionHandler(audioClientInterface =>
                    {
                        activatedAudioClient = new AudioClient(audioClientInterface);
                    });

                    NativeMethods.ActivateAudioInterfaceAsync(
                        VirtualAudioDeviceProcessLoopback,
                        typeof(IAudioClient).GUID,
                        propVariant,
                        completionHandler,
                        out _);

                    await completionHandler.Completion.ConfigureAwait(false);
                    return activatedAudioClient
                        ?? throw new InvalidOperationException("应用监听音频接口已激活，但未返回 AudioClient。");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"无法启动应用监听音频捕获，请确认系统版本支持 Application Loopback，且目标进程仍在运行。PID={_targetProcessId}, Name={_targetProcessName}",
                        ex);
                }
                finally
                {
                    Marshal.FreeHGlobal(propVariant);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(blobData);
            }
        }

        private void InitializeCaptureDevice()
        {
            if (_initialized)
            {
                return;
            }

            if (_audioClient == null)
            {
                throw new InvalidOperationException("Audio client was not created.");
            }

            long requestedDuration = ReftimesPerMillisecond * AudioBufferMillisecondsLength;
            var streamFlags = _audioClientStreamFlags;

            if (_isUsingEventSync)
            {
                if (ShareMode == AudioClientShareMode.Shared)
                {
                    _audioClient.Initialize(ShareMode, AudioClientStreamFlags.EventCallback | streamFlags, requestedDuration, 0, _waveFormat, Guid.Empty);
                }
                else
                {
                    _audioClient.Initialize(ShareMode, AudioClientStreamFlags.EventCallback | streamFlags, requestedDuration, requestedDuration, _waveFormat, Guid.Empty);
                }

                _frameEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
                _audioClient.SetEventHandle(_frameEventWaitHandle.SafeWaitHandle.DangerousGetHandle());
            }
            else
            {
                _audioClient.Initialize(ShareMode, streamFlags, requestedDuration, 0, _waveFormat, Guid.Empty);
            }

            _bytesPerFrame = _waveFormat.Channels * _waveFormat.BitsPerSample / 8;
            _recordBuffer = new byte[_bufferFrameCount * _bytesPerFrame];
            _initialized = true;
        }

        private void CaptureThread(AudioClient client)
        {
            Exception? exception = null;
            Logger.Information("应用监听音频采集线程启动。Pid={ProcessId}, ManagedThreadId={ThreadId}", _targetProcessId, Environment.CurrentManagedThreadId);

            try
            {
                DoRecording(client);
            }
            catch (Exception ex)
            {
                exception = ex;
                Logger.Error(ex, "应用监听音频采集线程异常退出。Pid={ProcessId}", _targetProcessId);
            }
            finally
            {
                try
                {
                    client.Stop();
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "停止应用监听音频采集客户端失败。Pid={ProcessId}", _targetProcessId);
                }
            }

            _captureThread = null;
            _captureState = CaptureState.Stopped;
            Logger.Information("应用监听音频采集线程退出。Pid={ProcessId}, ManagedThreadId={ThreadId}, Exception={HasException}", _targetProcessId, Environment.CurrentManagedThreadId, exception != null);
            RaiseRecordingStopped(exception);
        }

        private void DoRecording(AudioClient client)
        {
            int bufferFrameCount = client.BufferSize;
            long actualDuration = (long)((double)ReftimesPerSecond * bufferFrameCount / _waveFormat.SampleRate);
            int waitMilliseconds = (int)(3 * actualDuration / ReftimesPerMillisecond);

            var capture = client.AudioCaptureClient;
            client.Start();

            if (_captureState == CaptureState.Starting)
            {
                _captureState = CaptureState.Capturing;
            }

            while (_captureState == CaptureState.Capturing)
            {
                _frameEventWaitHandle?.WaitOne(waitMilliseconds, false);

                if (_captureState != CaptureState.Capturing)
                {
                    break;
                }

                ReadNextPacket(capture);
            }
        }

        private void ReadNextPacket(AudioCaptureClient capture)
        {
            int packetSize = capture.GetNextPacketSize();
            int recordBufferOffset = 0;

            while (packetSize != 0)
            {
                IntPtr buffer = capture.GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);
                int bytesAvailable = framesAvailable * _bytesPerFrame;

                int spaceRemaining = Math.Max(0, _recordBuffer.Length - recordBufferOffset);
                if (spaceRemaining < bytesAvailable && recordBufferOffset > 0)
                {
                    DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, recordBufferOffset));
                    recordBufferOffset = 0;
                }

                if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent)
                {
                    Marshal.Copy(buffer, _recordBuffer, recordBufferOffset, bytesAvailable);
                }
                else
                {
                    Array.Clear(_recordBuffer, recordBufferOffset, bytesAvailable);
                }

                recordBufferOffset += bytesAvailable;
                capture.ReleaseBuffer(framesAvailable);
                packetSize = capture.GetNextPacketSize();
            }

            if (recordBufferOffset > 0)
            {
                DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, recordBufferOffset));
            }
        }

        private void RaiseRecordingStopped(Exception? exception)
        {
            var handler = RecordingStopped;
            if (handler == null)
            {
                return;
            }

            if (_syncContext == null)
            {
                handler(this, new StoppedEventArgs(exception));
                return;
            }

            _syncContext.Post(_ => handler(this, new StoppedEventArgs(exception)), null);
        }

        private static void EnsureApplicationLoopbackSupported()
        {
            if (!AudioPlatformHelper.IsApplicationLoopbackSupported())
            {
                throw new PlatformNotSupportedException(
                    $"Application Loopback 需要更新过的 Windows 10 2004+ 或更新系统，当前 Build={AudioPlatformHelper.GetCurrentWindowsBuildNumber()}。");
            }
        }

        private static void RunInMta(Action action)
        {
            ExceptionDispatchInfo? capturedException = null;
            using var completed = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    capturedException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    completed.Set();
                }
            })
            {
                IsBackground = true,
                Name = "ProcessLoopbackCapture-Init"
            };

            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            completed.Wait();
            capturedException?.Throw();
        }

        private static class NativeMethods
        {
            [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
            public static extern void ActivateAudioInterfaceAsync(
                [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
                [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                [In] IntPtr activationParams,
                [In] IActivateAudioInterfaceCompletionHandler completionHandler,
                out IActivateAudioInterfaceAsyncOperation activationOperation);
        }

        private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            private readonly Action<IAudioClient> _initializeAction;
            private readonly TaskCompletionSource<IAudioClient> _taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ActivateAudioInterfaceCompletionHandler(Action<IAudioClient> initializeAction)
            {
                _initializeAction = initializeAction;
            }

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                activateOperation.GetActivateResult(out int hr, out object unk);
                if (hr != 0)
                {
                    _taskCompletionSource.TrySetException(Marshal.GetExceptionForHR(hr, new IntPtr(-1))!);
                    return;
                }

                var audioClient = (IAudioClient)unk;

                try
                {
                    _initializeAction(audioClient);
                    _taskCompletionSource.SetResult(audioClient);
                }
                catch (Exception ex)
                {
                    _taskCompletionSource.TrySetException(ex);
                }
            }

            public TaskAwaiter<IAudioClient> GetAwaiter()
            {
                return _taskCompletionSource.Task.GetAwaiter();
            }

            public Task<IAudioClient> Completion => _taskCompletionSource.Task;
        }

        [ComImport]
        [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParams ProcessLoopbackParams;
        }

        private enum AudioClientActivationType
        {
            Default = 0,
            ProcessLoopback = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParams
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        private enum ProcessLoopbackMode
        {
            IncludeTargetProcessTree = 0,
            ExcludeTargetProcessTree = 1
        }
    }
}
