<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { formatCurrency, formatNumber, translateApiError, useI18n } from '../../i18n'
import { type PortalBillingAddonGuide, type PortalBillingGuideOption, useUserStore } from '../../stores/user'
import { formatPlanLabel, translateActionLabel } from '../../utils/portal'

const userStore = useUserStore()
const { t } = useI18n()

const selectedAction = ref('renew')
const selectedMonths = ref(1)
const selectedAmount = ref(0)
const checkoutError = ref('')
const checkoutStatus = ref('')
const checkoutLoading = ref(false)

const monthOptions = Array.from({ length: 12 }, (_, index) => index + 1)

const actionCards = computed(() => {
  const guide = userStore.billingGuide
  const actions = ['renew', 'upgrade', 'downgrade', 'addon']
  return actions.map((action) => {
    const allowed = guide?.allowedActions.includes(action) ?? false
    return {
      action,
      allowed,
      title: translateActionLabel(action),
      body: t(`billing.actionDescription${action[0].toUpperCase()}${action.slice(1)}`),
    }
  })
})

const currentOption = computed(() => {
  const guide = userStore.billingGuide
  if (!guide) {
    return null
  }
  if (selectedAction.value === 'renew') {
    return guide.renewOption
  }
  if (selectedAction.value === 'upgrade') {
    return guide.upgradeOption
  }
  if (selectedAction.value === 'downgrade') {
    return guide.downgradeOption
  }
  if (selectedAction.value === 'addon') {
    return guide.addonOption
  }
  return null
})

const isAddon = computed(() => selectedAction.value === 'addon')

const currentStep = computed(() => {
  const option = currentOption.value
  if (!option) {
    return 0
  }
  return option.step ?? 0
})

const canAdjustAmount = computed(() => {
  const option = currentOption.value
  return Boolean(option && !option.readonly && (option.step ?? 0) > 0)
})

const totalAmount = computed(() => {
  if (isAddon.value) {
    return selectedAmount.value
  }
  if (selectedAction.value === 'upgrade') {
    return Math.max(selectedAmount.value - userStore.currentPlanPrice, 0) * selectedMonths.value
  }
  return selectedAmount.value * selectedMonths.value
})

const quotaPreviewValue = computed(() => {
  const guide = userStore.billingGuide
  const option = currentOption.value
  if (!guide || !option) {
    return 0
  }
  if (isAddon.value) {
    const step = option.step ?? 0
    const increment = 'resourceIncrement' in option ? option.resourceIncrement ?? 0 : 0
    if (step <= 0) {
      return 0
    }
    return Math.max(Math.floor(selectedAmount.value / step), 0) * increment
  }
  const step = option.step ?? guide.priceStep
  if (step <= 0) {
    return 0
  }
  return Math.max(Math.floor(selectedAmount.value / step), 1) * guide.resourceIncrementPerStep
})

const quotaPreviewText = computed(() =>
  isAddon.value ? `+${formatNumber(quotaPreviewValue.value)}` : `${formatNumber(quotaPreviewValue.value)}/day`,
)

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
    detail: isAddon.value ? t('common.addon') : `${selectedMonths.value} ${t('common.month')}`,
  },
  {
    title: t('billing.expectedQuota'),
    value: quotaPreviewText.value,
    detail:
      selectedAction.value === 'addon'
        ? t('billing.addonBalance')
        : t('billing.expectedResourceAfter', { quota: formatNumber(quotaPreviewValue.value) }),
  },
])

function asAddonOption() {
  return currentOption.value as PortalBillingAddonGuide | null
}

function asSubscriptionOption() {
  return currentOption.value as PortalBillingGuideOption | null
}

function clampAmount(rawValue: number) {
  const option = currentOption.value
  if (!option) {
    return 0
  }
  const step = option.step ?? 1
  const min = isAddon.value
    ? ('minAmount' in option ? option.minAmount : null)
    : ('minMonthlyPrice' in option ? option.minMonthlyPrice : null)
  const max = isAddon.value
    ? ('maxAmount' in option ? option.maxAmount : null)
    : ('maxMonthlyPrice' in option ? option.maxMonthlyPrice : null)

  let next = rawValue
  if (min !== null && min !== undefined) {
    next = Math.max(next, min)
  }
  if (max !== null && max !== undefined) {
    next = Math.min(next, max)
  }
  if (step > 0) {
    next = Math.floor(next / step) * step
  }
  if (min !== null && min !== undefined) {
    next = Math.max(next, min)
  }
  return Number(next.toFixed(2))
}

function applyActionDefaults() {
  const guide = userStore.billingGuide
  if (!guide) {
    return
  }

  const allowedActions = guide.allowedActions
  if (!allowedActions.includes(selectedAction.value)) {
    selectedAction.value = allowedActions[0] || 'renew'
  }

  const option = currentOption.value
  if (!option) {
    return
  }

  if (selectedAction.value === 'addon') {
    selectedMonths.value = 1
    const addonOption = asAddonOption()
    selectedAmount.value = clampAmount(
      addonOption?.defaultAmount ?? addonOption?.minAmount ?? guide.addonOption.step ?? 0,
    )
    return
  }

  const subscriptionOption = asSubscriptionOption()
  selectedAmount.value = clampAmount(
    subscriptionOption?.defaultMonthlyPrice ??
      subscriptionOption?.minMonthlyPrice ??
      guide.displayMonthlyPrice ??
      guide.priceStep,
  )
}

