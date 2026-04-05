# VRCLS Portal

这是 `vrclswebsite` 中承接账号中心的前端子应用，按实施规划承接：

- 登录与登录态管理
- 账号总览 / 安全设置
- 邮箱、订单、发电相关页面骨架
- 后续与 `/api` 反向代理对接

## 本地开发

```bash
npm install
npm run dev
```

默认通过 Vite 启动，应用基准路径为 `/app/`。

## 构建

```bash
npm run build
```

## 环境变量

可选环境变量：

- `VITE_API_BASE_URL`：默认 `/api`

如需对接独立后端，可在本目录添加 `.env.local`。
