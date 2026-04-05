<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'

type NoticeKind = 'info' | 'success' | 'error'
type TabKey = 'dashboard' | 'users' | 'orders' | 'stats' | 'plans'

type AdminUser = {
  id: string
  username: string
  email: string
  display_name: string
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

type PlanTier = {
  id: string
  monthly_price: number
  daily_quota: number
}

type PlanCatalogPayload = {
  free_plan: PlanTier
  tiers: PlanTier[]
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
    display_name: '',
    is_admin: false,
    is_active: true,
    limit_rule: '',
    expiration_date: '',
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

const booting = ref(true)
const busy = ref(false)
const activeTab = ref<TabKey>('dashboard')
const authState = ref<AuthStateResponse>({ has_admin: true, user: null })
const dashboard = ref<DashboardPayload | null>(null)
const usersPayload = ref<UserListPayload | null>(null)
const userDetail = ref<UserDetailPayload | null>(null)
const ordersPayload = ref<OrdersPayload | null>(null)
const statsPayload = ref<StatsPayload | null>(null)
const planCatalog = ref<PlanCatalogPayload | null>(null)
const selectedStatsHour = ref('')
const lookupKeyword = ref('')
const lookupResult = ref<unknown>(null)
const tokenParseResult = ref<unknown>(null)
const selectedUserId = ref<string>('')

const notice = reactive({
  kind: 'info' as NoticeKind,
  message: '',
})

const bootstrapForm = reactive({
  username: 'admin',
  password: '',
  email: '',
  display_name: 'Administrator',
})

const loginForm = reactive({
  login: '',
  password: '',
})

const createForm = reactive(emptyUserForm())
const editForm = reactive(emptyUserForm())

const hasSession = computed(() => Boolean(authState.value.user))
const selectedUserOrders = computed(() => userDetail.value?.orders ?? [])

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
  target.display_name = user.display_name ?? ''
  target.is_admin = user.is_admin
  target.is_active = user.is_active
  target.limit_rule = user.limit_rule ?? ''
  target.expiration_date = toDateInput(user.expiration_date)
}

async function refreshDashboard() {
  dashboard.value = await requestJson<DashboardPayload>('/adminapi/dashboard/summary')
}

async function refreshUsers() {
  usersPayload.value = await requestJson<UserListPayload>('/adminapi/users')
}

async function refreshOrders() {
  ordersPayload.value = await requestJson<OrdersPayload>('/adminapi/orders')
}

async function refreshStats(hour?: string) {
  const query = hour ? `?hour=${encodeURIComponent(hour)}` : ''
  statsPayload.value = await requestJson<StatsPayload>(`/adminapi/stats${query}`)
  selectedStatsHour.value = statsPayload.value.selected_hour ?? ''
}

async function refreshPlans() {
  planCatalog.value = await requestJson<PlanCatalogPayload>('/adminapi/plans')
}

async function refreshAll() {
  await Promise.all([
    refreshDashboard(),
    refreshUsers(),
    refreshOrders(),
    refreshStats(selectedStatsHour.value || undefined),
    refreshPlans(),
  ])
}

async function loadUserDetail(id: string) {
  userDetail.value = await requestJson<UserDetailPayload>(`/adminapi/users/${id}`)
  selectedUserId.value = id
  applyUserToForm(editForm, userDetail.value.user)
}

async function loadAuthState() {
  authState.value = await requestJson<AuthStateResponse>('/adminapi/auth/state')
}

async function bootstrapApp() {
  booting.value = true
  try {
    await loadAuthState()
    if (authState.value.user) {
      await refreshAll()
    }
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to initialize admin console')
  } finally {
    booting.value = false
  }
}

function payloadFromForm(form: ReturnType<typeof emptyUserForm>) {
  return {
    username: form.username,
    password: form.password || null,
    email: form.email || null,
    display_name: form.display_name || null,
    is_admin: form.is_admin,
    is_active: form.is_active,
    limit_rule: form.limit_rule || null,
    expiration_date: form.expiration_date || null,
  }
}

async function handleBootstrap() {
  busy.value = true
  clearNotice()
  try {
    await requestJson('/adminapi/auth/bootstrap', {
      method: 'POST',
      body: JSON.stringify(bootstrapForm),
    })
    await loadAuthState()
    await refreshAll()
    setNotice('success', 'First admin created and signed in.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to bootstrap admin')
  } finally {
    busy.value = false
  }
}

async function handleLogin() {
  busy.value = true
  clearNotice()
  try {
    await requestJson('/adminapi/auth/login', {
      method: 'POST',
      body: JSON.stringify(loginForm),
    })
    await loadAuthState()
    await refreshAll()
    setNotice('success', 'Signed in.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Login failed')
  } finally {
    busy.value = false
  }
}

async function handleLogout() {
  busy.value = true
  try {
    await requestJson('/adminapi/auth/logout', { method: 'POST' })
  } catch {
    // Ignore transport failures and clear local state anyway.
  } finally {
    authState.value = { has_admin: true, user: null }
    dashboard.value = null
    usersPayload.value = null
    userDetail.value = null
    ordersPayload.value = null
    statsPayload.value = null
    planCatalog.value = null
    lookupResult.value = null
    tokenParseResult.value = null
    selectedUserId.value = ''
    setNotice('info', 'Signed out.')
    busy.value = false
  }
}

async function handleCreateUser() {
  busy.value = true
  clearNotice()
  try {
    await requestJson('/adminapi/users', {
      method: 'POST',
      body: JSON.stringify(payloadFromForm(createForm)),
    })
    Object.assign(createForm, emptyUserForm())
    await Promise.all([refreshUsers(), refreshDashboard()])
    setNotice('success', 'User created.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to create user')
  } finally {
    busy.value = false
  }
}

async function handleUpdateUser() {
  if (!selectedUserId.value) {
    setNotice('error', 'Select a user first.')
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`/adminapi/users/${selectedUserId.value}`, {
      method: 'PATCH',
      body: JSON.stringify(payloadFromForm(editForm)),
    })
    await Promise.all([refreshUsers(), refreshDashboard(), loadUserDetail(selectedUserId.value)])
    setNotice('success', 'User updated.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to update user')
  } finally {
    busy.value = false
  }
}

