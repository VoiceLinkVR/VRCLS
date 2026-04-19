<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import ContentManagementTab from './components/ContentManagementTab.vue'

type NoticeKind = 'info' | 'success' | 'error'
type TabKey = 'dashboard' | 'users' | 'orders' | 'stats' | 'configs' | 'content'

const ADMIN_API_PREFIX = '/v2/adminapi'

type AdminUser = {
  id: string
  username: string
  email: string
  email_verified: boolean
  is_admin: boolean
}

type AuthStateResponse = {
  has_admin: boolean
  user: AdminUser | null
}

type UserSummary = {
  order_count: number
  activated_order_count: number
  pending_order_count: number
  unpaid_order_count: number
  closed_order_count: number
  activated_amount_total: number
  latest_order_at: string | null
}

type UserRecord = AdminUser & {
  is_active: boolean
  limit_rule: string | null
  current_quota_display: string | null
  plan_ref: string | null
  pricing_mode: string | null
  plan_price: number | null
  daily_quota: number | null
  rate_limit_rule: string | null
  expiration_date: string | null
  boost_expiration_date: string | null
  boost_limit_rule: string | null
  addon_resource_balance: number
  addon_resource_total: number
  addon_expiration_date: string | null
  created_at: string | null
  last_login_at: string | null
  afdian_summary: UserSummary
}

type UserListPayload = {
  items: UserRecord[]
  summary: Record<string, number>
}

type OrderRecord = {
  id: number
  order_no: string | null
  out_trade_no: string | null
  custom_id: string | null
  remark: string | null
  username: string | null
  action: string | null
  amount: number | null
  status: string
  status_label: string
  activation_source_label: string | null
  activation_error: string | null
  created_at: string | null
  updated_at: string | null
}

type UserDetailPayload = {
  user: UserRecord
  afdian_summary: Record<string, unknown>
  orders: OrderRecord[]
}

type OrdersPayload = {
  summary: Record<string, number>
  items: OrderRecord[]
}

type DashboardPayload = {
  users: Record<string, number>
  orders: Record<string, number>
  latest_users: UserRecord[]
  latest_orders: OrderRecord[]
}

type StatsPayload = {
  selected_hour: string | null
  hours: string[]
  summary: Record<string, number>
  daily_stats: Array<Record<string, string | number>>
  top_endpoints: Array<Record<string, string | number>>
  hourly_stats: Array<Record<string, string | number>>
  duration_stats: Array<Record<string, string | number>>
  meta: Record<string, string | number>
}

type SiteConfigGroup = {
  key: string
  label: string
  description: string
  count: number
}

type SiteConfigItem = {
  key: string
  group: string
  group_label: string
  label: string
  description: string
  is_secret: boolean
  requires_restart: boolean
  source: string
  value: string
  raw_value: unknown
  base_value: string
  base_raw_value: unknown
  updated_at: string | null
  updated_by_username: string | null
  updated_note: string | null
}

type SiteConfigListPayload = {
  groups: SiteConfigGroup[]
  items: SiteConfigItem[]
}

type SiteConfigUpdatePayload = SiteConfigListPayload & {
  applied: boolean
  message: string
}

type SiteConfigAuditItem = {
  id: string
  config_key: string
  config_group: string
  operation: string
  is_secret: boolean
  requires_restart: boolean
  old_value: string | null
  new_value: string | null
  old_fingerprint: string | null
  new_fingerprint: string | null
  changed_by_user_id: string | null
  changed_by_username: string | null
  changed_from_ip: string | null
  change_note: string | null
  created_at: string | null
}

type SiteConfigAuditPayload = {
  items: SiteConfigAuditItem[]
}

class ApiError extends Error {
  status: number
  payload: unknown

  constructor(message: string, status: number, payload: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.payload = payload
  }
}

async function requestJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers ?? {})
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'include',
  })

  const rawText = await response.text()
  let payload: unknown = null

  if (rawText) {
    try {
      payload = JSON.parse(rawText)
    } catch {
      payload = rawText
    }
  }

  if (!response.ok) {
    let message = `Request failed: ${response.status}`
    if (typeof payload === 'string' && payload.trim()) {
      message = payload
    } else if (payload && typeof payload === 'object') {
      const detail = (payload as { detail?: unknown }).detail
      if (typeof detail === 'string' && detail.trim()) {
        message = detail
      } else if (detail && typeof detail === 'object' && 'message' in detail) {
        const nextMessage = (detail as { message?: unknown }).message
        if (typeof nextMessage === 'string' && nextMessage.trim()) {
          message = nextMessage
        }
      }
    }
    throw new ApiError(message, response.status, payload)
  }

  return payload as T
}

function emptyUserForm() {
  return {
    username: '',
    password: '',
    email: '',
    is_admin: false,
    is_active: true,
    expiration_date: '',
  }
}

function emptyEntitlementForm() {
  return {
    plan_price: '',
    daily_quota: '',
    pricing_mode: 'fixed_tiers',
    plan_ref: '',
    subscription_status: 'active',
    subscription_expires_at: '',
    rate_limit_rule: '',
    addon_balance: '',
    addon_total_purchased: '',
    addon_expires_at: '',
    clear_addon: false,
    reset_to_free: false,
  }
}

function formatDate(value: string | null | undefined) {
  if (!value) {
    return '-'
  }
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')} ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
}

function toDateInput(value: string | null | undefined) {
  if (!value) {
    return ''
  }
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return ''
  }
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  const hour = String(date.getHours()).padStart(2, '0')
  const minute = String(date.getMinutes()).padStart(2, '0')
  return `${year}-${month}-${day}T${hour}:${minute}`
}

function asJson(value: unknown) {
  return JSON.stringify(value, null, 2)
}

function hasChineseText(value: string | null | undefined) {
  return /[\u4e00-\u9fff]/.test(value ?? '')
}

function configDisplayLabel(item: SiteConfigItem) {
  const label = item.label?.trim() ?? ''
  if (label && hasChineseText(label)) {
    return label
  }

  const description = item.description?.trim() ?? ''
  if (description && hasChineseText(description)) {
    return description
  }

  return label || description || '未命名配置项'
}

