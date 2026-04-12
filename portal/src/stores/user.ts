import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'
import { t, translateApiError } from '../i18n'

const API_PREFIX = '/v2'
const AUTH_API_PREFIX = `${API_PREFIX}/auth`
const ACCOUNT_API_PREFIX = `${API_PREFIX}/api/account`
const BILLING_API_PREFIX = `${API_PREFIX}/api/billing`
const MIGRATION_STORAGE_KEY = 'vrcls.portal.migration'

type NullableString = string | null | undefined

type AuthUser = {
  id?: string | number | null
  user_uuid?: string | null
  userUuid?: string | null
  username?: string | null
  email?: string | null
  display_name?: string | null
  displayName?: string | null
  email_verified?: boolean | null
  emailVerified?: boolean | null
  is_admin?: boolean | null
  identity_version?: number | null
  identityVersion?: number | null
  migration_required?: boolean | null
  migrationRequired?: boolean | null
  legacy_username?: string | null
  legacyUsername?: string | null
  plan_name?: string | null
  planName?: string | null
  plan_price?: number | null
  planPrice?: number | null
  account_status?: string | null
  accountStatus?: string | null
}

type AuthTokenResponse = {
  access_token: string
  refresh_token: string
  token_type: string
  user: AuthUser
}

type AuthVerificationChallengeResponse = {
  message: string
  verification_token: string
  expires_in: number
}

type PortalProfileResponse = {
  id?: string | number | null
  userUuid?: string | null
  user_uuid?: string | null
  username?: string | null
  legacyUsername?: string | null
  legacy_username?: string | null
  displayName?: string | null
  display_name?: string | null
  email?: string | null
  emailVerified?: boolean | null
  email_verified?: boolean | null
  identityVersion?: number | null
  identity_version?: number | null
  migrationRequired?: boolean | null
  migration_required?: boolean | null
  planName?: string | null
  plan_name?: string | null
  planPrice?: number | null
  plan_price?: number | null
  accountStatus?: string | null
  account_status?: string | null
  roles?: string[] | null
  createdAt?: string | null
  lastLoginAt?: string | null
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
  pricingMode: string
  defaultMonthlyPrice: number | null
  minMonthlyPrice: number | null
  maxMonthlyPrice: number | null
  step: number | null
  targetResourceQuotaPreview: number | null
  priceOptions: number[]
  quotaPreviewByPrice: Record<string, number>
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
  userUuid?: string | null
  username: string
  isRegistered: boolean
  isExpired: boolean
  status: string
  subscriptionMode: string
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
  cancellable: boolean
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
  expiresIn: number
}

export type LegacyMigrationSession = {
  migrationRequired: boolean
  emailBound: boolean
  emailVerified: boolean
  legacyUserId: number | null
  legacyUsername: string
  migrationToken: string
}

type LegacyLoginResponse = LegacyMigrationSession