async function handleDeleteUser(id: string) {
  if (!window.confirm('Delete this user?')) {
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`/adminapi/users/${id}`, { method: 'DELETE' })
    if (selectedUserId.value === id) {
      selectedUserId.value = ''
      userDetail.value = null
      Object.assign(editForm, emptyUserForm())
    }
    await Promise.all([refreshUsers(), refreshDashboard()])
    setNotice('success', 'User deleted.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to delete user')
  } finally {
    busy.value = false
  }
}

async function handleLookupOrder() {
  if (!lookupKeyword.value.trim()) {
    setNotice('error', 'Enter an order keyword first.')
    return
  }
  busy.value = true
  clearNotice()
  try {
    lookupResult.value = await requestJson('/adminapi/orders/lookup', {
      method: 'POST',
      body: JSON.stringify({ keyword: lookupKeyword.value }),
    })
    tokenParseResult.value = null
    setNotice('success', 'Lookup finished.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Lookup failed')
  } finally {
    busy.value = false
  }
}

async function handleParseToken() {
  if (!lookupKeyword.value.trim()) {
    setNotice('error', 'Enter a token or keyword first.')
    return
  }
  busy.value = true
  clearNotice()
  try {
    tokenParseResult.value = await requestJson('/adminapi/afdian/parse-token', {
      method: 'POST',
      body: JSON.stringify({ keyword: lookupKeyword.value }),
    })
    setNotice('success', 'Token parsed.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to parse token')
  } finally {
    busy.value = false
  }
}

async function handleActivateOrder() {
  if (!lookupKeyword.value.trim()) {
    setNotice('error', 'Enter an order keyword first.')
    return
  }
  busy.value = true
  clearNotice()
  try {
    lookupResult.value = await requestJson('/adminapi/orders/activate', {
      method: 'POST',
      body: JSON.stringify({ keyword: lookupKeyword.value }),
    })
    await Promise.all([refreshOrders(), refreshUsers(), refreshDashboard()])
    setNotice('success', 'Activation finished.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to activate order')
  } finally {
    busy.value = false
  }
}

async function handleStatsReload() {
  busy.value = true
  clearNotice()
  try {
    await refreshStats(selectedStatsHour.value || undefined)
    setNotice('success', 'Stats reloaded.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to reload stats')
  } finally {
    busy.value = false
  }
}

