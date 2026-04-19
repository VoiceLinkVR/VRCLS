<script setup lang="ts">
import { formatCurrency, formatDateTime } from '../../i18n'
import { useUserStore } from '../../stores/user'
import { translateOrderStatus, translateActionLabel } from '../../utils/portal'

const userStore = useUserStore()

function getStatusColor(status: string) {
  const s = (status || '').trim().toLowerCase()
  if (s === 'paid') return 'text-success'
  if (s === 'pending') return 'text-warning'
  if (s === 'cancelled' || s === 'expired') return 'text-muted'
  return ''
}
</script>

<template>
  <div class="panel">
    <div class="panel-header">
      <h2>账单历史</h2>
      <p>查看历史订单与支付记录。</p>
    </div>
    <div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>订单号</th>
            <th>业务类型</th>
            <th>金额</th>
            <th>状态</th>
            <th>时间</th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="userStore.orders.length === 0">
            <td colspan="5" style="text-align: center; color: var(--text-muted)">暂无订单记录。</td>
          </tr>
          <tr v-for="order in userStore.orders" :key="order.id">
            <td class="mono">{{ order.id }}</td>
            <td>{{ translateActionLabel(order.action) }}</td>
            <td>{{ formatCurrency(order.amount, order.currency) }}</td>
            <td :class="getStatusColor(order.status)">{{ translateOrderStatus(order.status) }}</td>
            <td>{{ formatDateTime(order.createdAt) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