function increaseAmount() {
  if (!canAdjustAmount.value) {
    return
  }
  selectedAmount.value = clampAmount(selectedAmount.value + currentStep.value)
}

function decreaseAmount() {
  if (!canAdjustAmount.value) {
    return
  }
  selectedAmount.value = clampAmount(selectedAmount.value - currentStep.value)
}

watch(
  () => userStore.billingGuide,
  () => {
    applyActionDefaults()
  },
  { immediate: true },
)

watch(selectedAction, () => {
  checkoutError.value = ''
  checkoutStatus.value = ''
  applyActionDefaults()
})

watch(selectedMonths, () => {
  checkoutError.value = ''
  checkoutStatus.value = ''
})

async function createOrder() {
  if (!userStore.billingGuide) {
    return
  }

  checkoutLoading.value = true
  checkoutError.value = ''
  checkoutStatus.value = ''

  try {
    await userStore.beginCheckout({
      action: selectedAction.value,
      months: isAddon.value ? 1 : selectedMonths.value,
      targetMonthlyPrice: isAddon.value ? undefined : selectedAmount.value,
      addonAmount: isAddon.value ? selectedAmount.value : undefined,
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
            <strong>
              {{
                t('billing.ruleSummary', {
                  priceStep: formatNumber(userStore.billingGuide?.priceStep ?? 0),
                  resourceStep: formatNumber(userStore.billingGuide?.resourceIncrementPerStep ?? 0),
                })
              }}
            </strong>
          </div>
        </div>
      </article>

      <article class="guide-card glass-panel">
        <h3>{{ t('billing.warningTitle') }}</h3>
        <p class="guide-warning">{{ t('billing.warningBody') }}</p>
        <p class="guide-warning">{{ t('common.directPayHint') }}</p>
        <p class="guide-hint" v-if="userStore.billingGuide?.anchorReason === 'unregistered'">
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

        <div v-if="!isAddon" class="field-group">
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
          <span class="field-label">{{ t('billing.amountLabel') }}</span>
          <div class="amount-box">
            <button type="button" class="amount-btn" :disabled="!canAdjustAmount" @click="decreaseAmount">-</button>
            <div class="amount-value">
              <small>{{ t('common.amountPerMonth') }}</small>
              <strong>{{ formatCurrency(selectedAmount) }}</strong>
            </div>
            <button type="button" class="amount-btn" :disabled="!canAdjustAmount" @click="increaseAmount">+</button>
          </div>
        </div>

        <p class="helper-line">{{ t('billing.expectedResourceAfter', { quota: formatNumber(quotaPreviewValue) }) }}</p>

        <div class="action-footer">
          <p v-if="checkoutError" class="checkout-error">{{ checkoutError }}</p>
          <p v-else-if="checkoutStatus" class="checkout-status">{{ checkoutStatus }}</p>
          <button type="button" class="btn btn-primary w-full" :disabled="checkoutLoading" @click="createOrder">
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
        <router-link to="/app/orders">{{ t('billing.goOrders') }}</router-link>
      </div>

      <div v-if="userStore.pendingOrders.length === 0" class="pending-empty">
        {{ t('billing.pendingEmpty') }}
      </div>

      <div v-else class="pending-list">
        <div v-for="order in userStore.pendingOrders" :key="order.id" class="pending-item">
          <div>
            <strong>{{ translateActionLabel(order.action) }}</strong>
            <span>{{ formatCurrency(order.amount, order.currency) }}</span>
          </div>
          <button type="button" class="btn btn-outline" @click="userStore.openPaymentUrl(order.payUrl)">
            {{ t('common.continuePay') }}
          </button>
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
.field-label,
.amount-value small {
  display: block;
  margin-bottom: 8px;
  color: var(--text-muted);
}

.guide-warning {
  color: var(--text-muted);
  line-height: 1.8;
}

.guide-warning + .guide-warning {
  margin-top: 10px;
}

.guide-hint {
  margin-top: 16px;
  color: var(--primary);
}

.action-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
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

.month-chip {
  min-height: 42px;
  padding: 0 14px;
  border-radius: 999px;
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

.amount-box {
  display: grid;
  grid-template-columns: 58px 1fr 58px;
  gap: 14px;
  align-items: center;
}

.amount-btn {
  min-height: 68px;
  border-radius: 18px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.03);
  color: var(--text-main);
  font-size: 1.5rem;
  cursor: pointer;
}

.amount-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.amount-value {
  padding: 18px 20px;
  border-radius: 22px;
  background:
    radial-gradient(circle at top, rgba(0, 229, 255, 0.08), transparent 50%),
    rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  text-align: center;
}

.amount-value strong {
  font-size: clamp(2rem, 4vw, 3rem);
  letter-spacing: -0.04em;
}

.helper-line {
  margin-top: 18px;
  color: var(--text-muted);
  line-height: 1.8;
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

@media (max-width: 1080px) {
  .hero,
  .guide-grid,
  .payment-grid,
  .action-grid {
    grid-template-columns: 1fr;
  }

  .hero {
    flex-direction: column;
    align-items: flex-start;
  }
}

@media (max-width: 820px) {
  .month-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .amount-box {
    grid-template-columns: 48px 1fr 48px;
  }

  .pending-item {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
