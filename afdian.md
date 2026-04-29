# VRCLS 客户端中“爱发电 / 用户中心”相关 UI 与逻辑说明

本文档用于让另一个 AI 在**不重新通读整个仓库**的情况下，快速理解当前项目里与“爱发电”相关的客户端实现：

- 当前入口在哪里
- 页面结构与视觉风格是什么样
- ViewModel 里有哪些状态、命令、接口调用
- 首页限流提示与“发电”入口如何联动
- 目前哪些地方适合重设计，哪些地方是现有约束

本文档聚焦 **客户端（WPF）**，后端只描述到客户端已经依赖的接口契约，不展开服务端内部实现。

---

## 1. 相关文件总览

### 1.1 主要文件

- `03.VRCLS.UI/Views/SettingsView.xaml`
  - 用户中心页的主体 UI，爱发电相关表单都在这里。
- `03.VRCLS.UI/Views/SettingsView.xaml.cs`
  - 处理 `PasswordBox` 与 ViewModel 的同步。
- `03.VRCLS.UI/ViewModels/SettingsViewModel.cs`
  - 爱发电下单、订单激活、账户权益查询的客户端逻辑都在这里。
- `03.VRCLS.UI/Views/MainWindow.xaml`
  - 首页顶部的账户提醒条、限流提示条，以及“发电”快捷入口。
- `03.VRCLS.UI/ViewModels/MainViewModel.cs`
  - 首页限流消息聚合、打开“用户中心”标签页的逻辑在这里。
- `03.VRCLS.UI/Resources/Strings.zh.xaml`
  - 爱发电/用户中心的中文文案资源。
- `03.VRCLS.UI/Resources/Strings.en.xaml`
  - 对应英文文案资源。
- `docs/2026-02-23_用户中心_爱发电_鉴权改造方案.md`
  - 现有设计背景和前后端约定的执行稿，适合作为补充上下文。

### 1.2 页面所在窗口

- `03.VRCLS.UI/Views/SettingsWindow.xaml`
  - 设置窗口尺寸：`900x700`
  - 最小尺寸：`700x500`
  - `SettingsView` 被作为整个窗口内容承载

---

## 2. 当前信息架构

当前“爱发电”不是单独弹窗，而是被整合进 **Settings / 用户中心** 的第一个标签页。

### 2.1 Tab 结构

`SettingsView.xaml` 使用左侧导航式 `TabControl`：

- `TabStripPlacement="Left"`
- 样式：`MaterialDesignNavigationRailTabControl`
- 每个 TabItem 基于 `MaterialDesignNavigationRailTabItem`
- 单个 tab 高度 `72`
- `Padding=8`
- `MinWidth=80`

第一个 tab 是 **用户中心**，图标为 `AccountNetwork`。

### 2.2 用户中心页内部顺序

当前从上到下依次是四块卡片：

1. 服务器连接 / 账号登录
2. 爱发电支持（生成链接）
3. 订单激活
4. 账户权益查询

这意味着“爱发电”当前不是孤立流程，而是被设计成“用户中心”的一部分，和登录、激活、权益查询构成一个连续闭环。

---

## 3. 首页与用户中心的联动关系

### 3.1 首页有两个相关提示条

`MainWindow.xaml` 顶部有两类提示 UI：

1. `HomeAlertMessage`
   - 用于账户异常、过期等提醒
   - 支持关闭
2. `RateLimitNoticeMessage`
   - 用于“请求过于频繁 / 被限流”
   - 带一个“发电”按钮
   - 点击后不会直接跳浏览器，而是**打开设置页并切到用户中心 tab**

### 3.2 限流提示条的行为

`MainViewModel.cs` 当前逻辑：

- 接收服务端或业务层抛出的限流消息
- 解析消息来源 `source`
- 解析受限规则 `rules`
- 做去重聚合
- 拼成多行提示文案
- 写入 `RateLimitNoticeMessage`

拼接出的文案结构大致是：

1. 默认提示：请求过于频繁，请稍后重试
2. 触发来源（如果有）
3. 受限规则（如果有）
4. 结尾引导：去用户中心通过爱发电支持运维以解锁上限

### 3.3 “发电”按钮实际行为

不是直接打开爱发电链接，而是：

- `OpenDonationGuideCommand`
- 调用 `OpenDonationGuide()`
- 内部执行 `OpenSettings(UserCenterTabIndex)`
- `UserCenterTabIndex = 0`

也就是说，**首页“发电”按钮 = 打开设置窗口并定位到用户中心第一页**。

这对重设计很重要：

- 当前用户路径被设计为“先进入用户中心，再生成爱发电链接”
- 不是“首页一键打开支付页”

---

## 4. 当前爱发电客户端流程

当前客户端实际上包含三个串联流程：

