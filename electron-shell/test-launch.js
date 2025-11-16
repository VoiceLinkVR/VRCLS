// 测试脚本：验证Electron应用是否能正常启动
const { spawn } = require('child_process');
const path = require('path');

const exePath = path.join(__dirname, 'dist', 'win-unpacked', 'VRCLS.exe');
console.log('启动应用:', exePath);

const app = spawn(exePath, [], {
    stdio: 'inherit'
});

app.on('error', (err) => {
    console.error('启动失败:', err);
});

app.on('exit', (code, signal) => {
    console.log(`应用退出: code=${code}, signal=${signal}`);
});