function handleAddPlanTier() {
  if (!planCatalog.value) {
    return
  }
  const lastTier = [...planCatalog.value.tiers].sort((left, right) => left.monthly_price - right.monthly_price).at(-1)
  planCatalog.value.tiers.push({
    id: '',
    monthly_price: lastTier ? lastTier.monthly_price + 5 : 5,
    daily_quota: lastTier ? lastTier.daily_quota + 1500 : planCatalog.value.free_plan.daily_quota,
  })
}

function handleRemovePlanTier(index: number) {
  if (!planCatalog.value) {
    return
  }
  planCatalog.value.tiers.splice(index, 1)
}

async function handleSavePlans() {
  if (!planCatalog.value) {
    return
  }
  busy.value = true
  clearNotice()
  try {
    planCatalog.value = await requestJson<PlanCatalogPayload>('/adminapi/plans', {
      method: 'PUT',
      body: JSON.stringify({
        free_daily_quota: planCatalog.value.free_plan.daily_quota,
        tiers: planCatalog.value.tiers.map((tier) => ({
          monthly_price: tier.monthly_price,
          daily_quota: tier.daily_quota,
        })),
      }),
    })
    setNotice('success', 'Plan catalog saved.')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : 'Failed to save plan catalog')
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
        <p class="eyebrow">Secure Operations</p>
        <h1>运营管控中心</h1>
        <p class="muted">正在同步核心数据看板、用户信息、订单流水与平台运行统计。</p>
      </section>
    </template>

    <template v-else-if="!hasSession">
      <section class="auth-shell">
        <div class="hero-card glass-panel">
          <p class="eyebrow">Secure Operations</p>
          <h1>运营管控中心</h1>
          <p class="muted">核心数据看板与用户/订单管理中枢。仅授权管理员访问。</p>
          <div v-if="notice.message" class="notice" :data-kind="notice.kind">{{ notice.message }}</div>
        </div>

        <form v-if="!authState.has_admin" class="auth-card glass-panel" @submit.prevent="handleBootstrap">
          <p class="eyebrow">Bootstrap</p>
          <h2>Create the first administrator</h2>
          <label>
            <span>Username</span>
            <input v-model.trim="bootstrapForm.username" class="input-field" required />
          </label>
          <label>
            <span>Display Name</span>
            <input v-model.trim="bootstrapForm.display_name" class="input-field" />
          </label>
          <label>
            <span>Email</span>
            <input v-model.trim="bootstrapForm.email" class="input-field" type="email" />
          </label>
          <label>
            <span>Password</span>
            <input v-model="bootstrapForm.password" class="input-field" type="password" minlength="6" required />
          </label>
          <button class="btn btn-primary" :disabled="busy" type="submit">Create Admin</button>
        </form>

        <form v-else class="auth-card glass-panel" @submit.prevent="handleLogin">
          <p class="eyebrow">Sign In</p>
          <h2>Administrator login</h2>
          <label>
            <span>Username or Email</span>
            <input v-model.trim="loginForm.login" class="input-field" required />
          </label>
          <label>
            <span>Password</span>
            <input v-model="loginForm.password" class="input-field" type="password" required />
          </label>
          <button class="btn btn-primary" :disabled="busy" type="submit">Sign In</button>
        </form>
      </section>
    </template>

    <template v-else>
      <header class="topbar">
        <div>
          <p class="eyebrow">Secure Operations</p>
          <h1>运营管控中心</h1>
        </div>
        <div class="topbar-actions">
          <div class="identity glass-chip">
            <strong>{{ authState.user?.display_name || authState.user?.username }}</strong>
            <span>{{ authState.user?.email || authState.user?.username }}</span>
          </div>
          <button class="btn btn-outline" :disabled="busy" @click="handleLogout">Sign Out</button>
        </div>
      </header>

      <div v-if="notice.message" class="notice global-notice" :data-kind="notice.kind">{{ notice.message }}</div>

      <nav class="tabbar glass-panel">
        <button class="tab" :class="{ active: activeTab === 'dashboard' }" @click="activeTab = 'dashboard'">Dashboard</button>
        <button class="tab" :class="{ active: activeTab === 'users' }" @click="activeTab = 'users'">Users</button>
        <button class="tab" :class="{ active: activeTab === 'orders' }" @click="activeTab = 'orders'">Orders</button>
        <button class="tab" :class="{ active: activeTab === 'stats' }" @click="activeTab = 'stats'">Stats</button>
        <button class="tab" :class="{ active: activeTab === 'plans' }" @click="activeTab = 'plans'">Plans</button>
      </nav>

      <main class="workspace">
        <section v-if="activeTab === 'dashboard'" class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>Total Users</span><strong>{{ dashboard?.users.total_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Active Users</span><strong>{{ dashboard?.users.active_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Total Orders</span><strong>{{ dashboard?.orders.total_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Activated Amount</span><strong>CNY {{ dashboard?.orders.activated_amount_total ?? 0 }}</strong></article>
          </div>

          <div class="split-grid">
            <section class="glass-panel panel-section">
              <div class="section-head"><h2>Latest users</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshDashboard">Refresh</button></div>
              <table class="data-table">
                <thead><tr><th>User</th><th>Role</th><th>Status</th><th>Last Login</th></tr></thead>
                <tbody>
                  <tr v-for="user in dashboard?.latest_users ?? []" :key="user.id">
                    <td><strong>{{ user.display_name }}</strong><span>{{ user.username }}</span></td>
                    <td>{{ user.is_admin ? 'Admin' : 'Member' }}</td>
                    <td>{{ user.is_active ? 'Active' : 'Disabled' }}</td>
                    <td>{{ formatDate(user.last_login_at) }}</td>
                  </tr>
                </tbody>
              </table>
            </section>

            <section class="glass-panel panel-section">
              <div class="section-head"><h2>Latest orders</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshOrders">Refresh</button></div>
              <table class="data-table">
                <thead><tr><th>Order</th><th>User</th><th>Status</th><th>Amount</th></tr></thead>
                <tbody>
                  <tr v-for="order in dashboard?.latest_orders ?? []" :key="order.id">
                    <td>{{ order.out_trade_no || order.order_no || `#${order.id}` }}</td>
                    <td>{{ order.username || '-' }}</td>
                    <td>{{ order.status_label }}</td>
                    <td>{{ order.amount ?? '-' }}</td>
                  </tr>
                </tbody>
              </table>
            </section>
          </div>
        </section>

        <section v-else-if="activeTab === 'users'" class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>Total Users</span><strong>{{ usersPayload?.summary.total_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Admin Users</span><strong>{{ usersPayload?.summary.admin_users ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Expiring in 7 Days</span><strong>{{ usersPayload?.summary.expiring_in_7_days ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Non Permanent</span><strong>{{ usersPayload?.summary.non_permanent_users ?? 0 }}</strong></article>
          </div>

          <div class="split-grid">
            <section class="glass-panel panel-section form-panel">
              <div class="section-head"><h2>Create user</h2><button class="btn btn-ghost" :disabled="busy" @click="Object.assign(createForm, emptyUserForm())">Reset</button></div>
              <div class="form-grid">
                <label><span>Username</span><input v-model.trim="createForm.username" class="input-field" /></label>
                <label><span>Display Name</span><input v-model.trim="createForm.display_name" class="input-field" /></label>
                <label><span>Email</span><input v-model.trim="createForm.email" class="input-field" type="email" /></label>
                <label><span>Password</span><input v-model="createForm.password" class="input-field" type="password" /></label>
                <label><span>Limit Rule</span><input v-model.trim="createForm.limit_rule" class="input-field" placeholder="800/day;8/minute" /></label>
                <label><span>Expiration</span><input v-model="createForm.expiration_date" class="input-field" type="datetime-local" /></label>
                <label class="check-row"><input v-model="createForm.is_admin" type="checkbox" /> Admin</label>
                <label class="check-row"><input v-model="createForm.is_active" type="checkbox" /> Active</label>
              </div>
              <button class="btn btn-primary" :disabled="busy" @click="handleCreateUser">Create User</button>
            </section>

            <section class="glass-panel panel-section form-panel">
              <div class="section-head">
                <h2>Edit selected user</h2>
                <button class="btn btn-ghost" :disabled="busy || !selectedUserId" @click="selectedUserId = ''; userDetail = null; Object.assign(editForm, emptyUserForm())">Clear</button>
              </div>
              <div class="form-grid">
                <label><span>Username</span><input v-model.trim="editForm.username" class="input-field" :disabled="!selectedUserId" /></label>
                <label><span>Display Name</span><input v-model.trim="editForm.display_name" class="input-field" :disabled="!selectedUserId" /></label>
                <label><span>Email</span><input v-model.trim="editForm.email" class="input-field" type="email" :disabled="!selectedUserId" /></label>
                <label><span>New Password</span><input v-model="editForm.password" class="input-field" type="password" :disabled="!selectedUserId" /></label>
                <label><span>Limit Rule</span><input v-model.trim="editForm.limit_rule" class="input-field" :disabled="!selectedUserId" /></label>
                <label><span>Expiration</span><input v-model="editForm.expiration_date" class="input-field" type="datetime-local" :disabled="!selectedUserId" /></label>
                <label class="check-row"><input v-model="editForm.is_admin" type="checkbox" :disabled="!selectedUserId" /> Admin</label>
                <label class="check-row"><input v-model="editForm.is_active" type="checkbox" :disabled="!selectedUserId" /> Active</label>
              </div>
              <button class="btn btn-primary" :disabled="busy || !selectedUserId" @click="handleUpdateUser">Update User</button>
              <div v-if="userDetail" class="mini-card"><strong>User orders</strong><span>{{ selectedUserOrders.length }} linked orders</span></div>
            </section>
          </div>

          <section class="glass-panel panel-section">
            <div class="section-head"><h2>User directory</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshUsers">Refresh</button></div>
            <table class="data-table">
              <thead><tr><th>User</th><th>Role</th><th>Status</th><th>Limit</th><th>Orders</th><th>Actions</th></tr></thead>
              <tbody>
                <tr v-for="user in usersPayload?.items ?? []" :key="user.id" :class="{ selected: selectedUserId === user.id }">
                  <td><strong>{{ user.display_name }}</strong><span>{{ user.username }}</span></td>
                  <td>{{ user.is_admin ? 'Admin' : 'Member' }}</td>
                  <td>{{ user.is_active ? 'Active' : 'Disabled' }}</td>
                  <td>{{ user.limit_rule || '-' }}</td>
                  <td>{{ user.afdian_summary.order_count }}</td>
                  <td class="actions-cell">
                    <button class="btn btn-ghost" :disabled="busy" @click="loadUserDetail(user.id)">Open</button>
                    <button class="btn btn-danger" :disabled="busy || authState.user?.id === user.id" @click="handleDeleteUser(user.id)">Delete</button>
                  </td>
                </tr>
              </tbody>
            </table>
          </section>
        </section>

        <section v-else-if="activeTab === 'orders'" class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>Total Orders</span><strong>{{ ordersPayload?.summary.total_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Activated</span><strong>{{ ordersPayload?.summary.activated_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Pending</span><strong>{{ ordersPayload?.summary.pending_orders ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Activated Amount</span><strong>CNY {{ ordersPayload?.summary.activated_amount_total ?? 0 }}</strong></article>
          </div>

          <section class="glass-panel panel-section">
            <div class="section-head"><h2>Order lookup and activation</h2></div>
            <div class="lookup-row">
              <input v-model.trim="lookupKeyword" class="input-field" placeholder="out_trade_no / order_no / custom_id / remark" />
              <button class="btn btn-outline" :disabled="busy" @click="handleLookupOrder">Lookup</button>
              <button class="btn btn-primary" :disabled="busy" @click="handleActivateOrder">Activate</button>
              <button class="btn btn-ghost" :disabled="busy" @click="handleParseToken">Parse Token</button>
            </div>
            <div class="result-grid">
              <article class="glass-mini" v-if="lookupResult"><h3>Lookup Result</h3><pre>{{ asJson(lookupResult) }}</pre></article>
              <article class="glass-mini" v-if="tokenParseResult"><h3>Token Parse</h3><pre>{{ asJson(tokenParseResult) }}</pre></article>
            </div>
          </section>

          <section class="glass-panel panel-section">
            <div class="section-head"><h2>Order list</h2><button class="btn btn-ghost" :disabled="busy" @click="refreshOrders">Refresh</button></div>
            <table class="data-table">
              <thead><tr><th>Order</th><th>User</th><th>Status</th><th>Action</th><th>Amount</th><th>Updated</th></tr></thead>
              <tbody>
                <tr v-for="order in ordersPayload?.items ?? []" :key="order.id">
                  <td><strong>{{ order.out_trade_no || order.order_no || `#${order.id}` }}</strong><span>{{ order.remark || order.custom_id || '-' }}</span></td>
                  <td>{{ order.username || '-' }}</td>
                  <td>{{ order.status_label }}</td>
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
              <h2>Hourly request stats</h2>
              <div class="lookup-row compact">
                <select v-model="selectedStatsHour" class="input-field slim-select">
                  <option value="">Latest window</option>
                  <option v-for="hour in statsPayload?.hours ?? []" :key="hour" :value="hour">{{ hour }}</option>
                </select>
                <button class="btn btn-primary" :disabled="busy" @click="handleStatsReload">Reload</button>
              </div>
            </div>
            <div class="metrics-grid">
              <article class="metric-card glass-panel"><span>Requests</span><strong>{{ statsPayload?.summary.total_count ?? 0 }}</strong></article>
              <article class="metric-card glass-panel"><span>Success Rate</span><strong>{{ statsPayload?.summary.success_rate ?? 0 }}%</strong></article>
              <article class="metric-card glass-panel"><span>Rate Limited</span><strong>{{ statsPayload?.summary.total_rate_limited ?? 0 }}</strong></article>
              <article class="metric-card glass-panel"><span>Resource Cost</span><strong>{{ statsPayload?.summary.total_resource_cost ?? 0 }}</strong></article>
            </div>
          </section>

          <div class="split-grid">
            <section class="glass-panel panel-section">
              <div class="section-head"><h2>Daily trend</h2></div>
              <table class="data-table">
                <thead><tr><th>Day</th><th>Success</th><th>Fail</th><th>Rate Limited</th><th>Total</th></tr></thead>
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
              <div class="section-head"><h2>Top endpoints</h2></div>
              <table class="data-table">
                <thead><tr><th>Endpoint</th><th>Success</th><th>Fail</th><th>Total</th></tr></thead>
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

        <section v-else class="page-grid">
          <div class="metrics-grid">
            <article class="metric-card glass-panel"><span>Free Daily Quota</span><strong>{{ planCatalog?.free_plan.daily_quota ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Paid Tiers</span><strong>{{ planCatalog?.tiers.length ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Lowest Paid</span><strong>CNY {{ planCatalog?.tiers[0]?.monthly_price ?? 0 }}</strong></article>
            <article class="metric-card glass-panel"><span>Highest Paid</span><strong>CNY {{ planCatalog?.tiers[planCatalog.tiers.length - 1]?.monthly_price ?? 0 }}</strong></article>
          </div>

          <section class="glass-panel panel-section">
            <div class="section-head">
              <h2>Plan catalog</h2>
              <div class="lookup-row compact">
                <button class="btn btn-outline" :disabled="busy" @click="refreshPlans">Refresh</button>
                <button class="btn btn-ghost" :disabled="busy" @click="handleAddPlanTier">Add Tier</button>
                <button class="btn btn-primary" :disabled="busy || !planCatalog" @click="handleSavePlans">Save</button>
              </div>
            </div>

            <div v-if="planCatalog" class="form-grid">
              <label>
                <span>Free Plan Daily Quota</span>
                <input v-model.number="planCatalog.free_plan.daily_quota" class="input-field" type="number" min="1" />
              </label>
            </div>

            <table class="data-table" v-if="planCatalog">
              <thead><tr><th>Plan ID</th><th>Monthly Price</th><th>Daily Quota</th><th>Actions</th></tr></thead>
              <tbody>
                <tr v-for="(tier, index) in planCatalog.tiers" :key="tier.id || `draft-${index}`">
                  <td><strong>{{ tier.id || 'Generated on save' }}</strong></td>
                  <td><input v-model.number="tier.monthly_price" class="input-field slim-input" type="number" min="0.01" step="0.01" /></td>
                  <td><input v-model.number="tier.daily_quota" class="input-field slim-input" type="number" min="1" step="1" /></td>
                  <td class="actions-cell">
                    <button class="btn btn-danger" :disabled="busy || planCatalog.tiers.length <= 1" @click="handleRemovePlanTier(index)">Remove</button>
                  </td>
                </tr>
              </tbody>
            </table>
          </section>
        </section>
      </main>
    </template>
  </div>
</template>
