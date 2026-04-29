# VRCLS-CSharp 改进方案

## 概述

本文档详细描述了 VRCLS-CSharp 项目的改进计划，涵盖用户体验优化、功能增强和架构改进。

---

## 一、热键与按住说话优化

### 1.1 问题描述
- 按键切换和按住说话存在延迟启动问题

### 1.2 解决方案
**修改文件：**
- `02.VRCLS.Services/Input/HotkeyService.cs`
- `03.VRCLS.UI/ViewModels/MainViewModel.cs`

**实现方式：**
```csharp
// 1. 移除启动延迟，直接响应按键事件
// 2. 使用低级键盘钩子 (SetWindowsHookEx) 替代高级API
// 3. 预初始化音频管道，避免首次启动延迟
```

**具体改动：**
- 在应用启动时预热音频管道
- 热键注册时使用 `RegisterHotKey` 的即时响应模式
- 按住说话模式下，按下即刻开始录音，无需等待 VAD 确认

---

## 二、TTS 语速自定义

### 2.1 问题描述
- TTS 语音合成缺少语速控制选项

### 2.2 解决方案
**修改文件：**
- `01.VRCLS.Core/Models/AppConfig.cs` - 添加配置项
- `01.VRCLS.Core/Interfaces/ITtsService.cs` - 扩展接口
- `02.VRCLS.Services/Tts/ServerTtsService.cs` - 实现语速控制
- `02.VRCLS.Services/Tts/SystemSpeechTtsService.cs` - 实现语速控制
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs` - 添加绑定属性
- `03.VRCLS.UI/Views/SettingsView.xaml` - 添加 UI 控件

**AppConfig 新增字段：**
```csharp
[JsonPropertyName("ttsSpeed")]
public float TtsSpeed { get; set; } = 1.0f; // 0.5 - 2.0

[JsonPropertyName("ttsPitch")]
public float TtsPitch { get; set; } = 1.0f; // 0.5 - 2.0 (可选)
```

**UI 设计：**
```
语速控制: [====|====] 1.0x
         0.5x        2.0x
```

---

## 三、设置界面重组

### 3.1 本地识别模型位置调整

**问题：** 本地识别模型选项放在"翻译"标签页中，逻辑不清晰

**解决方案：** 将本地识别模型设置移动到"音频"标签页，并重命名为"语音识别"

**修改文件：**
- `03.VRCLS.UI/Views/SettingsView.xaml`

**UI 调整：**
```
音频标签页 -> 语音识别标签页
├── 麦克风设置
├── 桌面音频设置
├── VAD 设置
└── 语音识别模型 (从翻译页移入)
    ├── 本地识别开关
    └── 模型状态显示
```

### 3.2 过滤 Emoji 选项位置调整

**问题：** 过滤 Emoji 选项放在翻译设置中，实际应属于语音识别后处理

**解决方案：** 移动到语音识别设置区域

### 3.3 实时输出延迟选项移除

**问题：** 实时输出延迟功能不再使用

**解决方案：** 从 UI 和配置中移除 `RealtimeOutputDelay` 相关代码

**修改文件：**
- `03.VRCLS.UI/Views/SettingsView.xaml` - 删除 UI
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs` - 删除属性
- `01.VRCLS.Core/Models/AppConfig.cs` - 保留字段但标记废弃

---

## 四、桌面音频声音源选择

### 4.1 问题描述
- 桌面音频默认使用系统 Loopback，但用户无法选择具体设备

### 4.2 解决方案
**修改文件：**
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs` - 添加桌面音频设备列表
- `03.VRCLS.UI/Views/SettingsView.xaml` - 添加设备选择下拉框
- `02.VRCLS.Services/Audio/AudioCaptureService.cs` - 支持指定 Loopback 设备

**实现方式：**
```csharp
// 枚举所有音频渲染设备 (扬声器)
public ObservableCollection<string> DesktopAudioDevices { get; } = new();

private void RefreshDesktopAudioDevices()
{
    DesktopAudioDevices.Clear();
    DesktopAudioDevices.Add("系统默认扬声器");

    var enumerator = new MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        DesktopAudioDevices.Add(device.FriendlyName);
    }
}
```

**UI 设计：**
```
桌面音频 (系统声音)
├── 声音源: [系统默认扬声器 v]  <- 新增下拉框
├── 触发模式: [常开 v]
└── ...
```

---

## 五、翻译引擎优化

### 5.1 默认翻译引擎

**问题：** 默认翻译引擎为 `libre`，应改为 `developer`

**解决方案：**
```csharp
// AppConfig.cs
[JsonPropertyName("translationServer")]
public string TranslationServer { get; set; } = "developer"; // 改为 developer
```

### 5.2 语言显示优化

**问题：** 语言选择只显示代码 (zh, en)，用户不易理解

**解决方案：** 使用语言名称显示

**修改文件：**
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs`
- `03.VRCLS.UI/Views/SettingsView.xaml`

