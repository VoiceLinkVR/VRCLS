using System;
using System.Collections.Generic;
using System.IO;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.Audio
{
    public class ModelPathResolver : IModelPathResolver
    {
        private static readonly ILogger _logger = Log.ForContext<ModelPathResolver>();
        private readonly AppConfig _config;

        public ModelPathResolver(AppConfig config)
        {
            _logger.Debug("初始化 ModelPathResolver");
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger.Debug("ModelPathResolver 初始化完成");
        }

        public string? ResolveVadModelPath()
        {
            _logger.Debug("开始解析VAD模型路径");

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var rootDir = ResolveRootDir(baseDir);
            var preModelsRoot = GetPreModelsRoot(baseDir, rootDir);

            _logger.Debug("基础目录 - BaseDir: {BaseDir}, PreModelsRoot: {PreModelsRoot}, RootDir: {RootDir}", baseDir, preModelsRoot, rootDir);

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_config.VadModelPath))
            {
                candidates.Add(_config.VadModelPath);
                _logger.Debug("添加配置中的VAD模型路径: {Path}", _config.VadModelPath);
            }

            // Preferred default (bundled next to exe)
            candidates.Add(Path.Combine(preModelsRoot, "silero_vad.onnx"));
            candidates.Add(Path.Combine(preModelsRoot, "vad", "silero_vad.onnx"));

            candidates.Add(Path.Combine(baseDir, "models", "silero_vad.onnx"));
            candidates.Add(Path.Combine(rootDir, "models", "silero_vad.onnx"));
            candidates.Add(Path.Combine(rootDir, "02.VRCLS.Services", "models", "silero_vad.onnx"));
            candidates.Add(Path.Combine(rootDir, "02.VRCLS.Services", "Audio", "models", "silero_vad.onnx"));
            candidates.Add(Path.Combine(rootDir, "tests", "VRCLS.ConsoleTest", "models", "silero_vad.onnx"));

            _logger.Debug("VAD模型候选路径数量: {Count}", candidates.Count);

            foreach (var path in candidates)
            {
                _logger.Verbose("检查VAD模型路径: {Path}", path);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    _logger.Information("找到VAD模型 - 路径: {Path}", path);
                    return path;
                }
            }

            _logger.Warning("未找到VAD模型文件");
            return null;
        }

        private string GetPreModelsRoot(string baseDir, string rootDir)
        {
            var cfg = _config.PreModelsDir;
            if (!string.IsNullOrWhiteSpace(cfg))
            {
                try { return Path.GetFullPath(cfg); } catch { }
            }

            var candidate = Path.Combine(rootDir, "pre-models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(baseDir, "pre-models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            return Path.Combine(baseDir, "pre-models");
        }

        private string ResolveRootDir(string baseDir)
        {
            try
            {
                var current = new DirectoryInfo(baseDir);
                for (var i = 0; i < 8 && current != null; i++)
                {
                    if (File.Exists(Path.Combine(current.FullName, "VRCLS.sln"))
                        || Directory.Exists(Path.Combine(current.FullName, "02.VRCLS.Services"))
                        || Directory.Exists(Path.Combine(current.FullName, "01.VRCLS.Core")))
                    {
                        return current.FullName;
                    }

                    current = current.Parent;
                }
            }
            catch
            {
                // ignore and fall back to legacy path
            }

            return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        }

        public string? ResolveSherpaModelDir()
        {
            _logger.Debug("开始解析Sherpa模型目录");

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var rootDir = ResolveRootDir(baseDir);
            var preModelsRoot = GetPreModelsRoot(baseDir, rootDir);

            _logger.Debug("基础目录 - BaseDir: {BaseDir}, PreModelsRoot: {PreModelsRoot}, RootDir: {RootDir}", baseDir, preModelsRoot, rootDir);

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_config.SherpaModelDir))
            {
                candidates.Add(_config.SherpaModelDir);
                _logger.Debug("添加配置中的Sherpa模型目录: {Path}", _config.SherpaModelDir);
            }

            // Preferred default: bilingual zh-en streaming model (only zh/en is needed).
            candidates.Add(Path.Combine(preModelsRoot, "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"));
            candidates.Add(Path.Combine(preModelsRoot, "zipformer-zh-en"));

            // Legacy layout (dev / older releases)
            candidates.Add(Path.Combine(baseDir, "models", "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"));
            candidates.Add(Path.Combine(rootDir, "models", "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"));
            candidates.Add(Path.Combine(rootDir, "tests", "VRCLS.ConsoleTest", "models", "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"));

            _logger.Debug("Sherpa模型候选目录数量: {Count}", candidates.Count);

            foreach (var path in candidates)
            {
                _logger.Verbose("检查Sherpa模型目录: {Path}", path);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    _logger.Information("找到Sherpa模型目录 - 路径: {Path}", path);
                    return path;
                }
            }

            _logger.Warning("未找到Sherpa模型目录");
            return null;
        }
    }
}
