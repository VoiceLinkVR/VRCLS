# PyInstaller 打包说明（translator.exe）

本文档用于后续维护 `translator.exe` 的打包流程，适用于 VRCLS 项目。

## 1. 目标与现状

- Python 入口脚本：`scripts/translator.py`
- 打包脚本：`scripts/build_translator.ps1`
- 产物目录：`tools/translator/`（onedir）
- UI 工程会自动把 `tools/translator/**` 复制到输出目录 `translator/**`

> 说明：当前三语言（主/第二/第三目标语种）由 C# 侧 `TranslationServiceManager` 负责组合调用（针对 python 引擎），无需在 `translator.py` 内单独实现三语协议。

## 2. 前置环境

建议在干净的 Python 虚拟环境中执行：

1. Python 3.10+（建议 3.11）
2. 可联网安装依赖
3. PowerShell 可执行脚本

安装依赖（示例）：

```powershell
python -m pip install -U pip
python -m pip install -r scripts/requirements_translator.txt
python -m pip install pyinstaller
```

## 3. 一键打包

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_translator.ps1 -Python python
```

说明：

- 默认使用 `--onedir`，避免 `--onefile` 的每次解包开销。
- Build command includes `--collect-all translators --collect-submodules translators` to bundle translators dependencies.
- 打包完成后，产物位于：`tools/translator/translator.exe`（及其依赖文件）。

## 4. 打包后自检

### 4.1 启动握手检查

```powershell
.\tools\translator\translator.exe
```

预期第一行输出：

```json
{"status": "ready"}
```

### 4.2 协议检查（可选）

向 stdin 发送：

```json
{"cmd":"ping"}
```

预期输出：

```json
{"status":"pong"}
```

### 4.3 应用侧验证

- 在设置中选择 `google/bing/baidu/yandex/deepl` 任一 python 翻译引擎。
- 观察日志应出现 python 路由。
- 配置三语言目标后，确认能返回多段翻译（由 C# 侧拼接为 `t1||t2||t3`）。

## 5. 常见问题

### 5.1 打包成功但运行报缺 DLL/模块

- 先确认虚拟环境内依赖已安装完整。
- 优先升级 `pyinstaller` 与 `translators`。
- 必要时在 `build_translator.ps1` 增加 `--hidden-import`。

### 5.2 产物存在但应用未调用

检查：

1. 输出目录是否存在 `translator/translator.exe`
2. 日志是否显示 `Translation route selected. engine=... service=EmbeddedTranslatorService`
3. 设置项是否仍被旧配置覆盖（`translationServer` / `translateService`）

### 5.3 三语言未生效

- 确认目标语种2/3不是 `none`
- 确认当前引擎属于 python 路由（google/bing/baidu/yandex/deepl）
- 确认 `TranslationServiceManager` 为最新版本（含 multi-target 逻辑）

## 6. 发布建议

发布前建议流程：

1. 重新打包 translator
2. 清理旧 `tools/translator` 后覆盖新产物
3. 本地运行 UI 验证 python 引擎
4. 再执行主程序发布脚本（如 `scripts/publish.ps1`）

---

维护人可在此文档底部追加“已验证环境与日期”，方便回溯。
