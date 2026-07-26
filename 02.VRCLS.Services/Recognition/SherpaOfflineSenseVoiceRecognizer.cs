using System;
using System.IO;
using SherpaOnnx;

namespace VRCLS.Services.Recognition
{
    internal sealed class SherpaOfflineSenseVoiceRecognizer : IOfflineSenseVoiceRecognizer
    {
        private const int SampleRate = 16000;
        private readonly OfflineRecognizer _recognizer;

        public SherpaOfflineSenseVoiceRecognizer(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
            {
                throw new DirectoryNotFoundException(
                    $"SenseVoice model path not found: {modelPath}");
            }

            var modelFile = Path.Combine(modelPath, "model.onnx");
            var tokensFile = Path.Combine(modelPath, "tokens.txt");
            if (!File.Exists(modelFile) || !File.Exists(tokensFile))
            {
                throw new FileNotFoundException(
                    "SenseVoice requires both model.onnx and tokens.txt.",
                    !File.Exists(modelFile) ? modelFile : tokensFile);
            }

            var config = new OfflineRecognizerConfig
            {
                FeatConfig = new FeatureConfig
                {
                    SampleRate = SampleRate,
                    FeatureDim = 80
                },
                ModelConfig = new OfflineModelConfig
                {
                    SenseVoice = new OfflineSenseVoiceModelConfig
                    {
                        Model = modelFile,
                        Language = "auto",
                        UseInverseTextNormalization = 1
                    },
                    Tokens = tokensFile,
                    Provider = "cpu",
                    NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    Debug = 0
                },
                DecodingMethod = "greedy_search"
            };

            _recognizer = new OfflineRecognizer(config);
        }

        public string Decode(float[] audioData)
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, audioData);
            _recognizer.Decode(stream);
            return stream.Result.Text ?? string.Empty;
        }

        public void Dispose()
        {
            _recognizer.Dispose();
        }
    }
}