function configDescriptionText(item: SiteConfigItem | null) {
  if (!item) {
    return ''
  }

  const description = item.description?.trim() ?? ''
  if (description && hasChineseText(description)) {
    return description
  }

  return `${item.group_label}中的 ${item.key} 配置项。`
}

function orderStatusLabel(order: Pick<OrderRecord, 'status' | 'status_label'>) {
  const normalized = (order.status || '').trim().toLowerCase()
  const mapped = {
    activated: '已激活',
    pending: '待处理',
    unpaid: '已关闭',
    cancelled: '已取消',
    canceled: '已取消',
    closed: '已关闭',
    paid: '已支付',
    unknown: '未知',
  }[normalized]

  if (mapped) {
    return mapped
  }

  if (hasChineseText(order.status_label)) {
    return order.status_label
  }

  return order.status || order.status_label || '-'
}

function planModeLabel(mode: string) {
  return mode === 'ladder' ? '阶梯模式' : '固定档模式'
}

function userQuotaLabel(user: UserRecord) {
  if (user.current_quota_display) {
    return user.current_quota_display
  }
  if (user.daily_quota) {
    return `${user.daily_quota}/day`
  }
  return '-'
}

function syncConfigDraft(item: SiteConfigItem | null) {
  if (!item) {
    configDraftValue.value = ''
    return
  }
  if (typeof item.raw_value === 'boolean') {
    configDraftValue.value = item.raw_value ? 'true' : 'false'
    return
  }
  if (item.raw_value === null || item.raw_value === undefined) {
    configDraftValue.value = ''
    return
  }
  if (typeof item.raw_value === 'object') {
    configDraftValue.value = JSON.stringify(item.raw_value, null, 2)
    return
  }
  configDraftValue.value = String(item.raw_value)
}

function openConfigEditor(item: SiteConfigItem) {
  selectedConfigKey.value = item.key
  syncConfigDraft(item)
}

function parseConfigDraftValue(item: SiteConfigItem) {
  const raw = configDraftValue.value
  if (typeof item.raw_value === 'boolean') {
    return raw === 'true'
  }
  if (typeof item.raw_value === 'number') {
    const parsed = Number(raw)
    if (Number.isNaN(parsed)) {
      throw new Error('配置值必须是合法数字')
    }
    return parsed
  }
  if (Array.isArray(item.raw_value) || (item.raw_value && typeof item.raw_value === 'object')) {
    try {
      return JSON.parse(raw)
    } catch {
      throw new Error('配置值必须是合法 JSON')
    }
  }
  return raw
}

const booting = ref(true)
const busy = ref(false)
const activeTab = ref<TabKey>('dashboard')
const authState = ref<AuthStateResponse>({ has_admin: true, user: null })
const dashboard = ref<DashboardPayload | null>(null)
const usersPayload = ref<UserListPayload | null>(null)
const userDetail = ref<UserDetailPayload | null>(null)
const ordersPayload = ref<OrdersPayload | null>(null)
const statsPayload = ref<StatsPayload | null>(null)
const siteConfigsPayload = ref<SiteConfigListPayload | null>(null)
const configAuditPayload = ref<SiteConfigAuditPayload | null>(null)
const selectedStatsHour = ref('')
const lookupKeyword = ref('')
const lookupResult = ref<unknown>(null)
const tokenParseResult = ref<unknown>(null)
const selectedUserId = ref<string>('')
const selectedConfigKey = ref('')
const configGroupFilter = ref('all')
const configSourceFilter = ref('all')
const configSearchKeyword = ref('')
const configDraftValue = ref('')
const configChangeNote = ref('')
const configAuditLimit = ref('50')

const notice = reactive({
  kind: 'info' as NoticeKind,
  message: '',
})

const bootstrapForm = reactive({
  username: 'admin',
  password: '',
  email: '',
})

const loginForm = reactive({
  login: '',
  password: '',
})

const createForm = reactive(emptyUserForm())
const editForm = reactive(emptyUserForm())
const entitlementForm = reactive(emptyEntitlementForm())

const hasSession = computed(() => Boolean(authState.value.user))
const selectedUserOrders = computed(() => userDetail.value?.orders ?? [])
const selectedConfig = computed(() => {
  return siteConfigsPayload.value?.items.find((item) => item.key === selectedConfigKey.value) ?? null
})
const filteredConfigItems = computed(() => {
  const items = siteConfigsPayload.value?.items ?? []
  const keyword = configSearchKeyword.value.trim().toLowerCase()
  return items.filter((item) => {
    if (configGroupFilter.value !== 'all' && item.group !== configGroupFilter.value) {
      return false
    }
    if (configSourceFilter.value !== 'all' && item.source !== configSourceFilter.value) {
      return false
    }
    if (!keyword) {
      return true
    }
    const haystack = `${item.key} ${item.label} ${item.description} ${item.value} ${item.base_value}`.toLowerCase()
    return haystack.includes(keyword)
  })
})
const overrideConfigCount = computed(() => siteConfigsPayload.value?.items.filter((item) => item.source === 'override').length ?? 0)
const secretConfigCount = computed(() => siteConfigsPayload.value?.items.filter((item) => item.is_secret).length ?? 0)
const restartRequiredCount = computed(() => siteConfigsPayload.value?.items.filter((item) => item.requires_restart).length ?? 0)
const billingSubscriptionMode = computed(() => {
  const raw = siteConfigsPayload.value?.items.find((item) => item.key === 'billing.subscription_mode')?.raw_value
  return raw === 'ladder' ? 'ladder' : 'fixed_tiers'
})
const freeDailyQuota = computed(() => {
  const raw = siteConfigsPayload.value?.items.find((item) => item.key === 'free_plan.daily_quota')?.raw_value
  return typeof raw === 'number' ? raw : 0
})
const fixedTierCount = computed(() => {
  const raw = siteConfigsPayload.value?.items.find((item) => item.key === 'tiers')?.raw_value
  return Array.isArray(raw) ? raw.length : 0
})
const addonCount = computed(() => {
  const raw = siteConfigsPayload.value?.items.find((item) => item.key === 'addons')?.raw_value
  return Array.isArray(raw) ? raw.length : 0
})

function setNotice(kind: NoticeKind, message: string) {
  notice.kind = kind
  notice.message = message
}

function clearNotice() {
  notice.message = ''
}

