# VRCLS 更新系统具体改造实施方案

## 1. 文档目的

本文档用于把“更新系统重构方案”落实到具体代码修改层面，明确：

- 需要改哪些文件
- 每个文件改什么
- 改造顺序是什么
- 哪些旧逻辑必须删除
- 发布链路需要如何调整

本文档仍然是实施设计文档，不包含代码修改。

---

## 2. 最终目标

VRCLS 的更新体系拆成三条独立链路：

1. 首次安装链路
2. 主程序自更新链路
3. 资源热更新链路

对应原则如下：

- 首次安装继续使用 Inno Setup 安装包
- 主程序版本升级改为 Velopack 自更新
- 模型、翻译配置、translator runtime 继续使用静态 manifest
- 缓存清理从主程序更新链路中剥离

---

## 3. 当前代码中的关键问题

### 3.1 接口设计仍然是“下载包再安装”

当前 [IUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\01.VRCLS.Core\Interfaces\IUpdateService.cs) 的语义是：

- `CheckForUpdatesAsync`
- `DownloadUpdateAsync(...)` 返回本地包路径
- `ApplyUpdateAsync(string downloadedPackagePath)`

这说明当前接口默认客户端需要自己处理：

- 下载更新包
- 保存本地文件
- 识别 zip / exe
- 决定如何安装

这与目标架构冲突，因为 Velopack 的正确使用方式不是“让 UI 持有安装包路径”，而是“让更新框架接管下载、应用和重启”。

### 3.2 主程序更新和资源更新混在一起

当前 [AppConfig.cs](D:\git\gitlab\VRCLS-CSharp\01.VRCLS.Core\Models\AppConfig.cs) 中只有一个：

- `UpdateServerUrl`

但该字段目前同时承担：

- 主程序更新源
- 模型清单和模型包基地址
- 翻译配置更新源
- translator runtime 资源更新源

这会导致：

- 配置语义不清
- 迁移到 Velopack 时无法明确 feed 地址
- 发布侧目录结构难以拆分

### 3.3 旧更新服务承担了过多职责

当前 [HttpUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\Update\HttpUpdateService.cs) 同时承担：

- 读取静态 update manifest
- 下载应用更新包
- 校验哈希
- 处理 zip 解压
- 处理 exe 安装器调度
- 清理 `%AppData%\VRCLS\updates`
- 删除 `%AppData%\VRCLS\model-cache`

这已经不是“更新服务”，而是“安装调度 + 临时目录清理 + 资源删除”的混合入口。

### 3.4 UI 和启动流程仍然围绕安装包工作

当前：

- [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs) 仍注册 `HttpUpdateService`
- 启动自动更新逻辑会下载包文件
- [SettingsViewModel.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\ViewModels\SettingsViewModel.cs) 仍然是“检查更新 -> 下载更新包 -> 立即安装”

这意味着即使底层替换一部分实现，只要接口和 ViewModel 不变，整体交互仍然会回到旧设计。

### 3.5 发布链路仍然是静态包思维

当前脚本和 GUI 仍然围绕以下产物组织：

- zip 包
- exe 安装器
- 静态 update manifest

相关文件包括：

- [generate_bucket_layout.py](D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout.py)
- [generate_bucket_layout_gui.py](D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout_gui.py)
- [build-update-manifest.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\build-update-manifest.ps1)

这套输出结构不能直接供 Velopack 消费，因此发布端也必须同步改造。

---

## 4. 目标架构

### 4.1 职责拆分

#### A. 首次安装

由 Inno Setup 负责：

- 安装到目标目录
- 创建快捷方式
- 注册卸载信息
- 提供首次安装入口

#### B. 主程序更新

由 Velopack 负责：

- 检查新版本
- 下载更新
- 应用更新
- 自动重启

#### C. 资源热更新

由现有静态 manifest 机制负责：

- 模型清单和模型包更新
- 翻译配置更新
- translator runtime 资源更新

#### D. 缓存治理

单独作为 housekeeping 服务负责：

- `model-cache`
- translator runtime 包缓存
- 未来其他业务缓存

不再挂在“主程序更新完成后”这个时机。

---

## 5. 具体代码改造方案

## 5.1 Phase 1：接口层与配置层重构

### 目标

