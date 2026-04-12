<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { formatCurrency, formatNumber, translateApiError, useI18n } from '../../i18n'
import { type PortalBillingGuideOption, useUserStore } from '../../stores/user'
import { formatPlanLabel, translateActionLabel } from '../../utils/portal'

const userStore = useUserStore()
const { t } = useI18n()
const PRICE_EPSILON = 0.0001

const selectedAction = ref('renew')
const selectedMonths = ref(1)
const selectedAmount = ref(0)
const checkoutError = ref('')
const checkoutStatus = ref('')
const checkoutLoading = ref(false)
const cancelOrderId = ref('')
const cancelError = ref('')

const monthOptions = Array.from({ length: 12 }, (_, index) => index + 1)

const actionCards = computed(() => {
  const guide = userStore.billingGuide
  if (guide?.subscriptionMode === 'ladder') {
    return [
      {
        action: 'renew',
        allowed: guide.allowedActions.some((action) => ['renew', 'upgrade'].includes(action)),
        title: t('billing.actionLabelAdjust'),
        body: t('billing.actionDescriptionAdjust'),
      },
      {
        action: 'addon',
        allowed: guide.allowedActions.includes('addon'),
        title: translateActionLabel('addon'),
        body: t('billing.actionDescriptionAddon'),
      },
    ]
  }
  const orderedActions = ['renew', 'upgrade', 'addon']
  return orderedActions
    .filter((action) => guide?.allowedActions.includes(action) || action === 'renew' || action === 'upgrade')
    .map((action) => ({
      action,
      allowed: guide?.allowedActions.includes(action) ?? false,
      title: translateActionLabel(action),
      body: t(`billing.actionDescription${action[0].toUpperCase()}${action.slice(1)}`),
    }))
})

const isAddonAction = computed(() => selectedAction.value === 'addon')

function mergeLadderOptions(options: Array<PortalBillingGuideOption | null | undefined>): PortalBillingGuideOption | null {
  const allowedOptions = options.filter((option): option is PortalBillingGuideOption => Boolean(option?.allowed))
  if (allowedOptions.length === 0) {
    return null
  }
  const minValues = allowedOptions
    .map((option) => option.minMonthlyPrice ?? option.defaultMonthlyPrice)
    .filter((value): value is number => typeof value === 'number')
  const maxValues = allowedOptions
    .map((option) => option.maxMonthlyPrice ?? option.defaultMonthlyPrice)
    .filter((value): value is number => typeof value === 'number')
  const mergedQuotaPreviewByPrice = Object.assign({}, ...allowedOptions.map((option) => option.quotaPreviewByPrice ?? {}))
  const currentPrice = Number((userStore.currentPlanPrice || 0).toFixed(2))
  const defaultMonthlyPrice =
    currentPrice > 0
      ? currentPrice
      : (allowedOptions.find((option) => typeof option.defaultMonthlyPrice === 'number')?.defaultMonthlyPrice ?? null)

  return {
    allowed: true,
    readonly: minValues.length === 0 || maxValues.length === 0 || Math.abs(Math.max(...maxValues) - Math.min(...minValues)) <= PRICE_EPSILON,
    pricingMode: 'ladder',
    defaultMonthlyPrice,
    minMonthlyPrice: minValues.length > 0 ? Math.min(...minValues) : defaultMonthlyPrice,
    maxMonthlyPrice: maxValues.length > 0 ? Math.max(...maxValues) : defaultMonthlyPrice,
    step: allowedOptions.find((option) => typeof option.step === 'number')?.step ?? userStore.billingGuide?.priceStep ?? null,
    targetResourceQuotaPreview:
      defaultMonthlyPrice !== null && defaultMonthlyPrice !== undefined
        ? mergedQuotaPreviewByPrice[defaultMonthlyPrice.toFixed(2)] ?? null
        : null,
    priceOptions: [],
    quotaPreviewByPrice: mergedQuotaPreviewByPrice,
  }
}

const currentOption = computed<PortalBillingGuideOption | null>(() => {
  const guide = userStore.billingGuide
  if (!guide || isAddonAction.value) {
    return null
  }
  if (guide.subscriptionMode === 'ladder') {
    return mergeLadderOptions([guide.renewOption, guide.upgradeOption])
  }
  return selectedAction.value === 'upgrade' ? guide.upgradeOption : guide.renewOption
})