function applyUserToForm(target: ReturnType<typeof emptyUserForm>, user: UserRecord) {
  target.username = user.username
  target.password = ''
  target.email = user.email ?? ''
  target.is_admin = user.is_admin
  target.is_active = user.is_active
  target.expiration_date = toDateInput(user.expiration_date)
}

function applyEntitlementToForm(user: UserRecord) {
  entitlementForm.plan_price = user.plan_price == null ? '' : String(user.plan_price)
  entitlementForm.daily_quota = user.daily_quota == null ? '' : String(user.daily_quota)
  entitlementForm.pricing_mode = user.pricing_mode || 'fixed_tiers'
  entitlementForm.plan_ref = user.plan_ref || ''
  entitlementForm.subscription_status = (user.plan_price ?? 0) <= 0 ? 'free' : (user.is_active ? 'active' : 'inactive')
  entitlementForm.subscription_expires_at = toDateInput(user.expiration_date)
  entitlementForm.rate_limit_rule = user.rate_limit_rule || ''
  entitlementForm.addon_balance = String(user.addon_resource_balance ?? 0)
  entitlementForm.addon_total_purchased = String(user.addon_resource_total ?? 0)
  entitlementForm.addon_expires_at = toDateInput(user.addon_expiration_date)
  entitlementForm.clear_addon = false
  entitlementForm.reset_to_free = (user.plan_price ?? 0) <= 0
}

function resetEntitlementForm() {
  Object.assign(entitlementForm, emptyEntitlementForm())
}

async function refreshDashboard() {
  dashboard.value = await requestJson<DashboardPayload>(`${ADMIN_API_PREFIX}/dashboard/summary`)
}

async function refreshUsers() {
  usersPayload.value = await requestJson<UserListPayload>(`${ADMIN_API_PREFIX}/users`)
}

async function refreshOrders() {
  ordersPayload.value = await requestJson<OrdersPayload>(`${ADMIN_API_PREFIX}/orders`)
}

async function refreshStats(hour?: string) {
  const query = hour ? `?hour=${encodeURIComponent(hour)}` : ''
  statsPayload.value = await requestJson<StatsPayload>(`${ADMIN_API_PREFIX}/stats${query}`)
  selectedStatsHour.value = statsPayload.value.selected_hour ?? ''
}

async function refreshConfigs() {
  siteConfigsPayload.value = await requestJson<SiteConfigListPayload>(`${ADMIN_API_PREFIX}/configs`)
  if (!selectedConfigKey.value && siteConfigsPayload.value.items.length > 0) {
    openConfigEditor(siteConfigsPayload.value.items[0])
    return
  }
  const current = siteConfigsPayload.value.items.find((item) => item.key === selectedConfigKey.value) ?? null
  if (current) {
    syncConfigDraft(current)
  }
}

async function refreshAll() {
  await Promise.all([
    refreshDashboard(),
    refreshUsers(),
    refreshOrders(),
    refreshStats(selectedStatsHour.value || undefined),
    refreshConfigs(),
  ])
}

async function loadUserDetail(id: string) {
  userDetail.value = await requestJson<UserDetailPayload>(`${ADMIN_API_PREFIX}/users/${id}`)
  selectedUserId.value = id
  applyUserToForm(editForm, userDetail.value.user)
  applyEntitlementToForm(userDetail.value.user)
}

async function loadAuthState() {
  authState.value = await requestJson<AuthStateResponse>(`${ADMIN_API_PREFIX}/auth/state`)
}

async function bootstrapApp() {
  booting.value = true
  try {
    await loadAuthState()
    if (authState.value.user) {
      await refreshAll()
    }
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '初始化管理后台失败')
  } finally {
    booting.value = false
  }
}

function payloadFromForm(form: ReturnType<typeof emptyUserForm>) {
  return {
    username: form.username,
    password: form.password || null,
    email: form.email || null,
    is_admin: form.is_admin,
    is_active: form.is_active,
    expiration_date: form.expiration_date || null,
  }
}

async function handleBootstrap() {
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/auth/bootstrap`, {
      method: 'POST',
      body: JSON.stringify(bootstrapForm),
    })
    await loadAuthState()
    await refreshAll()
    setNotice('success', '首个管理员已创建并登录。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '创建管理员失败')
  } finally {
    busy.value = false
  }
}

async function handleLogin() {
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/auth/login`, {
      method: 'POST',
      body: JSON.stringify(loginForm),
    })
    await loadAuthState()
    await refreshAll()
    setNotice('success', '登录成功。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '登录失败')
  } finally {
    busy.value = false
  }
}

async function handleLogout() {
  busy.value = true
  try {
    await requestJson(`${ADMIN_API_PREFIX}/auth/logout`, { method: 'POST' })
  } catch {
    // Ignore transport failures and clear local state anyway.
  } finally {
    authState.value = { has_admin: true, user: null }
    dashboard.value = null
    usersPayload.value = null
    userDetail.value = null
    ordersPayload.value = null
    statsPayload.value = null
    lookupResult.value = null
    tokenParseResult.value = null
    siteConfigsPayload.value = null
    configAuditPayload.value = null
    selectedUserId.value = ''
    selectedConfigKey.value = ''
    resetEntitlementForm()
    setNotice('info', '已退出登录。')
    busy.value = false
  }
}

async function handleCreateUser() {
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/users`, {
      method: 'POST',
      body: JSON.stringify(payloadFromForm(createForm)),
    })
    Object.assign(createForm, emptyUserForm())
    await Promise.all([refreshUsers(), refreshDashboard()])
    setNotice('success', '用户已创建。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '创建用户失败')
  } finally {
    busy.value = false
  }
}

async function handleUpdateUser() {
  if (!selectedUserId.value) {
    setNotice('error', '请先选择用户。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/users/${selectedUserId.value}`, {
      method: 'PATCH',
      body: JSON.stringify(payloadFromForm(editForm)),
    })
    await Promise.all([refreshUsers(), refreshDashboard(), loadUserDetail(selectedUserId.value)])
    setNotice('success', '用户已更新。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '更新用户失败')
  } finally {
    busy.value = false
  }
}

