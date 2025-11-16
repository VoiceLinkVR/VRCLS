# VRCLS Electron 桌面应用开发文档

## 项目概述

VRCLS Electron 应用是一个桌面客户端，用于包装 VRCLS Python 后端服务。应用启动时会自动启动 Python 后端服务，并通过内置浏览器访问后端的 Web 界面。

## 架构说明

```
Electron 应用架构：
┌─────────────────────────────────────────┐
│          Electron 主进程                 │
│  ┌─────────────────────────────────────┐ │
│  │     无边框窗口 (BrowserWindow)      │ │
│  │  ┌─────────────────────────────────┐ │ │
│  │  │    加载后端 Web 界面             │ │ │
│  │  │  http://127.0.0.1:8981          │ │ │
│  │  └─────────────────────────────────┘ │ │
│  └─────────────────────────────────────┘ │
│                    │                     │
│  ┌─────────────────▼───────────────────┐ │
│  │     Python 后端子进程                │ │
│  │  VRCLS.exe (Flask + WebView)        │ │
│  └─────────────────────────────────────┘ │
└─────────────────────────────────────────┘
```

## 开发环境设置

### 前置要求

- Node.js 14+
- Python 3.8+（开发时）
- Windows 10/11

### 安装依赖

```bash
cd electron-shell
npm install
```

### 开发模式启动

```bash
# 同时启动 Vue 开发服务器和 Electron
npm run dev

# 或者分别启动
npm run run-vue-dev  # 启动 Vue 开发服务器
electron .           # 启动 Electron
```

开发模式下，Electron 会直接访问 `http://127.0.0.1:8981`（Python 后端服务地址）。

## 项目结构

```
electron-shell/
├── main.js              # Electron 主进程入口
├── preload.js           # 预加载脚本（安全通信）
├── package.json         # 项目配置和依赖
├── test-launch.js       # 测试启动脚本
└── dist/                # 打包输出目录
    ├── win-unpacked/    # 解压后的应用
    └── VRCLS Setup.exe  # 安装程序
```

## 核心功能

### 1. 后端服务管理

- **自动启动**：应用启动时自动启动 Python 后端
- **健康检查**：定期检查后端服务状态
- **优雅关闭**：应用退出时正确终止后端进程

### 2. 界面加载

- **加载动画**：显示启动进度
- **错误处理**：服务启动失败时显示错误页面
- **超时控制**：30 秒启动超时保护

### 3. 窗口控制

- **无边框设计**：现代化界面风格
- **最小化/最大化/关闭**：标准窗口操作
- **尺寸限制**：最小 800x600 像素

## 配置参数

### 后端服务配置