const currentPricingMode = computed(() => currentOption.value?.pricingMode || userStore.billingGuide?.subscriptionMode || 'fixed_tiers')

const usesPriceStepper = computed(() => !isAddonAction.value && currentPricingMode.value === 'ladder')

const priceOptions = computed(() => {
  const option = currentOption.value
  if (!option) {
    return []
  }
  if (option.priceOptions.length > 0) {
    return option.priceOptions
  }
  if (option.defaultMonthlyPrice !== null && option.defaultMonthlyPrice !== undefined) {
    return [option.defaultMonthlyPrice]
  }
  return []
})

const addonOption = computed(() => userStore.billingGuide?.addonOption ?? null)

const selectedAmountKey = computed(() => selectedAmount.value.toFixed(2))

const quotaPreviewValue = computed(() => {
  if (isAddonAction.value) {
    const option = addonOption.value
    if (!option) {
      return 0
    }
    const step = option.step ?? 5
    const increment = option.resourceIncrement ?? option.defaultResourceQuotaPreview ?? 0
    const units = Math.max(1, Math.floor(selectedAmount.value / step))
    return units * increment
  }

  const option = currentOption.value
  if (!option) {
    return 0
  }
  return (
    option.quotaPreviewByPrice[selectedAmountKey.value] ??
    option.targetResourceQuotaPreview ??
    userStore.billingGuide?.displayResourceQuota ??
    0
  )
})

const checkoutAction = computed(() => {
  if (isAddonAction.value) {
    return 'addon'
  }
  if (currentPricingMode.value !== 'ladder') {
    return selectedAction.value
  }
  const currentPrice = Number((userStore.currentPlanPrice || 0).toFixed(2))
  if (selectedAmount.value > currentPrice + PRICE_EPSILON) {
    return 'upgrade'
  }
  return 'renew'
})

const totalAmount = computed(() => {
  if (isAddonAction.value) {
    return selectedAmount.value
  }
  if (checkoutAction.value === 'upgrade') {
    return Math.max(selectedAmount.value - userStore.currentPlanPrice, 0) * selectedMonths.value
  }
  return selectedAmount.value * selectedMonths.value
})

const amountHint = computed(() => {
  if (isAddonAction.value || usesPriceStepper.value) {
    return t('billing.warningBody')
  }
  return t('billing.amountLockedHint')
})

const guideSummary = computed(() => {
  const guide = userStore.billingGuide
  if (!guide) {
    return t('common.noData')
  }
  if (guide.subscriptionMode === 'ladder') {
    return t('billing.ruleSummary', {
      priceStep: formatNumber(guide.priceStep ?? 0),
      resourceStep: formatNumber(guide.resourceIncrementPerStep ?? 0),
    })
  }
  return t('billing.amountLockedHint')
})

const previewCards = computed(() => [
  {
    title: t('billing.currentPlan'),
    value: formatPlanLabel(userStore.currentPlanPrice),
    detail: userStore.billingGuide?.displayPlanName || t('common.noData'),
  },
  {
    title: t('billing.currentQuota'),
    value: userStore.billingGuide?.displayResourceSummary || `${formatNumber(userStore.remainingQuota)}/day`,
    detail: t('common.quotaRemaining'),
  },
  {
    title: t('common.totalAmount'),
    value: formatCurrency(totalAmount.value),
    detail: isAddonAction.value ? t('common.addon') : `${translateActionLabel(checkoutAction.value)} / ${selectedMonths.value} ${t('common.month')}`,
  },
  {
    title: isAddonAction.value ? t('billing.addonBalance') : t('billing.expectedQuota'),
    value: isAddonAction.value ? formatNumber(quotaPreviewValue.value) : `${formatNumber(quotaPreviewValue.value)}/day`,
    detail: t('billing.expectedResourceAfter', { quota: formatNumber(quotaPreviewValue.value) }),
  },
])

function resetStatus() {
  checkoutError.value = ''
  checkoutStatus.value = ''
}

