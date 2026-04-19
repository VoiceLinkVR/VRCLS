# 请求统计不一致问题修复说明

## 1. 问题现象

这次问题的核心不是“服务端算错了一个数字”，而是三端混用了两种不同口径的数据：

- 客户端把“请求次数”拿去和“资源额度”对比。
- 官网概览页把“区间汇总”标成了“今日”。
- 服务端接口同时返回了“区间统计”和“今日快照”，但前端没有把两者严格分开使用。

用户看到的直接表现就是：

- 左边客户端显示的“今日已用 / 今日剩余”与右边官网的“今日请求数 / 今日消耗”对不上。
- 尤其是官网里的“今日消耗”，实际上不应该读取区间累计值。

## 2. 根因

### 2.1 服务端接口里本来就有两类语义

`/v1/api/account/usage` 和 `/v2/api/account/usage` 都同时承担了两件事：

- 返回查询区间内的汇总结果
  - `daily_success_count`
  - `daily_total_count`
  - `resource_used`
  - `daily_stats`
- 返回今天这一日的实时额度快照
  - `today_daily_resource_used`
  - `today_remaining_daily`

问题在于，之前接口没有把“今日请求数”也单独返回出来，于是前端容易直接拿区间字段去凑“今日”展示。

### 2.2 客户端的问题

客户端概览页之前把：

- `daily_success_count`

当成了：

- “今日已耗额度”

但实际上额度应该看的是：

- `today_daily_resource_used`

所以客户端会出现“请求次数”和“额度消耗”混为一谈的问题。

### 2.3 官网的问题

官网概览页之前把区间查询结果直接标成“今日”：

- `daily_success_count` 被当成“今日请求数”
- `daily_total_count` 被当成“今日总请求”
- `resource_used` 被当成“今日消耗”

同时它又读取了：

- `today_remaining_daily`

作为“今日剩余”

于是同一张卡片里混用了“区间累计”和“今日快照”。

之后又发现官网还有第二层问题：

- 概览页的“今日快照”
- 用量页的“区间查询结果”

共用了同一个前端状态对象。用户只要在用量页切过日期范围，概览页就可能继续读到这份区间结果。

## 3. 修复方案

### 3.1 服务端

在以下文件中补充了明确的“今日请求快照”字段：

- `D:\git\gitlab\VoiceLinkServer1\src\routers\api.py`
- `D:\git\gitlab\VoiceLinkServer1\src\routers\portal_api.py`

新增字段：

- `today_success_count`
- `today_total_count`

保留并继续使用原有今日额度字段：

- `today_daily_resource_used`
- `today_remaining_daily`

这样服务端现在明确区分为两套口径：

- 区间汇总：`daily_*`、`resource_used`
- 今日快照：`today_*`

### 3.2 客户端

修改文件：

- `D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\ViewModels\SettingsViewModel.cs`

调整后：

- “今日资源消耗”读取 `today_daily_resource_used`
- “今日请求数”单独读取 `today_success_count`

不再把请求次数和资源额度混在一起显示。

### 3.3 官网

修改文件：

- `D:\git\gitlab\vrclswebsite\portal\src\stores\user.ts`
- `D:\git\gitlab\vrclswebsite\portal\src\views\dashboard\Overview.vue`
- `D:\git\gitlab\vrclswebsite\portal\src\views\dashboard\Usage.vue`

关键调整：

- 新增 `usageSnapshot`，专门保存“今天”的单日快照。
- 保留 `usage`，专门保存“用户选择区间”的查询结果。
- 概览页优先读取 `usageSnapshot`。
- 用量页明确显示“区间成功响应 / 区间已耗额度 / 区间已耗附加资源”。

这样概览页与用量页不再共享同一份统计语义。

## 4. 为什么你会觉得“今日消耗不应看单日，网站这里好像错了”

这个判断是对的。

官网原来的“今日消耗”确实错在这里：

- 它显示的是区间累计消耗。
- 但标题写的是“今日”。

修复后的规则已经统一为：

- 标题里写“今日”的，只能读取单日快照。
- 标题里写“区间”的，才读取查询范围累计值。

也就是说：