**实现方式：**
```csharp
public class LanguageItem
{
    public string Code { get; set; }
    public string DisplayName { get; set; }
}

public ObservableCollection<LanguageItem> SourceLanguages { get; } = new(new[]
{
    new LanguageItem { Code = "auto", DisplayName = "自动检测" },
    new LanguageItem { Code = "zh", DisplayName = "中文" },
    new LanguageItem { Code = "en", DisplayName = "英语" },
    new LanguageItem { Code = "ja", DisplayName = "日语" },
    new LanguageItem { Code = "ko", DisplayName = "韩语" },
    new LanguageItem { Code = "de", DisplayName = "德语" },
    new LanguageItem { Code = "fr", DisplayName = "法语" },
    new LanguageItem { Code = "es", DisplayName = "西班牙语" },
    new LanguageItem { Code = "ru", DisplayName = "俄语" },
});
```

### 5.3 多语言控制增强

**新增功能：** 添加应用界面多语言支持

**修改文件：**
- 新建 `01.VRCLS.Core/Localization/` 目录
- 新建 `Resources/Strings.zh-CN.resx`
- 新建 `Resources/Strings.en-US.resx`

---

## 六、外观设置优化

### 6.1 深色模式修复

**问题：** 深色模式开关不起作用

**解决方案：**
**修改文件：**
- `03.VRCLS.UI/App.xaml.cs`
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs`

**实现方式：**
```csharp
public bool DarkMode
{
    get => _darkMode;
    set
    {
        if (SetField(ref _darkMode, value))
        {
            _config.DarkMode = value;
            ApplyTheme(value); // 新增：立即应用主题
        }
    }
}

private void ApplyTheme(bool isDark)
{
    var paletteHelper = new PaletteHelper();
    var theme = paletteHelper.GetTheme();
    theme.SetBaseTheme(isDark ? Theme.Dark : Theme.Light);
    paletteHelper.SetTheme(theme);
}
```

### 6.2 主菜单边框优化

**问题：** 主窗口边框不清晰，看不出边界

**解决方案：**
**修改文件：**
- `03.VRCLS.UI/Views/MainWindow.xaml`

```xml
<!-- 修改前 -->
<Border CornerRadius="8" BorderThickness="1" BorderBrush="#00FFFFFF">

<!-- 修改后 -->
<Border CornerRadius="8" BorderThickness="1" BorderBrush="#44FFFFFF">
    <Border.Effect>
        <DropShadowEffect BlurRadius="10" ShadowDepth="0" Color="Black" Opacity="0.5"/>
    </Border.Effect>
```

### 6.3 窗口透明背景选项

**问题：** 窗口透明背景选项可能不需要

**建议：** 保留该选项，但简化为单一开关，移除 `TransparentWindowSize` 预设

**或者：** 如确认不需要，则从 UI 移除，配置保留以兼容旧版

---

## 七、OSC 混合模板位置调整

### 7.1 问题描述
- OSC 混合模板放在"外观"标签页的高级设置中，逻辑不清晰

### 7.2 解决方案
将 `ChatboxOscMixTemplate` 移动到 OSC 服务器设置区域

**修改文件：**
- `03.VRCLS.UI/Views/SettingsView.xaml`

**UI 调整：**
```
VR集成标签页
├── OSC 通信 (VRChat)
│   ├── 目标 IP/端口
│   ├── Chatbox 消息模板
│   └── OSC 服务器 (接收)
│       ├── 启用 OSC 监听服务
│       ├── 本机监听 IP/端口
│       └── OSC 混合模板  <- 移入此处
└── SteamVR 掌心字幕
```

---

## 八、TTS 流式播报优化

### 8.1 问题描述
- 语音合成播报无法并发请求，新消息需等待上一条播放完成

### 8.2 解决方案
实现流式 TTS 队列管理，支持打断和无缝切换

**修改文件：**
- `02.VRCLS.Services/Tts/TtsServiceManager.cs`
- `02.VRCLS.Services/Tts/ServerTtsService.cs`
- 新建 `02.VRCLS.Services/Tts/TtsQueueManager.cs`

**实现方式：**
```csharp
public class TtsQueueManager
{
    private readonly ConcurrentQueue<TtsRequest> _queue = new();
    private CancellationTokenSource? _currentCts;

