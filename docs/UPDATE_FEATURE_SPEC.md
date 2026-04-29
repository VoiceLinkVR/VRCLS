
  ---
  VRCLS-CSharp 自动更新与热更新功能规划文档

  本文档用于指导开发人员实现 VRCLS-CSharp 的更新功能。

  ---
  1. 项目背景

  1.1 项目结构

  VRCLS-CSharp/
  ├── 01.VRCLS.Core/           # 核心层：接口定义、数据模型
  │   ├── Interfaces/          # 服务接口
  │   └── Models/              # 数据模型 (AppConfig 等)
  ├── 02.VRCLS.Services/       # 服务层：业务逻辑实现
  │   ├── Audio/               # 音频服务
  │   ├── Translation/         # 翻译服务
  │   ├── Tts/                 # TTS 服务
  │   ├── Input/               # 热键服务
  │   └── External/            # OSC 等外部服务
  ├── 03.VRCLS.UI/             # UI 层：WPF 应用
  │   ├── Views/               # XAML 视图
  │   └── ViewModels/          # MVVM ViewModel
  └── models/                  # ONNX 模型文件目录

  1.2 技术栈

  - .NET 8.0 (Windows-only)
  - WPF + MVVM (CommunityToolkit.Mvvm)
  - 依赖注入: Microsoft.Extensions.DependencyInjection
  - 日志: Serilog
  - 配置存储: %APPDATA%/VRCLS/config.json

  1.3 关键现有文件
  ┌─────────────────────────────────────────────────┬──────────────────────────┐
  │                      文件                       │           说明           │
  ├─────────────────────────────────────────────────┼──────────────────────────┤
  │ 01.VRCLS.Core/Models/AppConfig.cs               │ 配置模型，40+ 属性       │
  ├─────────────────────────────────────────────────┼──────────────────────────┤
  │ 02.VRCLS.Services/Audio/VadService.cs           │ VAD 服务，加载 ONNX 模型 │
  ├─────────────────────────────────────────────────┼──────────────────────────┤
  │ 02.VRCLS.Services/Audio/AudioPipelineFactory.cs │ 音频管道工厂             │
  ├─────────────────────────────────────────────────┼──────────────────────────┤
  │ 03.VRCLS.UI/App.xaml.cs                         │ DI 容器配置，服务注册    │
  ├─────────────────────────────────────────────────┼──────────────────────────┤
  │ 03.VRCLS.UI/ViewModels/MainViewModel.cs         │ 主界面 ViewModel         │
  └─────────────────────────────────────────────────┴──────────────────────────┘
  ---
  2. 功能概述
  ┌──────────────────┬────────────────────────────┬────────────────┐
  │       功能       │            说明            │  是否需要重启  │
  ├──────────────────┼────────────────────────────┼────────────────┤
  │ 应用程序自动更新 │ 检测新版本、下载、安装     │ 是             │
  ├──────────────────┼────────────────────────────┼────────────────┤
  │ 配置热更新       │ 监听配置文件变化，自动重载 │ 否             │
  ├──────────────────┼────────────────────────────┼────────────────┤
  │ 模型热更新       │ 监听模型文件变化，安全重载 │ 否（短暂暂停） │
  └──────────────────┴────────────────────────────┴────────────────┘
  ---
  3. 技术选型

  3.1 应用程序更新：Velopack

  NuGet 包: Velopack

  选择理由:
  - Squirrel.Windows 的现代继承者，专为 .NET 8+ 设计
  - 支持增量更新 (delta updates)
  - 支持静默后台更新
  - 简单的自托管方案（只需 HTTP 静态文件服务器）

  官方文档: https://velopack.io/

  3.2 文件监听

  使用 .NET 内置的 System.IO.FileSystemWatcher

  ---
  4. 功能一：应用程序自动更新

  4.1 新增接口

  文件: 01.VRCLS.Core/Interfaces/IUpdateService.cs

  using System;
  using System.Threading;
  using System.Threading.Tasks;

  namespace VRCLS.Core.Interfaces
  {
      public interface IUpdateService : IDisposable
      {
          Version CurrentVersion { get; }
          UpdateStatus Status { get; }
          int DownloadProgress { get; }
          UpdateInfo? AvailableUpdate { get; }

          event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;
          event EventHandler<int>? DownloadProgressChanged;

          Task<bool> CheckForUpdatesAsync();
          Task DownloadUpdateAsync(CancellationToken cancellationToken = default);
          void ApplyUpdateAndRestart();
          Task SilentUpdateAsync();
      }

      public enum UpdateStatus
      {
          Idle,
          Checking,
          UpdateAvailable,
          Downloading,
          ReadyToInstall,
          Installing,
          Error
      }

      public class UpdateInfo
      {
          public Version Version { get; set; } = new();
          public string ReleaseNotes { get; set; } = string.Empty;
          public DateTime ReleaseDate { get; set; }
          public long DownloadSize { get; set; }
          public bool IsMandatory { get; set; }
      }

      public class UpdateStatusChangedEventArgs : EventArgs
      {
          public UpdateStatus OldStatus { get; set; }
          public UpdateStatus NewStatus { get; set; }
          public string? ErrorMessage { get; set; }
      }
  }

  4.2 服务实现

  文件: 02.VRCLS.Services/Update/VelopackUpdateService.cs

  实现要点:
  1. 构造函数中从 AppConfig.UpdateServerUrl 读取更新服务器地址
  2. 使用 Velopack.UpdateManager 和 SimpleWebSource 创建更新管理器
  3. CheckForUpdatesAsync() 调用 _updateManager.CheckForUpdatesAsync()
  4. DownloadUpdateAsync() 调用 _updateManager.DownloadUpdatesAsync() 并报告进度
  5. ApplyUpdateAndRestart() 调用 _updateManager.ApplyUpdatesAndRestart(null)

  4.3 AppConfig 扩展

  修改文件: 01.VRCLS.Core/Models/AppConfig.cs

  [JsonPropertyName("updateServerUrl")]
  public string UpdateServerUrl { get; set; } = "https://your-server.com/vrcls/releases";

  [JsonPropertyName("autoCheckUpdate")]
  public bool AutoCheckUpdate { get; set; } = true;

  [JsonPropertyName("autoDownloadUpdate")]
  public bool AutoDownloadUpdate { get; set; } = false;

  [JsonPropertyName("lastUpdateCheck")]
  public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;

  [JsonPropertyName("updateCheckIntervalHours")]
  public int UpdateCheckIntervalHours { get; set; } = 24;

  4.4 MainViewModel 集成

  在构造函数末尾添加启动时检查更新：

  private async Task CheckUpdateOnStartupAsync()
  {
      if (!_config.AutoCheckUpdate) return;

      var hoursSinceLastCheck = (DateTime.Now - _config.LastUpdateCheck).TotalHours;
      if (hoursSinceLastCheck < _config.UpdateCheckIntervalHours) return;

      var hasUpdate = await _updateService.CheckForUpdatesAsync();
      _config.LastUpdateCheck = DateTime.Now;

      if (hasUpdate)
      {
          AddLog("System", $"发现新版本: {_updateService.AvailableUpdate?.Version}");

          if (_config.TextInSteamVr && _steamVrService.IsConnected)
          {
              _steamVrService.UpdateText($"VRCLS 有新版本: {_updateService.AvailableUpdate?.Version}");
          }

          if (_config.AutoDownloadUpdate)
          {
              await _updateService.DownloadUpdateAsync();
          }
      }
  }

  ---
  5. 功能二：配置热更新

  5.1 新增接口

  文件: 01.VRCLS.Core/Interfaces/IConfigWatcherService.cs

  using System;
  using System.Collections.Generic;
  using System.Threading.Tasks;

  namespace VRCLS.Core.Interfaces
  {
      public interface IConfigWatcherService : IDisposable
      {
          event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
          void StartWatching();
          void StopWatching();
          Task ReloadConfigAsync();
      }

      public class ConfigChangedEventArgs : EventArgs
      {
          public IReadOnlyList<string> ChangedProperties { get; set; } = Array.Empty<string>();
          public ConfigChangeSource Source { get; set; }
      }

      public enum ConfigChangeSource
      {
          FileWatcher,
          Manual
      }
  }

  文件: 01.VRCLS.Core/Interfaces/IConfigReloadable.cs

  using System.Collections.Generic;

  namespace VRCLS.Core.Interfaces
  {
      public interface IConfigReloadable
      {
          void OnConfigReloaded(IReadOnlyList<string> changedProperties);
      }
  }

  5.2 服务实现

  文件: 02.VRCLS.Services/Config/ConfigWatcherService.cs

  实现要点:
  1. 使用 FileSystemWatcher 监听 %APPDATA%/VRCLS/config.json
  2. 实现防抖（debounce）：文件变化后等待 500ms 再处理
  3. 读取新配置并与旧配置对比，找出变更的属性
  4. 通过反射更新 AppConfig 单例的属性值
  5. 遍历所有实现 IConfigReloadable 的服务，调用 OnConfigReloaded()

  5.3 需要实现 IConfigReloadable 的服务
  ┌───────────────────────────┬──────────────────────────────────┐
  │           服务            │        需要响应的配置变更        │
  ├───────────────────────────┼──────────────────────────────────┤
  │ OscService                │ OscIp, OscPort                   │
  ├───────────────────────────┼──────────────────────────────────┤
  │ HotkeyService             │ VoiceHotKey, MicPressingKey 等   │
  ├───────────────────────────┼──────────────────────────────────┤
  │ TranslationServiceManager │ TranslateService, SourceLanguage │
  ├───────────────────────────┼──────────────────────────────────┤
  │ TtsServiceManager         │ TtsEngine, TtsToggle             │
  └───────────────────────────┴──────────────────────────────────┘
  ---
  6. 功能三：模型热更新

  6.1 新增接口

  文件: 01.VRCLS.Core/Interfaces/IModelReloadService.cs

  using System;
  using System.Threading.Tasks;

  namespace VRCLS.Core.Interfaces
  {
      public interface IModelReloadService : IDisposable
      {
          event EventHandler<ModelReloadEventArgs>? ModelReloading;
          event EventHandler<ModelReloadEventArgs>? ModelReloaded;

          void StartWatching();
          void StopWatching();
          Task ReloadVadModelAsync();
          Task ReloadAsrModelAsync();
      }

      public class ModelReloadEventArgs : EventArgs
      {
          public string ModelType { get; set; } = string.Empty; // "VAD" | "ASR"
          public bool Success { get; set; }
          public string? ErrorMessage { get; set; }
      }
  }

  6.2 重载流程

  1. 检测到模型文件变化
  2. 触发 ModelReloading 事件
  3. MainViewModel 暂停音频管道
  4. AudioPipelineFactory 重新创建管道
  5. 触发 ModelReloaded 事件
  6. MainViewModel 恢复音频管道

  6.3 AudioPipelineFactory 修改

  添加方法：

  public interface IAudioPipelineFactory
  {
      AudioPipelineService CreatePipeline(string name, bool isLoopback);
      void InvalidateCache(); // 新增：清除缓存，强制重建
  }

  ---
  7. 实现步骤

  Phase 1: 配置热更新

  1. 创建 IConfigWatcherService 接口
  2. 创建 IConfigReloadable 接口
  3. 实现 ConfigWatcherService
  4. 修改 OscService 实现 IConfigReloadable
  5. 修改 HotkeyService 实现 IConfigReloadable
  6. 在 App.xaml.cs 注册并启动服务

  Phase 2: 应用程序自动更新

  1. 添加 NuGet 包: dotnet add 02.VRCLS.Services package Velopack
  2. 创建 IUpdateService 接口
  3. 实现 VelopackUpdateService
  4. 扩展 AppConfig
  5. 在 App.xaml.cs 注册服务
  6. 在 MainViewModel 添加启动检查逻辑
  7. 在 SettingsView.xaml 添加更新设置 UI

  Phase 3: 模型热更新

  1. 创建 IModelReloadService 接口
  2. 实现 ModelReloadService
  3. 修改 AudioPipelineFactory
  4. 在 MainViewModel 订阅事件
  5. 在 App.xaml.cs 注册服务

  ---
  8. 服务器部署指南

  8.1 目录结构

  /vrcls/releases/
    ├── RELEASES                 # 版本清单（自动生成）
    ├── VRCLS-1.0.0-full.nupkg  # 完整包
    └── VRCLS-1.1.0-full.nupkg  # 新版本包

  8.2 发布流程

  步骤 1: 安装 Velopack CLI
  dotnet tool install -g vpk

  步骤 2: 构建应用
  dotnet publish 03.VRCLS.UI -c Release -r win-x64 --self-contained -o ./publish

  步骤 3: 打包
  vpk pack --packId VRCLS --packVersion 1.0.0 --packDir ./publish --mainExe VRCLS.UI.exe --outputDir ./releases

  步骤 4: 上传

  将 ./releases/ 目录下所有文件上传到服务器 /vrcls/releases/ 目录。

  8.3 Nginx 配置示例

  location /vrcls/releases/ {
      alias /var/www/vrcls/releases/;
      autoindex off;
      add_header Access-Control-Allow-Origin *;
  }

  8.4 客户端配置

  {
    "updateServerUrl": "https://your-server.com/vrcls/releases"
  }

  ---
  9. 文件清单

  新增文件
  ┌───────────────────────────────────────────────────┬────────────────┐
  │                     文件路径                      │      说明      │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 01.VRCLS.Core/Interfaces/IUpdateService.cs        │ 更新服务接口   │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 01.VRCLS.Core/Interfaces/IConfigWatcherService.cs │ 配置监听接口   │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 01.VRCLS.Core/Interfaces/IConfigReloadable.cs     │ 可重载标记接口 │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 01.VRCLS.Core/Interfaces/IModelReloadService.cs   │ 模型重载接口   │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 02.VRCLS.Services/Update/VelopackUpdateService.cs │ 更新服务实现   │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 02.VRCLS.Services/Config/ConfigWatcherService.cs  │ 配置监听实现   │
  ├───────────────────────────────────────────────────┼────────────────┤
  │ 02.VRCLS.Services/Model/ModelReloadService.cs     │ 模型重载实现   │
  └───────────────────────────────────────────────────┴────────────────┘
  需修改文件
  ┌─────────────────────────────────────────────────┬────────────────────────┐
  │                    文件路径                     │        修改内容        │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 01.VRCLS.Core/Models/AppConfig.cs               │ 添加更新配置属性       │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 02.VRCLS.Services/02.VRCLS.Services.csproj      │ 添加 Velopack 引用     │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 02.VRCLS.Services/External/OscService.cs        │ 实现 IConfigReloadable │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 02.VRCLS.Services/Input/HotkeyService.cs        │ 实现 IConfigReloadable │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 02.VRCLS.Services/Audio/AudioPipelineFactory.cs │ 添加 InvalidateCache() │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 03.VRCLS.UI/App.xaml.cs                         │ 注册新服务             │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 03.VRCLS.UI/ViewModels/MainViewModel.cs         │ 订阅更新和重载事件     │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 03.VRCLS.UI/ViewModels/SettingsViewModel.cs     │ 添加更新设置绑定       │
  ├─────────────────────────────────────────────────┼────────────────────────┤
  │ 03.VRCLS.UI/Views/SettingsView.xaml             │ 添加更新设置 UI        │
  └────────────