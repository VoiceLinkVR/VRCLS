# VRCLS GUI 更新系统迁移交接说明

## 目的

本文件用于在新 Codex 线程中继续实施 GUI 更新系统迁移。

目标路线已经确定：

- 首次安装：`传统安装器（Inno Setup / WiX）`
- 应用内更新：`NetSparkle`

不再继续沿用 `Velopack` 作为安装器与更新框架的一体化方案。

## 当前结论

`Velopack` 不适合当前项目的最终诉求，原因如下：

- 首次安装希望具备传统安装器体验：
  - 可选安装目录
  - 可选桌面图标
  - 安装完成后可选立即启动
- 已尝试“外层 Inno + 内层 Velopack Setup.exe”的嵌套包装方案，但用户反馈安装后会触发一次资源管理器异常，方案不稳定
- `Velopack` 适合一体化安装/更新，但不适合继续强行适配当前安装体验需求

因此，后续方案统一切换为：

- `Inno Setup` 或 `WiX` 负责首次安装
- `NetSparkle` 负责应用内检查更新、下载更新安装包、执行更新、更新后重启

## 当前仓库状态

最后一个相对干净、已提交的基线提交：

- `81d3357`
- 提交信息：`refactor updater to use velopack releases`

在此之后，存在一组“恢复传统安装体验”的未提交试验改动。这组改动的方向是：

- 先执行 Velopack 发布
- 再用 Inno Setup 包装生成一个外层安装器

该方案目前应视为废弃候选，不应继续作为主线方案推进。

## 已知未提交试验文件

以下文件是上一轮围绕 `Velopack + 外层 Inno 包装` 做的未提交实验，后续迁移时需要重点检查、清理或重写：

- `D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\Program.cs`
- `D:\git\gitlab\VRCLS-CSharp\docs\GUI发布操作清单.md`
- `D:\git\gitlab\VRCLS-CSharp\scripts\build-installer.ps1`
- `D:\git\gitlab\VRCLS-CSharp\scripts\build-velopack-release.ps1`
- `D:\git\gitlab\VRCLS-CSharp\scripts\generate_bucket_layout_gui.py`

## 当前试验方案里已经做过的事

这些内容不是最终方案，但新线程需要知道，以避免重复判断：

1. `scripts/build-installer.ps1`
   - 曾被改造成“先构建 Velopack release，再包一层 Inno 安装器”的流程
2. `03.VRCLS.UI/Program.cs`
   - 曾加入 Velopack 生命周期钩子：
   - `OnAfterInstallFastCallback`
   - `OnBeforeUpdateFastCallback`
   - `OnAfterUpdateFastCallback`
   - `OnBeforeUninstallFastCallback`
3. `scripts/build-velopack-release.ps1`
   - 使用过 `--shortcuts StartMenuRoot`
   - 目的是避免 Velopack 默认直接创建桌面快捷方式
4. GUI 发布文档
   - 已被调整过，用于描述 Velopack 发布与外层安装器的混合流程

这些改动大部分都需要被替换掉，而不是继续修补。

## 用户明确要求

以下要求已经由用户明确提出，并且后续实现必须满足：

### 安装器要求

- 采用传统安装器体验
- 支持自定义安装位置
- 支持是否创建桌面图标
- 支持安装完成后是否启动程序

### 更新系统要求

- 启动程序时自动检查更新
- 发现新版本时弹窗提示更新
- 下载更新后自动执行安装
- 更新完成后自动重启程序

### 资源与模型要求

- 启动程序时检查模型 / VAD 模型是否存在
- 不存在时弹窗提示并自动下载
- 打开本地识别时也要再次检查模型是否存在
- 模型文件 cache 需要支持清理

### 上传要求

- 上传更新文件前，先检查待上传清单
- 避免一次性上传大量历史文件
- GUI 上传脚本之前传错过位置，后续需要重新核对上传目标路径

### 构建与产物要求

- GUI 那边的打包流程需要一起改
- 安装器输出到 `Output` 目录
- 生成的安装器文件名需要带显示版本号
- 需要单独文档说明 GUI 如何编译和发布当前更新系统

### 配置模板要求

- GUI 配置模板保存时，希望能保存到用户指定的位置

## 新方案的实施范围

### 一、应用侧：从 Velopack 切到 NetSparkle

需要处理的内容：

1. 从 GUI 项目中移除 Velopack 相关依赖
2. 删除 `Program.cs` 中的 Velopack 生命周期钩子
3. 接入 `NetSparkle`
4. 在 GUI 启动阶段初始化更新器
5. 启动时自动检查更新
6. 保留或新增“手动检查更新”入口
7. 配置更新完成后自动关闭当前程序并启动安装器
8. 安装完成后自动重启程序

建议重点检查：

- `03.VRCLS.UI/03.VRCLS.UI.csproj`
- `03.VRCLS.UI/Program.cs`
- GUI 中现有“检查更新”按钮、菜单或命令绑定位置

### 二、发布链路：从 Velopack 发布切到 Inno + NetSparkle