    public async Task EnqueueAsync(string text, string language, bool interruptCurrent = true)
    {
        if (interruptCurrent && _currentCts != null)
        {
            _currentCts.Cancel(); // 打断当前播放
        }

        _currentCts = new CancellationTokenSource();

        // 流式请求 TTS 数据
        await foreach (var chunk in _ttsService.StreamSpeakAsync(text, language))
        {
            if (_currentCts.Token.IsCancellationRequested)
                break;

            await _audioPlayer.PlayChunkAsync(chunk);
        }
    }
}
```

**配置选项：**
```csharp
[JsonPropertyName("ttsInterruptMode")]
public int TtsInterruptMode { get; set; } = 1; // 0=队列等待, 1=打断切换
```

---

## 九、服务器连接状态显示优化

### 9.1 问题描述
- 服务器连接状态图标和其他图标重叠

### 9.2 解决方案
**修改文件：**
- `03.VRCLS.UI/Views/MainWindow.xaml`

**UI 调整：**
```xml
<!-- 修改前：连接状态点与麦克风按钮距离太近 -->
<materialDesign:PackIcon Grid.Column="0" Kind="CircleMedium" Width="12" Height="12"
                         VerticalAlignment="Center" Margin="0,0,16,0"/>

<!-- 修改后：增加间距，添加状态文字提示 -->
<StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center" Margin="0,0,20,0">
    <materialDesign:PackIcon Kind="CircleMedium" Width="14" Height="14"
                             Style="{StaticResource ConnectionDotStyle}"/>
    <TextBlock Text="{Binding ConnectionStateText}" FontSize="10" Foreground="#AAAAAA"
               VerticalAlignment="Center" Margin="4,0,0,0"
               Visibility="{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=Grid},
                           Converter={StaticResource BooleanToVisibilityConverter}}"/>
</StackPanel>
```

---

## 十、翻译引擎无感化 - Python 性能保留与优化

### 10.1 目标与现状
- 目标：保留 Python translators，不要求用户安装 Python，但尽量降低运行时开销与延迟
- 现状：已支持嵌入式 translator 进程协议，但仍存在首次启动与重复请求开销

### 10.2 核心策略
- 常驻翻译进程：保持单实例/可复用进程，避免每次启动
- 预热与健康检查：启动后 warmup + ping，减少首句延迟
- 结果缓存：对相同文本/语言组合做 LRU 缓存
- 批量合并：多条短句合并翻译，减少网络往返
- 网络优化：保持连接、并发 I/O（由 translators 内部/requests 处理）

### 10.3 交互协议增强
- `cmd=warmup`：加载 translators 与必要资源
- `cmd=ping`：快速健康检查
- 失败重启：超时或异常时自动重启 translator 进程

### 10.4 落地改动
**修改文件：**
- `scripts/translator.py`：增加 LRU 缓存/预热/短路逻辑
- `02.VRCLS.Services/Translation/EmbeddedTranslatorService.cs`：支持 warmup/ping、后台预热与容错
- `03.VRCLS.UI/App.xaml.cs`：启动时根据配置触发预热（仅在选用 python 引擎时）

**验证点：**
- 首次翻译延迟下降
- 连续翻译吞吐提升
- 进程异常可自恢复

---

## 十一、热更新与模型更新系统

### 11.1 功能需求
- 支持应用内检查更新
- 支持模型文件热更新
- 支持翻译引擎配置热更新

### 11.2 架构设计

**新建文件：**
- `01.VRCLS.Core/Interfaces/IUpdateService.cs`
- `02.VRCLS.Services/Update/UpdateService.cs`
- `02.VRCLS.Services/Update/ModelUpdateService.cs`

**接口定义：**
```csharp
public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdatesAsync();
    Task<bool> DownloadUpdateAsync(UpdateInfo info, IProgress<double> progress);
    Task<bool> ApplyUpdateAsync(UpdateInfo info);
}

public interface IModelUpdateService
{
    Task<List<ModelInfo>> GetAvailableModelsAsync();
    Task<bool> DownloadModelAsync(ModelInfo model, IProgress<double> progress);
    Task<bool> InstallModelAsync(ModelInfo model);
}

public class UpdateInfo
{
    public string Version { get; set; }
    public string DownloadUrl { get; set; }
    public string ReleaseNotes { get; set; }
    public long FileSize { get; set; }
    public string Checksum { get; set; }
}

public class ModelInfo
{
    public string Name { get; set; }
    public string Type { get; set; } // sherpa, vad, sensevoice
    public string Version { get; set; }
    public string DownloadUrl { get; set; }
    public long FileSize { get; set; }
    public string Checksum { get; set; }
}
```

### 11.3 更新服务器 API

```
GET /api/updates/check?version={current_version}&platform=windows
Response:
{
    "hasUpdate": true,
    "latestVersion": "1.2.0",
    "downloadUrl": "https://...",
    "releaseNotes": "...",
    "fileSize": 12345678,
    "checksum": "sha256:..."
}

