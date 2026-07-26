using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Recognition;

namespace VRCLS.Services.Audio
{
    public interface IAudioPipelineFactory
    {
        AudioPipelineService CreatePipeline(string source, bool isLoopback);
    }

    /// <summary>
    /// Centralized place to build audio pipelines (capture -> VAD -> recognition) per source.
    /// </summary>
    public class AudioPipelineFactory : IAudioPipelineFactory
    {
        private static readonly ILogger _logger = Log.ForContext<AudioPipelineFactory>();
        private static readonly TimeSpan ModelCatalogTimeout = TimeSpan.FromSeconds(5);

        private readonly AppConfig _config;
        private readonly IModelPathResolver _modelPathResolver;
        private readonly IBackendAuthService _backendAuthService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IModelUpdateService _modelUpdateService;

        public AudioPipelineFactory(
            AppConfig config,
            IModelPathResolver modelPathResolver,
            IBackendAuthService backendAuthService,
            IHttpClientFactory httpClientFactory,
            IModelUpdateService modelUpdateService)
        {
            _logger.Debug("Initializing AudioPipelineFactory");

            _config = config ?? throw new ArgumentNullException(nameof(config));
            _modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));
            _backendAuthService = backendAuthService ?? throw new ArgumentNullException(nameof(backendAuthService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _modelUpdateService = modelUpdateService ?? throw new ArgumentNullException(nameof(modelUpdateService));

            _logger.Information("AudioPipelineFactory initialized. RecognitionMode: {RecognitionMode}", _config.RecognitionBackendModeValue);
        }

        public AudioPipelineService CreatePipeline(string source, bool isLoopback)
        {
            _logger.Information("Creating audio pipeline. Source: {Source}, Loopback: {IsLoopback}", source, isLoopback);

            try
            {
                var useVad = !IsMicStreamingLocalMode(source);
                var vadRoute = _config.ResolveVadRoute(source);

                string? vadPath = null;
                if (useVad)
                {
                    _logger.Debug("Resolving VAD model path.");
                    vadPath = _modelPathResolver.ResolveVadModelPath();

                    if (string.IsNullOrEmpty(vadPath))
                    {
                        _logger.Error("VAD model not found.");
                        throw new InvalidOperationException("silero_vad.onnx not found. Check model path configuration.");
                    }

                    _logger.Debug("VAD model path: {VadPath}", vadPath);
                }
                else
                {
                    _logger.Information("Local streaming mode detected; VAD disabled for low-latency partial updates.");
                }

                _logger.Debug("Creating audio capture service.");
                var capture = new AudioCaptureService();

                IVadService? vad = null;
                if (useVad)
                {
                    _logger.Debug("Creating VAD service.");
                    try
                    {
                        ValidateOfficialVadChecksum(vadPath!);
                        vad = new OfficialSileroVadService(vadPath!, vadRoute.MinSilenceDuration, vadRoute.MaxSegmentSeconds, vadRoute.Threshold);
                        _logger.Information("Using official Silero ONNX VAD implementation. Source: {Source}", source);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Official Silero VAD initialization failed. Falling back to Sherpa VAD wrapper.");
                        vad = new VadService(vadPath!, vadRoute.MinSilenceDuration, vadRoute.MaxSegmentSeconds, vadRoute.Threshold);
                    }

                    _logger.Information(
                        "VAD route config applied. Source={Source}, MinSilence={MinSilence:F2}s, MaxSegment={MaxSegment:F2}s, Threshold={Threshold:F2}",
                        source,
                        vadRoute.MinSilenceDuration,
                        vadRoute.MaxSegmentSeconds,
                        vadRoute.Threshold);
                }

                _logger.Debug("Creating recognition service.");
                var recognition = CreateRecognitionService(source, useVad);

                _logger.Debug("Creating audio pipeline service.");
                var pipeline = new AudioPipelineService(
                    capture,
                    vad,
                    recognition,
                    controlRecognitionService: null,
                    ownsRecognitionService: true,
                    ownsControlRecognitionService: false,
                    sourceName: source);

                _logger.Information("Audio pipeline created. Source: {Source}", source);
                return pipeline;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create audio pipeline. Source: {Source}", source);
                throw;
            }
        }

        private bool IsMicStreamingLocalMode(string source)
        {
            return RecognitionRouteSelector.Select(
                source,
                _config.RecognitionBackendModeValue,
                _config.LocalRecognitionModelValue) == RecognitionRoute.LocalSherpa;
        }

        private IRecognitionService CreateRecognitionService(string source, bool useVad)
        {
            var mode = _config.RecognitionBackendModeValue;
            _logger.Debug("Recognition service mode: {Mode}", mode);

            var route = RecognitionRouteSelector.Select(
                source,
                mode,
                _config.LocalRecognitionModelValue);

            if (route == RecognitionRoute.Remote)
            {
                _logger.Information("Using remote FastAPI recognition service. Source: {Source}", source);
                return new FastApiRecognitionService(_config, source, _backendAuthService, _httpClientFactory);
            }

            if (route == RecognitionRoute.LocalSenseVoice)
            {
                var senseVoiceDir = _modelPathResolver.ResolveSenseVoiceModelDir();
                if (string.IsNullOrWhiteSpace(senseVoiceDir))
                {
                    throw new InvalidOperationException(
                        "Local SenseVoice model is incomplete. Configure a directory containing model.onnx and tokens.txt.");
                }

                _logger.Information(
                    "Using local SenseVoice recognition service. Source: {Source}, ModelDir: {SenseVoiceDir}",
                    source,
                    senseVoiceDir);
                return new SenseVoiceAsrService(senseVoiceDir, source);
            }

            var sherpaDir = _modelPathResolver.ResolveSherpaModelDir();
            if (string.IsNullOrWhiteSpace(sherpaDir))
            {
                throw new InvalidOperationException(
                    "Local Sherpa model is incomplete or missing. Check the local model directory.");
            }

            _logger.Information(
                "Using local Sherpa recognition service. Source: {Source}, ModelDir: {SherpaDir}",
                source,
                sherpaDir);
            var endpointSilence = Math.Clamp(_config.LocalStreamingEndpointSilenceSeconds, 0.8f, 3.0f);
            return new SherpaAsrService(sherpaDir, source, useVad, endpointSilence);
        }

        private void ValidateOfficialVadChecksum(string vadPath)
        {
            if (!ShouldValidateOfficialVadChecksum(vadPath))
            {
                return;
            }

            if (!TryGetCatalogVadChecksum(out var expectedChecksum, out var version))
            {
                return;
            }

            var actualChecksum = ComputeSha256(vadPath);
            if (string.IsNullOrWhiteSpace(actualChecksum))
            {
                return;
            }

            if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Official VAD checksum mismatch. path={vadPath}, expected={expectedChecksum}, actual={actualChecksum}, version={version}");
            }

            _logger.Debug(
                "Official VAD checksum verified. path={VadPath}, version={Version}, checksum={Checksum}",
                vadPath,
                version,
                actualChecksum);
        }

