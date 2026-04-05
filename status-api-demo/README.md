# VRCLS Status API Demo

这是一个给公开状态页提供数据的最小可运行示例。

## 特点

- 无第三方依赖，Node.js 18+ 即可运行
- 定时探测你配置的 HTTP 健康检查地址
- 输出给前端状态页使用的 JSON
- 自动生成简单的事件记录

## 使用方式

1. 复制 `services.example.json` 为 `services.json`
2. 把 `probeUrl` 和 `publicEndpoint` 改成你的真实地址
3. 运行：

```bash
node server.js
```

默认会监听 `8787` 端口，并提供：

- `GET /healthz`
- `GET /api/status/public`
- `GET /api/status/refresh`

## 对接前端

将站点里的 `assets/js/status-config.js` 中 `apiBaseUrl` 改成你的状态 API 根地址，例如：

```js
window.VRCLS_STATUS_CONFIG = {
  apiBaseUrl: "https://status-api.example.com",
  sampleDataPath: "../assets/data/status-sample.json",
  refreshIntervalMs: 60000,
  useSampleOnFailure: true
};
```
