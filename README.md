# VRCLS - VRChat LinguaSync

VRCLS 全称 **VRChat LinguaSync**。这是面向 VRChat 的实时语音识别、翻译、语音合成、OSC 与 SteamVR 同步工具，也是原 Python 版 VRCLS 的 C#/.NET 8 + WPF 重制版。

VRCLS 不只是字幕浮窗。它可以把麦克风、监听音频、手动输入和 VRChat 照片转成可同步的多语言内容，并通过主窗口、VRChat Chatbox、SteamVR 掌心面板或 TTS 播报输出。

## 当前能力

### 识别与音频采集

- 麦克风识别：支持常开、按键切换和按住说话。
- 监听音频识别：支持系统 loopback 音频，以及 Windows 10 2004+ 上的指定应用音频捕获，默认目标应用为 VRChat。
- 双路输入管理：麦克风和监听音频可分别设置设备、自动启动、热键和触发模式。
- 实时音量反馈：主窗口显示麦克风和监听音频电平。
- VAD 语音活动检测：基于 Silero VAD，支持麦克风和监听音频独立阈值、最短静音分段、最大片段时长。
- VAD 校准：内置麦克风和监听音频校准窗口，便于快速找到合适阈值。
- 本地识别：支持 Sherpa-ONNX 本地流式识别，仅用于麦克风链路。
- 服务端识别：支持后端 ASR，并可为中文识别选择 FireRed/SenseVoice 等引擎。

### 翻译

- 多通道语言设置：麦克风、监听音频、手动输入、拍照翻译可以分别配置语言。
- 最多三路目标语言：识别结果可以同时输出译文 1、译文 2、译文 3。
- 多翻译引擎：支持开发者服务器、OpenAI 兼容接口，以及 Google、Bing、Baidu、Yandex、DeepL、LibreTranslate 等非开发者引擎。
- OpenAI 兼容参数：支持 API Key、Base URL、模型名、temperature、max tokens、extra body、system prompt 和预设模板。
- 翻译运行时热更新：可从更新中心同步最新翻译配置和翻译运行时能力。
- 显示过滤：可控制主窗口显示原文/译文通道，并支持过滤 Emoji。

### VRChat 与 SteamVR 输出

- VRChat OSC Chatbox：支持目标 IP/端口、Chatbox 模板、发送提示音和退出行为配置。
- OSC 接收：可监听 VRChat 状态，并通过混合模板拼接本地文本和 OSC 状态。
- SteamVR 掌心字幕：支持右手/左手、手柄/手势追踪、掌心/手背/头部悬浮锚点。
- SteamVR 面板调节：支持尺寸、字体缩放、非文字透明度、位置偏移、旋转偏移和光标轴反转。
- SteamVR 自动启动：可注册到 SteamVR，并使用 SteamVR 自动拉起机制。
- VR 内操作：SteamVR 面板可触发麦克风/监听开关、拍照翻译、锚点切换、复制字幕等动作。

### TTS 语音合成

- 服务器 TTS 与本地系统 TTS 两种模式。
- 服务端音色提供方：支持 Edge-TTS、Qwen-TTS、MOSS-TTS、OmniVoice。
- Qwen 语气控制：支持自动语气和自然语言语气指令。
- 内置音色与声音克隆：Qwen/MOSS/OmniVoice 支持内置音色或声音克隆模式。
- 声音预设管理：支持声音克隆预设、预设市场、导入/导出预设包和自定义参考音频。
- 播报控制：可选择输出设备、语速、音量、打断策略，并分别决定麦克风/监听音频播报原文或各路译文。
- 音色试听：支持当前音色、输出设备和语速组合的试听。

### 拍照翻译

- 可等待下一张 VRChat 照片并自动触发拍照翻译。
- 支持文字结果和图片结果两种返回模式。
- 拍照翻译使用独立目标语言，默认翻译为中文。
- 主窗口和 SteamVR 面板均可显示拍照翻译状态与结果。

### 用户中心、更新与工具

- 官网浏览器授权登录：客户端不接收或保存官网密码。
- 匿名试用模式和自建旧版服务器兼容模式。
- 用户中心：展示用户资料、连接状态、账户额度、到期时间、区间用量明细。
- 爱发电流程：支持发电引导、方案读取、支付后自动激活校验。
- 更新中心：支持应用更新、模型包安装、翻译配置热更新。
- 多语言界面：支持中文、English、日本語、Français、Русский。
- 外观与辅助：支持主窗口尺寸、背景透明度、点击字幕复制字段、运行日志查看和打开日志文件。
- 实验性关键词动作：可通过识别文本中的关键词触发自定义动作脚本和 Avatar 参数操作。

## 项目结构