function clampSubscriptionAmount(value: number, option: PortalBillingGuideOption): number {
  const min = option.minMonthlyPrice ?? option.defaultMonthlyPrice ?? 0
  const max = option.maxMonthlyPrice ?? min
  const step = option.step ?? userStore.billingGuide?.priceStep ?? 5
  const safeValue = Math.min(Math.max(value, min), max)
  const snapped = min + Math.round((safeValue - min) / step) * step
  return Number(Math.min(Math.max(snapped, min), max).toFixed(2))
}

function clampAddonAmount(value: number): number {
  const option = addonOption.value
  if (!option) {
    return 0
  }
  const min = option.minAmount ?? option.defaultAmount ?? option.step ?? 5
  const max = option.maxAmount ?? Number.POSITIVE_INFINITY
  const step = option.step ?? 5
  const safeValue = Math.min(Math.max(value, min), max)
  const snapped = min + Math.round((safeValue - min) / step) * step
  return Number(Math.min(Math.max(snapped, min), max).toFixed(2))
}

function applyActionDefaults() {
  const guide = userStore.billingGuide
  if (!guide) {
    return
  }

  if (guide.subscriptionMode === 'ladder' && selectedAction.value !== 'addon') {
    selectedAction.value = 'renew'
  }

  if (!guide.allowedActions.includes(selectedAction.value)) {
    selectedAction.value = guide.allowedActions[0] || 'renew'
  }

  if (selectedAction.value === 'addon') {
    selectedMonths.value = 1
    selectedAmount.value = clampAddonAmount(addonOption.value?.defaultAmount ?? addonOption.value?.minAmount ?? 0)
    return
  }

  const option = currentOption.value
  const choices = priceOptions.value
  if (!option) {
    selectedAmount.value = 0
    return
  }

  if (usesPriceStepper.value) {
    const defaultAmount = option.defaultMonthlyPrice ?? option.minMonthlyPrice ?? 0
    selectedAmount.value = clampSubscriptionAmount(defaultAmount, option)
    return
  }

  if (choices.length === 0) {
    selectedAmount.value = 0
    return
  }

  const defaultAmount = option.defaultMonthlyPrice ?? choices[0]
  selectedAmount.value = choices.includes(defaultAmount) ? defaultAmount : choices[0]
}

function adjustAmount(direction: number) {
  if (isAddonAction.value) {
    const step = addonOption.value?.step ?? 5
    selectedAmount.value = clampAddonAmount(selectedAmount.value + direction * step)
    return
  }

  const option = currentOption.value
  if (!option || !usesPriceStepper.value) {
    return
  }

  const step = option.step ?? userStore.billingGuide?.priceStep ?? 5
  selectedAmount.value = clampSubscriptionAmount(selectedAmount.value + direction * step, option)
}

const canDecreaseAmount = computed(() => {
  if (isAddonAction.value) {
    return selectedAmount.value > (addonOption.value?.minAmount ?? addonOption.value?.defaultAmount ?? 0)
  }
  const option = currentOption.value
  return Boolean(option && usesPriceStepper.value && selectedAmount.value > (option.minMonthlyPrice ?? 0))
})

const canIncreaseAmount = computed(() => {
  if (isAddonAction.value) {
    const max = addonOption.value?.maxAmount
    return max === null || max === undefined || selectedAmount.value < max
  }
  const option = currentOption.value
  if (!option || !usesPriceStepper.value) {
    return false
  }
  const max = option.maxMonthlyPrice
  return max !== null && max !== undefined ? selectedAmount.value < max : false
})

watch(
  () => userStore.billingGuide,
  () => {
    applyActionDefaults()
  },
  { immediate: true },
)

watch(selectedAction, () => {
  resetStatus()
  applyActionDefaults()
})

watch(selectedMonths, () => {
  resetStatus()
})

async function createOrder() {
  const hasSubscriptionSelection = currentOption.value && currentOption.value.allowed && selectedAmount.value > 0
  const hasAddonSelection = addonOption.value && addonOption.value.allowed && selectedAmount.value > 0
  if ((!isAddonAction.value && !hasSubscriptionSelection) || (isAddonAction.value && !hasAddonSelection) || !userStore.billingGuide) {
    return
  }

  checkoutLoading.value = true
  resetStatus()

  try {
    await userStore.beginCheckout({
      action: checkoutAction.value,
      months: isAddonAction.value ? 1 : selectedMonths.value,
      targetMonthlyPrice: isAddonAction.value ? undefined : selectedAmount.value,
      addonAmount: isAddonAction.value ? selectedAmount.value : undefined,
      guideToken: userStore.billingGuide.guideToken,
    })
    checkoutStatus.value = t('billing.opened')
  } catch (error) {
    checkoutError.value =
      error instanceof Error
        ? translateApiError({ message: error.message }, 'errors.generic')
        : t('errors.generic')
  } finally {
    checkoutLoading.value = false
  }
}