### 4.1 流程 A：生成爱发电链接

用户在“爱发电支持”卡片中输入：

- 发电用户名 `DonationUsername`
- 发电密码 `DonationPassword`
- 发电类型 `DonationAction`
  - `renew` = 续费
  - `upgrade` = 升级
- 续费月数 `DonationMonths`
  - 仅 `renew` 时显示
  - 范围 1~12
- 套餐月价 `DonationPrice`

点击按钮：`CreateDonationLinkCommand`

当前逻辑：

1. 校验用户名和密码非空
2. 校验价格能否解析为数字
3. 将价格限制到 `5 ~ 999`
4. 将月份限制到 `1 ~ 12`
5. 构造请求 `UserCenterPaymentLinkRequest`
6. 调用后端 `POST user-center/payment-link`
7. 成功后读取：
   - `donation_url`
   - `remark`
8. 写回页面状态：
   - `DonationRemark`
   - `DonationUrl`
   - `DonationStatus`
9. 自动 `Process.Start(...)` 打开系统浏览器

请求体字段如下：

- `username`
- `password`
- `action`
- `months`
- `plan_price`
- `custom_price`

其中：

- `plan_price = 月价`
- `custom_price = 月价 * 月数`

### 4.2 流程 B：订单激活

“订单激活”卡片当前支持两种方式：

- `remark`：订单备注激活
- `out_trade_no`：商家订单号激活

UI 只有一个输入框 `ActivationInput`，其 Hint 会随激活方式切换：

- 选择 `remark` 时显示“订单备注”
- 选择 `out_trade_no` 时显示“商家订单号”

点击按钮：`ActivateOrderCommand`

当前逻辑：

1. 校验输入非空
2. 调用 `POST user-center/activate`
3. 请求体按激活方式填写：
   - `remark`
   - `custom_id`
   - `out_trade_no`
4. 如果成功，显示后端返回的 `message`
5. 如果返回了 `username`，则附加到成功信息里
6. 激活成功后，如果当前设置页里已有 `Username/Password`，会自动调用一次登录，刷新客户端账户状态

注意：

- 当激活方式是 `remark` 时，当前代码会把输入**同时写入** `remark` 和 `custom_id`
- 这意味着客户端默认把“备注”和“custom_id”当作同一份字符串兼容使用

### 4.3 流程 C：查询账户权益

“账户权益查询”卡片使用当前登录 token 去调用：

- `GET account/usage?start_date=yyyy-MM-dd&end_date=yyyy-MM-dd`

使用条件：

- 不能是测试账户
- 必须已有 access token
- 开始/结束日期必须存在
- 开始日期不能晚于结束日期

请求成功后会更新：

- `AccountUsageStatus`
- `AccountUsageSummary`
- `AccountExpirationSummary`
- `AccountUsageDailyStats`

展示内容包括：

- 区间总成功请求数
- 区间总额度
- 区间总剩余额度
- 账户到期时间
- 每日统计表格

如果接口返回 `401`：

- 客户端会调用 `_authService.InvalidateToken(...)`
- 然后提示重新登录

---

## 5. SettingsViewModel 中的关键状态

爱发电相关的状态主要集中在 `SettingsViewModel.cs`。

### 5.1 发电下单状态

- `DonationUsername`
- `DonationPassword`
- `DonationAction`，默认 `renew`
- `DonationMonths`，默认 `1`
- `DonationPrice`，默认 `5.00`
- `DonationStatus`
- `DonationRemark`
- `DonationUrl`
- `IsDonationSubmitting`

### 5.2 激活状态

- `ActivationMethod`，默认 `remark`
- `ActivationInput`
- `ActivationStatus`
- `IsActivationSubmitting`
- `ActivationInputHint`

### 5.3 权益查询状态

- `AccountUsageStatus`
- `AccountUsageSummary`
- `AccountExpirationSummary`
- `AccountUsageStartDate`，默认今天往前 6 天
- `AccountUsageEndDate`，默认今天
- `IsAccountUsageSubmitting`
- `AccountUsageDailyStats`

### 5.4 选项源

- `DonationActions`
  - `renew / 续费`
  - `upgrade / 升级`
- `DonationMonthOptions`
  - `1..12`
- `ActivationMethods`
  - `remark / 订单备注激活`
  - `out_trade_no / 商家订单号激活`

---

## 6. 登录态与用户中心之间的关系

### 6.1 连接状态显示

`SettingsViewModel.ApplyAuthState(...)` 会把鉴权状态映射为 `ConnectionStatus`：

- `正在连接...`
- `已连接`
- `已连接 (测试账户)`
- `连接失败: ...`
- `未连接`

这块显示在用户中心最上方“服务器连接”卡片中。

### 6.2 激活成功后的回登录

