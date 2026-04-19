<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { formatCurrency, translateApiError } from '../../i18n'
import { useUserStore } from '../../stores/user'
import type { PortalBillingGuideOption, PortalBillingAddonGuide, PortalBillingGuide } from '../../stores/user'
import { formatPlanLabel } from '../../utils/portal'

const userStore = useUserStore()

const selectedAction = ref('renew')
const selectedMonths = ref(1)
const selectedAmount = ref(5)
const loading = ref(false)

const actionLabels: Record<string, string> = {
  renew: '续费',
  upgrade: '升级',
  addon: '资源包',
}

const EPSILON = 0.0001

const actions = computed(() => {
  const guide = userStore.billingGuide
  if (!guide) return []
  return ['renew', 'upgrade', 'addon'].map((action) => ({
    id: action,
    label: actionLabels[action] || action,
    allowed: guide.allowedActions.includes(action),
  }))
})

const selectedActionLabel = computed(() => actionLabels[selectedAction.value] || selectedAction.value)

const currentOptionConfig = computed(() => {
  if (selectedAction.value === 'addon') return userStore.billingGuide?.addonOption as PortalBillingAddonGuide | undefined
  return userStore.billingGuide?.[`${selectedAction.value}Option` as keyof PortalBillingGuide] as PortalBillingGuideOption | undefined
})

const currentDailyQuota = computed(() => userStore.billingGuide?.displayResourceQuota ?? 0)
const currentAddonBalance = computed(() => userStore.billingGuide?.addonResourceBalance ?? 0)

const amountStep = computed(() => {
  if (!currentOptionConfig.value) return 5
  return ('step' in currentOptionConfig.value && currentOptionConfig.value.step) ? currentOptionConfig.value.step : 5
})

const minAmount = computed(() => {
  if (!currentOptionConfig.value) return 5
  if ('minMonthlyPrice' in currentOptionConfig.value && currentOptionConfig.value.minMonthlyPrice) return currentOptionConfig.value.minMonthlyPrice
  if ('minAmount' in currentOptionConfig.value && currentOptionConfig.value.minAmount) return currentOptionConfig.value.minAmount
  return amountStep.value
})

function clampMonths(rawValue: number) {
  if (!Number.isFinite(rawValue)) return 1
  return Math.max(1, Math.min(12, Math.trunc(rawValue)))
}

function allowedPrices() {
  const config = currentOptionConfig.value as PortalBillingGuideOption | undefined
  if (!config || !('priceOptions' in config) || !Array.isArray(config.priceOptions)) {
    return [] as number[]
  }
  return config.priceOptions
    .map((value) => Number(value))
    .filter((value) => Number.isFinite(value))
    .sort((left, right) => left - right)
}

function snapToNearestPrice(rawValue: number, prices: number[]) {
  if (!prices.length) {
    return rawValue
  }
  return prices.reduce((best, current) => {
    const currentDiff = Math.abs(current - rawValue)
    const bestDiff = Math.abs(best - rawValue)
    if (currentDiff < bestDiff) return current
    if (Math.abs(currentDiff - bestDiff) <= EPSILON && current > best) return current
    return best
  })
}

function normalizeAmount(rawValue: number) {
  if (!Number.isFinite(rawValue)) {
    return minAmount.value
  }

  const min = minAmount.value
  const config = currentOptionConfig.value
  let normalized = Math.max(rawValue, min)

  if (selectedAction.value === 'addon') {
    const addonConfig = config as PortalBillingAddonGuide | undefined
    const step = amountStep.value || 5
    const base = addonConfig?.minAmount ?? min
    const snapped = Math.round((normalized - base) / step) * step + base
    const max = addonConfig?.maxAmount ?? snapped
    return Math.min(max, Math.max(base, Number(snapped.toFixed(2))))
  }

  const prices = allowedPrices()
  if (prices.length > 0) {
    return snapToNearestPrice(normalized, prices)
  }

  const optionConfig = config as PortalBillingGuideOption | undefined
  const step = amountStep.value || 5
  const base = optionConfig?.minMonthlyPrice ?? min
  const snapped = Math.round((normalized - base) / step) * step + base
  const max = optionConfig?.maxMonthlyPrice ?? snapped
  return Math.min(max, Math.max(base, Number(snapped.toFixed(2))))
}