async function cancelOrder(orderId: string) {
  cancelOrderId.value = orderId
  cancelError.value = ''
  try {
    await userStore.cancelOrder(orderId)
  } catch (error) {
    cancelError.value =
      error instanceof Error
        ? translateApiError({ message: error.message }, 'errors.generic')
        : t('errors.generic')
  } finally {
    cancelOrderId.value = ''
  }
}
</script>

<template>
  <div class="sponsor panel-grid">
    <section class="hero glass-panel">
      <div>
        <p class="hero-tag">{{ t('billing.heroTag') }}</p>
        <h2 class="text-gradient">{{ t('billing.heroTitle') }}</h2>
        <p class="hero-copy">
          {{ t('billing.heroBody') }}
        </p>
      </div>

      <div class="hero-highlight glass-panel">
        <strong>{{ formatNumber(userStore.remainingQuota) }}</strong>
        <span>{{ t('common.quotaRemaining') }}</span>
      </div>
    </section>

    <section class="guide-grid">
      <article class="guide-card glass-panel">
        <h3>{{ t('billing.guideTitle') }}</h3>
        <div class="guide-list">
          <div>
            <span>{{ t('billing.currentPlan') }}</span>
            <strong>{{ formatPlanLabel(userStore.currentPlanPrice) }}</strong>
          </div>
          <div>
            <span>{{ t('billing.currentQuota') }}</span>
            <strong>{{ userStore.billingGuide?.displayResourceSummary || t('common.noData') }}</strong>
          </div>
          <div>
            <span>{{ t('common.quotaRule') }}</span>
            <strong>{{ guideSummary }}</strong>
          </div>
        </div>
      </article>

      <article class="guide-card glass-panel">
        <h3>{{ t('billing.warningTitle') }}</h3>
        <p class="guide-warning">{{ t('billing.warningBody') }}</p>
        <p class="guide-warning">{{ t('common.directPayHint') }}</p>
        <p class="guide-warning">{{ t('billing.calendarMonthHint') }}</p>
        <p class="guide-hint" v-if="userStore.billingGuide?.anchorReason !== 'current'">
          {{ t('billing.firstPurchaseHint') }}
        </p>
      </article>
    </section>

    <section class="action-section glass-panel">
      <div class="section-head">
        <h3>{{ t('billing.actionTitle') }}</h3>
      </div>
      <div class="action-grid">
        <button
          v-for="item in actionCards"
          :key="item.action"
          type="button"
          class="action-card"
          :class="{ active: selectedAction === item.action, disabled: !item.allowed }"
          :disabled="!item.allowed"
          @click="selectedAction = item.action"
        >
          <strong>{{ item.title }}</strong>
          <span>{{ item.body }}</span>
        </button>
      </div>
    </section>

    <section class="payment-grid">
      <article class="payment-card glass-panel">
        <div class="section-head">
          <h3>{{ t('billing.priceTitle') }}</h3>
        </div>

        <div v-if="!isAddonAction" class="field-group">
          <span class="field-label">{{ t('billing.monthsLabel') }}</span>
          <div class="month-grid">
            <button
              v-for="month in monthOptions"
              :key="month"
              type="button"
              class="month-chip"
              :class="{ active: selectedMonths === month }"
              @click="selectedMonths = month"
            >
              {{ month }} {{ t('common.month') }}
            </button>
          </div>
        </div>

        <div class="field-group">
          <span class="field-label">{{ isAddonAction ? t('common.addon') : t('billing.amountLabel') }}</span>

          <div v-if="usesPriceStepper || isAddonAction" class="stepper-box">
            <div class="stepper-row">
              <button type="button" class="stepper-btn" :disabled="!canDecreaseAmount" @click="adjustAmount(-1)">-</button>
              <div class="stepper-value">
                <strong>{{ formatCurrency(selectedAmount) }}</strong>
                <small>
                  {{
                    isAddonAction ? formatNumber(quotaPreviewValue) : `${formatNumber(quotaPreviewValue)}/day`
                  }}
                </small>
              </div>
              <button type="button" class="stepper-btn" :disabled="!canIncreaseAmount" @click="adjustAmount(1)">+</button>
            </div>
          </div>

          <div v-else class="price-grid">
            <button
              v-for="price in priceOptions"
              :key="price"
              type="button"
              class="price-chip"
              :class="{ active: selectedAmount === price }"
              @click="selectedAmount = price"
            >
              <strong>{{ formatCurrency(price) }}</strong>
              <small>{{ formatNumber(currentOption?.quotaPreviewByPrice[price.toFixed(2)] ?? quotaPreviewValue) }}/day</small>
            </button>
          </div>
        </div>

        <p class="helper-line">{{ amountHint }}</p>
        <p class="helper-line">{{ t('billing.expectedResourceAfter', { quota: formatNumber(quotaPreviewValue) }) }}</p>

        <div class="action-footer">
          <p v-if="checkoutError" class="checkout-error">{{ checkoutError }}</p>
          <p v-else-if="checkoutStatus" class="checkout-status">{{ checkoutStatus }}</p>
          <button type="button" class="btn btn-primary w-full" :disabled="checkoutLoading || selectedAmount <= 0" @click="createOrder">
            {{ checkoutLoading ? t('billing.creating') : t('common.goPay') }}
          </button>
        </div>
      </article>

      <article class="preview-card glass-panel">
        <div class="section-head">
          <h3>{{ t('billing.previewTitle') }}</h3>
        </div>
        <div class="preview-grid">
          <div v-for="card in previewCards" :key="card.title" class="preview-item">
            <span>{{ card.title }}</span>
            <strong>{{ card.value }}</strong>
            <small>{{ card.detail }}</small>
          </div>
        </div>
      </article>
    </section>

    <section class="pending-card glass-panel">
      <div class="section-head">
        <h3>{{ t('billing.pendingTitle') }}</h3>
        <router-link to="/orders">{{ t('billing.goOrders') }}</router-link>
      </div>

      <div v-if="cancelError" class="checkout-error">{{ cancelError }}</div>

      <div v-if="userStore.pendingOrders.length === 0" class="pending-empty">
        {{ t('billing.pendingEmpty') }}
      </div>

      <div v-else class="pending-list">
        <div v-for="order in userStore.pendingOrders" :key="order.id" class="pending-item">
          <div>
            <strong>{{ translateActionLabel(order.action) }}</strong>
            <span>{{ formatCurrency(order.amount, order.currency) }}</span>
          </div>
          <div class="pending-actions">
            <button type="button" class="btn btn-outline" @click="userStore.openPaymentUrl(order.payUrl)">
              {{ t('common.continuePay') }}
            </button>
            <button
              v-if="order.cancellable"
              type="button"
              class="btn btn-outline danger-btn"
              :disabled="cancelOrderId === order.id"
              @click="cancelOrder(order.id)"
            >
              {{ cancelOrderId === order.id ? t('common.loading') : t('common.cancel') }}
            </button>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.hero {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 24px;
  padding: 30px;
  background:
    radial-gradient(circle at left, rgba(0, 229, 255, 0.12), transparent 30%),
    rgba(14, 18, 28, 0.84);
}

