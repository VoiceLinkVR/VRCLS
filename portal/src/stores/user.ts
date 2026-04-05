import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { t, translateApiError } from '../i18n'

type AuthUser = {
  id: string
  username: string
  email: string
  display_name: string
  email_verified: boolean
  is_admin: boolean
}

type AuthTokenResponse = {
  access_token: string
  refresh_token: string
  token_type: string
  user: AuthUser
}

type PortalProfileResponse = {
  id: string
  username: string
  displayName: string
  email: string
  emailVerified: boolean
  planName: string
  planPrice: number
  accountStatus: string
}

type PortalEntitlementResponse = {
  planName: string
  planPrice: number
  remainingQuota: number | null
  renewsAt: string | null
  features: string[]
}

export type PortalBillingGuideOption = {
  allowed: boolean
  readonly: boolean
  defaultMonthlyPrice: number | null
  minMonthlyPrice: number | null
  maxMonthlyPrice: number | null
  step: number | null
  targetResourceQuotaPreview: number | null
}

export type PortalBillingAddonGuide = {
  allowed: boolean
  readonly: boolean
  defaultAmount: number | null
  minAmount: number | null
  maxAmount: number | null
  step: number | null
  resourceIncrement: number | null
  defaultResourceQuotaPreview: number | null
  requiresActiveSubscription: boolean
}

export type PortalBillingGuide = {
  username: string
  isRegistered: boolean
  isExpired: boolean
  status: string
  priceStep: number
  resourceIncrementPerStep: number
  displayPlanName: string
  displayMonthlyPrice: number
  displayResourceQuota: number
  displayResourceSummary: string
  anchorReason: string
  allowedActions: string[]
  renewOption: PortalBillingGuideOption
  upgradeOption: PortalBillingGuideOption
  downgradeOption: PortalBillingGuideOption
  addonOption: PortalBillingAddonGuide
  addonEnabled: boolean
  addonResourceBalance: number
  addonResourceTotal: number
  addonExpirationUtc: string | null
  guideToken: string
}

export type PortalOrder = {
  id: string
  title: string
  action: string
  amount: number
  currency: string
  status: string
  createdAt: string | null
  payUrl: string
}

type PortalOrderIntentResponse = {
  id: string
  status: string
  payUrl: string
}

type PortalActionResponse = {
  message: string
  nextUrl: string
  token: string
}

export type DeviceAuthorizationRequest = {
  user_code: string
  client_name: string
  requested_scope: string
  status: string
  expires_at: string
  username: string
  display_name: string
}

type DeviceAuthorizationDecisionResponse = {
  message: string
  status: string
}

export type AccountUsageDailyStat = {
  date: string
  daily_success_count: number
  daily_failed_count: number
  daily_rate_limited_count: number
  daily_resource_cost: number
  daily_subscription_resource_cost: number
  daily_addon_resource_cost: number
  daily_deducted_request_count: number
  daily_total_count: number
  daily_limit: number | null
  remaining_daily: number | null
}

export type AccountUsage = {
  username: string
  is_test_user: boolean
  is_active: boolean
  is_expired: boolean
  expiration_utc: string | null
  remaining_seconds: number | null
  limit_rule: string
  daily_limit: number | null
  remaining_daily: number | null
  daily_resource_limit: number | null
  remaining_resource: number | null
  resource_used: number
  subscription_resource_used: number
  addon_resource_used: number
  resource_deducted_request_count: number
  daily_success_count: number
  daily_failed_count: number
  daily_rate_limited_count: number
  daily_total_count: number
  today_daily_resource_used: number
  today_remaining_daily: number | null
  addon_resource_balance: number
  addon_resource_total: number
  addon_expiration_utc: string | null
  query_start_date: string
  query_end_date: string
  daily_stats: AccountUsageDailyStat[]
}

export type UserProfile = {
  id: string
  username: string
  name: string
  email: string
  emailVerified: boolean
  planName: string
  planPrice: number
  accountStatus: string
}

export type CheckoutRequest = {
  planId?: string
  action?: string
  months?: number
  targetMonthlyPrice?: number
  addonAmount?: number
  guideToken?: string
}

class ApiError extends Error {
  status: number
  code?: string
  payload?: unknown

  constructor(message: string, status: number, code?: string, payload?: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.payload = payload
  }
}