订单激活成功后，如果设置页的登录区已有用户名和密码，则会：

- 自动再次调用 `_authService.LoginWithCredentialsAsync(Username, Password)`

目的：

- 刷新 token
- 触发首页账户状态更新
- 让新权益尽快反映到 UI

### 6.3 测试账户限制

当前用量/到期查询明确限制测试账户：

- 测试账户不允许查询 `account/usage`
- 会直接提示“测试账户不支持查询到期和用量”

因此当前产品意图很明确：

- 测试账户只用于基础体验
- 正式账户 + 爱发电支付后激活，才进入完整权益体系

---

## 7. 当前 UI 结构细节

### 7.1 总体风格

当前界面明显基于 **MaterialDesignInXaml**，爱发电区域没有单独自定义一套视觉语言，而是复用全局 Material Design 风格。

核心特征：

- 页面背景：`MaterialDesignPaper`
- 左侧标签导航：`MaterialDesignNavigationRailTabControl`
- 按钮主样式：`MaterialDesignRaisedButton`
- 次要按钮：`MaterialDesignOutlinedButton`
- 输入框：`MaterialDesignFloatingHintTextBox`
- 密码框：`MaterialDesignFloatingHintPasswordBox`
- 强调色常来自：`PrimaryHueMidBrush`

也就是说：

- **现在不是“爱发电品牌化 UI”**
- 而是“在统一设置页中的一组表单卡片”

### 7.2 用户中心页的版式

用户中心页使用：

- `ScrollViewer`
- 内容容器为纵向 `StackPanel`
- 宽度绑定到 ScrollViewer 的 `ViewportWidth`
- 整体内边距 `24`

每个区块基本都是：

- `materialDesign:Card`
- `Padding=20`
- `UniformCornerRadius=8`
- 垂直方向 `Margin` 分隔
- 内部再用 `StackPanel` + `Grid` 做表单排版

### 7.3 爱发电卡片当前布局

“爱发电支持”卡片内部大致是三段：

1. 标题 + 辅助说明
2. 输入区
3. 结果区

#### 输入区第一行

两列布局：

- 左：发电用户名
- 右：发电密码

#### 输入区第二行

三列布局：

- 左：发电类型（续费 / 升级）
- 中：续费月数（仅续费时显示）
- 右：套餐月价

#### 操作区

- 右对齐主按钮：“生成并打开发电链接”

#### 结果区

- 一行状态文案 `DonationStatus`
- 一行只读订单备注框 `DonationRemark`
- 一行只读发电链接框 `DonationUrl`

### 7.4 订单激活卡片布局

- 标题 + 说明
- 一个下拉框：激活方式
- 一个输入框：根据方式切换 Hint
- 一个右对齐按钮：查询并激活
- 一个状态文本：`ActivationStatus`

### 7.5 账户权益查询卡片布局

- 标题 + 说明
- 开始日期 + 结束日期 + 查询按钮 同一行
- 状态文本
- 两个只读结果框：
  - 区间汇总
  - 账户到期时间
- 一个 DataGrid：按日统计

---

## 8. 当前样式特征（适合重设计时参考）

### 8.1 文本层级

用户中心页的文本层级比较统一：

- 页面标题：`MaterialDesignHeadline5TextBlock`
- 卡片标题：`FontWeight="Bold"` + `Opacity="0.8"`
- 说明文案：`FontSize="12"` + `Opacity="0.7"`
- 状态文案：`FontSize="12"` + `Opacity="0.8"`

整体视觉偏“工具型设置页”，信息密度高，但品牌感不强。

### 8.2 首页提示条的样式

首页有两种与爱发电有关的提示条：

#### 账户提醒条

- 背景：`#CC8A4F00`
- 边框：`#99FFB74D`
- 圆角：`6`
- 文本白色
- 右侧关闭按钮

#### 限流提醒条

- 背景：`#CC1E3A8A`
- 边框：`#995B9BFF`
- 圆角：`6`
- 文本白色
- 一个主按钮“发电”
- 一个关闭按钮

这说明当前首页“爱发电”视觉上其实是通过**蓝色限流提示条**来承接的，而不是一个常驻品牌区域。

### 8.3 当前并没有的东西

目前没有：

- 爱发电品牌插图
- 专用色板或渐变
- 订单步骤条
- 购买前后状态卡片切换
- 套餐对比卡
- 权益概览 Dashboard
- 支付完成后的显式成功页

所以如果要重设计，空间很大。

---

## 9. 文案与资源键

爱发电相关文案主要在 `Strings.zh.xaml` / `Strings.en.xaml` 中，以 DynamicResource 方式注入。

常见资源键包括：

