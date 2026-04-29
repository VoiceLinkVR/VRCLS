using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.Json;
using VRCLS.Core.Models;

namespace VRCLS.Core.Helpers
{
    public static class AppConfigStorage
    {
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
                lock (SaveLock)
                {
                    SaveUnsafe(config, GetConfigPath());
                }
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
            lock (SaveLock)
            {
                SaveUnsafe(config, GetConfigPath());
            }
        }

        private static void SaveUnsafe(AppConfig config, string configPath)
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
                    return;
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
