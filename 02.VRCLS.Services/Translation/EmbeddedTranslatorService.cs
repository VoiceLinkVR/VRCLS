using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Translation
{
    /// <summary>
    /// Embedded python translator bridge.
    /// Prefers a bundled translator.exe (PyInstaller) and falls back to running scripts/translator.py via an external Python.
    ///
    /// Protocol: line-delimited JSON over stdin/stdout. The process prints {"status":"ready"} once on startup.
    /// </summary>
    public sealed class EmbeddedTranslatorService : ITranslationService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<EmbeddedTranslatorService>();
        private readonly AppConfig _config;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private Process? _process;
        private StreamWriter? _stdin;
        private StreamReader? _stdout;
        private Task? _stderrPump;
        private readonly object _stderrLock = new();
        private readonly Queue<string> _stderrRing = new();
        private const int StderrRingCapacity = 80;

        public EmbeddedTranslatorService(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(string.Empty);
            return TranslateInternalAsync(text, targetLanguage, sourceLanguage, routeHint);
        }

        public Task<string> TranslateBatchAsync(string[] texts, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (texts == null || texts.Length == 0) return Task.FromResult(string.Empty);
            var joined = string.Join("\n", texts);
            return TranslateInternalAsync(joined, targetLanguage, sourceLanguage, routeHint);
        }

        public async Task WarmupAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureProcessStartedAsync().ConfigureAwait(false);
                if (_process == null || _process.HasExited || _stdin == null || _stdout == null)
                {
                    return;
                }

                await _stdin.WriteLineAsync(JsonSerializer.Serialize(new { cmd = "warmup" })).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);

                var line = await ReadLineWithTimeoutAsync(_stdout, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (!IsStatusLine(line, "ready"))
                {
                    _logger.Warning("Embedded translator warmup failed: {Line}", line ?? "<null>");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Embedded translator warmup exception.");
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ReloadAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                RestartProcess();
                await EnsureProcessStartedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Embedded translator reload exception.");
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<string> TranslateInternalAsync(string text, string targetLanguage, string? sourceLanguage, string? routeHint)
        {
            // Serialize per request because stdin/stdout is a single shared stream.
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureProcessStartedAsync().ConfigureAwait(false);
                if (_process == null || _process.HasExited || _stdin == null || _stdout == null)
                {
                    return string.Empty;
                }

                var req = new TranslatorRequest
                {
                    Cmd = "translate",
                    Text = text,
                    Translator = _config.ResolveTranslationEngine(routeHint),
                    FromLang = (sourceLanguage ?? _config.SourceLanguage ?? "auto").Trim(),
                    ToLang = (targetLanguage ?? "en").Trim(),
                };

                var json = JsonSerializer.Serialize(req);
                await _stdin.WriteLineAsync(json).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);

                // Read a single JSON line response.
                var responseLine = await ReadLineWithTimeoutAsync(_stdout, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                if (responseLine == null)
                {
                    _logger.Warning("Embedded translator timeout (no response). stderr_tail={StderrTail}", GetRecentStderrTail());
                    RestartProcess();
                    return string.Empty;
                }

                TranslatorResponse? resp;
                try
                {
                    resp = JsonSerializer.Deserialize<TranslatorResponse>(responseLine);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Embedded translator returned invalid JSON: {Line}; stderr_tail={StderrTail}",
                        responseLine, GetRecentStderrTail());
                    RestartProcess();
                    return string.Empty;
                }

                if (resp?.Success == true)
                {
                    return resp.Text ?? string.Empty;
                }

                _logger.Warning("Embedded translator failed: {Error}; type={ErrorType}; traceback={Traceback}; stderr_tail={StderrTail}",
                    resp?.Error ?? "unknown",
                    resp?.ErrorType ?? "<none>",
                    TrimForLog(resp?.Traceback),
                    GetRecentStderrTail());
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Embedded translator exception. Restarting translator process. stderr_tail={StderrTail}",
                    GetRecentStderrTail());
                RestartProcess();
                return string.Empty;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task EnsureProcessStartedAsync()
        {
            if (_process != null && !_process.HasExited)
            {
                return;
            }

            RestartProcess(); // cleanup old state

            var command = TranslatorRuntimeCapabilitiesLoader.ResolveTranslatorCommand(_config);
            var fileName = command.FileName;
            var args = command.Arguments;
            var mode = command.Mode;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.Warning("No embedded translator available (translator.exe not found, and python/script not available).");
                return;
            }

            _logger.Information("Starting embedded translator ({Mode}): {FileName} {Args}", mode, fileName, args);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
            };

            // Force UTF-8 stdio for both python.exe mode and pyinstaller exe mode.
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var region = (_config.TranslatorsDefaultRegion ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(region))
            {
                psi.Environment["translators_default_region"] = region;
            }

            var p = new Process { StartInfo = psi };
            if (!p.Start())
            {
                _logger.Warning("Failed to start embedded translator process: {FileName}", fileName);
                return;
            }

            _process = p;
            _stdin = p.StandardInput;
            _stdout = p.StandardOutput;

            // Pump stderr to avoid deadlocks.
            _stderrPump = Task.Run(async () =>
            {
                try
                {
                    while (!p.HasExited)
                    {
                        var line = await p.StandardError.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var trimmed = line.Trim();
                            AppendStderrLine(trimmed);
                            _logger.Warning("translator stderr: {Line}", trimmed);
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            });

            // Expect a ready line.
            var readyLine = await ReadLineWithTimeoutAsync(_stdout, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (readyLine == null)
            {
                _logger.Warning("Embedded translator did not become ready in time; killing process.");
                RestartProcess();
                return;
            }

            if (!IsReadyLine(readyLine))
            {
                _logger.Warning("Embedded translator unexpected ready line: {Line}", readyLine.Trim());
                // Still allow using it; next line may be a response.
            }
        }

        private void AppendStderrLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_stderrLock)
            {
                _stderrRing.Enqueue(line);
                while (_stderrRing.Count > StderrRingCapacity)
                {
                    _stderrRing.Dequeue();
                }
            }
        }

        private string GetRecentStderrTail(int maxLines = 10)
        {
            lock (_stderrLock)
            {
                if (_stderrRing.Count == 0)
                {
                    return "<none>";
                }

                return string.Join(" | ", _stderrRing.TakeLast(Math.Max(1, maxLines)));
            }
        }

        private void ClearStderrBuffer()
        {
            lock (_stderrLock)
            {
                _stderrRing.Clear();
            }
        }

        private static string TrimForLog(string? value, int maxLength = 4000)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<none>";
            }

            var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized[..maxLength] + "...(truncated)";
        }

        private static bool IsReadyLine(string line)
        {
            return IsStatusLine(line, "ready");
        }

        private static bool IsStatusLine(string? line, string expected)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                if (doc.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                {
                    return string.Equals(status.GetString(), expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
        {
            var readTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != readTask) return null;
            return await readTask.ConfigureAwait(false);
        }

        private void RestartProcess()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    try
                    {
                        // best-effort graceful exit
                        _stdin?.WriteLine(JsonSerializer.Serialize(new { cmd = "exit" }));
                        _stdin?.Flush();
                    }
                    catch { }

                    if (!_process.WaitForExit(1000))
                    {
                        try { _process.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }

            try { _stdin?.Dispose(); } catch { }
            try { _stdout?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }

            _stdin = null;
            _stdout = null;
            _process = null;
            _stderrPump = null;
            ClearStderrBuffer();
        }

        public void Dispose()
        {
            var acquired = false;
            try
            {
                acquired = _lock.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }

            try
            {
                RestartProcess();
            }
            finally
            {
                if (acquired)
                {
                    try { _lock.Release(); } catch { }
                }
                _lock.Dispose();
            }
        }

        private sealed class TranslatorRequest
        {
            [JsonPropertyName("cmd")]
            public string Cmd { get; set; } = "translate";

            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("translator")]
            public string Translator { get; set; } = "google";

            [JsonPropertyName("from_lang")]
            public string FromLang { get; set; } = "auto";

            [JsonPropertyName("to_lang")]
            public string ToLang { get; set; } = "en";
        }

        private sealed class TranslatorResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("error_type")]
            public string? ErrorType { get; set; }

            [JsonPropertyName("traceback")]
            public string? Traceback { get; set; }
        }
    }
}