```javascript
const BACKEND_HOST = '127.0.0.1';
const BACKEND_PORT = 8981;
const BACKEND_URL = `http://${BACKEND_HOST}:${BACKEND_PORT}`;
```

### 打包配置

在 `package.json` 中配置：

```json
{
  "build": {
    "appId": "com.voicelinkvr.vrcls",
    "productName": "VRCLS",
    "win": {
      "target": "nsis",
      "icon": "../img/favicon.ico"
    },
    "extraResources": [
      {
        "from": "../dist/VRCLS",
        "to": "py/VRCLS"
      }
    ]
  }
}
```

## 打包流程

### 1. 编译前端

```bash
cd ../webUI
npm run build
```

### 2. 打包 Python 后端

```bash
cd ..
pyinstaller main.spec
```

### 3. 打包 Electron 应用

```bash
cd electron-shell
npm run build
```

### 4. 输出文件

- `dist/win-unpacked/` - 解压版应用
- `dist/VRCLS Setup 0.6.0.exe` - 安装程序

## 核心代码说明

### 主进程 (main.js)

#### 后端服务检查

```javascript
function checkBackendService(callback, retries = 30) {
    const req = http.request({
        hostname: BACKEND_HOST,
        port: BACKEND_PORT,
        path: '/',
        method: 'GET',
        timeout: 2000
    }, (res) => {
        if (res.statusCode === 200 || res.statusCode === 302) {
            console.log('Backend service is ready!');
            callback(true);
        } else {
            callback(false);
        }
    });
    // 错误处理和重试逻辑...
}
```

#### 窗口创建

```javascript
function createWindow() {
    mainWindow = new BrowserWindow({
        width: 1200,
        height: 900,
        minWidth: 800,
        minHeight: 600,
        frame: false,
        webPreferences: {
            preload: path.join(__dirname, 'preload.js'),
            contextIsolation: true,
            nodeIntegration: false,
        },
    });
    // 加载后端URL...
}
```

#### Python 进程管理

```javascript
function createPythonProcess() {
    if (isDev) {
        // 开发环境
        const scriptPath = path.join(__dirname, '..', 'main.py');
        pyProc = new PythonShell(scriptPath, {
            pythonOptions: ['-u'],
            args: ['--show-console']
        });
    } else {
        // 生产环境
        const exePath = path.join(process.resourcesPath, 'py', 'VRCLS', 'VRCLS.exe');
        pyProc = require('child_process').execFile(exePath);
    }
}
```

## 常见问题

### 1. "Cannot find module 'python-shell'"

**原因**：`python-shell` 模块未正确打包

**解决**：确保在 `package.json` 的 `dependencies` 中：
```json
"dependencies": {
  "python-shell": "^5.0.0"
}
```

### 2. 空白页面问题

**原因**：后端服务未启动或启动失败

**解决**：
1. 检查 Python 打包是否正确
2. 查看控制台日志
3. 确保后端服务端口未被占用

### 3. 打包失败

**原因**：文件被占用或权限问题

**解决**：
```bash
# 关闭所有相关进程
taskkill /f /im electron.exe
taskkill /f /im VRCLS.exe

# 清理并重新打包
rmdir /s /q dist
npm run build
```

### 4. 后端服务启动超时

**原因**：Python 后端启动缓慢

**解决**：
1. 检查 Python 打包是否完整
2. 增加重试次数或延长时间
3. 查看后端日志定位问题

## 调试技巧

### 1. 开发模式调试

```bash
electron . --debug
```

### 2. 查看控制台日志

在开发模式下自动打开 DevTools，生产模式可通过快捷键：
- `Ctrl+Shift+I` - 打开开发者工具
- `F12` - 打开开发者工具

### 3. 进程监控

使用任务管理器查看：
- VRCLS.exe (Electron 主进程)
- VRCLS.exe (Python 后端进程)

### 4. 日志分析

Python 后端日志通常位于：
- 运行目录下的 `VRCLS.log`
- 控制台输出（开发模式）

## 性能优化

### 1. 启动优化

- 使用 `extraResources` 避免重复打包
- 合理设置重试间隔和次数
- 实现渐进式加载

### 2. 内存优化

- 及时清理子进程
- 避免内存泄漏
- 使用轻量级加载页面

### 3. 打包优化

- 配置 `.electron-builder` 忽略文件
- 使用压缩减少体积
- 分离开发/生产依赖

## 安全考虑

### 1. 进程隔离

- 使用 `contextIsolation: true`
- 禁用 `nodeIntegration`
- 通过预加载脚本通信

### 2. 资源访问

- 限制文件系统访问
- 验证URL输入
- 使用安全的通信协议

## 更新维护

### 1. 版本管理

- 遵循语义化版本规范
- 更新 `package.json` 中的版本号
- 记录变更日志

### 2. 依赖更新

```bash
# 检查更新
npm outdated

# 更新依赖
npm update

# 安全更新
npm audit fix
```

### 3. 兼容性测试

- 测试不同 Windows 版本
- 验证 Python 后端兼容性
- 检查依赖项更新影响

## 相关资源

- [Electron 官方文档](https://electronjs.org/docs)
- [Electron Builder 文档](https://electron.build/)
- [Python Shell 文档](https://github.com/extrabacon/python-shell)
- [VRCLS 主项目文档](../README.md)

## 技术支持

如遇到问题，请：
1. 查看本文档的常见问题部分
2. 检查控制台日志和错误信息
3. 验证所有依赖是否正确安装
4. 确保 Python 后端打包完整
5. 参考相关资源链接

---

**注意**：这是一个 Windows 专用应用，需要 Windows 10/11 环境运行。