先把“主程序更新”的抽象语义改对，停止围绕“本地安装包路径”设计调用链。

### 涉及文件

- [IUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\01.VRCLS.Core\Interfaces\IUpdateService.cs)
- [AppConfig.cs](D:\git\gitlab\VRCLS-CSharp\01.VRCLS.Core\Models\AppConfig.cs)
- [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs)

### 具体修改

#### 1. 废弃现有 `IUpdateService` 语义

当前接口不应继续保留以下语义：

- `DownloadUpdateAsync` 返回本地文件路径
- `ApplyUpdateAsync(string downloadedPackagePath)`

建议改造为新的主程序更新接口，例如：

- `IAppUpdateService`

建议职责：

- 获取当前版本
- 检查是否有可用更新
- 获取可用更新信息
- 下载更新
- 应用更新并重启
- 暴露状态和进度

建议接口语义示例：

```csharp
public interface IAppUpdateService
{
    Version CurrentVersion { get; }
    AppUpdateState State { get; }
    AppUpdateInfo? PendingUpdate { get; }

    Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task DownloadUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task ApplyUpdateAndRestartAsync(CancellationToken cancellationToken = default);
}
```

这里的重点不是命名，而是语义必须改成：

- 更新由服务内部管理
- UI 不再持有安装包路径
- UI 只关心状态和动作

#### 2. `AppConfig` 拆分更新配置

当前只有：

- `UpdateServerUrl`

建议拆成：

- `AppUpdateFeedUrl`
- `StaticContentBaseUrl`

其中：

- `AppUpdateFeedUrl` 供 Velopack 使用
- `StaticContentBaseUrl` 供模型 / 翻译配置 / runtime 资源更新使用

如果希望尽量减少改动，也可以保留 `UpdateServerUrl` 作为资源更新地址，但必须新增：

- `AppUpdateFeedUrl`

推荐新增字段：

```json
{
  "appUpdateFeedUrl": "https://cdn.example.com/vrcls/app-releases/",
  "updateServerUrl": "https://cdn.example.com/vrcls/"
}
```

#### 3. 配置兼容与迁移

在 [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs) 的配置加载阶段增加迁移逻辑：

- 如果旧配置只有 `updateServerUrl`
- 且未配置 `appUpdateFeedUrl`
- 则按既定规则补出新的 `appUpdateFeedUrl`

迁移策略建议：

- 旧 `updateServerUrl` 继续视为资源基础地址
- `appUpdateFeedUrl` 自动推导为 `<updateServerUrl>/app-releases/` 或固定目标目录

同时更新配置校验逻辑：

- `AppUpdateFeedUrl` 必须是绝对 URL
- `UpdateServerUrl` 继续校验为绝对 URL

### Phase 1 完成标准

- 主程序更新接口不再暴露安装包路径
- 配置中可以明确区分“应用更新源”和“资源更新源”
- 启动配置迁移路径明确

---

## 5.2 Phase 2：主程序更新服务切换到 Velopack

### 目标

让主程序升级彻底脱离 `zip/exe` 分支逻辑，改由 Velopack 统一负责更新、替换、重启。

### 涉及文件

- [02.VRCLS.Services.csproj](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\02.VRCLS.Services.csproj)
- [03.VRCLS.UI.csproj](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\03.VRCLS.UI.csproj)
- [HttpUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\Update\HttpUpdateService.cs)
- 新增 `VelopackAppUpdateService.cs`
- [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs)

### 具体修改

#### 1. 引入 Velopack 依赖

在服务层增加 Velopack 依赖。

优先放在：

- [02.VRCLS.Services.csproj](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\02.VRCLS.Services.csproj)

如果 Velopack 的启动接管要求入口程序集初始化，也可能需要同步调整：

- [03.VRCLS.UI.csproj](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\03.VRCLS.UI.csproj)

#### 2. 新建 `VelopackAppUpdateService`

建议新建：

- `02.VRCLS.Services/Update/VelopackAppUpdateService.cs`

职责：

- 读取 `AppUpdateFeedUrl`
- 通过 Velopack 检查更新
- 下载更新
- 应用更新并重启
- 对外返回统一的 `AppUpdateInfo`

内部应封装 Velopack 的细节，不把其对象直接暴露到 UI 层。

