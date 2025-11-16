const { contextBridge, ipcRenderer } = require('electron');

// 安全的API暴露给前端
contextBridge.exposeInMainWorld('electronAPI', {
    // 窗口控制
    minimizeWindow: () => ipcRenderer.send('minimize-window'),
    maximizeWindow: () => ipcRenderer.send('maximize-window'),
    closeWindow: () => ipcRenderer.send('close-window'),

    // 监听窗口状态
    onWindowStateChange: (callback) => {
        ipcRenderer.on('window-state-changed', callback);
    }
});