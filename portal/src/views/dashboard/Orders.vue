<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatCurrency, formatDateTime, translateApiError, useI18n } from '../../i18n'
import { useUserStore } from '../../stores/user'
import { translateActionLabel, translateOrderStatus } from '../../utils/portal'

const userStore = useUserStore()
const { t } = useI18n()
const cancelOrderId = ref('')
const cancelError = ref('')

const statusClassMap = computed(() => ({
  paid: 'paid',
  pending: 'pending',
  expired: 'expired',
  cancelled: 'expired',
}))

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
  <div class="orders panel-grid">
    <section class="orders-hero glass-panel">
      <div>
        <p class="hero-tag">{{ t('orders.heroTag') }}</p>
        <h2>{{ t('orders.heroTitle') }}</h2>
        <p>
          {{ t('orders.heroBody') }}
        </p>
      </div>

      <router-link to="/billing" class="btn btn-primary">{{ t('common.openBilling') }}</router-link>
    </section>

    <section class="table-wrap glass-panel">
      <div class="table-head">
        <div>
          <h3>{{ t('orders.listTitle') }}</h3>
          <p>{{ t('orders.count', { count: userStore.orders.length }) }}</p>
        </div>
      </div>

      <div v-if="cancelError" class="cancel-error">{{ cancelError }}</div>

      <div v-if="userStore.orders.length === 0" class="empty-state">{{ t('orders.empty') }}</div>

      <div v-else class="table-scroll">
        <table class="order-table">
          <thead>
            <tr>
              <th>{{ t('orders.orderId') }}</th>
              <th>{{ t('orders.action') }}</th>
              <th>{{ t('orders.amount') }}</th>
              <th>{{ t('orders.channel') }}</th>
              <th>{{ t('orders.created') }}</th>
              <th>{{ t('orders.status') }}</th>
              <th>{{ t('common.openInNewTab') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="order in userStore.orders" :key="order.id">
              <td class="mono">{{ order.id }}</td>
              <td>{{ translateActionLabel(order.action) }}</td>
              <td>{{ formatCurrency(order.amount, order.currency) }}</td>
              <td>Afdian</td>
              <td>{{ formatDateTime(order.createdAt) }}</td>
              <td>
                <span class="status-pill" :class="statusClassMap[order.status as keyof typeof statusClassMap]">
                  {{ translateOrderStatus(order.status) }}
                </span>
              </td>
              <td>
                <div v-if="order.payUrl || order.cancellable" class="order-actions">
                  <button
                    v-if="order.payUrl"
                    type="button"
                    class="btn btn-outline mini-btn"
                    @click="userStore.openPaymentUrl(order.payUrl)"
                  >
                    {{ t('orders.continuePay') }}
                  </button>
                  <button
                    v-if="order.cancellable"
                    type="button"
                    class="btn btn-outline mini-btn danger-btn"
                    :disabled="cancelOrderId === order.id"
                    @click="cancelOrder(order.id)"
                  >
                    {{ cancelOrderId === order.id ? t('common.loading') : t('common.cancel') }}
                  </button>
                </div>
                <span v-else>-</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>

<style scoped>
.orders-hero {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 20px;
  padding: 28px;
}

.hero-tag {
  color: var(--primary);
  font-size: 0.82rem;
  letter-spacing: 0.18em;
  text-transform: uppercase;
}

.orders-hero h2 {
  margin: 12px 0 14px;
  font-size: clamp(1.9rem, 4vw, 2.7rem);
}

.orders-hero p:last-child {
  max-width: 760px;
  color: var(--text-muted);
  line-height: 1.8;
}

.table-wrap {
  padding: 24px;
}

.table-head {
  margin-bottom: 18px;
}

.table-head p {
  color: var(--text-muted);
  margin-top: 6px;
}

.cancel-error {
  margin-bottom: 14px;
  color: #ffb4b4;
}

.empty-state {
  padding: 32px;
  border-radius: 18px;
  color: var(--text-muted);
  background: rgba(255, 255, 255, 0.03);
}

.table-scroll {
  overflow-x: auto;
}

.order-table {
  width: 100%;
  border-collapse: collapse;
  min-width: 920px;
}

.order-table th,
.order-table td {
  padding: 16px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  text-align: left;
  white-space: nowrap;
}

.order-table th {
  color: var(--text-muted);
  font-size: 0.85rem;
  font-weight: 700;
}

.order-table tbody tr:hover {
  background: rgba(255, 255, 255, 0.03);
}

.mono {
  font-family: "Cascadia Code", "SFMono-Regular", Consolas, monospace;
  color: var(--text-soft);
}

.status-pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 78px;
  padding: 8px 10px;
  border-radius: 999px;
  font-size: 0.82rem;
  font-weight: 700;
}

.status-pill.paid {
  color: var(--primary);
  background: rgba(0, 229, 255, 0.12);
}

.status-pill.pending {
  color: #ffd36b;
  background: rgba(251, 191, 36, 0.12);
}

.status-pill.expired {
  color: #ffb4b4;
  background: rgba(255, 94, 94, 0.12);
}

.mini-btn {
  min-height: 36px;
  padding: 0 14px;
}

.order-actions {
  display: flex;
  gap: 10px;
}

.danger-btn {
  border-color: rgba(255, 94, 94, 0.35);
  color: #ffb4b4;
}

@media (max-width: 760px) {
  .orders-hero {
    flex-direction: column;
    align-items: flex-start;
  }

  .order-actions {
    flex-direction: column;
  }
}
</style>
