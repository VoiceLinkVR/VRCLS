# VRCLS v0.7.0 静态更新指南（公网桶/CDN）

本指南对应 **纯静态更新系统**：不依赖任何旧 `/api/*` 动态接口。

## 1. 你可以更新的内容

当前更新系统覆盖这 5 类内容：
1. 主程序版本（VRCLS）
2. `translators.exe` 运行时版本
3. `translators.exe` 支持的翻译引擎列表
4. 模型文件版本（VAD/Sherpa/SenseVoice）
5. OpenAI 兼容预设模板

---

## 2. 客户端配置（%AppData%/VRCLS/config.json）

```json
{
  "updateServerUrl": "https://cloudflarestorage.boyqiu001.top/",
  "updateChannel": "stable",
  "updateClientId": "",

  "autoCheckUpdate": true,
  "autoDownloadUpdate": false,
  "autoDownloadModels": true,
  "autoSyncTranslationProfile": true
}
```

说明：
- `updateServerUrl`：静态资源根地址（必须）
- `updateChannel`：`stable/beta/canary`
- `updateClientId`：灰度分桶标识（留空时客户端自动生成稳定标识）

---

## 3. 桶内目录建议

```text
manifests/
  update/
    index.json
    windows-x64-stable.json
    windows-arm64-stable.json
  models/
    list.json
  translations/
    profile.json

releases/
  VRCLS-v0.7.0-win-x64.zip

translator-runtime/
  translators-v0.7.0.zip

models/
  silero_vad.onnx
  sherpa-zh-en-2023-02-20.zip
  sensevoice-small.zip

translation-profiles/
  profile-2026.02.22.json
```

---

## 4. 应用更新清单

## 4.1 `manifests/update/index.json`

```json
{
  "schema": 1,
  "defaultChannel": "stable",
  "manifests": {
    "windows-x64": {
      "stable": "/manifests/update/windows-x64-stable.json"
    },
    "windows-arm64": {
      "stable": "/manifests/update/windows-arm64-stable.json"
    }
  }
}
```

## 4.2 `manifests/update/windows-x64-stable.json`

```json
{
  "latestVersion": "0.7.0.1000",
  "displayVersion": "0.7.0",
  "minimumSupportedVersion": "0.6.0.1000",
  "forceUpdateBelow": "0.5.5.1000",
  "rolloutPercent": 100,
  "rolloutSeed": "vrcls-0.7.0.1000",
  "releaseNotes": "VRCLS v0.7.0",
  "blockedVersions": [],
  "release": {
    "url": "https://cloudflarestorage.boyqiu001.top/releases/VRCLS-v0.7.0-win-x64.zip",
    "fileSize": 123456789,
    "checksum": "sha256:..."
  }
}
```

---

## 5. 模型目录清单

`manifests/models/list.json`

```json
{
  "models": [
    {
      "name": "silero-vad-v1",
      "type": "vad",
      "version": "1.0.0",
      "downloadUrl": "https://cloudflarestorage.boyqiu001.top/models/silero_vad.onnx",
      "fileSize": 1807522,
      "checksum": "sha256:...",
      "installPath": "silero_vad.onnx",
      "archiveRoot": "",
      "required": true
    }
  ]
}
```

---

## 6. 翻译与 translators 运行时清单

`manifests/translations/profile.json`

```json
{
  "hasUpdate": true,
  "version": "2026.02.22",
  "downloadUrl": "https://cloudflarestorage.boyqiu001.top/translation-profiles/profile-2026.02.22.json",
  "checksum": "sha256:...",
  "translators": {
    "version": "0.7.0.1000",
    "downloadUrl": "https://cloudflarestorage.boyqiu001.top/translator-runtime/translators-v0.7.0.zip",
    "checksum": "sha256:..."
  },
  "profile": {
    "translationServer": "google",
    "translateService": "google",
    "translateServicecap": "google",
    "sourceLanguage": "zh",
    "targetTranslationLanguage": "en",
    "targetTranslationLanguage2": "ja",
    "targetTranslationLanguage3": "none",
    "supportedTranslationEngines": [
      "google", "bing", "baidu", "yandex", "deepl"
    ],
    "openaiPresetTemplates": [
      {
        "name": "OpenAI GPT-4o-mini",
        "base_url": "https://api.openai.com/v1",
        "model": "gpt-4o-mini",
        "temperature": 0.3,
        "max_tokens": 1000,
        "system_prompt": ""
      },
      {
        "name": "GLM-4-Flash",
        "base_url": "https://open.bigmodel.cn/api/paas/v4/",
        "model": "glm-4-flash",
        "temperature": 0.3,
        "max_tokens": 1000,
        "system_prompt": ""
      }
    ],
    "openai_config": {
      "api_key": "",
      "base_url": "https://api.openai.com/v1",
      "model": "gpt-4o-mini",
      "temperature": 0.3,
      "max_tokens": 1000,
      "system_prompt": ""
    }
  }
}
```

注意：
- `api_key` 不应下发真实密钥；客户端会保留用户本地填写的密钥。
- `translators` 更新后会安装到 `%AppData%/VRCLS/translator-runtime/current`。

---

## 7. 发布流程

1. 上传新主程序包到 `releases/`
2. 更新平台清单 `latestVersion/release/checksum`
3. 上传 translators runtime 包到 `translator-runtime/`
4. 更新 `manifests/translations/profile.json` 的 `translators.version/downloadUrl/checksum`
5. 如需更新引擎列表或 OpenAI 预设，更新 `profile.supportedTranslationEngines` 与 `profile.openaiPresetTemplates`
6. 上传模型并更新 `manifests/models/list.json`

---

## 8. 缓存策略

- `manifests/update/index.json`: `Cache-Control: no-cache`
- `manifests/update/*.json`: `Cache-Control: no-cache`
- `manifests/models/list.json`: `Cache-Control: no-cache`
- `manifests/translations/profile.json`: `Cache-Control: no-cache`
- 大文件（`releases/*`, `models/*`, `translator-runtime/*`, `translation-profiles/*`）：长缓存 + 文件名带版本

---

## 9. SHA256 生成（Windows）

```powershell
Get-FileHash .\VRCLS-v0.7.0-win-x64.zip -Algorithm SHA256
Get-FileHash .\translators-v0.7.0.zip -Algorithm SHA256
Get-FileHash .\silero_vad.onnx -Algorithm SHA256
Get-FileHash .\profile-2026.02.22.json -Algorithm SHA256
```

---

## 10. 最小验收清单

1. 主程序更新可检测并下载
2. translators 运行时版本变更后可自动替换
3. 翻译引擎下拉随 `supportedTranslationEngines` 变化
4. OpenAI 预设模板可在设置页选择并生效（不覆盖 API Key）
5. 模型目录刷新与安装可用