async function requestJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers ?? {})
  const body = init.body

  if (body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'include',
  })

  const rawText = await response.text()
  let data: unknown = null

  if (rawText) {
    try {
      data = JSON.parse(rawText)
    } catch {
      data = { message: rawText }
    }
  }

  if (!response.ok) {
    const payload = (data ?? {}) as Record<string, unknown>
    const detail = payload.detail as Record<string, unknown> | string | undefined
    const message =
      typeof detail === 'string'
        ? detail
        : detail && typeof detail.message === 'string'
          ? detail.message
          : typeof payload.message === 'string'
            ? payload.message
            : `Request failed: ${response.status}`
    const code =
      detail && typeof detail === 'object' && typeof detail.code === 'string'
        ? detail.code
        : typeof payload.code === 'string'
          ? payload.code
          : undefined
    throw new ApiError(message, response.status, code, data)
  }

  return data as T
}

function toProfile(authUser: AuthUser, currentProfile?: UserProfile | null): UserProfile {
  return {
    id: authUser.id,
    username: authUser.username,
    name: authUser.display_name || authUser.username,
    email: authUser.email,
    emailVerified: authUser.email_verified,
    planName: currentProfile?.planName ?? '',
    planPrice: currentProfile?.planPrice ?? 0,
    accountStatus: currentProfile?.accountStatus ?? 'active',
  }
}

function localizeError(error: unknown, fallbackKey: string): string {
  if (error instanceof ApiError) {
    return translateApiError(
      {
        status: error.status,
        code: error.code,
        message: error.message,
      },
      fallbackKey,
    )
  }
  if (error instanceof Error && error.message) {
    return error.message
  }
  return t(fallbackKey)
}

