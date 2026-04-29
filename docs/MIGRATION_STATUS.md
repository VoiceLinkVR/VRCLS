# VRCLS Python → C# 迁移状态报告

## 概述

本文档详细对比了 Python 原项目 (`VRCLS-master`) 与 C# 重写项目 (`VRCLS-CSharp`) 的功能差异，并标注了各功能的迁移状态。

**最后更新**: 2025-02-01

---

## 一、功能迁移状态总览

| 功能模块 | Python | C# | 状态 |
|---------|--------|-----|------|
| 配置系统 | ✅ | ✅ | ✅ 已完成 |
| 音频捕获 (麦克风) | ✅ | ✅ | ✅ 已完成 |
| 音频捕获 (桌面音频/WASAPI) | ✅ | ✅ | ✅ 已完成 |
| VAD 语音检测 (Silero ONNX) | ✅ | ✅ | ✅ 已完成 |
| 本地语音识别 (SherpaOnnx) | ✅ | ✅ | ✅ 已完成 |
| 远程语音识别 | ✅ | ✅ | ✅ 已完成 |
| OSC 客户端 | ✅ | ✅ | ✅ 已完成 |
| OSC 服务器 | ✅ | ✅ | ✅ 已完成 |
| 翻译系统 (开发者服务器) | ✅ | ✅ | ✅ 已完成 |
| 翻译系统 (OpenAI) | ✅ | ❌ | ❌ 未实现 |
| 翻译系统 (Translators库) | ✅ | ⚠️ | ⚠️ 部分实现 (需打包) |
| ChatBox 输出 | ✅ | ✅ | ✅ 已完成 |
| VRCBitmapLed 点阵屏 | ✅ | ❌ | ❌ 未实现 |
| SteamVR Overlay | ✅ | ✅ | ✅ 已完成 |
| TTS 语音合成 (本地) | ✅ | ✅ | ✅ 已完成 |
| TTS 语音合成 (服务器) | ✅ | ✅ | ✅ 已完成 |
| 热键系统 (全局热键) | ✅ | ✅ | ✅ 已完成 |
| 热键系统 (按住说话) | ✅ | ✅ | ✅ 已完成 |
| 语音命令/脚本系统 | ✅ | ✅ | ✅ 已完成 |
| 默认命令 (模式切换) | ✅ | ✅ | ✅ 已完成 |
| Avatar 参数控制 | ✅ | ✅ | ✅ 已完成 |
| VRC 麦克风状态同步 | ✅ | ✅ | ✅ 已完成 |
| 模型自动下载/解压 | ✅ | ❌ | ❌ 未实现 |
| 自动更新系统 | ✅ | ❌ | ❌ 未实现 (方案已定) |
| 透明悬浮窗 | ✅ | ❌ | ❌ 未实现 |
| 剪贴板功能 (CopyBox) | ✅ | ⚠️ | ⚠️ 部分实现 |
| 日志统计系统 | ✅ | ✅ | ✅ 已完成 (Serilog) |
| 繁简转换 | ✅ | ❌ | ❌ 未实现 |
| 自定义 Emoji 映射 | ✅ | ❌ | ❌ 未实现 |
| 过滤词列表 | ✅ | ❌ | ❌ 未实现 |
| Web UI (Flask) | ✅ | ❌ | ❌ 不需要 (C#使用WPF) |
| WebSocket 日志推送 | ✅ | ❌ | ❌ 不需要 |

---

## 二、详细功能对比

### 2.1 配置系统

#### Python 配置项 (defaultConfig.py)
```python
defaultConfig = {
    "userInfo": {"username": "", "password": ""},
    "baseurl": "https://whisper.boyqiu001.cn:7070",
    "api-ip": "127.0.0.1",
    "api-port": 8980,
    "osc-port": 9000,
    "osc-ip": "127.0.0.1",
    "defaultMode": "translation",
    "activateText": "",
    "stopText": "",
    "micName": "default",
    "voiceMode": 0,  # 0=常开, 1=按键切换, 2=按住说话, 3=VRC同步
    "webBrowserPath": "",
    "dynamicThreshold": False,
    "customThreshold": 0.01,
    "voiceHotKey_new": "<alt>+1",
    "sourceLanguage": "zh",
    "targetTranslationLanguage": "en",
    "targetTranslationLanguage2": "none",
    "targetTranslationLanguage3": "none",
    "translationServer": "libre",
    "VRCBitmapLed_row": 8,
    "VRCBitmapLed_col": 16,
    "VRCBitmapLed_COLOR": True,
    "Separate_Self_Game_Mic": 0,
    "gameMicName": "default",
    "gameVoiceMode": 0,
    "gameCustomThreshold": 0.01,
    "gameVoiceHotKey_new": "<alt>+2",
    "textInSteamVR": False,
    "SteamVRHad": 0,  # 0=右手, 1=左手, 2=双手
    "SteamVRSize": 0.15,
    "localizedSpeech": False,
    "localizedCapture": False,
    "CopyBox": False,
    "TTSToggle": 0,  # 0=关闭, 1=翻译模式麦克风, 2=文字模式, 3=混合
    "TTSOutputName": "default",
    "translateService": "developer",
    "translateRegion": "CN",
    "oscShutdown": False,
    "VRCChatboxformat_new": "{translatedText}\n{translatedText2}\n{translatedText3}\n{text}",
    "VRCChatboxformat_text": "{text}",
    "realtimeOutputDelay": -1.0,
    "micPressingKey": "v",
    "capPressingKey": "b",
    "enableOscServer": False,
    "oscServerIp": "127.0.0.1",
    "oscServerPort": 9003,
    "chatboxOscMixTemplate": "{clientdata}{serverdata}",
    "darkmode": False,
    "transparentWindow": False,
    "transparentWindowSize": "large",
    "overlayWidth": 800,
    "overlayHeight": 500,
    "filteremoji": "false",
    "translateServicecap": "developer",
    "capOutputStyle": 0,
    "openai_config": {
        "api_key": "",
        "base_url": "https://open.bigmodel.cn/api/paas/v4/",
        "model": "glm-4-flash"
    },
    "defaultScripts": [...],  # 8个内置命令
    "scripts": [...]  # 自定义脚本
}
```

#### C# 配置项 (AppConfig.cs)
**状态**: ✅ 已完成 - 配置项基本对齐

**缺失配置项**:
- ❌ `webBrowserPath` - 浏览器路径 (C#不需要)
- ❌ `capOutputStyle` - 桌面音频输出风格

---

### 2.2 音频处理系统

#### 2.2.1 麦克风模式 (voiceMode)

| 模式 | Python | C# |
|------|--------|-----|
| 0 - 常开模式 | ✅ | ✅ |
| 1 - 按键切换模式 | ✅ | ✅ |
| 2 - 按住说话模式 | ✅ | ✅ |
| 3 - VRC麦克风同步模式 | ✅ | ✅ |

**状态**: ✅ 已完成
- `HotkeyService.cs` - 全局热键和按住说话
- `OscService.cs` - OSC 服务器监听 MuteSelf 状态

#### 2.2.2 VAD 语音检测

| 功能 | Python | C# |
|------|--------|-----|
| Silero VAD ONNX | ✅ | ✅ |
| 动态阈值 | ✅ | ✅ |
| 自定义阈值 | ✅ | ✅ |
| 静音检测 | ✅ | ✅ |

**状态**: ✅ 已完成

---

### 2.3 语音识别系统

#### 2.3.1 本地识别 (SherpaOnnx)

| 功能 | Python | C# |
|------|--------|-----|
| 流式识别 | ✅ | ✅ |
| 中英双语模型 | ✅ | ✅ |
| 多语言模型 | ✅ | ✅ |
| 模型自动下载 | ✅ | ❌ |
| 模型自动解压 (7z) | ✅ | ❌ |
| 中文路径检测 | ✅ | ❌ |

**缺失功能**:
- ❌ `module_download()` - 模型自动下载
- ❌ 7z 解压支持 (`py7zr`)
- ❌ 中文路径检测和警告

#### 2.3.2 远程识别

**状态**: ✅ 已完成

---

### 2.4 翻译系统

| 翻译引擎 | Python | C# |
|---------|--------|-----|
| developer (开发者服务器) | ✅ | ✅ |
| openai (OpenAI/GLM) | ✅ | ❌ (待实现) |
| google (Google翻译) | ✅ | ⚠️ (需打包) |
| baidu (百度翻译) | ✅ | ⚠️ (需打包) |
| bing (Bing翻译) | ✅ | ⚠️ (需打包) |
| 其他 translators 引擎 | ✅ | ⚠️ (需打包) |

**已实现**:
- ✅ `ServerTranslationService.cs` - 开发者服务器翻译
- ✅ `PythonTranslationService.cs` - 调用 Python translators 库
- ✅ `TranslationServiceManager.cs` - 多引擎路由

**待实现**:
- ❌ `OpenAiTranslationService.cs` - OpenAI 通用接口翻译
- ❌ 内嵌 Python 翻译器 (PyInstaller 打包，用户无需配置环境)

---

### 2.5 VRChat 集成

#### 2.5.1 OSC 通信

| 功能 | Python | C# |
|------|--------|-----|
| OSC 客户端发送 | ✅ | ✅ |
| OSC 服务器接收 | ✅ | ⚠️ |
| ChatBox 输出 | ✅ | ✅ |
| 实时输出延迟 | ✅ | ✅ |

#### 2.5.2 VRCBitmapLed 点阵屏

| 功能 | Python | C# |
|------|--------|-----|
| 点阵屏显示 | ✅ | ❌ |
| 彩色支持 | ✅ | ❌ |
| 半角转全角 | ✅ | ❌ |
| 自动换行 | ✅ | ❌ |
| 任务队列 | ✅ | ❌ |

**缺失文件**:
- ❌ `VRCBitmapLedHandler.cs`
- ❌ `bitLedColor.cs` (颜色映射)

#### 2.5.3 SteamVR Overlay

| 功能 | Python | C# |
|------|--------|-----|
| 掌心显示 | ✅ | ✅ |
| 单手/双手模式 | ✅ | ✅ |
| 手柄连接检测 | ✅ | ✅ |
| 自动恢复 | ✅ | ✅ |
| 纹理渲染 | ✅ | ✅ |

**状态**: ✅ 已完成
- `SteamVROverlayService.cs` - VR 覆盖显示服务

---

### 2.6 语音命令系统

#### 2.6.1 默认命令 (DefaultCommand)

| 命令 | Python | C# |
|------|--------|-----|
| 切换到文字发送模式 | ✅ | ✅ |
| 切换到翻译模式 | ✅ | ✅ |
| 切换到控制模式 | ✅ | ✅ |
| 切换到点阵屏模式 | ✅ | ✅ |
| 切换翻译语言 (英/日/俄/韩) | ✅ | ✅ |

**状态**: ✅ 已完成
- `DefaultCommandHandler.cs` - 8 个内置命令
- `CommandService.cs` - 命令匹配和执行

#### 2.6.2 自定义脚本 (Avatar控制)

| 功能 | Python | C# |
|------|--------|-----|
| 脚本匹配执行 | ✅ | ✅ |
| VRC参数控制 (float/int/bool) | ✅ | ✅ |
| 执行延迟 (sleeptime) | ✅ | ✅ |
| 语音反馈 (TTS) | ✅ | ✅ |

**状态**: ✅ 已完成
- `ScriptExecutor.cs` - 脚本执行器

---

### 2.7 TTS 语音合成

| 功能 | Python | C# |
|------|--------|-----|
| Edge TTS (服务器) | ✅ | ✅ |
| 本地 TTS | ✅ | ✅ |
| 流式播放 | ✅ | ✅ |
| 多语言语音映射 | ✅ | ✅ |
| TTS模式切换 | ✅ | ✅ |
| 虚拟麦克风输出 | ✅ | ❌ |

**状态**: ✅ 基本完成
- `SystemSpeechTtsService.cs` - 本地 System.Speech
- `ServerTtsService.cs` - 服务器 EdgeTTS
- `TtsServiceManager.cs` - 引擎切换

---

### 2.8 热键系统

| 功能 | Python | C# |
|------|--------|-----|
| 全局热键 (GlobalHotKeys) | ✅ | ✅ |
| 按住说话 (VKeyHandler) | ✅ | ✅ |
| 热键配置 | ✅ | ✅ |
| 延迟释放 | ✅ | ✅ |

**状态**: ✅ 已完成
- `HotkeyService.cs` - Windows 低级键盘钩子实现

---

### 2.9 更新系统

| 功能 | Python | C# |
|------|--------|-----|
| 版本检查 | ✅ | ❌ (方案已定) |
| 多线程下载 | ✅ | ❌ (方案已定) |
| 进度显示 | ✅ | ❌ (方案已定) |
| 7z 解压 | ✅ | ❌ |
| 自动安装 | ✅ | ❌ (方案已定) |

**待实现** (使用 Velopack):
- ❌ `VelopackUpdateService.cs`

详见 `docs/UPDATE_FEATURE_SPEC.md`

---

### 2.10 其他功能

| 功能 | Python | C# |
|------|--------|-----|
| 透明悬浮窗 | ✅ | ❌ |
| 剪贴板监听 (CopyBox) | ✅ | ❌ |
| 日志统计 (SQLite) | ✅ | ⚠️ |
| WebSocket 日志推送 | ✅ | ❌ |
| 过滤词列表 | ✅ | ❌ |
| 自定义 Emoji 映射 | ✅ | ❌ |

---

## 三、迁移优先级建议

### 高优先级 (核心功能)
1. **OpenAI 通用接口翻译** - 支持 GLM/DeepSeek 等兼容 API
2. **内嵌 Python 翻译引擎** - PyInstaller 打包，用户无需配置环境

### 中优先级 (增强功能)
3. **应用程序自动更新** - Velopack 实现
4. **配置热更新** - FileSystemWatcher 监听
5. **模型热更新** - ONNX 模型动态重载

### 低优先级 (辅助功能)
6. **VRCBitmapLed 点阵屏** - 特殊功能
7. **透明悬浮窗** - 可选功能
8. **繁简转换** - 可选功能

---

## 四、技术栈对比

| 组件 | Python | C# |
|------|--------|-----|
| UI 框架 | Flask + WebView2 | WPF |
| 音频库 | pyaudiowpatch | NAudio |
| VAD | onnxruntime | Microsoft.ML.OnnxRuntime |
| 语音识别 | sherpa-onnx | SherpaOnnx.NET |
| OSC | python-osc | Rug.Osc |
| 热键 | pynput | Windows API (已实现) |
| TTS | edge-tts + miniaudio | System.Speech + 服务器 API (已实现) |
| VR | openvr | OpenVR.NET (已实现) |
| 翻译 | translators | PythonTranslationService (部分实现) |
| 繁简转换 | hanziconv | (待实现) |
| 7z 解压 | py7zr | (待实现) |

---

## 五、文件结构对比

### C# 项目当前结构
```
VRCLS-CSharp/
├── 01.VRCLS.Core/
│   ├── Models/
│   │   └── AppConfig.cs           # ✅ 配置模型
│   ├── Interfaces/                # ✅ 接口定义
│   │   ├── IAudioService.cs
│   │   ├── IVadService.cs
│   │   ├── IRecognitionService.cs
│   │   ├── ITranslationService.cs
│   │   ├── ITtsService.cs
│   │   ├── IOscService.cs
│   │   ├── IHotkeyService.cs
│   │   ├── ICommandService.cs
│   │   ├── ISteamVRService.cs
│   │   └── IBackendAuthService.cs
│   └── Events/                    # ✅ 事件定义
├── 02.VRCLS.Services/
│   ├── Audio/
│   │   ├── AudioCaptureService.cs # ✅ 音频捕获
│   │   ├── VadService.cs          # ✅ VAD 检测
│   │   ├── AudioPipelineService.cs # ✅ 音频管道
│   │   └── AudioPipelineFactory.cs # ✅ 管道工厂
│   ├── Recognition/
│   │   ├── SherpaAsrService.cs    # ✅ 本地识别
│   │   └── FastApiRecognitionService.cs # ✅ 远程识别
│   ├── Translation/
│   │   ├── ServerTranslationService.cs # ✅ 服务器翻译
│   │   ├── PythonTranslationService.cs # ✅ Python 翻译
│   │   └── TranslationServiceManager.cs # ✅ 翻译管理
│   ├── Tts/
│   │   ├── SystemSpeechTtsService.cs # ✅ 本地 TTS
│   │   ├── ServerTtsService.cs    # ✅ 服务器 TTS
│   │   └── TtsServiceManager.cs   # ✅ TTS 管理
│   ├── Input/
│   │   └── HotkeyService.cs       # ✅ 热键服务
│   ├── Command/
│   │   ├── CommandService.cs      # ✅ 命令服务
│   │   ├── DefaultCommandHandler.cs # ✅ 默认命令
│   │   └── ScriptExecutor.cs      # ✅ 脚本执行
│   ├── VR/
│   │   └── SteamVROverlayService.cs # ✅ VR Overlay
│   ├── External/
│   │   └── OscService.cs          # ✅ OSC 通信
│   └── Auth/
│       └── BackendAuthService.cs  # ✅ 后端认证
├── 03.VRCLS.UI/
│   ├── ViewModels/
│   │   ├── MainViewModel.cs       # ✅ 主视图模型
│   │   └── SettingsViewModel.cs   # ✅ 设置视图模型
│   ├── Views/
│   │   ├── MainWindow.xaml        # ✅ 主窗口
│   │   └── SettingsView.xaml      # ✅ 设置窗口
│   └── App.xaml.cs                # ✅ 应用入口
├── scripts/
│   ├── translator.py              # ⚠️ Python 翻译脚本
│   └── build_translator.ps1       # ⚠️ 打包脚本
└── tests/
    └── VRCLS.ConsoleTest/         # ✅ 控制台测试
```

---

## 六、下一步行动

### 高优先级
1. **实现 OpenAI 通用接口翻译** - 支持 GLM/DeepSeek 等
2. **打包内嵌 Python 翻译器** - PyInstaller 打包为独立 exe

### 中优先级
3. **实现应用程序自动更新** - Velopack
4. **实现配置热更新** - FileSystemWatcher

### 低优先级
5. **实现 VRCBitmapLed 点阵屏**
6. **实现透明悬浮窗**
7. **实现繁简转换**

---

*文档更新时间: 2025-02-01*
*基于 Python 项目版本: v0.6.0-fix2*