        private bool ShouldValidateOfficialVadChecksum(string vadPath)
        {
            if (string.IsNullOrWhiteSpace(vadPath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_config.VadModelPath))
            {
                _logger.Debug("Skip official VAD checksum validation because VadModelPath is explicitly configured.");
                return false;
            }

            return string.Equals(Path.GetFileName(vadPath), "silero_vad.onnx", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetCatalogVadChecksum(out string checksum, out string version)
        {
            checksum = string.Empty;
            version = string.Empty;

            try
            {
                using var cts = new CancellationTokenSource(ModelCatalogTimeout);
                var models = _modelUpdateService.GetAvailableModelsAsync(cts.Token).GetAwaiter().GetResult();
                var vadModel = models.FirstOrDefault(model =>
                    string.Equals(model.Type, "vad", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(model.Checksum));

                if (vadModel == null)
                {
                    _logger.Debug("Skip official VAD checksum validation because no VAD checksum was found in model catalog.");
                    return false;
                }

                checksum = NormalizeSha256(vadModel.Checksum);
                version = vadModel.Version;
                if (string.IsNullOrWhiteSpace(checksum))
                {
                    _logger.Debug("Skip official VAD checksum validation because the catalog checksum is empty.");
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException
                || ex is HttpRequestException)
            {
                _logger.Warning(ex, "Failed to fetch model catalog for official VAD checksum validation. Continuing without checksum verification.");
                return false;
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static string NormalizeSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            const string prefix = "sha256:";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(prefix.Length);
            }

            return normalized.Replace("-", string.Empty).Trim();
        }
    }
}
