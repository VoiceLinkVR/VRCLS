# VRCLS - VRChat Live Subtitle (C# 重制版)

这是一个将 Python 版 VRCLS (VRChat LinguaSync) 完全重制为 C# .NET 8 + WPF 的项目。相比原版，提供了更好的性能、更低的延迟和更好的用户体验。

## 项目概述

VRCLS 是一个用于在 VRChat 中进行语音识别、翻译和 OSC 控制的工具。支持本地语音识别、多引擎翻译、TTS、SteamVR 叠加显示等功能。

## 主要特性

### 核心功能 (已完成)
- ✅ **音频录制**
  - 麦克风录音 (NAudio WaveInEvent)
  - 桌面音频环路录音 (NAudio WasapiLoopbackCapture)
  - 自动重采样和声道转换

- ✅ **语音活动检测 (VAD)**
  - 使用 Silero VAD ONNX 模型
  - 状态机管理 (SpeechStart / SpeechEnd)
  - 可配置阈值和静音时长

- ✅ **语音识别 (ASR)**
  - 集成 SherpaOnnx
  - 流式识别，低延迟
  - 支持中文等多种语言

### 待实现功能
- **UI 界面** (WPF + MVVM)
  - 主窗口和导航
  - 实时日志显示
  - 设置页面
  - 透明字幕悬浮窗

- **OSC 通信**
  - VRChat 聊天框发送
  - Avatar 参数控制
  - OSC 服务器监听

- **翻译服务**
  - 多引擎支持 (OpenAI、Google、Baidu、LibreTranslate)
  - 批量翻译
  - 多目标语言

- **TTS 语音合成**
  - EdgeTTS 集成
  - 音频播放 (NAudio WaveOut)
  - 多模式输出

- **SteamVR 集成**
  - VR 叠加显示
  - 左右手位置控制
  - 大小调节

- **点阵屏显示**
  - VRCBitmapLed 16x8 彩色显示
  - 文本渲染和滚动
  - 颜色控制

## 项目结构

```
VRCLS.Solution
├── 01.VRCLS.Core (.NET 8 类库)
│   ├── Models/          # 数据模型 (AppConfig, LogItem, TranscriptionResult)
│   ├── Interfaces/      # 所有服务接口
│   ├── Events/          # 事件定义
│   └── Helpers/         # 工具类
│
├── 02.VRCLS.Services (.NET 8 类库)
│   ├── Audio/
│   │   ├── AudioCaptureService.cs  # NAudio 录音封装
│   │   ├── VadService.cs           # VAD 服务
│   │   └── AudioPipelineService.cs # 音频处理管道
│   ├── Recognition/
│   │   └── SherpaAsrService.cs     # SherpaOnnx 识别
│   └── External/        # OSC、翻译、SteamVR 等
│
├── 03.VRCLS.UI (WPF 应用程序)
│   ├── App.xaml.cs      # DI 容器和启动
│   ├── Views/           # XAML 窗口和控件
│   └── ViewModels/      # MVVM 逻辑
│
└── tests/
    └── VRCLS.ConsoleTest/  # 控制台测试程序
```

## 技术栈

| 功能模块 | C# 库 |
|---------|--------|
| UI 框架 | WPF (.NET 8) |
| MVVM 框架 | CommunityToolkit.Mvvm |
| 音频录制 | NAudio |
| VAD | Microsoft.ML.OnnxRuntime |
| 语音识别 | SherpaOnnx |
| OSC 通信 | Rug.Osc |
| 配置存储 | System.Text.Json |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |

## 快速开始

### 环境要求
- .NET 8.0 SDK 或更高版本
- Windows 10/11
- 麦克风设备

### 模型准备

1. **下载 Silero VAD 模型**
```bash
mkdir models
curl -L -o models/silero_vad.onnx https://github.com/snakers4/silero-vad/raw/master/files/silero_vad.onnx
```

2. **下载 SherpaOnnx 中文模型**
```bash
curl -L -o model.zip https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-zh-CN-2023-12-12.tar.bz2
tar -xvf model.zip -C models/
```

### 编译和运行

1. **克隆项目**
```bash
git clone https://github.com/your-repo/VRCLS-CSharp.git
cd VRCLS-CSharp
```

2. **编译解决方案**
```bash
dotnet build VRCLS.sln
```

3. **运行控制台测试程序**
```bash
cd tests/VRCLS.ConsoleTest
dotnet run
```

4. **运行 WPF UI 程序** (完成后)
```bash
cd ../../03.VRCLS.UI
dotnet run
```

### 配置说明

配置文件使用 JSON 格式，默认保存在用户配置目录下。

关键配置项：
- `micName`: 麦克风设备名称
- `voiceHotKey`: 麦克风开关热键
- `osc-ip`/`osc-port`: VRChat OSC 目标地址
- `translationServer`: 翻译服务提供商
- `sourceLanguage`: 源语言 (zh/en/jp 等)
- `targetTranslationLanguage`: 目标翻译语言

## 开发进度

### 核心音频链路 ✅
- [x] 音频录制服务
- [x] VAD 语音检测
- [x] SherpaOnnx 识别集成
- [x] 音频管道服务
- [x] 控制台测试程序

### UI 界面 (进行中)
- [ ] 主窗口设计
- [ ] 导航菜单
- [ ] 日志显示
- [ ] 设置页面
- [ ] 透明悬浮窗
- [ ] 深色模式支持

### 服务集成
- [ ] OSC 服务
- [ ] 翻译服务 (多引擎)
- [ ] TTS 服务
- [ ] SteamVR 集成
- [ ] 点阵屏功能
- [ ] 热键注册

## 性能优化

相比 Python 版本，C# 版本提供了：

1. **更低的延迟**
   - 内存内音频流传输（无进程间通信）
   - 直接使用 WASAPI，降低音频 API 开销
   - 实时流式识别

2. **更高的稳定性**
   - 强类型检查减少运行时错误
   - 基于 .NET 托管内存管理
   - 更好的异常处理

3. **更好的用户体验**
   - 原生 WPF 界面，响应更快
   - 单文件部署，无需 Python 环境
   - 真正的多线程音频处理

## 迁移方案详情

参见 [迁移方案](迁移方案.md) 获取详细的技术栈映射和架构设计说明。

## 许可证

本项目基于原 VRCLS 项目的许可证（待确认）。

## 贡献

欢迎提交 Issue 和 Pull Request！

## 联系方式

项目维护者：boyqiu001
原项目地址：https://github.com/VoiceLinkVR/VRCLS
