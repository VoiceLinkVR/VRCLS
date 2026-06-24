using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Text.Json;
using Serilog;
using VRCLS.Core.Models;

namespace VRCLS.Core.Helpers
{
    public static class AppConfigStorage
    {
        private const string ConfigSnapshotCompressionAlgorithm = "GZip";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        private static readonly object SaveLock = new();

        public static string GetConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS",
                "config.json");
        }

        public static bool TrySave(AppConfig config)
        {
            return TrySave(config, out _);
        }

        public static bool TrySave(AppConfig config, out string? errorMessage)
        {
            errorMessage = null;

            if (config == null)
            {
                errorMessage = "Config is null.";
                return false;
            }

            try
            {
                string json;
                lock (SaveLock)
                {
                    json = SaveUnsafe(config, GetConfigPath());
                }
                LogCompressedConfigSnapshot(json, "save");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static void SaveOrThrow(AppConfig config)
        {
            string json;
            lock (SaveLock)
            {
                json = SaveUnsafe(config, GetConfigPath());
            }
            LogCompressedConfigSnapshot(json, "save");
        }

        public static void LogCompressedConfigSnapshot(AppConfig config, string reason)
        {
            var logger = Log.ForContext(typeof(AppConfigStorage));
            try
            {
                if (config == null)
                {
                    throw new ArgumentNullException(nameof(config));
                }

                var json = JsonSerializer.Serialize(config, SerializerOptions);
                LogCompressedConfigSnapshot(json, reason, logger);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "记录配置快照压缩数据失败。reason={Reason}", reason);
            }
        }

        private static void LogCompressedConfigSnapshot(string json, string reason)
        {
            var logger = Log.ForContext(typeof(AppConfigStorage));
            try
            {
                LogCompressedConfigSnapshot(json, reason, logger);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "记录配置快照压缩数据失败。reason={Reason}", reason);
            }
        }

        private static void LogCompressedConfigSnapshot(string json, string reason, ILogger logger)
        {
            var snapshot = CreateCompressedConfigSnapshot(json);
            logger.Information(
                "CONFIG_SNAPSHOT_V1 reason={Reason} algorithm={Algorithm} jsonBytes={JsonByteCount} compressedBytes={CompressedByteCount} base64={CompressedConfigBase64}",
                reason,
                ConfigSnapshotCompressionAlgorithm,
                snapshot.JsonByteCount,
                snapshot.CompressedByteCount,
                snapshot.Base64);
        }

        public static (string Base64, int JsonByteCount, int CompressedByteCount) CreateCompressedConfigSnapshot(AppConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var json = JsonSerializer.Serialize(config, SerializerOptions);
            return CreateCompressedConfigSnapshot(json);
        }

        private static (string Base64, int JsonByteCount, int CompressedByteCount) CreateCompressedConfigSnapshot(string json)
        {
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(jsonBytes, 0, jsonBytes.Length);
            }

            var compressedBytes = output.ToArray();
            return (
                Convert.ToBase64String(compressedBytes),
                jsonBytes.Length,
                compressedBytes.Length);
        }

        public static string DecodeCompressedConfigSnapshot(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ArgumentException("Snapshot base64 is required.", nameof(base64));
            }

            var compressedBytes = Convert.FromBase64String(base64.Trim());
            using var input = new MemoryStream(compressedBytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string SaveUnsafe(AppConfig config, string configPath)
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, SerializerOptions);
            var tempPath = configPath + ".tmp";
            var backupPath = configPath + ".bak";
            var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            IOException? lastIoException = null;
            UnauthorizedAccessException? lastUnauthorized = null;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.WriteAllText(tempPath, json, utf8WithoutBom);

                    if (File.Exists(configPath))
                    {
                        File.Replace(tempPath, configPath, backupPath, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tempPath, configPath);
                    }
                    return json;
                }
                catch (IOException ex)
                {
                    lastIoException = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastUnauthorized = ex;
                }
                finally
                {
                    TryDeleteTempFile(tempPath);
                }

                Thread.Sleep(50 * (attempt + 1));
            }

            if (lastUnauthorized != null)
            {
                throw lastUnauthorized;
            }

            if (lastIoException != null)
            {
                throw lastIoException;
            }

            throw new IOException($"Failed to save config to '{configPath}'.");
        }

        private static void TryDeleteTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                return;
            }
        }
    }
}