需要处理的内容：

1. 废弃 `build-velopack-release.ps1` 主流程
2. 重写 `build-installer.ps1`
3. 新流程应为：
   - 构建 GUI 发布目录
   - 生成 Inno Setup 安装器
   - 生成 NetSparkle 所需 `appcast.xml`
   - 生成对应签名信息
   - 将本次版本产物输出到 `Output`
4. 安装器文件名带显示版本号
5. 上传前生成或显示待上传清单
6. 上传逻辑仅针对当前版本产物，不扫描整个历史目录

建议优先使用 `Inno Setup`，原因：

- 当前项目已有使用基础
- 对“安装目录 / 桌面图标 / 安装完成后启动”支持最直接
- 与 NetSparkle 配合方式清晰

## 推荐的新发布结构

建议统一发布结构如下：

### 本地构建产物

- `Output\`
  - `VRCLS-{DisplayVersion}-setup.exe`
  - `appcast.xml`
  - 可能的签名文件
  - 可能的发布清单文件

### 远端更新源

建议远端目录仅保存当前更新所需的明确文件，例如：

- 安装器
- `appcast.xml`
- 对应签名文件

不要让上传脚本递归扫整个历史构建目录。

## NetSparkle 方案注意点

新线程实施时，需要先确认以下设计点：

1. 更新包形式
   - 对 Windows，NetSparkle 可以直接分发安装器，由客户端下载后执行安装
2. 安装器静默参数
   - 需要为 Inno Setup 提供适合自动更新场景的命令行参数
   - 例如静默安装、保留原安装路径、安装完成后重启应用
3. 签名
   - NetSparkle 使用 `appcast` 与签名机制校验更新
   - 发布脚本需要明确签名密钥来源与使用方式
4. 版本来源
   - `internal version` 与 `display version` 的来源和传递链路要统一
   - 安装器命名、GUI 显示、更新元数据必须一致

## 新线程建议的第一步

新开 Codex 线程后，先不要直接编辑，先执行以下检查：

1. `git status --short`
2. 检查这几个文件当前状态：
   - `03.VRCLS.UI/Program.cs`
   - `03.VRCLS.UI/03.VRCLS.UI.csproj`
   - `scripts/build-installer.ps1`
   - `scripts/build-velopack-release.ps1`
   - `scripts/generate_bucket_layout_gui.py`
   - `docs/GUI发布操作清单.md`
3. 搜索仓库中的以下关键字：
   - `Velopack`
   - `vpk`
   - `Setup.exe`
   - `update`
   - `NetSparkle`

只有在确认仓库状态后，再开始替换更新框架。

## 新线程建议的执行顺序

### 阶段 1：盘点并清理旧方案

1. 确认当前未提交改动
2. 识别所有 Velopack 相关代码和脚本引用
3. 判断哪些文件直接重写，哪些文件只做增量修改

### 阶段 2：完成应用内更新迁移

1. 移除 Velopack NuGet 依赖
2. 接入 NetSparkle
3. 完成启动自动检查更新
4. 完成手动检查更新入口
5. 完成更新下载、执行安装器和重启链路

### 阶段 3：完成发布脚本迁移

1. 重写 GUI 构建脚本
2. 重写安装器构建脚本
3. 增加 `appcast.xml` 生成逻辑
4. 增加签名逻辑
5. 增加上传前清单检查

### 阶段 4：补齐文档

1. 更新 GUI 编译文档
2. 更新 GUI 发布文档
3. 单独补一份“怎么测试更新”的文档

## 新线程中应避免的方向

以下方向不要再继续推进：

1. 不要继续做“外层 Inno + 内层 Velopack Setup.exe”的嵌套安装器
2. 不要继续围绕 Velopack 修补安装体验问题
3. 不要让上传脚本扫描整个历史发布目录
4. 不要让 GUI 构筑入口同时混合两套发布逻辑

## 建议的新线程提示词

可以在新线程直接使用下面这段作为起始说明：

```text
继续实施 VRCLS GUI 更新系统迁移。

目标路线已经确定：
- 首次安装改为 Inno Setup（或 WiX，但优先 Inno Setup）
- 应用内热更新改为 NetSparkle

请先检查仓库当前状态，并以 docs/NetSparkle迁移交接说明.md 为准继续工作。

优先完成：
1. 移除 Velopack 依赖和 Program.cs 里的 Velopack 钩子
2. 接入 NetSparkle，实现启动自动检查更新和手动检查更新
3. 重写 scripts/build-installer.ps1 和相关 GUI 发布流程
4. 生成 Output 下带显示版本号的安装器
5. 增加 appcast/signature 生成与上传前清单检查
6. 更新相关文档
```

## 补充说明

本次旧线程中断的原因不是代码问题，而是本地命令执行环境异常，表现为：

- 任意命令执行失败
- 典型错误：`0xC0000142 (-1073741502)`

因此本文件的作用是帮助新线程快速恢复上下文，避免重复分析。