#### 3. 旧 `HttpUpdateService` 降级为资源更新辅助或彻底拆分

当前 [HttpUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\Update\HttpUpdateService.cs) 中以下逻辑必须退出主程序升级场景：

- `ApplyUpdateAsync`
- zip 解压分支
- exe 调度分支
- `ScheduleInstallerAfterExit`
- `BuildDeferredInstallerEncodedCommand`
- `CleanupHistoricalUpdateArtifacts`
- `GetModelCacheRoot`

保留策略有两种：

#### 方案 A：直接废弃 `HttpUpdateService`

优点：

- 边界最清晰
- 不会残留旧职责

缺点：

- 需要把资源相关公共能力挪到新的资源服务中

#### 方案 B：重命名并拆成资源服务

例如：

- `StaticResourceUpdateHttpClient`
- `StaticManifestResourceService`

推荐方案 B，因为模型和翻译资源更新仍然需要静态 HTTP 能力，但名称和职责必须改正，不应继续叫 “UpdateService”。

#### 4. DI 注册切换

在 [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs) 中：

- 去掉 `services.AddSingleton<IUpdateService, HttpUpdateService>();`
- 改成注册新的 `IAppUpdateService`

### Phase 2 完成标准

- 主程序更新不再下载 zip/exe 给客户端自己处理
- 更新后可由框架自动重启
- 服务层职责从“包处理器”变成“更新状态服务”

---

## 5.3 Phase 3：启动流程与设置页交互改造

### 目标

把 UI 和启动流程从“安装包工作流”切换成“状态驱动更新工作流”。

### 涉及文件

- [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs)
- [SettingsViewModel.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\ViewModels\SettingsViewModel.cs)
- [SettingsView.xaml](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\Views\SettingsView.xaml)

### 具体修改

#### 1. 启动自动更新逻辑重写

当前 [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs) 中 `RunStartupUpdateJobsAsync` 的主程序更新部分仍会：

- 调用检查更新
- 自动下载包文件

这部分应改为：

- 检查是否存在可用更新
- 若开启自动下载，则由 Velopack 预下载
- 若下载完成，则记录“可重启应用更新”

这里不再出现：

- 包路径
- 压缩包类型判断
- 安装器排队执行

#### 2. 设置页命令重构

当前 [SettingsViewModel.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\ViewModels\SettingsViewModel.cs) 中：

- `CheckAppUpdateAsync`
- `DownloadAndApplyAppUpdateAsync`

都还是旧的安装包思路。

建议重构为三段式：

1. `CheckAppUpdateAsync`
2. `DownloadAppUpdateAsync`
3. `RestartAndApplyAppUpdateAsync`

或者保留一个统一命令，但内部根据状态切换：

- 无更新时：检查
- 有更新未下载：下载
- 已下载完成：重启并应用

#### 3. ViewModel 状态字段重构

当前字段：

- `_pendingAppUpdate`
- `_updateStatusText`
- `_updateProgress`

可以保留，但要改语义。

建议新增或明确状态枚举：

- `Idle`
- `Checking`
- `UpdateAvailable`
- `Downloading`
- `ReadyToRestart`
- `Applying`
- `Failed`

UI 根据状态启用或禁用按钮。

#### 4. 设置页文案调整

[SettingsView.xaml](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\Views\SettingsView.xaml) 中与更新有关的文案应改为：

- 检查更新
- 发现新版本
- 下载更新
- 更新已准备就绪
- 重启并完成更新

不应再出现以下概念：

- update package
- installer
- zip
- exe
- pending folder

### Phase 3 完成标准

- UI 不再以“下载包并安装”为核心交互
- 自动更新和手动更新都基于状态驱动
- 用户只感知“检查、下载、重启更新”

---

## 5.4 Phase 4：资源热更新逻辑独立化

### 目标

保留模型与翻译资源热更新，但从主程序更新链路中彻底拆出来。

### 涉及文件

- [HttpUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\Update\HttpUpdateService.cs)
- [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs)
- 模型更新服务相关文件
- 翻译热更新服务相关文件

### 具体修改

#### 1. 明确资源更新的边界

资源更新只负责：

- 模型 catalog 获取
- 模型包下载
- 翻译 profile 同步
- translator runtime 包下载