GET /api/models/list
Response:
{
    "models": [
        {
            "name": "sherpa-bilingual-v2",
            "type": "sherpa",
            "version": "2.0.0",
            "downloadUrl": "https://...",
            "fileSize": 123456789,
            "checksum": "sha256:..."
        }
    ]
}
```

### 11.4 UI 设计

在设置页面添加"更新"标签页：

```
更新标签页
├── 应用更新
│   ├── 当前版本: 1.0.0
│   ├── [检查更新] 按钮
│   └── 更新状态/进度
├── 模型管理
│   ├── 已安装模型列表
│   ├── 可用模型列表
│   └── [下载/更新] 按钮
└── 翻译引擎配置
    ├── 配置版本: 2024.01.15
    └── [同步最新配置] 按钮
```

---

## 十二、实施优先级

### P0 - 紧急修复
1. 深色模式不起作用
2. 主菜单边框不清晰
3. 服务器连接状态图标重叠

### P1 - 高优先级
1. 按键切换/按住说话延迟优化
2. TTS 语速自定义
3. 翻译引擎默认值修改
4. 语言显示优化 (代码 -> 名称)

### P2 - 中优先级
1. 设置界面重组 (本地识别、过滤Emoji位置)
2. 桌面音频设备选择
3. OSC 混合模板位置调整
4. 移除实时输出延迟选项

### P3 - 低优先级
1. TTS 流式播报优化
2. Python 翻译性能优化（常驻/预热/缓存）
3. 热更新系统
4. 多语言界面支持

---

## 十三、文件修改清单

| 文件路径 | 修改类型 | 优先级 |
|---------|---------|-------|
| `01.VRCLS.Core/Models/AppConfig.cs` | 修改 | P1 |
| `01.VRCLS.Core/Interfaces/ITtsService.cs` | 修改 | P1 |
| `02.VRCLS.Services/Tts/TtsServiceManager.cs` | 修改 | P1 |
| `02.VRCLS.Services/Tts/ServerTtsService.cs` | 修改 | P1 |
| `02.VRCLS.Services/Tts/SystemSpeechTtsService.cs` | 修改 | P1 |
| `02.VRCLS.Services/Input/HotkeyService.cs` | 修改 | P1 |
| `02.VRCLS.Services/Audio/AudioCaptureService.cs` | 修改 | P2 |
| `scripts/translator.py` | 修改 | P3 |
| `02.VRCLS.Services/Translation/EmbeddedTranslatorService.cs` | 修改 | P3 |
| `02.VRCLS.Services/Update/UpdateService.cs` | 新建 | P3 |
| `03.VRCLS.UI/ViewModels/SettingsViewModel.cs` | 修改 | P1 |
| `03.VRCLS.UI/ViewModels/MainViewModel.cs` | 修改 | P0 |
| `03.VRCLS.UI/Views/SettingsView.xaml` | 修改 | P1 |
| `03.VRCLS.UI/Views/MainWindow.xaml` | 修改 | P0 |
| `03.VRCLS.UI/App.xaml.cs` | 修改（P0+P3） | P0/P3 |

---

## 十四、测试计划

### 单元测试
- 翻译引擎 API 调用测试
- TTS 语速参数测试
- 热键响应延迟测试

### 集成测试
- 完整音频管道测试
- OSC 通信测试
- 更新流程测试

### UI 测试
- 深色/浅色模式切换
- 设置保存/加载
- 多语言显示

---

## 附录 A：语言代码对照表

| 代码 | 中文名称 | 英文名称 |
|-----|---------|---------|
| auto | 自动检测 | Auto Detect |
| zh | 中文 | Chinese |
| en | 英语 | English |
| ja | 日语 | Japanese |
| ko | 韩语 | Korean |
| de | 德语 | German |
| fr | 法语 | French |
| es | 西班牙语 | Spanish |
| ru | 俄语 | Russian |
| none | 不翻译 | None |

---

## 附录 B：翻译引擎对照表

| 代码 | 显示名称 | 说明 |
|-----|---------|-----|
| developer | 开发者服务器 | 使用后端 API (推荐) |
| google | Google 翻译 | 免费，质量好 |
| bing | 必应翻译 | 微软服务 |
| baidu | 百度翻译 | 中文优化 |
| deepl | DeepL | 高质量，有限制 |
| yandex | Yandex | 俄语优化 |
| openai | OpenAI/LLM | 需要 API Key |



