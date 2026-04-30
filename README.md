# VRCLS

语言：简体中文 | [English](README.en.md)

VRCLS 是一个面向 VRChat 的 Windows 桌面客户端，用于实时字幕、语音识别、翻译、文字转语音、OSC 控制和 SteamVR 叠加显示。本仓库是 C# / .NET 8 / WPF 实现。

当前桌面版已经不是迁移骨架：主 WPF 应用、设置界面、音频管线、后端鉴权、更新流程、翻译路由、TTS 路由、OSC 输出和可选 SteamVR Overlay 集成都在这个代码库里。

## 功能

- 麦克风、桌面环回、指定进程音频采集。
- 基于 Silero VAD 的语音分段。
- 本地模型可用时使用 Sherpa-ONNX 本地识别。
- 通过配置的 VRCLS 后端进行远端 FastAPI 识别和翻译。
- 麦克风和桌面音频链路支持多目标语言翻译输出。
- 支持后端翻译、OpenAI 兼容接口，以及打包的 Python `translators` 运行时。
- 通过 OSC 输出到 VRChat 聊天框和 Avatar 参数。
- 支持后端 TTS、Qwen 风格音色别名，以及 Windows 本地 `System.Speech`。
- 从 `音色.json` 加载 Edge TTS 音色列表。
- 通过 `ref/openvr` 中提交的最小 OpenVR 文件实现 SteamVR 文本 Overlay。
- 支持拍照/OCR 翻译流程。
- 包含用户中心、网页登录授权、账号状态、发电/权益界面和限流反馈。
- 支持 NetSparkle 应用更新、模型目录更新和翻译配置热更新。

## 仓库结构

```text
01.VRCLS.Core/        共享模型、接口、配置、事件、工具类
02.VRCLS.Services/    音频、鉴权、识别、翻译、TTS、更新、OSC、SteamVR 服务
03.VRCLS.UI/          WPF 应用、视图、ViewModel、资源和 UI 辅助类
docs/                 设计说明和实现说明
scripts/              构建、发布、安装包、更新清单、翻译运行时工具
tests/                控制台测试和后端冒烟测试项目
ref/openvr/           本项目实际需要的最小 OpenVR 文件
ref/translators/      可选 translators 子模块指针
```

## 环境要求

- Windows 10/11。
- .NET 8 SDK。
- Visual Studio 2022，或其他支持 WPF / .NET 桌面开发的编辑器。
- 完整运行流程需要麦克风权限，并在 VRChat 中启用 OSC。
- 只有构建打包版翻译运行时或发布产物时才需要 Python。
- 只有构建安装包时才需要 Inno Setup。

## 构建

```powershell
dotnet restore VRCLS.sln
dotnet build VRCLS.sln
```

当前解决方案构建会从 Valve 生成的 OpenVR C# 绑定 `ref/openvr/headers/openvr_api.cs` 输出 nullable 警告。这些警告是已知项，不会阻断构建。

运行桌面应用：

```powershell
dotnet run --project 03.VRCLS.UI/03.VRCLS.UI.csproj
```

运行控制台测试项目：

```powershell
dotnet run --project tests/VRCLS.ConsoleTest/VRCLS.ConsoleTest.csproj
```

`tests/VRCLS.BackendSmoke/` 下存在后端冒烟测试项目，但它当前没有加入 `VRCLS.sln`。

## 模型

仓库不会提交大型识别模型包。应用会从以下位置解析模型：

- 仓库或发布应用旁边的 `pre-models/`。
- 可执行文件旁边的 `models/`。
- `%APPDATA%\VRCLS\config.json` 中配置的路径。
- 从配置的更新服务器下载的模型。

仓库中提交了一个小型 Silero VAD 模型作为开发兜底。较大的 Sherpa-ONNX ASR 模型会被排除在 Git 之外，应通过更新流程下载，或手动放到 `pre-models/`。

## 配置

运行时配置保存在：

```text
%APPDATA%\VRCLS\config.json
```

重要默认值：

- 后端基础地址：`https://www.voicelinkvr.top`
- 静态更新服务器：`https://cloudflarestorage.boyqiu001.top/`
- 应用更新 feed：`https://cloudflarestorage.boyqiu001.top/app-releases/appcast.xml`
- 默认应用音频目标进程：`VRChat`
- 默认 TTS provider：`qwen`

不要提交本地 `.env`、发布预设、生成的更新 bucket、本地模型目录、日志或 `tools/translator` 输出。这些内容已被 `.gitignore` 排除。

## 发布

标准自包含发布：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Configuration Release -Runtime win-x64 -Zip
```

构建安装包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -InternalVersion 0.7.0.123 -DisplayVersion 0.7.0-beta23
```

发布脚本会构建 Python 翻译运行时到 `tools/translator`，发布 WPF 应用，移除大型模型文件，并在存在 `pre-models/silero_vad.onnx` 时把 VAD 模型恢复到发布输出。

## 翻译运行时

应用可以通过基于 Python `translators` 包的打包运行时执行翻译。手动构建命令：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_translator.ps1
```

生成文件会落在 `tools/translator/`，该目录不会提交到 Git。

## 更新资源

以下脚本用于生成 appcast、模型目录和翻译配置等静态托管资源：

- `scripts/build-update-manifest.ps1`
- `scripts/generate_bucket_layout.py`
- `scripts/generate_bucket_layout_gui.py`

`bucket-generated/` 等生成目录是本地产物，不应提交。

## 开源协议

VRCLS 使用 Apache License, Version 2.0。详见 [LICENSE](LICENSE)。

第三方依赖、OpenVR 文件、NuGet 包和 Python 包仍遵循各自的许可证。