不负责：

- 主程序版本升级
- 程序重启
- 应用安装过程

#### 2. 保留静态 manifest 目录结构

保留现有：

- `manifests/models/list.json`
- `manifests/translations/profile.json`

后续如有 runtime 包，也继续作为资源清单的一部分管理。

#### 3. 资源服务命名调整

如果继续复用一部分 HTTP 逻辑，建议拆成更明确的服务名称，例如：

- `StaticManifestClient`
- `ResourcePackageDownloader`

避免继续让主程序更新和资源更新共享同一个“总更新服务”。

### Phase 4 完成标准

- 主程序更新与资源更新服务在类型上完全分离
- 资源更新不再依赖应用安装逻辑

---

## 5.5 Phase 5：缓存清理从更新链路剥离

### 目标

让 `model-cache` 等清理逻辑成为独立治理功能，而不是主程序更新的副作用。

### 涉及文件

- [HttpUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\Update\HttpUpdateService.cs)
- 新增 `CacheHousekeepingService.cs`
- [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs)
- 如需要，增加设置页入口

### 具体修改

#### 1. 从旧更新服务中删除以下逻辑

- 删除 `model-cache`
- 删除 updates 临时目录
- 安装器完成后顺带清理业务缓存

#### 2. 新建独立 housekeeping 服务

建议新增：

- `ICacheHousekeepingService`
- `CacheHousekeepingService`

建议职责：

- 清理过期模型缓存
- 清理 runtime 包缓存
- 清理超过阈值的临时目录

#### 3. 触发时机独立

建议触发方式：

- 应用启动后异步执行
- 空闲时执行
- 用户在设置页中手动触发

不再通过“应用更新完成后”触发。

### Phase 5 完成标准

- 更新服务中不再出现模型缓存删除逻辑
- 缓存治理可以独立扩展

---

## 6. 发布链路改造方案

## 6.1 最终发布产物拆分

发布端必须输出两类完全不同的产物：

### A. 首次安装产物

由 Inno Setup 输出：

- `VRCLS-<display-version>-<runtime>-setup.exe`

当前这条链路可继续基于：

- [publish.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\publish.ps1)
- [build-installer.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\build-installer.ps1)

并继续满足以下要求：

- 产物放到 `Output/`
- 文件名带显示版本号

### B. 主程序更新产物

由 Velopack 输出：

- `RELEASES`
- `*.nupkg`

建议目录示例：

```text
<bucket-root>/
  app-releases/
    RELEASES
    VRCLS-0.8.0-full.nupkg
    VRCLS-0.8.1-delta.nupkg
```

### C. 资源更新产物

继续保留：

```text
<bucket-root>/
  manifests/
    models/
      list.json
    translations/
      profile.json
  models/
  translation-profiles/
  translator-runtime/
```

## 6.2 脚本层需要修改的文件

### [publish.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\publish.ps1)

继续保留为基础发布脚本，不负责更新仓库生成。

### [build-installer.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\build-installer.ps1)

继续保留，只负责首次安装 exe，不参与后续日常更新包。

### 新增脚本：`build-velopack-release.ps1`

建议新增专用脚本，职责：

- 调用 `publish.ps1`
- 基于 publish 结果执行 Velopack 打包
- 输出到独立目录
- 生成 `RELEASES` 和 `.nupkg`

### [build-update-manifest.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\build-update-manifest.ps1)

主程序更新相关内容应逐步退出使用范围，仅保留资源清单用途，或改名为更明确的资源清单脚本。

### [generate_bucket_layout.py](D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout.py)

当前应用更新部分围绕：

- app package
- releases/
- manifests/update/

这部分需要重构为：

- 资源清单生成
- 可选整合 Velopack 产物复制

不再生成“主程序静态更新 manifest”。

### [generate_bucket_layout_gui.py](D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout_gui.py)

GUI 应拆成至少两个动作：

- 生成首次安装包
- 生成应用更新源

并保留：

- 输出到 `Output/`
- 版本号展示
- 用户指定模板保存位置

但主程序更新部分的 GUI 语义必须改成 Velopack 发布，而不是选择 zip/exe 包生成静态 manifest。

---

## 7. 逐文件修改清单

## 7.1 Core 层