type LegacyLinkResponse = {
  message: string
  mergedOrders?: number | null
  mergedAddons?: number | null
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
  user_uuid?: string | null
  username: string
  display_name?: string | null
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
  userUuid: string
  shortUserUuid: string
  legacyUsername: string
  name: string
  email: string
  emailVerified: boolean
  planName: string
  planPrice: number
  accountStatus: string
  identityVersion: number
  migrationRequired: boolean
  roles: string[]
  createdAt: string
  lastLoginAt: string
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

function readMigrationSession(): LegacyMigrationSession | null {
  if (typeof window === 'undefined') {
    return null
  }

  const rawValue = window.sessionStorage.getItem(MIGRATION_STORAGE_KEY)
  if (!rawValue) {
    return null
  }

  try {
    return JSON.parse(rawValue) as LegacyMigrationSession
  } catch {
    window.sessionStorage.removeItem(MIGRATION_STORAGE_KEY)
    return null
  }
}

function writeMigrationSession(session: LegacyMigrationSession | null) {
  if (typeof window === 'undefined') {
    return
  }

  if (!session) {
    window.sessionStorage.removeItem(MIGRATION_STORAGE_KEY)
    return
  }

  window.sessionStorage.setItem(MIGRATION_STORAGE_KEY, JSON.stringify(session))
}

function pickFirstString(...values: NullableString[]): string {
  for (const value of values) {
    if (typeof value === 'string' && value.trim()) {
      return value.trim()
    }
  }
  return ''
}

function shortUuid(value: string): string {
  if (!value) {
    return ''
  }
  return value.split('-')[0] || value.slice(0, 8)
}

function resolveUserUuid(payload: { user_uuid?: NullableString; userUuid?: NullableString; id?: string | number | null }): string {
  return pickFirstString(
    payload.user_uuid,
    payload.userUuid,
    typeof payload.id === 'number' || typeof payload.id === 'string' ? String(payload.id) : '',
  )
}

function resolveDisplayName(payload: {
  display_name?: NullableString
  displayName?: NullableString
  username?: NullableString
  user_uuid?: NullableString
  userUuid?: NullableString
}): string {
  const fallbackUuid = resolveUserUuid(payload)
  return (
    pickFirstString(payload.display_name, payload.displayName, payload.username) ||
    (fallbackUuid ? `用户 ${shortUuid(fallbackUuid)}` : 'VoiceLinkVR User')
  )
}

function toProfile(
  payload: AuthUser | PortalProfileResponse,
  currentProfile?: UserProfile | null,
): UserProfile {
  const userUuid = resolveUserUuid(payload)
  const legacyUsername = pickFirstString(payload.legacy_username, payload.legacyUsername, payload.username)

  return {
    userUuid,
    shortUserUuid: shortUuid(userUuid),
    legacyUsername,
    name: resolveDisplayName(payload),
    email: pickFirstString(payload.email),
    emailVerified: Boolean(payload.email_verified ?? payload.emailVerified),
    planName: pickFirstString(payload.plan_name, payload.planName, currentProfile?.planName),
    planPrice: Number(payload.plan_price ?? payload.planPrice ?? currentProfile?.planPrice ?? 0),
    accountStatus: pickFirstString(payload.account_status, payload.accountStatus, currentProfile?.accountStatus, 'active'),
    identityVersion: Number(payload.identity_version ?? payload.identityVersion ?? currentProfile?.identityVersion ?? 2),
    migrationRequired: Boolean(payload.migration_required ?? payload.migrationRequired ?? currentProfile?.migrationRequired),
    roles: Array.isArray((payload as PortalProfileResponse).roles)
      ? ((payload as PortalProfileResponse).roles ?? []).map((value) => String(value))
      : currentProfile?.roles ?? [],
    createdAt: pickFirstString((payload as PortalProfileResponse).createdAt, currentProfile?.createdAt),
    lastLoginAt: pickFirstString((payload as PortalProfileResponse).lastLoginAt, currentProfile?.lastLoginAt),
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
  const migrationSession = ref<LegacyMigrationSession | null>(readMigrationSession())

  watch(migrationSession, (value) => {
    writeMigrationSession(value)
  })

  const isAuthenticated = computed(() => Boolean(profile.value))
  const isMigrationPending = computed(() => Boolean(profile.value?.migrationRequired || migrationSession.value))
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

  function clearMigrationSession() {
    migrationSession.value = null
  }

  function resetState() {
    profile.value = null
    entitlements.value = null
    billingGuide.value = null
    orders.value = []
    usage.value = null
    authError.value = ''
  }

  function applyAuthUser(authUser: AuthUser | PortalProfileResponse) {
    profile.value = toProfile(authUser, profile.value)
    if (!profile.value.migrationRequired) {
      clearMigrationSession()
    }
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
    usage.value = await requestJson<AccountUsage>(`${ACCOUNT_API_PREFIX}/usage${suffix}`)
  }

  async function loadPortalData() {
    if (!profile.value) {
      return
    }

    const [portalProfile, nextEntitlements, nextGuide, nextOrders] = await Promise.all([
      requestJson<PortalProfileResponse>(`${ACCOUNT_API_PREFIX}/profile`),
      requestJson<PortalEntitlementResponse>(`${ACCOUNT_API_PREFIX}/entitlements`),
      requestJson<PortalBillingGuide>(`${BILLING_API_PREFIX}/guide`),
      requestJson<PortalOrder[]>(`${ACCOUNT_API_PREFIX}/orders`),
    ])

    profile.value = toProfile(portalProfile, profile.value)
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
      const response = await requestJson<AuthUser | { user: AuthUser }>(`${AUTH_API_PREFIX}/me`)
      const user = 'user' in response ? response.user : response
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
      const response = await requestJson<AuthTokenResponse>(`${AUTH_API_PREFIX}/login`, {
        method: 'POST',
        body: JSON.stringify({ email, password }),
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

  async function legacyLogin(legacyUsername: string, legacyPassword: string) {
    loading.value = true
    authError.value = ''
    try {
      const response = await requestJson<LegacyLoginResponse>(`${ACCOUNT_API_PREFIX}/legacy-login`, {
        method: 'POST',
        body: JSON.stringify({ legacyUsername, legacyPassword }),
      })
      migrationSession.value = response
      initialized.value = true
      return response
    } catch (error) {
      authError.value = localizeError(error, 'errors.login')
      throw error
    } finally {
      loading.value = false
    }
  }

  async function sendRegisterCode(email: string) {
    return await requestJson<AuthVerificationChallengeResponse>(`${AUTH_API_PREFIX}/register/send-code`, {
      method: 'POST',
      body: JSON.stringify({ email }),
    })
  }

  async function register(
    email: string,
    password: string,
    verificationToken: string,
    verificationCode: string,
  ) {
    loading.value = true
    authError.value = ''
    try {
      const response = await requestJson<AuthTokenResponse>(`${AUTH_API_PREFIX}/register`, {
        method: 'POST',
        body: JSON.stringify({
          email,
          password,
          verification_token: verificationToken,
          verification_code: verificationCode,
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
      await requestJson<{ message: string }>(`${AUTH_API_PREFIX}/logout`, { method: 'POST' })
    } catch {
      // Ignore logout transport errors and clear local state anyway.
    } finally {
      resetState()
      clearMigrationSession()
      initialized.value = true
    }
  }

  async function refreshData() {
    if (!profile.value) {
      return
    }
    await loadPortalData()
  }

  async function updateProfile(payload: { displayName?: string }) {
    const response = await requestJson<PortalProfileResponse>(`${ACCOUNT_API_PREFIX}/profile`, {
      method: 'PATCH',
      body: JSON.stringify(payload),
    })
    profile.value = toProfile(response, profile.value)
    return profile.value
  }

  function openPaymentUrl(payUrl: string) {
    if (!payUrl) {
      return
    }
    window.open(payUrl, '_blank', 'noopener,noreferrer')
  }

  async function beginCheckout(payload: CheckoutRequest) {
    const response = await requestJson<PortalOrderIntentResponse>(`${BILLING_API_PREFIX}/order-intents`, {
      method: 'POST',
      body: JSON.stringify(payload),
    })
    if (response.payUrl) {
      openPaymentUrl(response.payUrl)
    }
    await refreshData()
    return response
  }

  async function cancelOrder(orderId: string) {
    const response = await requestJson<PortalActionResponse>(`${BILLING_API_PREFIX}/orders/${encodeURIComponent(orderId)}/cancel`, {
      method: 'POST',
    })
    await refreshData()
    return response
  }

  async function sendChangePasswordCode() {
    return await requestJson<PortalActionResponse>(`${ACCOUNT_API_PREFIX}/change-password/send-code`, {
      method: 'POST',
    })
  }

  async function changePasswordWithCode(
    verificationToken: string,
    verificationCode: string,
    newPassword: string,
  ) {
    return await requestJson<PortalActionResponse>(`${ACCOUNT_API_PREFIX}/change-password`, {
      method: 'POST',
      body: JSON.stringify({ verificationToken, verificationCode, newPassword }),
    })
  }

  async function bindEmail(email: string) {
    const response = await requestJson<PortalActionResponse>(`${ACCOUNT_API_PREFIX}/bind-email`, {
      method: 'POST',
      body: JSON.stringify({
        email,
        migrationToken: migrationSession.value?.migrationToken ?? undefined,
      }),
    })
    if (profile.value) {
      await refreshData()
    }
    return response
  }

  async function verifyEmail(token: string, code: string) {
    const response = await requestJson<PortalActionResponse>(`${ACCOUNT_API_PREFIX}/verify-email`, {
      method: 'POST',
      body: JSON.stringify({
        token,
        code,
        migrationToken: migrationSession.value?.migrationToken ?? undefined,
      }),
    })
    await bootstrap(true)
    return response
  }

  async function forgotPassword(email: string) {
    return await requestJson<PortalActionResponse>(`${ACCOUNT_API_PREFIX}/forgot-password`, {
      method: 'POST',
      body: JSON.stringify({ email }),
    })
  }

  async function resetPassword(token: string, code: string, password: string) {
    return await requestJson<PortalActionResponse>(`${ACCOUNT_API_PREFIX}/reset-password`, {
      method: 'POST',
      body: JSON.stringify({ token, code, password }),
    })
  }

  async function legacyLink(legacyUsername: string, legacyPassword: string) {
    const response = await requestJson<LegacyLinkResponse>(`${ACCOUNT_API_PREFIX}/legacy-link`, {
      method: 'POST',
      body: JSON.stringify({ legacyUsername, legacyPassword }),
    })
    await refreshData()
    return response
  }

  async function loadDeviceAuthorization(userCode: string) {
    return await requestJson<DeviceAuthorizationRequest>(
      `${AUTH_API_PREFIX}/device/requests/${encodeURIComponent(userCode)}`,
    )
  }

  async function decideDeviceAuthorization(userCode: string, decision: 'approve' | 'deny') {
    return await requestJson<DeviceAuthorizationDecisionResponse>(`${AUTH_API_PREFIX}/device/authorize`, {
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
    cancelOrder,
    changePasswordWithCode,
    clearMigrationSession,
    currentPlanPrice,
    dailyLimit,
    decideDeviceAuthorization,
    entitlements,
    forgotPassword,
    initialized,
    isAuthenticated,
    isMigrationPending,
    legacyLink,
    legacyLogin,
    loadDeviceAuthorization,
    loading,
    login,
    logout,
    migrationSession,
    openPaymentUrl,
    orders,
    pendingOrders,
    profile,
    quotaUsagePercent,
    refreshData,
    register,
    remainingQuota,
    resetPassword,
    sendChangePasswordCode,
    sendRegisterCode,
    requestSuccess,
    requestSuccessPercent,
    requestTotal,
    shouldUpgrade,
    usedQuota,
    updateProfile,
    usage,
    loadUsage,
    verifyEmail,
  }
})
