# VRCLS (Legacy Branch - Python)

> **此分支已停止维护。** 新版本已使用 C# 完全重写，即将发布新分支。

VRCLS 全称 VRChat LinguaSync，是一个用于在 VRChat 中使用语音来控制模型或作为翻译器输出内容的程序。

---

## 停止维护说明

本分支（master）为 Python 实现的旧版本，**已停止维护**，不再接受新功能开发和 bug 修复。

**新版本（C#）** 已接近开发完成，将完整覆盖本分支的所有功能。请关注新分支的发布动态。

选择 C# 重写而非在 Python 上修补的原因：
- 当前 Python 架构存在根本性的设计问题（详见下方[架构问题分析](#架构问题分析)），修补成本接近重写
- C# 原生支持多线程并行，不存在 GIL 限制，无需在"进程还是线程"上做取舍
- C# 在 Windows 平台上与音频设备、COM 组件、SteamVR 的集成更自然
- 可以编译为单个可执行文件，部署更简单，运行时无需 Python 环境

下方的[架构问题分析](#架构问题分析)和 [Python 单线程架构设计](#python-单线程架构设计参考)作为开发过程中的技术复盘，仅供参考。

---

## 架构问题分析

### 问题的起源

项目早期采用**单线程同步处理**模型：

```
录音 r.listen() → 同步 requests.post() 发送到服务器 → 等待识别结果 → 翻译 → 发送OSC
                   ↑                                                              ↑
                   |_____________ 这段时间没有任何代码在录音（3-5秒）________________|
```

`requests.post()` 是同步阻塞调用，在等待服务器返回期间（1-5秒），**录音完全停止**，操作系统的音频缓冲区溢出，后续语音数据丢失。表现为"说了话但程序没反应"。

### 错误的诊断

当时将此问题诊断为 **Python GIL（全局解释器锁）导致的线程阻塞**，因此选择了 `multiprocessing`（多进程）架构来"绕过 GIL"。

但实际上：
- `PyAudio.stream.read()` 是 C 扩展调用，执行时**释放 GIL**
- `Sherpa-ONNX` 底层是 C++ 的 ONNX Runtime，推理时**释放 GIL**
- `requests.post()` 是网络 I/O，等待时**释放 GIL**
- `audioop.rms()` 是 C 扩展调用，执行时**释放 GIL**

**所有耗时操作都会释放 GIL**，多线程完全可以真正并行。真正的问题只是"录音和处理在同一个执行流中串行执行"，只需将处理逻辑放到独立线程并用 `queue.Queue` 传递数据即可解决。

### 误判带来的连锁问题

采用多进程架构后，引入了以下问题：

#### 1. 进程间通信开销大

```python
# 当前: Manager.dict() 通过 IPC 代理访问，每次读写都走进程间通信
params = Manager().dict()            # 慢：~0.1ms/次
audioQueue = multiprocessing.Queue() # 慢：需要 pickle 序列化整个 AudioData

# 实际只需要: 普通字典 + 线程安全队列
params = {}                          # 快：~0.001ms/次（直接内存访问）
audioQueue = queue.Queue()           # 快：零拷贝引用传递
```

#### 2. 进程嵌套导致复杂度失控

当前进程树结构：

```
主进程 (Flask + SocketIO)
  |-- 麦克风监听进程 (Process)
  |     └── 结果处理子进程 (Process, daemon)    ← 进程中又套进程
  |-- 桌面音频监听进程 (Process)
  |     └── 结果处理子进程 (Process, daemon)    ← 进程中又套进程
  |-- SteamVR 进程 (Process)
  |-- 日志进程 (Process)
  └── OSC 服务器线程 (Thread)
```

最多 **7 个独立 Python 进程**，每个进程有独立的 Python 解释器和内存空间，额外内存开销约 300-500MB。

#### 3. 状态空转时无 sleep 导致 CPU 空跑

```python
# serverListener.py / sherpaOnnx.py 中的多处逻辑
while params["running"]:
    ifcontinue = not params["voiceKeyRun"] or params["micStopped"]
    if ifcontinue: continue  # 无 sleep 的死循环空转，100% 占用单核 CPU
```

在多进程架构下，这个空转只浪费 CPU 不影响其他进程。但它掩盖了代码质量问题，如果回到线程模型会导致 GIL 争抢，进一步强化了"需要多进程"的错误认知。

#### 4. 大量重复代码

`sherpa_onnx_run`、`sherpa_onnx_run_local`、`sherpa_onnx_run_mic` 三个函数逻辑高度相似，仅音频源不同。`selfMic_listen`、`gameMic_listen_capture`、`gameMic_listen_VoiceMeeter` 同理。因为多进程架构难以共享代码上下文，导致大量复制粘贴。

#### 5. pyttsx3 的 COM 线程问题被隐藏

```python
pyttsx3.speak("麦克风音频进程启动完毕")  # Windows COM 组件，有严格的线程模型要求
```

`pyttsx3` 在多进程下各进程有独立的 COM 环境，不会冲突。但这也掩盖了一个真实的兼容性问题。

---

## Python 单线程架构设计（参考）

> 以下为对 Python 版本的架构复盘，记录了"如果继续用 Python 应该怎么做"的思路。实际新版本已选择 C# 重写，此章节仅作技术参考。

### 核心思路

**单进程 + 多线程 + 异步处理**。所有耗时操作（音频采集、ONNX 推理、网络请求）都是 C 扩展或 I/O 操作，执行时释放 GIL，多线程即可真正并行。

### 架构图

```
单进程
|
|-- 主线程: Flask + SocketIO Web 服务器
|
|-- 麦克风采集线程: PyAudio 读取 + Sherpa-ONNX 流式识别
|       |
|       +---> queue.Queue (零拷贝传递识别结果)
|                 |
|-- 桌面音频采集线程: WASAPI 读取 + Sherpa-ONNX 流式识别 (可选)
|       |
|       +---> queue.Queue (零拷贝传递识别结果)
|                 |
|-- 结果处理线程: 翻译 + TTS + OSC 发送 (从 Queue 消费)
|
|-- SteamVR 线程: OpenVR 覆盖显示 (可选)
|
|-- 日志线程: 统一日志处理
```

### 关键设计变更

| 项目 | 旧架构 (多进程) | 理想架构 (多线程) |
|------|------------------|----------------|
| 共享状态 | `Manager.dict()` IPC 代理 | 普通 `dict` + 必要处加 `threading.Lock` |
| 数据传递 | `multiprocessing.Queue` (pickle) | `queue.Queue` (引用传递，零拷贝) |
| 音频采集 | 3个重复函数分布在不同文件 | 统一的 `AudioCapture` 类，参数区分音频源 |
| 语音分段 | `r.listen()` 能量阈值 + 固定超时截断 | Sherpa-ONNX 流式 endpoint 检测 (统一方案) |
| 云端识别路径 | `r.listen()` 分段 -> Whisper API | Sherpa-ONNX 框架采集分段 -> Whisper API |
| 暂停状态 | `continue` 空转 | `time.sleep()` 或 `threading.Event.wait()` |
| 内存占用 | ~300-500MB (多个 Python 解释器) | ~150-200MB (共享内存空间) |
| 进程数 | 5-7 个 | 1 个 |

### 性能对比预估

同时运行麦克风 + 桌面音频双路识别时：

```
每路 Sherpa-ONNX 推理: ~15ms / 100ms 音频 = 15% 单核
两路合计 CPU 占用: ~3-4% (8核CPU)
GIL 实际持有时间: <0.05ms / 100ms 周期 = 0.05%

数据传递延迟:
  多进程 Queue (pickle AudioData): ~2-5ms
  多线程 Queue (引用传递):         ~0.001ms
```

---

## 本分支使用方法 (仅供参考)

### 构筑包

如没有 Python 环境可以通过[发布页](https://github.com/VoiceLinkVR/VRCLS/releases)下载打包后的程序。

本地识别模型下载链接：[VRCLS本地识别模型包.zip](https://cloudflarestorage.boyqiu001.top/VRCLS%E6%9C%AC%E5%9C%B0%E8%AF%86%E5%88%AB%E6%A8%A1%E5%9E%8B%E5%8C%85.zip)

### 本地 Python 运行

```bash
pip install -r requirements.txt
python main.py
```

### 配置

启动后通过浏览器访问 Web UI 进行配置。主要配置文件为 `client.json`。

---

## 相关链接

- 开发者主页: [boyqiu玻璃球](https://space.bilibili.com/32751538)
- 使用教程: [B站视频教程](https://www.bilibili.com/video/BV1om9xY5Etu/)
- 爱发电: [支持服务器运维](https://afdian.com/a/boyqiu001)

## QQ群

<img width="256" height="456" alt="qrcode_1760669615571" src="https://github.com/user-attachments/assets/a7e3677d-4f52-4266-ba7b-faa8590b5abb" />
