const { app, BrowserWindow, ipcMain } = require('electron');
const path = require('path');
const { PythonShell } = require('python-shell');
const http = require('http');
const fs = require('fs');
const os = require('os');

let pyProc = null;
let mainWindow = null;

// 判断是否为开发环境
const isDev = !app.isPackaged;

// 读取配置文件
let BACKEND_HOST = '127.0.0.1';
let BACKEND_PORT = 8981;
let BACKEND_URL = `http://${BACKEND_HOST}:${BACKEND_PORT}`;

function loadConfig() {
    try {
        const configPath = path.join(os.homedir(), 'Documents', 'VRCLS', 'client.json');
        if (fs.existsSync(configPath)) {
            const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
            BACKEND_HOST = config['api-ip'] || '127.0.0.1';
            BACKEND_PORT = config['api-port'] || 8981;
            BACKEND_URL = `http://${BACKEND_HOST}:${BACKEND_PORT}`;
            console.log(`Loaded config from Documents/VRCLS/client.json: ${BACKEND_HOST}:${BACKEND_PORT}`);
            return true;
        } else {
            console.log('client.json not found in Documents/VRCLS, using default config');
            return false;
        }
    } catch (error) {
        console.error('Error reading config file:', error);
        return false;
    }
}

// 检查后端服务是否已启动
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
            console.log(`Backend returned status: ${res.statusCode}, retrying...`);
            callback(false);
        }
    });

    req.on('error', (err) => {
        console.log(`Backend not ready yet, retries left: ${retries - 1}`);
        if (retries > 0) {
            setTimeout(() => {
                checkBackendService(callback, retries - 1);
            }, 1000);
        } else {
            console.error('Backend service failed to start after multiple retries');
            callback(false);
        }
    });

    req.on('timeout', () => {
        req.destroy();
        console.log('Backend check timeout, retrying...');
        if (retries > 0) {
            setTimeout(() => {
                checkBackendService(callback, retries - 1);
            }, 1000);
        } else {
            callback(false);
        }
    });

    req.end();
}

function createPythonProcess() {
    // 开发环境：使用项目目录下的打包exe
    if (isDev) {
        // 优先使用最新的打包版本
        const possiblePaths = [
            path.join(__dirname, '..', 'dist', 'VRCLS-windows-v0.6.0-fix3', 'VRCLS', 'VRCLS.exe'),
            path.join(__dirname, '..', 'dist', 'VRCLS-windows-v0.6.0-fix2', 'VRCLS', 'VRCLS.exe'),
            path.join(__dirname, '..', 'dist', 'VRCLS-windows-v0.6.0-fix1', 'VRCLS', 'VRCLS.exe'),
            path.join(__dirname, '..', 'dist', 'VRCLS-windows-v0.6.0', 'VRCLS', 'VRCLS.exe'),
            path.join(__dirname, '..', 'dist', 'VRCLS', 'VRCLS.exe'),
            path.join(__dirname, 'dist', 'win-unpacked', 'resources', 'py', 'VRCLS', 'VRCLS.exe')
        ];

        let exePath = null;
        for (const testPath of possiblePaths) {
            if (fs.existsSync(testPath)) {
                exePath = testPath;
                break;
            }
        }

        if (!exePath) {
            console.error('No VRCLS.exe found in development paths');
            return;
        }

        console.log(`Starting Python process in dev mode: ${exePath}`);
        pyProc = require('child_process').execFile(exePath);
        console.log('Python process started in dev mode.');
    } else {
        // 生产环境：运行打包后的 .exe
        const exePath = path.join(process.resourcesPath, 'py', 'VRCLS', 'VRCLS.exe');
        console.log(`Starting Python process from: ${exePath}`);

        // 检查exe文件是否存在
        if (!fs.existsSync(exePath)) {
            console.error(`Python executable not found at: ${exePath}`);
            return;
        }

        pyProc = require('child_process').execFile(exePath);
        console.log(`Python process started from: ${exePath}`);
    }

    // 监听Python进程输出
    if (pyProc.stdout) {
        pyProc.stdout.on('data', (data) => {
            console.log(`[Python STDOUT]: ${data.toString().trim()}`);
        });
    }

    if (pyProc.stderr) {
        pyProc.stderr.on('data', (data) => {
            console.error(`[Python STDERR]: ${data.toString().trim()}`);
        });
    }

    pyProc.on('error', (err) => {
        console.error('[Python Process Error]:', err);
    });

    pyProc.on('exit', (code, signal) => {
        console.log(`Python process exited with code ${code} and signal ${signal}`);
    });
}

