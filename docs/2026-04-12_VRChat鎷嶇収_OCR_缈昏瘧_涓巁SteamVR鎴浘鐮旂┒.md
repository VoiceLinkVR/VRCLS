# 2026-04-12 VRChat 拍照、图片翻译与 SteamVR 截图研究

## 结论先说

基于当前项目和你现在已有的 TranslateGemma 服务，主链路应当是：

1. 监听 VRChat 拍照目录
2. 发现新图片
3. 等待文件稳定
4. 直接调用 TranslateGemma 的图片翻译能力
5. 把翻译结果显示到 VRCLS 主窗口和 SteamVR

当前实现不再推荐把“先 OCR，再文本翻译”作为默认主路径。原因很直接：

- 你已经有现成的图片翻译模型和测试脚本
- 直接图片翻译链路更短
- 少一次 OCR 中间层，就少一次格式损失和语言检测误差
- 服务端配置也可以直接复用现有 Gemma 体系

## 1. VRChat 拍照图片存储位置

本机已验证 VRChat 图片默认位于：

```text
C:\Users\<用户名>\Pictures\VRChat\YYYY-MM\
```

本机实际样本：

```text
C:\Users\boyqiu001\Pictures\VRChat\2026-03\VRChat_2026-03-08_00-34-08.184_1920x1080.png
```

命名规律通常为：

```text
VRChat_yyyy-MM-dd_HH-mm-ss.fff_WxH.png
```

工程上仍然建议允许用户手动覆盖目录，因为 VRChat 可能配置了自定义输出目录。

## 2. 如何监听用户拍了照片

最稳的方案不是依赖 VRChat 内部事件，而是监听图片目录本身。

推荐方式：

- `FileSystemWatcher`
- 监听 `Created`
- 监听 `Changed`
- 监听 `Renamed`
- 只处理：
  - `VRChat_*.png`
  - `VRChat_*.jpg`
  - `VRChat_*.jpeg`
  - `VRChat_*.webp`

还需要两层保护：

1. debounce，避免同一张图触发多次
2. 文件稳定性判断，避免图片还没写完就上传

当前实现已经采用：

- `FileSystemWatcher + Timer debounce`
- 连续采样文件长度
- 文件可读后再开始处理

## 3. 当前推荐的图片处理路径

### 过去备选

过去有两种思路：

1. OCR 提取文字，再调用文本翻译
2. 直接让多模态模型翻译图片

### 当前实际选择

现在应当明确选第 2 种：

```text
VRChat 拍照 -> 图片稳定 -> TranslateGemma 图片翻译 -> UI/SteamVR
```

不再默认走：

```text
VRChat 拍照 -> OCR -> do_translate(...)
```

## 4. 为什么改成直接图片翻译

### 4.1 你已经有现成能力

你已经明确给出了可工作的测试脚本：

- `D:\git\gitlab\translategemma-llama-server-docker\scripts\test-image-translate.ps1`

这个脚本说明现有服务已经支持：

- 本地图片翻译
- 远程图片翻译
- 多模态输入
- 目标语言输出

### 4.2 实际请求格式已经清楚

你的测试脚本当前走的是：

- `POST /completion`
- `prompt.prompt_string`
- `prompt.multimodal_data = [base64图片]`

因此在 VoiceLinkServer 里继续新增一套 OpenAI OCR 服务配置，本身就是偏离你现有部署方式的。

### 4.3 减少中间损失

先 OCR 再翻译会引入额外问题：

- OCR 先把图像信息压成文本，可能丢上下文
- OCR 语言检测可能出错
- OCR 输出格式可能污染后续翻译
- 对弯曲文本、复杂 UI、对话框混排等场景更脆弱

直接图片翻译虽然不是万能，但对你现在的模型栈更顺。

## 5. 服务端应怎么接

正确方向是复用现有 Gemma 翻译体系，而不是单独维护 `IMAGE_VISION_*`。

建议直接复用：

- `GEMMA_TRANSLATE_URL`
- `GEMMA_TRANSLATE_API_MODE`
- `GEMMA_TRANSLATE_MODEL`
- `GEMMA_TRANSLATE_TIMEOUT`
- `GEMMA_TRANSLATE_MAX_TOKENS`

其中：

- `completion` 模式对齐你当前测试脚本
- `openai` 模式用于兼容 `llama-server --jinja` 的 `/v1/chat/completions`

## 6. 监听 VRChat 拍照好，还是做 SteamVR 截图

主结论不变：

**优先做 VRChat 拍照，不要把 SteamVR/Steam 截图当主入口。**

原因：

- 用户语义更明确，拍照通常就是“我要处理这一画面”
- 路径更直观，默认就在 `Pictures\VRChat`
- 更容易做到自动监听
- 更适合当前外部桌面工具架构

## 7. SteamVR/Steam 截图为什么只适合作为兼容入口

可以兼容，但不适合当主方案。

主要问题：

1. 截图目录更绕，通常在 Steam `userdata` 下面
2. 独立外部工具不好优雅接 Steam 官方截图回调
3. 目录里历史截图更多，清洗更麻烦
4. 用户按 Steam 截图键，不一定等于“我要翻译这张图”

因此 Steam 截图更适合作为后续可选补充，而不是当前默认入口。

## 8. 当前项目中的最终落地建议

### 主链路

```text
VRChat 拍照目录监听
+ 文件稳定检测
+ 直接图片翻译
+ 输出到主窗口和 SteamVR
```

### 可选补充

```text
后续再加：
- Steam 截图目录监听
- 可选 OCR 辅助模式
- 元数据读取
- 失败重试
```

## 9. 本次实现对应关系

当前代码实现已经与上述方向对齐：

- 客户端负责监听目录和上传图片
- 服务端 `photoTranslate` 直接调用 TranslateGemma 图片翻译
- 不再默认走 OCR + `do_translate(...)`

如果后面需要恢复 OCR，也应该作为可选兜底，而不是主链路默认值。