async function handleDeleteUser(id: string) {
  if (!window.confirm('确定删除该用户吗？')) {
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/users/${id}`, { method: 'DELETE' })
    if (selectedUserId.value === id) {
      selectedUserId.value = ''
      userDetail.value = null
      Object.assign(editForm, emptyUserForm())
      resetEntitlementForm()
    }
    await Promise.all([refreshUsers(), refreshDashboard()])
    setNotice('success', '用户已删除。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '删除用户失败')
  } finally {
    busy.value = false
  }
}

function buildEntitlementPayload() {
  const payload: Record<string, unknown> = {
    clear_addon: entitlementForm.clear_addon,
    reset_to_free: entitlementForm.reset_to_free,
  }

  if (entitlementForm.plan_price.trim()) payload.plan_price = Number(entitlementForm.plan_price)
  if (entitlementForm.daily_quota.trim()) payload.daily_quota = Number(entitlementForm.daily_quota)
  if (entitlementForm.pricing_mode.trim()) payload.pricing_mode = entitlementForm.pricing_mode
  if (entitlementForm.plan_ref.trim()) payload.plan_ref = entitlementForm.plan_ref.trim()
  if (entitlementForm.subscription_status.trim()) payload.subscription_status = entitlementForm.subscription_status
  if (entitlementForm.subscription_expires_at.trim()) payload.subscription_expires_at = entitlementForm.subscription_expires_at
  if (entitlementForm.rate_limit_rule.trim()) payload.rate_limit_rule = entitlementForm.rate_limit_rule.trim()
  if (entitlementForm.addon_balance.trim()) payload.addon_balance = Number(entitlementForm.addon_balance)
  if (entitlementForm.addon_total_purchased.trim()) payload.addon_total_purchased = Number(entitlementForm.addon_total_purchased)
  if (entitlementForm.addon_expires_at.trim()) payload.addon_expires_at = entitlementForm.addon_expires_at

  return payload
}

async function handleManualEntitlementUpdate() {
  if (!selectedUserId.value) {
    setNotice('error', '请先选择用户。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/users/${selectedUserId.value}/entitlements`, {
      method: 'PATCH',
      body: JSON.stringify(buildEntitlementPayload()),
    })
    await Promise.all([refreshUsers(), refreshDashboard(), loadUserDetail(selectedUserId.value)])
    setNotice('success', '人工修正已生效。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '人工修正失败')
  } finally {
    busy.value = false
  }
}

async function handleLookupOrder() {
  if (!lookupKeyword.value.trim()) {
    setNotice('error', '请先输入订单关键词。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    lookupResult.value = await requestJson(`${ADMIN_API_PREFIX}/orders/lookup`, {
      method: 'POST',
      body: JSON.stringify({ keyword: lookupKeyword.value }),
    })
    tokenParseResult.value = null
    setNotice('success', '订单查询完成。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '订单查询失败')
  } finally {
    busy.value = false
  }
}

async function handleParseToken() {
  if (!lookupKeyword.value.trim()) {
    setNotice('error', '请先输入 token 或关键词。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    tokenParseResult.value = await requestJson(`${ADMIN_API_PREFIX}/afdian/parse-token`, {
      method: 'POST',
      body: JSON.stringify({ keyword: lookupKeyword.value }),
    })
    setNotice('success', 'Token 已解析。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '解析 token 失败')
  } finally {
    busy.value = false
  }
}

async function handleActivateOrder() {
  if (!lookupKeyword.value.trim()) {
    setNotice('error', '请先输入订单关键词。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    lookupResult.value = await requestJson(`${ADMIN_API_PREFIX}/orders/activate`, {
      method: 'POST',
      body: JSON.stringify({ keyword: lookupKeyword.value }),
    })
    await Promise.all([refreshOrders(), refreshUsers(), refreshDashboard()])
    setNotice('success', '订单激活完成。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '激活订单失败')
  } finally {
    busy.value = false
  }
}

