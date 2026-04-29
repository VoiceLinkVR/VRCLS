# 监听音频应用级捕获改造说明

## 本次改了什么

这次把原来只有“系统监听音频”的桌面回环链路，扩展成了两个可选来源：

1. 系统监听音频
   - 继续使用 `WasapiLoopbackCapture`
   - 仍然支持默认设备和指定渲染设备
2. 指定应用音频
   - 新增基于 Windows Application Loopback 的按进程捕获
   - 用户可以在设置页选择目标进程
   - 捕获范围使用 `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`
   - 会包含目标进程及其子进程输出的音频

同时，原来偏“桌面音频”的表述统一调整成了“监听音频”，因为现在这一条链路既可能来自系统回环，也可能来自指定应用。

## 设计与实现

### 1. UI 与配置层

新增了监听来源切换：

- 系统监听音频
- 指定应用音频

新增了应用进程选择：

- 枚举当前正在运行的进程
- 显示 `进程名 + PID + 窗口标题`
- 支持手动刷新列表

新增配置字段：

- `desktopAudioSourceMode`
- `appAudioTargetProcessId`
- `appAudioTargetProcessName`

新增结构化请求模型：

- `AudioCaptureRequest`

这样采集服务不再只依赖 `deviceId + isLoopback` 两个参数，而是能明确区分：

- 这是麦克风还是监听音频
- 监听音频是系统回环还是应用回环
- 如果是应用回环，目标 PID 和进程名是什么

### 2. 采集服务分发

`AudioCaptureService` 现在会按请求分发到三条底层链路：

- 麦克风：`WaveInEvent`
- 系统监听音频：`WasapiLoopbackCapture`
- 应用监听音频：`ProcessLoopbackCapture`

系统监听与应用监听后续继续共用同一条音频处理链：

- PCM / float 转换
- 重采样到 16k
- 转单声道
- 音量计算
- 送入现有识别管线

### 3. 应用监听音频底层实现

最终保留的是纯 C# 接入，但实现方式已经改成与已验证可工作的参考实现一致的形状。

当前 `ProcessLoopbackCapture.cs` 的核心链路是：

1. 调用 `ActivateAudioInterfaceAsync`
2. 目标设备使用 `VAD\\Process_Loopback`
3. 激活参数传入 `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`
4. completion handler 直接接收 `IAudioClient`
5. 使用 `new AudioClient(IAudioClient)` 包装成 NAudio 的 `AudioClient`
6. 显式指定固定 `WaveFormat`
7. 使用 event callback 模式读取 `AudioCaptureClient`

和之前失败版本相比，关键变化是：

- 不再走手写 `IAudioClientNative` / `QueryInterface` 那条不稳定路径
- 不再访问 `GetMixFormat`
- 不再依赖当前实现里那组自定义 COM 接口声明
- 直接复用上游可工作的 `IAudioClient -> AudioClient` 初始化方式
- `StopRecording` 时主动唤醒事件等待，避免静音目标卡在停止超时

## 为什么之前会报错

用户现场报错是：

- `InvalidCastException`
- `E_NOINTERFACE`

后续继续验证时，当前机器上的主故障点还表现为：

- `ActivateAudioInterfaceAsync` 返回 `0x8000000E`

最后确认问题不在操作系统版本，而在托管互操作实现形状本身。

关键验证结论：

1. 这台机器是 Windows 10 19045，满足 Application Loopback 的实际可用条件
2. 上游 `AppLoopback` 纯 C# 示例在这台机器上可以直接成功运行
3. 因此，问题是我们原先的 `ProcessLoopbackCapture` 写法不兼容，而不是系统不支持

## 本次验证结果

### 已完成的验证

- 上游 `AppLoopback` 示例工程可在本机成功编译并运行
- 示例在本机成功生成应用级捕获的 `sample.wav`
- 主工程里的 `ProcessLoopbackCapture` 已替换为同一路径的实现
- `Output/ProcessLoopbackProbe` 实测日志显示：
  - `StartCapture succeeded`
  - `StopCapture succeeded`

### 关于 `SawAudio=False`

探针里出现 `SawAudio=False` 并不表示实现失败，只表示在那次 2 秒窗口里目标进程没有实际输出音频帧。核心结论是：

- COM 激活成功
- AudioClient 初始化成功
- 主工程已经可以启动应用监听音频捕获

## 涉及的主要文件

- `01.VRCLS.Core/Helpers/AudioPlatformHelper.cs`
- `01.VRCLS.Core/Interfaces/IAudioService.cs`
- `01.VRCLS.Core/Models/AppConfig.cs`
- `01.VRCLS.Core/Models/AudioCaptureRequest.cs`
- `01.VRCLS.Core/Models/ConfigEnums.cs`
- `02.VRCLS.Services/Audio/AudioCaptureService.cs`
- `02.VRCLS.Services/Audio/AudioPipelineService.cs`
- `02.VRCLS.Services/Audio/ProcessLoopbackCapture.cs`
- `03.VRCLS.UI/Resources/Strings.en.xaml`
- `03.VRCLS.UI/Resources/Strings.zh.xaml`
- `03.VRCLS.UI/ViewModels/MainViewModel.cs`
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs`
- `03.VRCLS.UI/Views/SettingsView.xaml`
- `03.VRCLS.UI/Views/SettingsView.xaml.cs`

## 参考资料

- Microsoft Application Loopback sample:
  - <https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/>
- `ActivateAudioInterfaceAsync` 官方文档:
  - <https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync>
- 可工作的 C# 参考实现 `joseluisct/AppLoopback`:
  - <https://github.com/joseluisct/AppLoopback>
- OBS 相关实现 `bozbez/win-capture-audio`:
  - <https://github.com/bozbez/win-capture-audio>
- 提供预编译 DLL 的第三方项目 `tsubome/ProcessAudioCapture`:
  - <https://github.com/tsubome/ProcessAudioCapture>

## 2026-03-13 补充

- 监听音频的默认来源改为“指定应用音频”。
- 默认目标应用改为 `VRChat`。新配置、缺失字段配置、以及目标应用名被清空的旧配置，都会回退到这个默认值。
- 设置页中的应用列表改为优先显示窗口标题，不再把 PID 混在显示文本里；只有在没有窗口标题时才回退显示进程名。
- “程序启动时默认开启监听音频”复选框补齐了对应的多语言资源键，避免 UI 上出现空白标签。