```text
VRCLS-CSharp
├── 01.VRCLS.Core
│   ├── Models/          # 配置、状态、事件数据、枚举和传输模型
│   ├── Interfaces/      # 音频、识别、翻译、TTS、OSC、SteamVR、更新等接口
│   ├── Events/          # 音频管道事件
│   └── Helpers/         # 配置存储、版本、平台能力等工具
├── 02.VRCLS.Services
│   ├── Audio/           # 音频捕获、VAD、Opus、音频管道
│   ├── Recognition/     # 本地 Sherpa 与服务端识别
│   ├── Translation/     # OpenAI、服务端与嵌入式翻译运行时
│   ├── Tts/             # 服务端 TTS 与本地系统 TTS
│   ├── VR/              # SteamVR/OpenVR 掌心面板
│   ├── External/        # OSC
│   ├── Update/          # 应用、模型、翻译配置更新
│   └── Photo/           # VRChat 拍照翻译
├── 03.VRCLS.UI
│   ├── Views/           # 主窗口、设置页、弹窗和工具窗口
│   ├── ViewModels/      # WPF/MVVM 状态与命令
│   ├── Services/        # UI 侧目录、日志、音色预设、VAD 校准等服务
│   ├── Resources/       # 多语言资源
│   └── Assets/          # 图标与应用资源
├── scripts/             # 发布、安装包、翻译运行时和资源桶脚本
├── docs/                # 设计、迁移、更新、拍照翻译等说明
└── tests/               # 控制台和后端烟测项目
```

## 技术栈

| 模块 | 技术 |
| --- | --- |
| 桌面 UI | WPF, MaterialDesignThemes, .NET 8 |
| 架构 | MVVM, Microsoft.Extensions.DependencyInjection |
| 音频 | NAudio, WASAPI loopback, Windows application audio capture |
| VAD | Silero VAD, Microsoft.ML.OnnxRuntime |
| 本地 ASR | Sherpa-ONNX |
| 翻译 | OpenAI compatible API, Python translators runtime, server API |
| TTS | Server TTS, System.Speech/本地系统语音, NAudio playback |
| VRChat OSC | VRCOscLib |
| SteamVR | OpenVR |
| 配置 | System.Text.Json, `%APPDATA%\VRCLS\config.json` |
| 日志 | Serilog |
| 更新 | NetSparkle, 自定义模型/翻译配置更新服务 |

## 环境要求

- Windows 10/11。
- .NET 8 SDK，用于源码编译和运行。
- 麦克风设备。
- 使用指定应用音频捕获时，需要 Windows 10 2004+ 或更新系统。
- 使用 VRChat Chatbox 发送时，需要在 VRChat 中启用 OSC。
- 使用 VR 内字幕面板时，需要 SteamVR/OpenVR 环境。

## 构建与运行

```powershell
dotnet restore .\VRCLS.sln
dotnet build .\VRCLS.sln
dotnet run --project .\03.VRCLS.UI\03.VRCLS.UI.csproj
```

如果只需要验证服务链路，也可以运行测试项目：

```powershell
dotnet run --project .\tests\VRCLS.ConsoleTest\VRCLS.ConsoleTest.csproj
dotnet run --project .\tests\VRCLS.BackendSmoke\VRCLS.BackendSmoke.csproj
```

## 模型与资源

应用启动和更新中心会检查所需模型包。常见资源包括：

- Silero VAD ONNX 模型，用于语音活动检测。
- Sherpa-ONNX 本地识别模型，用于麦克风本地流式识别。
- 翻译运行时和能力文件，用于非开发者翻译引擎。
- TTS 音色目录和声音克隆预设包。

本地 Sherpa 识别目前只用于麦克风；监听音频识别走服务端链路。

## 配置位置

默认配置文件保存在：

```text
%APPDATA%\VRCLS\config.json
```

配置仍保持对旧版字段的兼容，新增设置会尽量映射到现有 JSON 结构，避免破坏用户已有配置。

## 发布脚本

常用脚本位于 `scripts/`：

- `publish.ps1`：发布普通构建。
- `publish_with_premodels.ps1`：发布带预置模型的构建。
- `build-installer.ps1`：生成 Inno Setup 安装包和 NetSparkle appcast。
- `build-update-manifest.ps1`：生成更新清单。
- `generate_bucket_layout_gui.py`：生成/上传静态资源桶布局的 GUI 工具。

## 文档

更多设计和实现细节见 `docs/`，包括：

- [项目结构梳理](docs/项目结构梳理.md)
- [迁移方案](docs/迁移方案.md)
- [开发说明](docs/开发说明.md)
- [更新功能规格](docs/UPDATE_FEATURE_SPEC.md)
- [VRChat 拍照翻译实现说明](docs/2026-04-12_VRChat拍照翻译实现说明.md)
- [声音克隆音色预设](docs/声音克隆音色预设.md)

## 许可证

本项目基于原 VRCLS 项目的许可证，具体条款待确认。

## 联系方式

项目维护者：boyqiu001

原项目地址：https://github.com/VoiceLinkVR/VRCLS