### [IUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\01.VRCLS.Core\Interfaces\IUpdateService.cs)

处理建议：

- 不建议继续沿用原接口定义
- 建议替换为新的主程序更新接口，或新建 `IAppUpdateService.cs`

### [AppConfig.cs](D:\git\gitlab\VRCLS-CSharp\01.VRCLS.Core\Models\AppConfig.cs)

需要修改：

- 新增 `AppUpdateFeedUrl`
- 保留或重命名 `UpdateServerUrl`
- 更新校验逻辑
- 增加旧配置迁移兼容

## 7.2 Services 层

### [HttpUpdateService.cs](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\Update\HttpUpdateService.cs)

需要处理：

- 去掉主程序更新职责
- 去掉安装器调度职责
- 去掉缓存删除职责
- 如保留，则重构成资源服务

### [02.VRCLS.Services.csproj](D:\git\gitlab\VRCLS-CSharp\02.VRCLS.Services\02.VRCLS.Services.csproj)

需要修改：

- 引入 Velopack 包

### 新增 `VelopackAppUpdateService.cs`

职责：

- 主程序更新检查
- 下载
- 应用并重启

### 新增 `CacheHousekeepingService.cs`

职责：

- 模型缓存清理
- 运行时包缓存清理

## 7.3 UI 层

### [App.xaml.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\App.xaml.cs)

需要修改：

- DI 注册切换到新的主程序更新服务
- 更新配置迁移逻辑
- 启动自动更新逻辑改为 Velopack 流程

### [SettingsViewModel.cs](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\ViewModels\SettingsViewModel.cs)

需要修改：

- 重写更新状态字段
- 重写更新命令
- 删除“下载包路径 -> 立即安装”的交互

### [SettingsView.xaml](D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\Views\SettingsView.xaml)

需要修改：

- 更新相关按钮文案
- 更新状态显示逻辑
- 删除 installer/package 概念

## 7.4 脚本与打包层

### [publish.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\publish.ps1)

保留基础构建职责。

### [build-installer.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\build-installer.ps1)

保留首次安装包产出职责。

### 新增 `build-velopack-release.ps1`

用于生成 Velopack 更新源。

### [build-update-manifest.ps1](D:\git\gitlab\VRCLS-CSharp\scripts\build-update-manifest.ps1)

缩减或迁移为资源 manifest 用途。

### [generate_bucket_layout.py](D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout.py)

重构掉主程序静态更新 manifest 生成。

### [generate_bucket_layout_gui.py](D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout_gui.py)

重构 GUI 打包语义和动作。

---

## 8. 实施顺序建议

建议严格按以下顺序推进：

1. 先改 `Core` 接口与 `AppConfig`
2. 再补 Velopack 发布脚本
3. 再实现 `VelopackAppUpdateService`
4. 再改 `App.xaml.cs` 启动逻辑
5. 再改设置页 ViewModel 和 XAML
6. 最后拆掉旧 `HttpUpdateService` 中的安装器与缓存清理逻辑
7. 最后再整理 GUI 打包工具

这样做的原因是：

- 先改接口，避免 UI 和服务继续沿用旧语义
- 先补发布产物，避免客户端改完却没有可消费的更新源
- 最后再删除旧逻辑，降低迁移期间的断层风险

---

## 9. 明确不再继续推进的旧方案

以下设计不建议再继续扩展：

- 客户端下载 `.exe` 后退出并调用安装器
- 客户端下载 `.zip` 后自行解压覆盖
- 主程序更新完成后顺带删除 `model-cache`
- 用单一 `UpdateServerUrl` 同时表示应用更新源和资源更新源
- 用静态 app manifest 长期充当主程序版本升级机制

---

## 10. 最终结论

本次具体改造的核心不是“把 zip 改成 exe”，而是把整个主程序更新机制从：

- 静态包下载
- 客户端手工安装调度

切换为：

- Velopack 自更新
- 状态驱动 UI
- 自动重启应用

同时保留：

- Inno Setup 作为首次安装入口
- 静态 manifest 作为资源热更新入口

并把：

- `model-cache`
- updates 临时目录
- 其他业务缓存

从主程序更新链路中拆出去，作为独立治理功能处理。

这才是后续代码实施时应坚持的边界。