function isSelectedAmountValid() {
  const rawValue = Number(selectedAmount.value)
  if (!Number.isFinite(rawValue)) return false

  if (selectedAction.value === 'addon') {
    const addonConfig = currentOptionConfig.value as PortalBillingAddonGuide | undefined
    const step = amountStep.value || 5
    const base = addonConfig?.minAmount ?? minAmount.value
    if (rawValue + EPSILON < base) return false
    if (addonConfig?.maxAmount != null && rawValue > addonConfig.maxAmount + EPSILON) return false
    const offset = (rawValue - base) / step
    return Math.abs(offset - Math.round(offset)) <= EPSILON
  }

  const prices = allowedPrices()
  if (prices.length > 0) {
    return prices.some((price) => Math.abs(price - rawValue) <= EPSILON)
  }

  const optionConfig = currentOptionConfig.value as PortalBillingGuideOption | undefined
  const step = amountStep.value || 5
  const base = optionConfig?.minMonthlyPrice ?? minAmount.value
  if (rawValue + EPSILON < base) return false
  if (optionConfig?.maxMonthlyPrice != null && rawValue > optionConfig.maxMonthlyPrice + EPSILON) return false
  const offset = (rawValue - base) / step
  return Math.abs(offset - Math.round(offset)) <= EPSILON
}

function normalizeMonthsInput() {
  selectedMonths.value = clampMonths(Number(selectedMonths.value))
}

function normalizeAmountInput() {
  selectedAmount.value = normalizeAmount(Number(selectedAmount.value))
}

const expectedResourceInfo = computed(() => {
  const guide = userStore.billingGuide
  if (!guide || !currentOptionConfig.value) return ''

  if (selectedAction.value === 'addon') {
    const opt = currentOptionConfig.value as PortalBillingAddonGuide
    const steps = Math.floor(selectedAmount.value / (opt.step || 5))
    const res = steps * (opt.resourceIncrement || 0)
    const nextBalance = currentAddonBalance.value + res
    return `预计新增 ${res} 单位资源包，余额将变为 ${nextBalance} 单位`
  } else {
    const opt = currentOptionConfig.value as PortalBillingGuideOption
    const previewQuota =
      opt.quotaPreviewByPrice?.[String(selectedAmount.value)] ??
      opt.quotaPreviewByPrice?.[selectedAmount.value.toFixed(2)]
    if (previewQuota) {
      if (selectedAction.value === 'upgrade') {
        return `日额度将从 ${currentDailyQuota.value} 提升到 ${previewQuota} 单位/日`
      }
      return `日额度将保持在 ${previewQuota} 单位/日，并延长 ${selectedMonths.value} 个月`
    }
    if (guide.subscriptionMode === 'ladder') {
      const steps = Math.floor(selectedAmount.value / (guide.priceStep || 5))
      const totalQuota = steps * (guide.resourceIncrementPerStep || 0)
      return `预计生效后日额度为 ${totalQuota} 单位/日`
    }
    return '请确认最终金额后提交'
  }
})

const payableAmount = computed(() => {
  if (selectedAction.value === 'addon') {
    return Number(selectedAmount.value || 0)
  }
  const months = clampMonths(Number(selectedMonths.value))
  const targetPrice = Number(selectedAmount.value || 0)
  const currentPrice = Number(userStore.currentPlanPrice || 0)
  if (selectedAction.value === 'upgrade') {
    return Math.max(0, targetPrice - currentPrice) * months
  }
  return targetPrice * months
})

const payableAmountLabel = computed(() => formatCurrency(payableAmount.value || 0))

const paymentBreakdown = computed(() => {
  if (selectedAction.value === 'addon') {
    return `资源包按单次购买计费，本次将支付 ${payableAmountLabel.value}`
  }

  if (selectedAction.value === 'upgrade') {
    return `升级按补差计费：(${formatCurrency(selectedAmount.value)} - ${formatCurrency(userStore.currentPlanPrice || 0)}) × ${selectedMonths.value} 个月`
  }

  return `${formatCurrency(selectedAmount.value)} × ${selectedMonths.value} 个月`
})

const targetTierLabel = computed(() => {
  if (selectedAction.value === 'addon') {
    return `${formatCurrency(selectedAmount.value)} 资源包`
  }
  return formatPlanLabel(selectedAmount.value)
})

const canCheckout = computed(() => {
  const monthsValid = selectedAction.value === 'addon' || (Number.isInteger(Number(selectedMonths.value)) && Number(selectedMonths.value) >= 1 && Number(selectedMonths.value) <= 12)
  return !loading.value && !!userStore.billingGuide && monthsValid && isSelectedAmountValid()
})

watch(
  () => userStore.billingGuide,
  (guide) => {
    if (!guide) return
    const firstAllowed = ['renew', 'upgrade', 'addon'].find((item) => guide.allowedActions.includes(item))
    if (firstAllowed) {
      selectedAction.value = firstAllowed
    }
    selectedAmount.value = Math.max(userStore.currentPlanPrice || minAmount.value, minAmount.value)
  },
  { immediate: true },
)