.hero-tag {
  color: var(--primary);
  font-size: 0.82rem;
  letter-spacing: 0.18em;
  text-transform: uppercase;
}

.hero h2 {
  margin: 12px 0 14px;
  font-size: clamp(2rem, 4vw, 3rem);
}

.hero-copy {
  max-width: 720px;
  color: var(--text-muted);
  line-height: 1.9;
}

.hero-highlight {
  min-width: 220px;
  padding: 22px;
  text-align: center;
  background: rgba(255, 255, 255, 0.03);
}

.hero-highlight strong {
  display: block;
  font-size: clamp(2rem, 3vw, 2.8rem);
  color: var(--primary);
}

.hero-highlight span {
  color: var(--text-muted);
}

.guide-grid,
.payment-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 24px;
}

.guide-card,
.action-section,
.pending-card,
.payment-card,
.preview-card {
  padding: 28px;
}

.section-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  margin-bottom: 18px;
}

.section-head a {
  color: var(--primary);
}

.guide-list {
  display: grid;
  gap: 16px;
}

.guide-list div,
.preview-item {
  padding: 16px 18px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.guide-list span,
.preview-item span,
.field-label {
  display: block;
  margin-bottom: 8px;
  color: var(--text-muted);
}

.guide-warning,
.helper-line {
  color: var(--text-muted);
  line-height: 1.8;
}

.guide-warning + .guide-warning,
.helper-line + .helper-line {
  margin-top: 10px;
}

.guide-hint {
  margin-top: 16px;
  color: var(--primary);
}

.action-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 16px;
}

