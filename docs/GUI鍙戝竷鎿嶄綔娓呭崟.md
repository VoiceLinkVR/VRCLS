# GUI 发布操作清单

本文说明当前 [`scripts/generate_bucket_layout_gui.py`](D:/git/gitlab/VRCLS-CSharp/scripts/generate_bucket_layout_gui.py) 的完整使用方式。现在这个 GUI 不只是上传工具，而是一套从“生成密钥”到“构建安装包/更新元数据”再到“上传校验”的完整发布流程。

启动 GUI：

```powershell
python .\scripts\generate_bucket_layout_gui.py
```

## 1. GUI 现在能做什么

应用发布链路：

1. 生成或加载 `NetSparkle` 公私钥
2. 保存密钥到本机固定目录，后续自动复用
3. 发布 WPF 应用
4. 生成 Inno Setup 安装包
5. 生成 `appcast.xml`
6. 生成 `upload-manifest.json`
7. 上传到 R2
8. 校验线上 URL

模型链路仍然独立：

1. 生成模型静态目录
2. 上传到 R2
3. 校验线上 URL

## 2. 应用发布产物

构建应用发布后会得到：

- 当前发布目录：`Output/app-release`
- 安装包副本：`Output/VRCLS-<DisplayVersion>-win-x64-setup.exe`

`Output/app-release` 内通常包含：

- `VRCLS-<DisplayVersion>-win-x64-setup.exe`
- `appcast.xml`
- `appcast.xml.signature`（如果工具输出）
- `upload-manifest.json`

上传到远端时会映射到：

- `app-releases/...`

## 3. 第一次使用时怎么填

### 3.1 `Publish` 区

- `Base URL`
  - 填线上公开访问根地址
  - 例如：`https://cloudflarestorage.boyqiu001.top/`

### 3.2 `App Release` 区

- `Internal Version`
  - 例如：`0.7.0.118`
- `Display Version`
  - 例如：`0.7.0-beta18`
- `Channel`
  - 正式版一般填 `stable`
- `Configuration`
  - 一般填 `Release`
- `Runtime`
  - 一般填 `win-x64`
- `Build Output`
  - 一般保持 `publish`

### 3.3 `Inno Compiler`

填写 `ISCC.exe` 的完整路径。

常见路径：

```text
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
```

或者：

```text
C:\Program Files\Inno Setup 6\ISCC.exe
```

如果已经装好了 Inno Setup 6，通常点 `Browse` 选一次即可。

### 3.4 `Key Store Dir`

这是 GUI 用来保存 `NetSparkle` 密钥的本地目录。

默认位置：

```text
%APPDATA%\VRCLS\release-publisher\keys
```

例如：

```text
C:\Users\<你的用户名>\AppData\Roaming\VRCLS\release-publisher\keys
```

这个目录里会保存：

- `netsparkle-keys.json`
- `NetSparkle_Ed25519.pub`
- `NetSparkle_Ed25519.priv`

建议：

- 保持默认即可
- 不要放到仓库目录里
- 不要提交到 git

## 4. 密钥怎么生成、保存、二次加载

这部分现在已经内置到 GUI。

### 4.1 一键生成

在 `App Release` 区点击：

- `Generate Keys`

GUI 会做这些事：

1. 调用 `netsparkle-generate-appcast --generate-keys`
2. 生成一套新的公钥/私钥
3. 自动写入 `Key Store Dir`
4. 自动回填到：
   - `NetSparkle Public Key`
   - `NetSparkle Private Key`

### 4.2 手动保存

如果你已经拿到了现有发布体系的公钥和私钥，可以直接粘贴到 GUI，然后点击：

- `Save Keys`

GUI 会把它们保存到 `Key Store Dir`。

### 4.3 手动加载

如果你换了电脑、换了 preset、或者刚打开 GUI 但没自动带出来，可以点击：

- `Load Keys`

GUI 会从 `Key Store Dir` 读取并回填。

### 4.4 自动加载

GUI 启动时会尝试从 `Key Store Dir` 自动读取密钥。

也就是说：

1. 你第一次生成或保存完密钥
2. 以后再次打开 GUI
3. 只要 `Key Store Dir` 没变，密钥就会自动回填

### 4.5 打开密钥目录

点击：

- `Open Key Folder`

可以直接打开密钥保存目录。

## 5. 公钥和私钥到底该怎么处理

### 5.1 新项目

如果还没有历史用户，也没有既有更新体系：

1. 直接点 `Generate Keys`
2. 保存到默认 `Key Store Dir`
3. 后续一直使用这一套

### 5.2 已经在发版的项目

如果线上已经有用户，不能随便换公钥。

正确做法：

1. 使用当前线上更新体系对应的那一套公钥/私钥
2. 粘贴到 GUI
3. 点击 `Save Keys`

不要做的事：

- 不要为了图省事重新生成一套再上线

否则旧客户端会不信任新更新包。

## 6. 构建应用发布

准备完成后：

1. 确认 `Base URL`
2. 确认版本号
3. 确认 `Inno Compiler`
4. 确认密钥已经加载
5. 点击 `Build App Release + Installer`

构建完成后：

- `Publish Source` 会自动指向 `Output/app-release`

## 7. 上传应用发布

点击：

- `Upload to R2`

上传前预览里应看到类似：

- `app-releases/appcast.xml`
- `app-releases/upload-manifest.json`
- `app-releases/VRCLS-<DisplayVersion>-win-x64-setup.exe`

注意：

- 不要上传顶层 `Output`
- GUI 会阻止直接上传顶层 `Output`

## 8. 校验线上发布

上传完成后点击：

- `Verify Public URLs`

应用发布至少应能检查到：

- 安装包 URL
- `appcast.xml`
- `upload-manifest.json`

## 9. 模型静态资源发布

模型链路不走 `NetSparkle`。

操作步骤：

1. 在 `Model Feed` 区填模型路径和版本
2. 点击 `Build Model Feed`
3. 检查 `Publish Source`
4. 点击 `Upload to R2`
5. 点击 `Verify Public URLs`

## 10. 推荐使用方式

推荐第一次这样操作：

1. 填好 `Base URL`
2. 填好 `Inno Compiler`
3. 保持默认 `Key Store Dir`
4. 如果是新项目，点击 `Generate Keys`
5. 如果是旧项目，粘贴现有公私钥后点击 `Save Keys`
6. 点击 `Save Preset`

之后每次发版：

1. 打开 GUI
2. 如有需要点击 `Load Preset`
3. 检查密钥是否已自动带出
4. 只改版本号
5. 点击 `Build App Release + Installer`

## 11. 安全建议

- 不要把 `Key Store Dir` 放在仓库里
- 不要把私钥提交到 git
- 不要把私钥放进共享文档
- `Save Preset` 现在主要保存发布参数，不建议用它长期保存私钥
- 私钥的长期保存位置应以 `Key Store Dir` 为准

## 12. 常见问题

- 构建时报缺少公钥
  - 先点 `Load Keys`
  - 或先点 `Generate Keys`
- 构建时报缺少私钥
  - 同上
- 构建时报缺少 `ISCC.exe`
  - 检查 `Inno Compiler`
- `appcast.xml` 下载地址不对
  - 检查 `Base URL`
- 上传预览不是 `app-releases/...`
  - 检查 `Publish Source` 是否是 `Output/app-release`
- 应用内检测不到更新
  - 检查线上 `app-releases/appcast.xml`
  - 检查客户端 `appUpdateFeedUrl`
