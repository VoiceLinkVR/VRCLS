# VRCLS Website Demo

这个仓库是一个面向 `GitHub Pages` 的 VRCLS 官网介绍页 Demo，同时包含一个可部署在自有服务器上的公开状态 API Demo。
现在开始按 `docs/04-官网账号中心实施规划.md` 演进，并新增 `portal/` 作为账号中心前端子应用。

## 包含内容

- 官网首页：`index.html`
- 公开状态页：`status/index.html`
- 状态 API Demo：`status-api-demo/`
- 账号中心前端：`portal/`
- 设计文档：`docs/`

## 本地预览

```bash
python -m http.server 8080
```

访问：

- `http://localhost:8080/`
- `http://localhost:8080/status/`

账号中心前端单独启动：

```bash
cd portal
npm install
npm run dev
```

默认开发地址由 Vite 提供，构建目标基准路径为 `/app/`。

## 状态页接入真实 API

编辑 `assets/js/status-config.js`：

```js
window.VRCLS_STATUS_CONFIG = {
  apiBaseUrl: "https://status-api.example.com",
  sampleDataPath: "../assets/data/status-sample.json",
  refreshIntervalMs: 60000,
  useSampleOnFailure: true
};
```

## 注意

- 当前没有执行任何 `git push`
- `status-api-demo` 只是第一版最小演示实现
- `portal` 当前为账号中心 MVP 骨架，已包含登录、账号总览、安全设置和订单/发电页面结构
- 真实生产环境建议增加日志、存储和鉴权
