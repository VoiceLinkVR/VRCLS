using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.External;
using VRCLS.Services.Recognition;
using VRCLS.Services.Translation;
using VRCLS.Services.Tts;

// Backend full-path smoke test.
// Tests (configurable): login + recognition send + translation + OSC send + local TTS.
// Config path priority: CLI arg[0] -> env SMOKE_CONFIG -> %AppData%/VRCLS/config.json

var options = SmokeOptions.Parse(args);
var config = LoadConfig(options.ConfigPath);
var apiBase = BuildBaseUrl(config);

Console.WriteLine("=== VRCLS Backend Smoke Test ===");
Console.WriteLine($"API: {apiBase}");
Console.WriteLine($"Languages: {config.SourceLanguage} -> {config.TargetTranslationLanguage}/{config.TargetTranslationLanguage2}/{config.TargetTranslationLanguage3}");
Console.WriteLine($"Run: login={options.TestLogin}, recognition={options.TestRecognition}, translation={options.TestTranslation}, osc={options.TestOsc}, tts={options.TestTts}");

if (options.TestLogin || options.TestRecognition || options.TestTranslation)
{
    Console.WriteLine("\n[Login] starting...");
    var login = new FastApiRecognitionService(config, "SmokeLogin");
    await login.LoginAsync(); // explicit login
    Console.WriteLine("Login attempted (check server for token ok).");
}

if (options.TestRecognition)
{
    Console.WriteLine("\n[Recognition] sending 1s silence (16k mono)...");
    var rec = new FastApiRecognitionService(config, "SmokeRec");
    rec.TextRecognized += (_, r) => Console.WriteLine($"Recognition callback: {r.Text}");
    rec.Start();
    var silence = Enumerable.Repeat(0f, 16000).ToArray(); // 1 second silence
    rec.ProcessAudio(silence);
    await Task.Delay(1000); // wait a bit for server response
    rec.Stop();
    Console.WriteLine("Recognition send completed (silence).");
}

if (options.TestTranslation)
{
    Console.WriteLine("\n[Translation] server translate...");
    ITranslationService translator = new ServerTranslationService(config);
    var sample = "你好，世界";
    var translated = await translator.TranslateAsync(sample, config.TargetTranslationLanguage, config.SourceLanguage);
    Console.WriteLine($"Input: {sample}");
    Console.WriteLine($"Translated: {translated}");
}

if (options.TestOsc)
{
    Console.WriteLine("\n[OSC] sending chatbox message (VRChat OSC)...");
    IOscService osc = new OscService();
    var msg = options.OscMessage ?? $"[SMOKE] {DateTime.Now:HH:mm:ss}";
    osc.SendChatbox(msg, direct: true);
    Console.WriteLine($"OSC chatbox sent: {msg}");
}

if (options.TestTts)
{
    Console.WriteLine("\n[TTS] local System.Speech (best-effort)...");
    ITtsService tts = new SystemSpeechTtsService();
    var text = options.TtsText ?? "TTS 测试";
    await tts.SpeakAsync(text);
    Console.WriteLine("TTS invoked (if silent, System.Speech unavailable).");
}

Console.WriteLine("\nSmoke test finished.");
return;

// Helpers
static AppConfig LoadConfig(string? path)
{
    string resolved = path ?? Environment.GetEnvironmentVariable("SMOKE_CONFIG") ?? DefaultConfigPath();
    if (!File.Exists(resolved))
    {
        throw new FileNotFoundException($"Config not found: {resolved}");
    }
    var json = File.ReadAllText(resolved);
    return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? throw new InvalidOperationException("Failed to parse config");
}

static string BuildBaseUrl(AppConfig config)
{
    var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
        ? $"http://{config.ApiIp}:{config.ApiPort}"
        : config.BaseUrl;

    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    if (!baseUrl.EndsWith("api/")) baseUrl += "api/";
    return baseUrl;
}

static string DefaultConfigPath()
{
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCLS",
        "config.json");
}

record SmokeOptions
{
    public string? ConfigPath { get; init; }
    public bool TestLogin { get; init; } = true;
    public bool TestRecognition { get; init; } = true;
    public bool TestTranslation { get; init; } = true;
    public bool TestOsc { get; init; } = true;
    public bool TestTts { get; init; } = true;
    public string? OscMessage { get; init; }
    public string? TtsText { get; init; }

    public static SmokeOptions Parse(string[] args)
    {
        var opt = new SmokeOptions();
        foreach (var arg in args ?? Array.Empty<string>())
        {
            var kv = arg.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            var val = kv[1].Trim();
            bool Bool(string v) => v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "config": opt = opt with { ConfigPath = val }; break;
                case "login": opt = opt with { TestLogin = Bool(val) }; break;
                case "recognition": opt = opt with { TestRecognition = Bool(val) }; break;
                case "translation": opt = opt with { TestTranslation = Bool(val) }; break;
                case "osc": opt = opt with { TestOsc = Bool(val) }; break;
                case "tts": opt = opt with { TestTts = Bool(val) }; break;
                case "oscmsg": opt = opt with { OscMessage = val }; break;
                case "ttstext": opt = opt with { TtsText = val }; break;
            }
        }
        return opt;
    }
}