function createWindow() {
    mainWindow = new BrowserWindow({
        width: 1200,
        height: 900,
        minWidth: 800,
        minHeight: 600,
        frame: false, // 创建无边框窗口
        webPreferences: {
            preload: path.join(__dirname, 'preload.js'),
            contextIsolation: true,
            nodeIntegration: false,
        },
    });

    if (isDev) {
        // 开发环境：直接访问后端服务
        console.log('Dev mode: Loading backend service...');
        mainWindow.loadURL(BACKEND_URL);
        mainWindow.webContents.openDevTools(); // 自动打开开发者工具
    } else {
        // 生产环境：等待后端服务启动后访问
        console.log('Production mode: Waiting for backend service...');

        // 显示加载页面
        mainWindow.loadURL(`data:text/html,
            <html>
                <head>
                    <style>
                        body {
                            display: flex;
                            justify-content: center;
                            align-items: center;
                            height: 100vh;
                            margin: 0;
                            background: #1a1a1a;
                            color: white;
                            font-family: Arial, sans-serif;
                        }
                        .loader {
                            text-align: center;
                        }
                        .spinner {
                            border: 4px solid #f3f3f3;
                            border-top: 4px solid #3498db;
                            border-radius: 50%;
                            width: 40px;
                            height: 40px;
                            animation: spin 2s linear infinite;
                            margin: 0 auto 20px;
                        }
                        @keyframes spin {
                            0% { transform: rotate(0deg); }
                            100% { transform: rotate(360deg); }
                        }
                    </style>
                </head>
                <body>
                    <div class="loader">
                        <div class="spinner"></div>
                        <div>正在启动VRCLS服务...</div>
                    </div>
                </body>
            </html>
        `);

        // 检查后端服务是否就绪
        checkBackendService((isReady) => {
            if (isReady) {
                console.log('Backend service ready, loading application...');
                mainWindow.loadURL(BACKEND_URL);
            } else {
                console.error('Failed to start backend service');
                mainWindow.loadURL(`data:text/html,
                    <html>
                        <head>
                            <style>
                                body {
                                    display: flex;
                                    justify-content: center;
                                    align-items: center;
                                    height: 100vh;
                                    margin: 0;
                                    background: #1a1a1a;
                                    color: white;
                                    font-family: Arial, sans-serif;
                                }
                                .error {
                                    text-align: center;
                                    padding: 20px;
                                }
                                .error h2 {
                                    color: #e74c3c;
                                }
                            </style>
                        </head>
                        <body>
                            <div class="error">
                                <h2>启动失败</h2>
                                <p>VRCLS服务启动失败，请检查日志或重试</p>
                            </div>
                        </body>
                    </html>
                `);
            }
        });
    }
}

// 监听前端发来的窗口控制事件
ipcMain.on('minimize-window', () => mainWindow.minimize());
ipcMain.on('maximize-window', () => {
    if (mainWindow.isMaximized()) {
        mainWindow.unmaximize();
    } else {
        mainWindow.maximize();
    }
});
ipcMain.on('close-window', () => app.quit());

app.whenReady().then(() => {
    // 加载配置文件
    loadConfig();

    // 创建Python进程
    createPythonProcess();

    // 创建窗口
    createWindow();
});

app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
        app.quit();
    }
});

// 应用退出时，确保 Python 进程也被终止
app.on('quit', () => {
    if (pyProc) {
        console.log('Killing Python process...');
        pyProc.kill('SIGTERM');
    }
});