export const useUserStore = defineStore('user', () => {
  const initialized = ref(false)
  const loading = ref(false)
  const authError = ref('')
  const profile = ref<UserProfile | null>(null)
  const entitlements = ref<PortalEntitlementResponse | null>(null)
  const billingGuide = ref<PortalBillingGuide | null>(null)
  const orders = ref<PortalOrder[]>([])
  const usage = ref<AccountUsage | null>(null)

  const isAuthenticated = computed(() => Boolean(profile.value))
  const remainingQuota = computed(() =>
    Math.max(
      usage.value?.today_remaining_daily ??
        usage.value?.remaining_daily ??
        entitlements.value?.remainingQuota ??
        0,
      0,
    ),
  )
  const dailyLimit = computed(() => Math.max(usage.value?.daily_limit ?? remainingQuota.value, 0))
  const usedQuota = computed(() => Math.max(dailyLimit.value - remainingQuota.value, 0))
  const quotaUsagePercent = computed(() =>
    dailyLimit.value > 0 ? Math.min((usedQuota.value / dailyLimit.value) * 100, 100) : 0,
  )
  const requestSuccess = computed(() => usage.value?.daily_success_count ?? 0)
  const requestTotal = computed(() => Math.max(usage.value?.daily_total_count ?? requestSuccess.value, 0))
  const requestSuccessPercent = computed(() =>
    requestTotal.value > 0 ? Math.min((requestSuccess.value / requestTotal.value) * 100, 100) : 0,
  )
  const shouldUpgrade = computed(() => dailyLimit.value > 0 && remainingQuota.value / dailyLimit.value <= 0.2)
  const currentPlanPrice = computed(
    () => profile.value?.planPrice || billingGuide.value?.displayMonthlyPrice || entitlements.value?.planPrice || 0,
  )
  const pendingOrders = computed(() =>
    orders.value.filter((order) => order.status === 'pending' && Boolean(order.payUrl)),
  )

  function resetState() {
    profile.value = null
    entitlements.value = null
    billingGuide.value = null
    orders.value = []
    usage.value = null
    authError.value = ''
  }

  function applyAuthUser(authUser: AuthUser) {
    profile.value = toProfile(authUser, profile.value)
  }

  async function loadUsage(startDate?: string, endDate?: string) {
    const search = new URLSearchParams()
    if (startDate) {
      search.set('start_date', startDate)
    }
    if (endDate) {
      search.set('end_date', endDate)
    }
    const suffix = search.size > 0 ? `?${search.toString()}` : ''
    usage.value = await requestJson<AccountUsage>(`/api/account/usage${suffix}`)
  }

  async function loadPortalData() {
    if (!profile.value) {
      return
    }

    const [portalProfile, nextEntitlements, nextGuide, nextOrders] = await Promise.all([
      requestJson<PortalProfileResponse>('/api/account/profile'),
      requestJson<PortalEntitlementResponse>('/api/account/entitlements'),
      requestJson<PortalBillingGuide>('/api/billing/guide'),
      requestJson<PortalOrder[]>('/api/account/orders'),
    ])

    profile.value = {
      id: portalProfile.id,
      username: portalProfile.username,
      name: portalProfile.displayName || portalProfile.username,
      email: portalProfile.email,
      emailVerified: portalProfile.emailVerified,
      planName: portalProfile.planName,
      planPrice: portalProfile.planPrice,
      accountStatus: portalProfile.accountStatus,
    }

    entitlements.value = nextEntitlements
    billingGuide.value = nextGuide
    orders.value = nextOrders

    try {
      await loadUsage()
    } catch {
      usage.value = null
    }
  }

  async function bootstrap(force = false) {
    if (initialized.value && !force) {
      return
    }

    try {
      const user = await requestJson<AuthUser>('/auth/me')
      applyAuthUser(user)
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 401) {
        authError.value = localizeError(error, 'errors.init')
      }
      resetState()
      initialized.value = true
      return
    }

    try {
      await loadPortalData()
    } catch (error) {
      authError.value = localizeError(error, 'errors.loadPortal')
    } finally {
      initialized.value = true
    }
  }

  async function login(email: string, password: string) {
    loading.value = true
    authError.value = ''
    try {
      const response = await requestJson<AuthTokenResponse>('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ login: email, password }),
      })
      applyAuthUser(response.user)
      await loadPortalData()
      initialized.value = true
    } catch (error) {
      authError.value = localizeError(error, 'errors.login')
      throw error
    } finally {
      loading.value = false
    }
  }

  async function register(email: string, password: string, displayName: string) {
    loading.value = true
    authError.value = ''
    try {
      const response = await requestJson<AuthTokenResponse>('/auth/register', {
        method: 'POST',
        body: JSON.stringify({
          email,
          password,
          display_name: displayName,
        }),
      })
      applyAuthUser(response.user)
      await loadPortalData()
      initialized.value = true
    } catch (error) {
      authError.value = localizeError(error, 'errors.register')
      throw error
    } finally {
      loading.value = false
    }
  }

  async function logout() {
    try {
      await requestJson<{ message: string }>('/auth/logout', { method: 'POST' })
    } catch {
      // Ignore logout transport errors and clear local state anyway.
    } finally {
      resetState()
      initialized.value = true
    }
  }

  async function refreshData() {
    if (!profile.value) {
      return
    }
    await loadPortalData()
  }

  function openPaymentUrl(payUrl: string) {
    if (!payUrl) {
      return
    }
    window.open(payUrl, '_blank', 'noopener,noreferrer')
  }

  async function beginCheckout(payload: CheckoutRequest) {
    const response = await requestJson<PortalOrderIntentResponse>('/api/billing/order-intents', {
      method: 'POST',
      body: JSON.stringify(payload),
    })
    if (response.payUrl) {
      openPaymentUrl(response.payUrl)
    }
    await refreshData()
    return response
  }

  async function changePassword(currentPassword: string, newPassword: string) {
    return await requestJson<PortalActionResponse>('/api/account/change-password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    })
  }

  async function bindEmail(email: string) {
    const response = await requestJson<PortalActionResponse>('/api/account/bind-email', {
      method: 'POST',
      body: JSON.stringify({ email }),
    })
    await refreshData()
    return response
  }

  async function verifyEmail(token: string) {
    const response = await requestJson<PortalActionResponse>('/api/account/verify-email', {
      method: 'POST',
      body: JSON.stringify({ token }),
    })
    await bootstrap(true)
    return response
  }

  async function forgotPassword(email: string) {
    return await requestJson<PortalActionResponse>('/api/account/forgot-password', {
      method: 'POST',
      body: JSON.stringify({ email }),
    })
  }

  async function resetPassword(token: string, password: string) {
    return await requestJson<PortalActionResponse>('/api/account/reset-password', {
      method: 'POST',
      body: JSON.stringify({ token, password }),
    })
  }

  async function loadDeviceAuthorization(userCode: string) {
    return await requestJson<DeviceAuthorizationRequest>(`/auth/device/requests/${encodeURIComponent(userCode)}`)
  }

  async function decideDeviceAuthorization(userCode: string, decision: 'approve' | 'deny') {
    return await requestJson<DeviceAuthorizationDecisionResponse>('/auth/device/authorize', {
      method: 'POST',
      body: JSON.stringify({ user_code: userCode, decision }),
    })
  }

  return {
    authError,
    beginCheckout,
    bindEmail,
    billingGuide,
    bootstrap,
    changePassword,
    currentPlanPrice,
    dailyLimit,
    decideDeviceAuthorization,
    entitlements,
    forgotPassword,
    initialized,
    isAuthenticated,
    loadDeviceAuthorization,
    loading,
    login,
    logout,
    openPaymentUrl,
    orders,
    pendingOrders,
    profile,
    quotaUsagePercent,
    refreshData,
    register,
    remainingQuota,
    resetPassword,
    requestSuccess,
    requestSuccessPercent,
    requestTotal,
    shouldUpgrade,
    usedQuota,
    usage,
    loadUsage,
    verifyEmail,
  }
})