- 概览页看“今日”
- 用量页看“区间”

## 5. 测试服务器部署情况

部署日期：

- 2026-04-17（Asia/Shanghai）

测试服务器：

- `root@192.168.2.53`

### 5.1 前端部署

已执行：

- `D:\git\gitlab\vrclswebsite\scripts\sync-deploy.ps1 -Build`

结果：

- 官网静态资源已同步到 `/root/vrcls-preview/frontend/site`
- 站点已更新为本次修复后的前端版本

### 5.2 后端部署

测试机预览后端实际目录不是本地仓库直出，而是：

- `/opt/vrcls-preview-backend/app`

处理过程：

1. 先确认了测试机预览容器与 compose 工程位置。
2. 发现直接用本地完整 `api.py` 覆盖会把预览环境里尚未同步的其他新能力一并带入，触发旧预览基线和新代码不兼容。
3. 因此改为“以测试机原有预览版本为基线，只补 usage 相关最小修复”。
4. 将修复后的 `api.py`、`portal_api.py` 同步到测试机并注入容器。
5. 重启 `vrcls-preview-backend`。

当前状态：

- 后端容器已恢复正常启动
- `http://127.0.0.1:18980/` 返回 `200`

## 6. 验证结果

### 6.1 本地构建 / 编译

已完成：

- `python -m py_compile src/routers/api.py src/routers/portal_api.py`
- `dotnet build D:\git\gitlab\VRCLS-CSharp\03.VRCLS.UI\03.VRCLS.UI.csproj -nologo`
- `npm run build`（`D:\git\gitlab\vrclswebsite\portal`）

结果：

- 全部通过

### 6.2 测试机接口验证

我在测试机上分别验证了：

- `GET /v1/api/account/usage`
- `GET /v2/api/account/usage`

验证日期使用了绝对日期：

- 单日：`2026-04-17`
- 区间：`2026-04-01 ~ 2026-04-17`

#### v2 官网接口结果

对 `boyqiu001` 验证时：

- 单日查询返回
  - `daily_success_count = 9`
  - `daily_total_count = 9`
  - `today_success_count = 9`
  - `today_total_count = 9`
  - `today_daily_resource_used = 18`
- 区间查询返回
  - `daily_success_count = 855`
  - `daily_total_count = 870`
  - `today_success_count = 9`
  - `today_total_count = 9`
  - `today_daily_resource_used = 18`

结论：

- `daily_*` 已经明确表示区间累计
- `today_*` 在区间查询下保持单日快照语义不变
- 官网修复目标已经达成

#### v1 兼容接口结果

`v1` 兼容接口也已经返回：

- `today_success_count`
- `today_total_count`

但测试时发现一个旧兼容链路特性：

- `v1` 的旧日志会把部分查询请求自身也记入 `request_log`

所以连续两次验证请求之间，今天计数会出现自增 1 的现象。这不是本次“今日 / 区间口径混淆”的问题，而是旧兼容日志链路本身的副作用。

不过即便如此，`v1` 接口已经具备了修复所需的新 today 字段，客户端如果按新字段展示，也不会再拿区间汇总去冒充“今日消耗”。

## 7. 当前状态

当前可以明确认为：

- 服务端：已支持“区间统计”和“今日快照”分离
- 官网：已部署到测试机，今日数据与区间数据已拆开
- 客户端：本地代码已修复并可编译，通过后续客户端发包即可生效

测试机上的实际状态是：

- 官网预览环境：已更新
- 后端预览环境：已更新并恢复正常
- `v2` 官网接口语义：已验证正确
- `v1` 兼容接口：today 字段已补齐，但旧日志链路仍会记录查询请求自身

## 8. 后续建议

建议下一步按这个顺序继续推进：

1. 用浏览器在测试官网实际点开概览页和用量页，肉眼确认文案与数值口径一致。
2. 把修复后的客户端测试包连到测试机，再确认客户端概览页改为读取 `today_daily_resource_used` 后显示正常。
3. 如果需要彻底消除 `v1` 验证时的自增噪音，可以后续单独处理“统计接口自身是否写入旧 request_log”的兼容逻辑。