- `RateLimitDonateButton`
- `RateLimitNoticeSuffix`
- `RateLimitNoticeDefault`
- `RateLimitSourcePrefix`
- `RateLimitRulePrefix`
- `用户中心`
- `爱发电支持`
- `用户中心发电说明`
- `发电用户名`
- `发电密码`
- `发电类型`
- `续费月数`
- `套餐月价`
- `生成并打开发电链接`
- `订单激活`
- `订单激活说明`
- `激活方式`
- `查询并激活`
- `账户权益查询`
- `账户权益说明`
- `查询账户用量与到期`

如果做 UI 重构，建议：

- 优先保持资源键机制不变
- 即使重做布局，也尽量继续从资源文件取文案
- 避免把中文写死在 XAML 或 ViewModel

---

## 10. PasswordBox 的特殊处理

WPF 的 `PasswordBox` 不能直接像普通 TextBox 一样绑定，因此 `SettingsView.xaml.cs` 里做了代码隐藏同步。

当前与爱发电相关的细节：

- 登录密码框变更时，写入 `vm.Password`
- 发电密码框变更时，写入 `vm.DonationPassword`
- 页面加载时：
  - `PasswordBox.Password = vm.Password`
  - `DonationPasswordBox.Password = vm.DonationPassword`
- `PasswordUpdated` 事件触发时，会同时更新：
  - `PasswordBox`
  - `DonationPasswordBox`

这里隐含一个当前产品假设：

- “登录密码”和“发电密码”通常是一致的

如果重设计成更清晰的购买流程，要注意这个行为是否还保留。

---

## 11. 当前客户端依赖的后端接口契约

从客户端视角，当前依赖 3 组接口：

### 11.1 生成支付链接

- `POST /api/user-center/payment-link`

请求字段：

- `username`
- `password`
- `action`
- `months`
- `plan_price`
- `custom_price`

响应字段：

- `donation_url`
- `remark`

### 11.2 激活订单

- `POST /api/user-center/activate`

请求字段：

- `remark`
- `custom_id`
- `out_trade_no`

响应字段：

- `message`
- `username`

### 11.3 查询账户权益

- `GET /api/account/usage`

查询参数：

- `start_date`
- `end_date`

响应字段（客户端当前会消费的）：

- `is_test_user`
- `expiration_utc`
- `daily_limit`
- `remaining_daily`
- `daily_success_count`
- `daily_stats[]`

如果另一个 AI 要重做前端，但不改服务端，这些契约最好先保持兼容。

---

## 12. 当前实现的设计意图

基于代码现状，可以推测当前设计想表达的是：

1. 用户先登录或验证账户
2. 如果遇到限流，首页给出“发电”引导
3. 用户进入用户中心
4. 在同一个地方完成：
   - 续费 / 升级
   - 获取支付链接
   - 查订单激活
   - 查权益与到期时间

因此当前信息架构更像：

- **用户中心 / 账户运营台**

而不是：

- **营销型购买页**

这会直接影响重设计方向：

- 如果想做更强转化，需要把“套餐选择、权益说明、购买路径”做得更前置
- 如果想保留工具属性，则可以继续围绕“账户管理台”去优化

---

## 13. 给另一个 AI 的重设计建议边界

如果要在当前项目基础上重设计，建议优先遵守以下边界：

### 13.1 尽量不先动的东西

- `SettingsViewModel` 的命令名称
- 接口请求字段名称
- `Strings.zh.xaml` / `Strings.en.xaml` 资源机制
- 首页“发电”按钮进入 `UserCenterTabIndex = 0` 的入口逻辑

### 13.2 可以优先重做的东西

- `SettingsView.xaml` 中用户中心第一页的布局层次
- 爱发电卡片的视觉样式
- 套餐选择方式
- 激活流程的引导文案
- 权益查询结果的展示方式
- 首页限流提示条与用户中心之间的视觉衔接

### 13.3 值得特别关注的痛点

- 爱发电下单区目前像“参数表单”，不像“购买页”
- `套餐月价` 对普通用户不够直观，缺少套餐档位语义
- `remark / custom_id / out_trade_no` 对非技术用户理解成本高
- 生成链接后只是显示 URL，没有明显的“下一步”反馈
- 成功激活后的结果展示较弱，没有形成强确认感
- 账户权益查询与支付流程放在一起，功能是完整的，但视觉优先级比较平均

---

## 14. 一句话总结

当前“爱发电”相关 UI 本质上是：

> 一个放在设置页“用户中心”里的 Material Design 风格账户运营面板，支持续费/升级下单、订单激活和权益查询；首页的“发电”按钮只是把用户带到这个面板，而不是直接打开支付页。

如果另一个 AI 要基于现状做 redesign，最需要理解的不是“某个按钮长什么样”，而是这套现有实现其实承载了**登录、支付、激活、权益查询**四段连续流程。
