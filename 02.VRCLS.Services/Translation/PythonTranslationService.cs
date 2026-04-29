using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Translation
{
    /// <summary>
    /// Calls a local Python script which uses the "translators" library.
    /// </summary>
    public class PythonTranslationService : ITranslationService
    {
        private static readonly ILogger _logger = Log.ForContext<PythonTranslationService>();
        private readonly AppConfig _config;

        public PythonTranslationService(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(string.Empty);
            return TranslateWithPythonAsync(text, targetLanguage, sourceLanguage, routeHint);
        }

        public Task<string> TranslateBatchAsync(string[] texts, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (texts == null || texts.Length == 0) return Task.FromResult(string.Empty);
            var joined = string.Join("\n", texts);
            return TranslateWithPythonAsync(joined, targetLanguage, sourceLanguage, routeHint);
        }

        private async Task<string> TranslateWithPythonAsync(string text, string targetLanguage, string? sourceLanguage, string? routeHint)
        {
            var translator = _config.ResolveTranslationEngine(routeHint);
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "translator.py");

            // Release-friendly: prefer a bundled standalone translator tool (no Python required on client machines).
            // Builder can produce this with PyInstaller and place it under tools/translator.exe.
            var bundledExe = Path.Combine(AppContext.BaseDirectory, "tools", "translator.exe");
            if (File.Exists(bundledExe))
            {
                return await TranslateWithProcessAsync(
                    fileName: bundledExe,
                    arguments: string.Empty,
                    text: text,
                    translator: translator,
                    sourceLanguage: sourceLanguage,
                    targetLanguage: targetLanguage);
            }

            // Fallback: external Python. This may not exist on client machines.
            var python = string.IsNullOrWhiteSpace(_config.PythonPath) ? "python" : _config.PythonPath.Trim();
            if (!File.Exists(script))
            {
                _logger.Warning("translator.py not found: {Path}", script);
                return string.Empty;
            }

            return await TranslateWithProcessAsync(
                fileName: python,
                arguments: $"\"{script}\"",
                text: text,
                translator: translator,
                sourceLanguage: sourceLanguage,
                targetLanguage: targetLanguage);
        }

        private async Task<string> TranslateWithProcessAsync(
            string fileName,
            string arguments,
            string text,
            string translator,
            string? sourceLanguage,
            string targetLanguage)
        {
            var input = new PythonTranslateRequest
            {
                Text = text,
                Translator = translator,
                FromLang = sourceLanguage ?? _config.SourceLanguage ?? "auto",
                ToLang = targetLanguage
            };

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8
            };

            var region = (_config.TranslatorsDefaultRegion ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(region))
            {
                psi.Environment["translators_default_region"] = region;
            }

            try
            {
                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    _logger.Warning("翻译进程启动失败: {FileName}", fileName);
                    return string.Empty;
                }

                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
                process.StandardInput.Close();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20));
                var exitTask = process.WaitForExitAsync();
                var completed = await Task.WhenAny(exitTask, timeoutTask);
                if (completed != exitTask)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    _logger.Warning("翻译超时，已终止进程: {FileName}", fileName);
                    return string.Empty;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.Debug("翻译 stderr ({FileName}): {Stderr}", fileName, stderr.Trim());
                }

                var result = JsonSerializer.Deserialize<PythonTranslateResponse>(stdout);
                if (result?.Success == true)
                {
                    return result.Text ?? string.Empty;
                }

                _logger.Warning("翻译失败 ({FileName}): {Error}", fileName, result?.Error ?? "unknown");
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "翻译异常 (file={FileName}, args={Args}, translator={Translator})",
                    fileName, arguments, translator);
                return string.Empty;
            }
        }

        private class PythonTranslateRequest
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("translator")]
            public string Translator { get; set; } = "google";

            [JsonPropertyName("from_lang")]
            public string FromLang { get; set; } = "auto";

            [JsonPropertyName("to_lang")]
            public string ToLang { get; set; } = "en";
        }

        private class PythonTranslateResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }
        }
    }
}