watch(selectedAction, (action) => {
  selectedAmount.value = Math.max(
    action === 'addon' ? (userStore.billingGuide?.addonOption?.defaultAmount ?? minAmount.value) : (userStore.currentPlanPrice || minAmount.value),
    minAmount.value
  )
  normalizeAmountInput()
})

function incMonths() {
  if (selectedMonths.value < 12) selectedMonths.value++
}

function decMonths() {
  if (selectedMonths.value > 1) selectedMonths.value--
}

function incAmount() {
  selectedAmount.value += amountStep.value
}

function decAmount() {
  if (selectedAmount.value - amountStep.value >= minAmount.value) {
    selectedAmount.value -= amountStep.value
  } else {
    selectedAmount.value = minAmount.value
  }
}

async function checkout() {
  const originalMonths = Number(selectedMonths.value)
  const originalAmount = Number(selectedAmount.value)
  normalizeMonthsInput()
  normalizeAmountInput()
  const normalizedMonths = Number(selectedMonths.value)
  const normalizedAmount = Number(selectedAmount.value)

  if (
    (selectedAction.value !== 'addon' && originalMonths !== normalizedMonths) ||
    Math.abs(originalAmount - normalizedAmount) > EPSILON
  ) {
    return
  }

  if (!canCheckout.value) {
    return
  }
  loading.value = true
  try {
    await userStore.beginCheckout({
      action: selectedAction.value,
      months: selectedAction.value === 'addon' ? 1 : selectedMonths.value,
      targetMonthlyPrice: selectedAction.value === 'addon' ? undefined : selectedAmount.value,
      addonAmount: selectedAction.value === 'addon' ? selectedAmount.value : undefined,
      guideToken: userStore.billingGuide?.guideToken,
    })
  } catch (error) {
    window.alert(error instanceof Error ? translateApiError({ message: error.message }) : '操作失败，请稍后重试。')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="billing-page">
    <div class="split-grid">
      <div class="panel flex-1">
        <div class="panel-header">
          <h2>配置订阅</h2>
          <p>选择操作类型、时长与档位。</p>
        </div>

        <div class="panel-body form-grid-single">
          <div class="input-group">
            <label>操作类型</label>
            <div class="radio-group">
              <label
                v-for="act in actions"
                :key="act.id"
                class="radio-card"
                :class="{ active: selectedAction === act.id, disabled: !act.allowed }"
              >
                <input v-model="selectedAction" type="radio" :value="act.id" :disabled="!act.allowed" class="hidden" />
                <span>{{ act.label }}</span>
              </label>
            </div>
          </div>

          <div class="input-group" v-if="selectedAction !== 'addon'">
            <label>时长（个月）</label>
            <div class="number-stepper">
              <button type="button" class="stepper-btn" @click="decMonths" :disabled="selectedMonths <= 1">−</button>
              <input v-model.number="selectedMonths" type="number" class="stepper-input" min="1" max="12" step="1" @blur="normalizeMonthsInput" />
              <button type="button" class="stepper-btn" @click="incMonths" :disabled="selectedMonths >= 12">+</button>
            </div>
          </div>

          <div class="input-group">
            <label>档位金额（元）</label>
            <div class="number-stepper">
              <button type="button" class="stepper-btn" @click="decAmount" :disabled="selectedAmount <= minAmount">−</button>
              <input v-model.number="selectedAmount" type="number" class="stepper-input" :step="amountStep" :min="minAmount" @blur="normalizeAmountInput" />
              <button type="button" class="stepper-btn" @click="incAmount">+</button>
            </div>
          </div>
        </div>

        <div class="panel-footer">
          <button class="btn btn-primary" @click="checkout" :disabled="!canCheckout">
            {{ loading ? '处理中...' : '去结算' }}
          </button>
        </div>
      </div>

      <div class="panel summary-panel">
        <div class="panel-header">
          <h2>订单预览</h2>
          <p>确认您的当前信息与变更预期。</p>
        </div>
        <div class="panel-body">
          <div class="desc-list-local">
            <div class="desc-row-local">
              <span class="desc-term-local">当前套餐</span>
              <span class="desc-detail-local"><strong>{{ formatPlanLabel(userStore.currentPlanPrice) }}</strong></span>
            </div>
            <div class="desc-row-local" v-if="userStore.billingGuide?.displayResourceQuota">
              <span class="desc-term-local">当前额度</span>
              <span class="desc-detail-local"><strong>{{ userStore.billingGuide.displayResourceQuota }}</strong> 单位/日</span>
            </div>
            <div class="desc-row-local" v-if="userStore.billingGuide?.addonResourceBalance">
              <span class="desc-term-local">加油包余额</span>
              <span class="desc-detail-local"><strong>{{ userStore.billingGuide.addonResourceBalance }}</strong> 单位</span>
            </div>

            <div class="divider"></div>

            <div class="desc-row-local">
              <span class="desc-term-local">所选操作</span>
              <span class="desc-detail-local highlight">{{ selectedActionLabel }}</span>
            </div>
            <div class="desc-row-local" v-if="selectedAction !== 'addon'">
              <span class="desc-term-local">购买时长</span>
              <span class="desc-detail-local"><strong>{{ selectedMonths }}</strong> 个月</span>
            </div>
            <div class="desc-row-local">
              <span class="desc-term-local">目标档位</span>
              <span class="desc-detail-local"><strong>{{ targetTierLabel }}</strong></span>
            </div>
            <div class="desc-row-local">
              <span class="desc-term-local">应付款金额</span>
              <span class="desc-detail-local"><strong>{{ payableAmountLabel }}</strong></span>
            </div>
            <div class="desc-row-local">
              <span class="desc-term-local">计费说明</span>
              <span class="desc-detail-local">{{ paymentBreakdown }}</span>
            </div>
            <div class="desc-row-local">
              <span class="desc-term-local">变更预期</span>
              <span class="desc-detail-local accent-text">{{ expectedResourceInfo }}</span>
            </div>
            <div class="payment-warning">
              <strong>支付提醒</strong>
              <p>跳转到爱发电后，请不要修改任何金额、月份或档位，否则订单校验会失败，无法自动激活。</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.billing-page { display: flex; flex-direction: column; gap: 32px; }

.split-grid {
  display: grid;
  grid-template-columns: 1fr 380px;
  gap: 24px;
  align-items: start;
}

@media (max-width: 900px) {
  .split-grid {
    grid-template-columns: 1fr;
  }
}

.flex-1 {
  flex: 1;
}

.form-grid-single {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.radio-group { display: grid; grid-template-columns: repeat(auto-fit, minmax(100px, 1fr)); gap: 12px; }
.radio-card { border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px; text-align: center; cursor: pointer; transition: all 0.2s; color: var(--text-soft); font-size: 0.875rem; font-weight: 500; background: var(--bg-base); }
.radio-card:hover:not(.disabled) { border-color: var(--border-hover); background: var(--bg-hover); color: var(--text-main); }
.radio-card.active { border-color: var(--primary); color: var(--primary-invert); background: var(--primary); }
.radio-card.disabled { opacity: 0.4; cursor: not-allowed; }
.hidden { display: none; }

.number-stepper {
  display: flex;
  align-items: center;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--bg-base);
  height: 40px;
  overflow: hidden;
  transition: border-color 0.2s;
}

.number-stepper:focus-within {
  border-color: var(--accent);
}

.stepper-btn {
  background: transparent;
  border: none;
  color: var(--text-main);
  width: 40px;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  font-size: 1.25rem;
  transition: background 0.2s, color 0.2s;
}

.stepper-btn:hover:not(:disabled) {
  background: var(--bg-hover);
}

.stepper-btn:disabled {
  opacity: 0.3;
  cursor: not-allowed;
}

.stepper-input {
  flex: 1;
  width: 100%;
  height: 100%;
  border: none;
  background: transparent;
  color: var(--text-main);
  text-align: center;
  font-size: 0.875rem;
  font-family: var(--font-mono);
  outline: none;
  -moz-appearance: textfield;
}

.stepper-input::-webkit-outer-spin-button,
.stepper-input::-webkit-inner-spin-button {
  -webkit-appearance: none;
  margin: 0;
}

.divider {
  height: 1px;
  background: var(--border);
  margin: 16px 0;
}

.desc-list-local {
  display: flex;
  flex-direction: column;
}

.desc-row-local {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  padding: 12px 0;
}

.desc-row-local:first-child {
  padding-top: 0;
}

.desc-row-local:last-child {
  padding-bottom: 0;
}

.desc-term-local {
  color: var(--text-soft);
  font-size: 0.875rem;
  white-space: nowrap;
}

.desc-detail-local {
  color: var(--text-main);
  font-size: 0.875rem;
  text-align: right;
  max-width: 65%;
  word-break: break-word;
  line-height: 1.5;
}

.desc-detail-local strong {
  font-weight: 600;
}

.highlight {
  color: var(--primary);
  font-weight: 600;
}

.accent-text {
  color: var(--accent);
  font-weight: 500;
}

.payment-warning {
  margin-top: 18px;
  padding: 14px 16px;
  border-radius: 14px;
  border: 1px solid rgba(255, 184, 0, 0.22);
  background: rgba(255, 184, 0, 0.08);
}

.payment-warning strong {
  display: block;
  margin-bottom: 8px;
  color: var(--text-main);
}

.payment-warning p {
  margin: 0;
  color: var(--text-soft);
  line-height: 1.7;
}

.summary-panel .panel-body {
  padding-top: 24px;
}
</style>