async function handleStatsReload() {
  busy.value = true
  clearNotice()
  try {
    await refreshStats(selectedStatsHour.value || undefined)
    setNotice('success', '统计数据已刷新。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '刷新统计数据失败')
  } finally {
    busy.value = false
  }
}

async function handleLoadConfigAudit(key?: string) {
  const targetKey = key ?? selectedConfigKey.value
  if (!targetKey) {
    setNotice('error', '请先选择一个配置项。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    selectedConfigKey.value = targetKey
    configAuditPayload.value = await requestJson<SiteConfigAuditPayload>(
      `${ADMIN_API_PREFIX}/configs/audit?config_key=${encodeURIComponent(targetKey)}&limit=${encodeURIComponent(configAuditLimit.value)}`,
    )
    setNotice('success', '配置审计已刷新。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '加载配置审计失败')
  } finally {
    busy.value = false
  }
}

async function handleSaveConfig() {
  if (!selectedConfig.value) {
    setNotice('error', '请先选择一个配置项。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    const payload = await requestJson<SiteConfigUpdatePayload>(`${ADMIN_API_PREFIX}/configs`, {
      method: 'PUT',
      body: JSON.stringify({
        items: [
          {
            key: selectedConfig.value.key,
            value: parseConfigDraftValue(selectedConfig.value),
          },
        ],
        change_note: configChangeNote.value || null,
      }),
    })
    siteConfigsPayload.value = payload
    const current = payload.items.find((item) => item.key === selectedConfigKey.value) ?? null
    if (current) {
      syncConfigDraft(current)
    }
    await handleLoadConfigAudit(selectedConfig.value.key)
    setNotice('success', payload.message || '配置已保存。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '保存配置失败')
  } finally {
    busy.value = false
  }
}

async function handleClearConfigOverride() {
  if (!selectedConfig.value) {
    setNotice('error', '请先选择一个配置项。')
    return
  }
  busy.value = true
  clearNotice()
  try {
    const payload = await requestJson<SiteConfigUpdatePayload>(`${ADMIN_API_PREFIX}/configs`, {
      method: 'PUT',
      body: JSON.stringify({
        items: [
          {
            key: selectedConfig.value.key,
            clear: true,
          },
        ],
        change_note: configChangeNote.value || null,
      }),
    })
    siteConfigsPayload.value = payload
    const current = payload.items.find((item) => item.key === selectedConfigKey.value) ?? null
    if (current) {
      syncConfigDraft(current)
    }
    await handleLoadConfigAudit(selectedConfig.value.key)
    setNotice('success', payload.message || '配置已恢复基础值。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '清空覆盖失败')
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  void bootstrapApp()
})
</script>

<template>
  <div class="admin-shell">
    <div class="admin-backdrop"></div>

    <template v-if="booting">
      <section class="center-stage glass-panel splash-panel">
        <p class="eyebrow">管理后台</p>
        <h1>运营管控中心</h1>
        <p class="muted">正在同步核心数据看板、用户信息、订单流水与平台运行统计。</p>
      </section>
    </template>

    <template v-else-if="!hasSession">
      <section class="auth-shell">
        <div class="hero-card glass-panel">
          <p class="eyebrow">管理后台</p>
          <h1>运营管控中心</h1>
          <p class="muted">核心数据看板与用户/订单管理中枢。仅授权管理员访问。</p>
          <div v-if="notice.message" class="notice" :data-kind="notice.kind">{{ notice.message }}</div>
        </div>

        <form v-if="!authState.has_admin" class="auth-card glass-panel" @submit.prevent="handleBootstrap">
          <p class="eyebrow">初始化</p>
          <h2>创建首个管理员</h2>
          <label>
            <span>用户名</span>
            <input v-model.trim="bootstrapForm.username" class="input-field" required />
          </label>
          <label>
            <span>邮箱</span>
            <input v-model.trim="bootstrapForm.email" class="input-field" type="email" />
          </label>
          <label>
            <span>密码</span>
            <input v-model="bootstrapForm.password" class="input-field" type="password" minlength="6" required />
          </label>
          <button class="btn btn-primary" :disabled="busy" type="submit">创建管理员</button>
        </form>

        <form v-else class="auth-card glass-panel" @submit.prevent="handleLogin">
          <p class="eyebrow">登录</p>
          <h2>管理员登录</h2>
          <label>
            <span>用户名或邮箱</span>
            <input v-model.trim="loginForm.login" class="input-field" required />
          </label>
          <label>
            <span>密码</span>
            <input v-model="loginForm.password" class="input-field" type="password" required />
          </label>
          <button class="btn btn-primary" :disabled="busy" type="submit">登录</button>
        </form>
      </section>
    </template>

    <template v-else>
      <header class="topbar">
        <div>
          <p class="eyebrow">管理后台</p>
          <h1>运营管控中心</h1>
        </div>
        <div class="topbar-actions">
          <div class="identity glass-chip">
            <strong>{{ authState.user?.username }}</strong>
            <span>{{ authState.user?.email || authState.user?.username }}</span>
          </div>
          <button class="btn btn-outline" :disabled="busy" @click="handleLogout">退出登录</button>
        </div>
      </header>

      <div v-if="notice.message" class="notice global-notice" :data-kind="notice.kind">{{ notice.message }}</div>

      <nav class="tabbar glass-panel">
        <button class="tab" :class="{ active: activeTab === 'dashboard' }" @click="activeTab = 'dashboard'">概览</button>
        <button class="tab" :class="{ active: activeTab === 'users' }" @click="activeTab = 'users'">用户</button>
        <button class="tab" :class="{ active: activeTab === 'orders' }" @click="activeTab = 'orders'">订单</button>
        <button class="tab" :class="{ active: activeTab === 'stats' }" @click="activeTab = 'stats'">统计</button>
        <button class="tab" :class="{ active: activeTab === 'content' }" @click="activeTab = 'content'">内容</button>
        <button class="tab" :class="{ active: activeTab === 'configs' }" @click="activeTab = 'configs'">配置</button>
      </nav>

      <main class="workspace">
        <section v-if="activeTab === 'dashboard'" class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>用户总数</span><strong>{{ dashboard?.users.total_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>活跃用户</span><strong>{{ dashboard?.users.active_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>订单总数</span><strong>{{ dashboard?.orders.total_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>已激活金额</span><strong>￥{{ dashboard?.orders.activated_amount_total ?? 0 }}</strong></article>
          </div>

          <div class="split-grid">
            <section class="glass-panel panel-section">
              <div class="section-head"><h2>最近用户</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshDashboard">刷新</button></div>
              <table class="data-table">
                <thead><tr><th>用户</th><th>角色</th><th>状态</th><th>最近登录</th></tr></thead>
                <tbody>
                  <tr v-for="user in dashboard?.latest_users ?? []" :key="user.id">
                    <td><strong>{{ user.username }}</strong><span>{{ user.email || '-' }}</span></td>
                    <td>{{ user.is_admin ? '管理员' : '普通用户' }}</td>
                    <td>{{ user.is_active ? '启用' : '停用' }}</td>
                    <td>{{ formatDate(user.last_login_at) }}</td>
                  </tr>
                </tbody>
              </table>
            </section>

            <section class="glass-panel panel-section">
              <div class="section-head"><h2>最近订单</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshOrders">刷新</button></div>
              <table class="data-table">
                <thead><tr><th>订单</th><th>用户</th><th>状态</th><th>金额</th></tr></thead>
                <tbody>
                  <tr v-for="order in dashboard?.latest_orders ?? []" :key="order.id">
                    <td>{{ order.out_trade_no || order.order_no || `#${order.id}` }}</td>
                    <td>{{ order.username || '-' }}</td>
                    <td>{{ orderStatusLabel(order) }}</td>
                    <td>{{ order.amount ?? '-' }}</td>
                  </tr>
                </tbody>
              </table>
            </section>
          </div>
        </section>

        <section v-else-if="activeTab === 'users'" class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>用户总数</span><strong>{{ usersPayload?.summary.total_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>管理员数</span><strong>{{ usersPayload?.summary.admin_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>7 天内到期</span><strong>{{ usersPayload?.summary.expiring_in_7_days ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>非永久用户</span><strong>{{ usersPayload?.summary.non_permanent_users ?? 0 }}</strong></article>
          </div>

          <div class="split-grid">
            <section class="glass-panel panel-section form-panel">
              <div class="section-head"><h2>创建用户</h2><button class="btn btn-ghost" :disabled="busy" @click="Object.assign(createForm, emptyUserForm())">重置</button></div>
              <div class="form-grid">
                <label><span>用户名</span><input v-model.trim="createForm.username" class="input-field" /></label>
                <label><span>邮箱</span><input v-model.trim="createForm.email" class="input-field" type="email" /></label>
                <label><span>密码</span><input v-model="createForm.password" class="input-field" type="password" /></label>
                <label><span>到期时间</span><input v-model="createForm.expiration_date" class="input-field" type="datetime-local" /></label>
                <label class="check-row"><input v-model="createForm.is_admin" type="checkbox" /> 管理员</label>
                <label class="check-row"><input v-model="createForm.is_active" type="checkbox" /> 启用</label>
              </div>
              <button class="btn btn-primary" :disabled="busy" @click="handleCreateUser">创建用户</button>
            </section>

            <section class="glass-panel panel-section form-panel">
              <div class="section-head">
                <h2>编辑选中用户</h2>
                <button class="btn btn-ghost" :disabled="busy || !selectedUserId" @click="selectedUserId = ''; userDetail = null; Object.assign(editForm, emptyUserForm())">清空</button>
              </div>
              <div class="form-grid">
                <label><span>用户名</span><input v-model.trim="editForm.username" class="input-field" :disabled="!selectedUserId" /></label>
                <label><span>邮箱</span><input v-model.trim="editForm.email" class="input-field" type="email" :disabled="!selectedUserId" /></label>
                <label><span>新密码</span><input v-model="editForm.password" class="input-field" type="password" :disabled="!selectedUserId" /></label>
                <label><span>到期时间</span><input v-model="editForm.expiration_date" class="input-field" type="datetime-local" :disabled="!selectedUserId" /></label>
                <label class="check-row"><input v-model="editForm.is_admin" type="checkbox" :disabled="!selectedUserId" /> 管理员</label>
                <label class="check-row"><input v-model="editForm.is_active" type="checkbox" :disabled="!selectedUserId" /> 启用</label>
              </div>
              <button class="btn btn-primary" :disabled="busy || !selectedUserId" @click="handleUpdateUser">保存用户</button>
              <div v-if="userDetail" class="mini-card"><strong>用户订单</strong><span>{{ selectedUserOrders.length }} 条关联订单</span></div>
            </section>
          </div>

          <section class="glass-panel panel-section form-panel">
            <div class="section-head">
              <h2>人工修正额度</h2>
              <button class="btn btn-ghost" :disabled="busy || !selectedUserId" @click="userDetail && applyEntitlementToForm(userDetail.user)">重置</button>
            </div>
            <div class="mini-card section-note">
              <strong>人工处理入口</strong>
              <span>用于修正套餐价格、日额度、到期时间、加油包余额等错误数据。付费用户已不再实际限速，`rate_limit_rule` 一般只对免费/测试用户有意义。</span>
            </div>
            <div class="form-grid">
              <label><span>套餐月价</span><input v-model.trim="entitlementForm.plan_price" class="input-field" type="number" min="0" step="0.01" :disabled="!selectedUserId" /></label>
              <label><span>日额度</span><input v-model.trim="entitlementForm.daily_quota" class="input-field" type="number" min="0" step="1" :disabled="!selectedUserId" /></label>
              <label>
                <span>计费模式</span>
                <select v-model="entitlementForm.pricing_mode" class="input-field" :disabled="!selectedUserId">
                  <option value="free">free</option>
                  <option value="fixed_tiers">fixed_tiers</option>
                  <option value="ladder">ladder</option>
                </select>
              </label>
              <label><span>套餐标识</span><input v-model.trim="entitlementForm.plan_ref" class="input-field" :disabled="!selectedUserId" /></label>
              <label>
                <span>订阅状态</span>
                <select v-model="entitlementForm.subscription_status" class="input-field" :disabled="!selectedUserId">
                  <option value="free">free</option>
                  <option value="active">active</option>
                  <option value="inactive">inactive</option>
                </select>
              </label>
              <label><span>订阅到期</span><input v-model="entitlementForm.subscription_expires_at" class="input-field" type="datetime-local" :disabled="!selectedUserId" /></label>
              <label><span>限速规则</span><input v-model.trim="entitlementForm.rate_limit_rule" class="input-field" :disabled="!selectedUserId" /></label>
              <label><span>加油包余额</span><input v-model.trim="entitlementForm.addon_balance" class="input-field" type="number" min="0" step="1" :disabled="!selectedUserId" /></label>
              <label><span>加油包累计总量</span><input v-model.trim="entitlementForm.addon_total_purchased" class="input-field" type="number" min="0" step="1" :disabled="!selectedUserId" /></label>
              <label><span>加油包到期</span><input v-model="entitlementForm.addon_expires_at" class="input-field" type="datetime-local" :disabled="!selectedUserId" /></label>
              <label class="check-row"><input v-model="entitlementForm.clear_addon" type="checkbox" :disabled="!selectedUserId" /> 清空加油包</label>
              <label class="check-row"><input v-model="entitlementForm.reset_to_free" type="checkbox" :disabled="!selectedUserId" /> 直接改回免费</label>
            </div>
            <div class="lookup-row">
              <button class="btn btn-primary" :disabled="busy || !selectedUserId" @click="handleManualEntitlementUpdate">保存人工修正</button>
            </div>
          </section>

          <section class="glass-panel panel-section config-list-panel">
            <div class="section-head"><h2>用户列表</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshUsers">刷新</button></div>
            <table class="data-table">
              <thead><tr><th>用户</th><th>角色</th><th>状态</th><th>当前额度</th><th>订单数</th><th>操作</th></tr></thead>
              <tbody>
                <tr v-for="user in usersPayload?.items ?? []" :key="user.id" :class="{ selected: selectedUserId === user.id }">
                  <td><strong>{{ user.username }}</strong><span>{{ user.email || '-' }}</span></td>
                  <td>{{ user.is_admin ? '管理员' : '普通用户' }}</td>
                  <td>{{ user.is_active ? '启用' : '停用' }}</td>
                  <td>{{ userQuotaLabel(user) }}</td>
                  <td>{{ user.afdian_summary.order_count }}</td>
                  <td class="actions-cell">
                    <button class="btn btn-ghost" :disabled="busy" @click="loadUserDetail(user.id)">打开</button>
                    <button class="btn btn-danger" :disabled="busy || authState.user?.id === user.id" @click="handleDeleteUser(user.id)">删除</button>
                  </td>
                </tr>
              </tbody>
            </table>
          </section>
        </section>

        <section v-else-if="activeTab === 'orders'" class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>订单总数</span><strong>{{ ordersPayload?.summary.total_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>已激活</span><strong>{{ ordersPayload?.summary.activated_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>待处理</span><strong>{{ ordersPayload?.summary.pending_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>已激活金额</span><strong>￥{{ ordersPayload?.summary.activated_amount_total ?? 0 }}</strong></article>
          </div>

          <section class="glass-panel panel-section config-list-panel">
            <div class="section-head"><h2>订单查询与激活</h2></div>
            <div class="lookup-row">
              <input v-model.trim="lookupKeyword" class="input-field" placeholder="out_trade_no / order_no / custom_id / remark" />
              <button class="btn btn-outline" :disabled="busy" @click="handleLookupOrder">查询</button>
              <button class="btn btn-primary" :disabled="busy" @click="handleActivateOrder">激活</button>
              <button class="btn btn-ghost" :disabled="busy" @click="handleParseToken">解析令牌</button>
            </div>
            <div class="result-grid">
              <article class="glass-mini" v-if="lookupResult"><h3>查询结果</h3><pre>{{ asJson(lookupResult) }}</pre></article>
              <article class="glass-mini" v-if="tokenParseResult"><h3>令牌解析</h3><pre>{{ asJson(tokenParseResult) }}</pre></article>
            </div>
          </section>

          <section class="glass-panel panel-section">
            <div class="section-head"><h2>订单列表</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshOrders">刷新</button></div>
            <table class="data-table">
              <thead><tr><th>订单</th><th>用户</th><th>状态</th><th>动作</th><th>金额</th><th>更新时间</th></tr></thead>
              <tbody>
                <tr v-for="order in ordersPayload?.items ?? []" :key="order.id">
                  <td><strong>{{ order.out_trade_no || order.order_no || `#${order.id}` }}</strong><span>{{ order.remark || order.custom_id || '-' }}</span></td>
                  <td>{{ order.username || '-' }}</td>
                  <td>{{ orderStatusLabel(order) }}</td>
                  <td>{{ order.action || '-' }}</td>
                  <td>{{ order.amount ?? '-' }}</td>
                  <td>{{ formatDate(order.updated_at) }}</td>
                </tr>
              </tbody>
            </table>
          </section>
        </section>

        <section v-else-if="activeTab === 'stats'" class="page-grid">
          <section class="glass-panel panel-section">
            <div class="section-head">
              <h2>小时请求统计</h2>
              <div class="lookup-row compact">
                <select v-model="selectedStatsHour" class="input-field slim-select">
                  <option value="">最近窗口</option>
                  <option v-for="hour in statsPayload?.hours ?? []" :key="hour" :value="hour">{{ hour }}</option>
                </select>
                <button class="btn btn-primary" :disabled="busy" @click="handleStatsReload">刷新</button>
              </div>
            </div>
            <div class="metrics-grid">
              <article class="metric-card glass-panel"><span>请求数</span><strong>{{ statsPayload?.summary.total_count ?? 0 }}</strong></article>
              <article class="metric-card glass-panel"><span>成功率</span><strong>{{ statsPayload?.summary.success_rate ?? 0 }}%</strong></article>
              <article class="metric-card glass-panel"><span>限流数</span><strong>{{ statsPayload?.summary.total_rate_limited ?? 0 }}</strong></article>
              <article class="metric-card glass-panel"><span>资源消耗</span><strong>{{ statsPayload?.summary.total_resource_cost ?? 0 }}</strong></article>
            </div>
          </section>

          <div class="split-grid">
            <section class="glass-panel panel-section">
              <div class="section-head"><h2>日趋势</h2></div>
              <table class="data-table">
                <thead><tr><th>日期</th><th>成功</th><th>失败</th><th>限流</th><th>总计</th></tr></thead>
                <tbody>
                  <tr v-for="row in statsPayload?.daily_stats ?? []" :key="String(row.day)">
                    <td>{{ row.day }}</td>
                    <td>{{ row.daily_success }}</td>
                    <td>{{ row.daily_fail }}</td>
                    <td>{{ row.daily_rate_limited }}</td>
                    <td>{{ row.daily_total }}</td>
                  </tr>
                </tbody>
              </table>
            </section>

            <section class="glass-panel panel-section">
              <div class="section-head"><h2>热门接口</h2></div>
              <table class="data-table">
                <thead><tr><th>接口</th><th>成功</th><th>失败</th><th>总计</th></tr></thead>
                <tbody>
                  <tr v-for="row in statsPayload?.top_endpoints ?? []" :key="String(row.endpoint)">
                    <td>{{ row.endpoint }}</td>
                    <td>{{ row.success_count }}</td>
                    <td>{{ row.fail_count }}</td>
                    <td>{{ row.total_count }}</td>
                  </tr>
                </tbody>
              </table>
            </section>
          </div>
        </section>

        <section v-else-if="activeTab === 'content'" class="page-grid">
          <ContentManagementTab />
        </section>

        <section v-else class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>配置总数</span><strong>{{ siteConfigsPayload?.items.length ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>覆盖项</span><strong>{{ overrideConfigCount }}</strong></article>
            <article class="metric-card glass-panel"><span>密钥项</span><strong>{{ secretConfigCount }}</strong></article>
            <article class="metric-card glass-panel"><span>需重启</span><strong>{{ restartRequiredCount }}</strong></article>
            <article class="metric-card glass-panel"><span>订阅模式</span><strong>{{ planModeLabel(billingSubscriptionMode) }}</strong></article>
            <article class="metric-card glass-panel"><span>免费日额度</span><strong>{{ freeDailyQuota }}</strong></article>
            <article class="metric-card glass-panel"><span>固定档数量</span><strong>{{ fixedTierCount }}</strong></article>
            <article class="metric-card glass-panel"><span>加油包数量</span><strong>{{ addonCount }}</strong></article>
          </div>

          <section class="glass-panel panel-section">
            <div class="section-head">
              <h2>统一配置中心</h2>
              <div class="lookup-row compact">
                <button class="btn btn-outline" :disabled="busy" @click="refreshConfigs">刷新配置列表</button>
              </div>
            </div>
            <div class="mini-card section-note">
              <strong>当前订阅模式：{{ planModeLabel(billingSubscriptionMode) }}</strong>
              <span>
                现在所有后端可配置项都统一通过本页治理。资费目录不再走独立维护入口；
                `billing.subscription_mode`、阶梯参数、`tiers`、`addons` 与其他后端配置共享同一套修改和审计链路。
              </span>
            </div>
          </section>

          <section class="glass-panel panel-section config-list-panel">
            <div class="section-head">
              <h2>站点配置</h2>
              <div class="lookup-row compact">
                <select v-model="configGroupFilter" class="input-field slim-select">
                  <option value="all">全部分组</option>
                  <option v-for="group in siteConfigsPayload?.groups ?? []" :key="group.key" :value="group.key">
                    {{ group.label }} ({{ group.count }})
                  </option>
                </select>
                <select v-model="configSourceFilter" class="input-field slim-select">
                  <option value="all">全部来源</option>
                  <option value="base">基础值</option>
                  <option value="override">覆盖值</option>
                </select>
                <input v-model.trim="configSearchKeyword" class="input-field" placeholder="搜索 key / 标签 / 描述 / 当前值" />
                <button class="btn btn-ghost" :disabled="busy" @click="refreshConfigs">刷新</button>
              </div>
            </div>
            <div class="table-scroll config-table-scroll">
              <table class="data-table config-table">
                <thead>
                  <tr>
                    <th class="config-col-key">配置项</th>
                    <th class="config-col-group">分组</th>
                    <th class="config-col-value">当前值</th>
                    <th class="config-col-value">基础值</th>
                    <th class="config-col-updated">最近修改</th>
                    <th class="config-col-actions">操作</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="item in filteredConfigItems" :key="item.key" :class="{ selected: selectedConfigKey === item.key }">
                    <td class="config-item-cell">
                      <strong>{{ item.key }}</strong>
                      <span>{{ configDisplayLabel(item) }}</span>
                    </td>
                    <td class="config-group-cell">
                      <strong>{{ item.group_label }}</strong>
                      <span>{{ item.requires_restart ? '需要重启' : '热更新' }} / {{ item.is_secret ? '密钥' : '普通项' }}</span>
                    </td>
                    <td class="config-value-cell">{{ item.value }}</td>
                    <td class="config-value-cell">{{ item.base_value }}</td>
                    <td class="config-updated-cell">
                      <strong>{{ item.updated_by_username || '-' }}</strong>
                      <span>{{ formatDate(item.updated_at) }}</span>
                    </td>
                    <td class="actions-cell config-actions-cell">
                      <button class="btn btn-ghost" :disabled="busy" @click="openConfigEditor(item)">编辑</button>
                      <button class="btn btn-outline" :disabled="busy" @click="handleLoadConfigAudit(item.key)">审计</button>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </section>

          <div class="split-grid">
            <section class="glass-panel panel-section form-panel">
              <div class="section-head">
                <h2>编辑配置</h2>
                <button class="btn btn-ghost" :disabled="busy || !selectedConfig" @click="selectedConfig && syncConfigDraft(selectedConfig)">重置</button>
              </div>
              <div v-if="selectedConfig" class="form-grid">
                <label><span>配置键</span><input :value="selectedConfig.key" class="input-field" disabled /></label>
                <label><span>分组</span><input :value="selectedConfig.group_label" class="input-field" disabled /></label>
                <label><span>当前值</span><input :value="selectedConfig.value" class="input-field" disabled /></label>
                <label><span>基础值</span><input :value="selectedConfig.base_value" class="input-field" disabled /></label>
                <label><span>变更备注</span><input v-model.trim="configChangeNote" class="input-field" placeholder="写入审计记录" /></label>
                <label class="config-editor-wide">
                  <span>配置说明</span>
                  <textarea :value="configDescriptionText(selectedConfig)" class="input-field config-meta-textarea" disabled></textarea>
                </label>
                <label v-if="typeof selectedConfig.raw_value === 'boolean'">
                  <span>配置值</span>
                  <select v-model="configDraftValue" class="input-field">
                    <option value="true">true</option>
                    <option value="false">false</option>
                  </select>
                </label>
                <label v-else class="config-editor-wide">
                  <span>配置值</span>
                  <textarea v-model="configDraftValue" class="input-field config-textarea"></textarea>
                </label>
              </div>
              <div v-else class="mini-card">
                <strong>未选择配置项</strong>
                <span>先从上方列表中选择一个配置项。</span>
              </div>
              <div class="lookup-row">
                <button class="btn btn-primary" :disabled="busy || !selectedConfig" @click="handleSaveConfig">保存配置</button>
                <button class="btn btn-danger" :disabled="busy || !selectedConfig" @click="handleClearConfigOverride">清空覆盖</button>
              </div>
            </section>

            <section class="glass-panel panel-section">
              <div class="section-head">
                <h2>配置审计</h2>
                <div class="lookup-row compact">
                  <select v-model="configAuditLimit" class="input-field slim-select">
                    <option value="20">20</option>
                    <option value="50">50</option>
                    <option value="100">100</option>
                    <option value="200">200</option>
                  </select>
                  <button class="btn btn-ghost" :disabled="busy || !selectedConfigKey" @click="handleLoadConfigAudit()">刷新</button>
                </div>
              </div>
              <div class="table-scroll">
                <table class="data-table audit-table">
                  <thead>
                    <tr>
                      <th>时间</th>
                      <th>操作</th>
                      <th>修改人</th>
                      <th>旧值</th>
                      <th>新值</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="item in configAuditPayload?.items ?? []" :key="item.id">
                      <td>{{ formatDate(item.created_at) }}</td>
                      <td><strong>{{ item.operation }}</strong><span>{{ item.change_note || '-' }}</span></td>
                      <td><strong>{{ item.changed_by_username || '-' }}</strong><span>{{ item.changed_from_ip || '-' }}</span></td>
                      <td>{{ item.old_value || '-' }}</td>
                      <td>{{ item.new_value || '-' }}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </section>
          </div>
        </section>
      </main>
    </template>
  </div>
</template>