.action-card {
  padding: 20px;
  text-align: left;
  border-radius: 20px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.03);
  color: var(--text-main);
  cursor: pointer;
  transition:
    transform 0.22s ease,
    border-color 0.22s ease,
    background 0.22s ease;
}

.action-card strong {
  display: block;
  margin-bottom: 8px;
}

.action-card span {
  color: var(--text-muted);
  line-height: 1.7;
}

.action-card.active {
  border-color: var(--border-strong);
  background: linear-gradient(180deg, rgba(0, 229, 255, 0.12), rgba(157, 78, 221, 0.06));
  transform: translateY(-4px);
}

.action-card.disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.field-group + .field-group {
  margin-top: 22px;
}

.month-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}

.month-chip,
.price-chip,
.stepper-btn {
  min-height: 42px;
  padding: 0 14px;
  border-radius: 18px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.03);
  color: var(--text-soft);
  cursor: pointer;
}

.month-chip.active {
  color: #000;
  background: var(--primary);
  border-color: transparent;
}

.price-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

.price-chip {
  display: grid;
  gap: 6px;
  min-height: 84px;
  padding: 16px;
  text-align: left;
}

.price-chip strong {
  font-size: 1.15rem;
}

.price-chip small,
.stepper-value small {
  color: var(--text-muted);
}

.price-chip.active {
  border-color: var(--primary);
  background: rgba(0, 229, 255, 0.1);
}

.stepper-box {
  padding: 18px;
  border-radius: 20px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.stepper-row {
  display: grid;
  grid-template-columns: 56px minmax(0, 1fr) 56px;
  gap: 16px;
  align-items: center;
}

.stepper-btn {
  font-size: 1.4rem;
  font-weight: 700;
}

.stepper-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.stepper-value {
  text-align: center;
}

.stepper-value strong {
  display: block;
  font-size: 1.5rem;
  margin-bottom: 8px;
}

.preview-grid {
  display: grid;
  gap: 14px;
}

.preview-item strong {
  display: block;
  margin-bottom: 8px;
  font-size: 1.24rem;
}

.preview-item small {
  color: var(--text-muted);
  line-height: 1.7;
}

.action-footer {
  margin-top: 20px;
  display: grid;
  gap: 10px;
}

.checkout-error {
  color: #ffb4b4;
}

.checkout-status {
  color: var(--primary);
}

.pending-empty {
  color: var(--text-muted);
}

.pending-list {
  display: grid;
  gap: 14px;
}

.pending-item {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: center;
  padding: 18px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.pending-item strong {
  display: block;
  margin-bottom: 6px;
}

.pending-item span {
  color: var(--text-muted);
}

.pending-actions {
  display: flex;
  gap: 10px;
}

.danger-btn {
  border-color: rgba(255, 94, 94, 0.35);
  color: #ffb4b4;
}

@media (max-width: 1080px) {
  .hero,
  .guide-grid,
  .payment-grid {
    grid-template-columns: 1fr;
  }

  .hero {
    flex-direction: column;
    align-items: flex-start;
  }
}

@media (max-width: 820px) {
  .month-grid,
  .price-grid {
    grid-template-columns: 1fr 1fr;
  }

  .stepper-row {
    grid-template-columns: 48px minmax(0, 1fr) 48px;
  }

  .pending-item {
    flex-direction: column;
    align-items: stretch;
  }

  .pending-actions {
    flex-direction: column;
  }
}
</style>
