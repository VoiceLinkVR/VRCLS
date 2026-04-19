<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatDateTime, formatNumber, translateApiError } from '../../i18n'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const startDate = ref(userStore.usage?.query_start_date ?? '')
const endDate = ref(userStore.usage?.query_end_date ?? '')
const loading = ref(false)
const errorText = ref('')
const identityLabel = computed(() => {
  if (userStore.profile?.username) {
    return userStore.profile.username
  }
  if (userStore.profile?.shortUserUuid) {
    return `用户 ${userStore.profile.shortUserUuid}`
  }
  return '暂无数据'
})

const summaryCards = computed(() => [
  {
    title: '当前身份',
    value: identityLabel.value,
    detail: userStore.profile?.userUuid || '暂无数据',
  },
  {
    title: '区间成功响应',
    value: formatNumber(userStore.usage?.daily_success_count ?? 0),
    detail: `区间请求总量：${formatNumber(userStore.usage?.daily_total_count ?? 0)}`,
  },
  {
    title: '区间已耗额度',
    value: formatNumber(userStore.usage?.resource_used ?? 0),
    detail: `今日剩余额度：${formatNumber(userStore.usage?.today_remaining_daily ?? userStore.usage?.remaining_daily ?? 0)}`,
  },
  {
    title: '区间已耗附加资源',
    value: formatNumber(userStore.usage?.addon_resource_used ?? 0),
    detail: `附加资源余额：${formatNumber(userStore.usage?.addon_resource_balance ?? 0)}`,
  },
  {
    title: '服务到期时间',
    value: formatDateTime(userStore.usage?.expiration_utc),
    detail:
      userStore.usage?.query_start_date && userStore.usage?.query_end_date
        ? `${userStore.usage.query_start_date} ~ ${userStore.usage.query_end_date}`
        : '暂无数据',
  },
])

async function queryUsage() {
  loading.value = true
  errorText.value = ''

  try {
    await userStore.loadUsage(startDate.value || undefined, endDate.value || undefined)
    startDate.value = userStore.usage?.query_start_date ?? startDate.value
    endDate.value = userStore.usage?.query_end_date ?? endDate.value
  } catch (error) {
    errorText.value = error instanceof Error ? translateApiError({ message: error.message }) : '查询失败，请稍后重试。'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="usage panel-grid">
    <section class="usage-hero glass-panel">
      <div>
        <p class="hero-tag">用量概览</p>
        <h2>查看指定日期区间内的请求与资源消耗</h2>
        <p>这里展示的是你所选时间范围内的汇总结果，不是“今日实时值”。今日实时剩余额度请以概览页和侧边栏为准。</p>
        <div class="identity-inline">
          <span>{{ identityLabel }}</span>
          <code v-if="userStore.profile?.shortUserUuid">{{ userStore.profile.shortUserUuid }}</code>
        </div>
      </div>
    </section>

    <section class="filters glass-panel">
      <div class="filter-fields">
        <label class="filter-item">
          <span>开始日期</span>
          <input v-model="startDate" type="date" class="input-field" />
        </label>
        <label class="filter-item">
          <span>结束日期</span>
          <input v-model="endDate" type="date" class="input-field" />
        </label>
      </div>

      <div class="filter-actions">
        <p v-if="errorText" class="error-copy">{{ errorText }}</p>
        <button type="button" class="btn btn-primary" :disabled="loading" @click="queryUsage">
          {{ loading ? '查询中...' : '查询' }}
        </button>
      </div>
    </section>

    <section class="summary-grid">
      <article v-for="card in summaryCards" :key="card.title" class="summary-card glass-panel">
        <span>{{ card.title }}</span>
        <strong>{{ card.value }}</strong>
        <small>{{ card.detail }}</small>
      </article>
    </section>

    <section class="usage-table glass-panel">
      <div class="section-head">
        <h3>调用监控日志</h3>
      </div>

      <div v-if="!userStore.usage || userStore.usage.daily_stats.length === 0" class="empty-state">
        指定日期范围内无服务调用记录。
      </div>

      <div v-else class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>日期</th>
              <th>成功调用</th>
              <th>失败 / 拒绝</th>
              <th>限流拦截</th>
              <th>折算消耗</th>
              <th>日结余量</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in userStore.usage.daily_stats" :key="row.date">
              <td>{{ row.date }}</td>
              <td>{{ formatNumber(row.daily_success_count) }}</td>
              <td>{{ formatNumber(row.daily_failed_count) }}</td>
              <td>{{ formatNumber(row.daily_rate_limited_count) }}</td>
              <td>{{ formatNumber(row.daily_resource_cost) }}</td>
              <td>{{ row.remaining_daily === null ? '-' : formatNumber(row.remaining_daily) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>

<style scoped>
.usage-hero, .filters, .usage-table { padding: 28px; }
.hero-tag { color: var(--text-soft); font-size: 0.82rem; letter-spacing: 0.18em; text-transform: uppercase; }
.usage-hero h2 { margin: 12px 0 14px; font-size: clamp(1.9rem, 4vw, 2.7rem); }
.usage-hero p:last-child { max-width: 820px; color: var(--text-muted); line-height: 1.8; }
.identity-inline { margin-top: 16px; display: flex; flex-wrap: wrap; gap: 12px; align-items: center; }
.identity-inline span { color: var(--text-main); font-weight: 700; }
.identity-inline code { padding: 6px 10px; border-radius: 999px; background: rgba(240, 240, 244, 0.08); color: var(--text-main); }
.filters { display: flex; justify-content: space-between; gap: 20px; align-items: end; }
.filter-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 240px)); gap: 16px; }
.filter-item { display: grid; gap: 10px; }
.filter-item span { color: var(--text-muted); }
.filter-actions { display: grid; gap: 10px; }
.error-copy { color: var(--danger); }
.summary-grid { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 24px; }
.summary-card { padding: 24px; }
.summary-card span { display: block; color: var(--text-muted); margin-bottom: 12px; }
.summary-card strong { display: block; margin-bottom: 12px; font-size: 1.24rem; }
.summary-card small { color: var(--text-muted); line-height: 1.7; }
.section-head { margin-bottom: 18px; }
.empty-state { padding: 32px; border-radius: 18px; color: var(--text-muted); background: rgba(240, 240, 244, 0.04); }
.table-scroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; min-width: 760px; }
th, td { padding: 16px 14px; border-bottom: 1px solid rgba(240, 240, 244, 0.08); text-align: left; white-space: nowrap; }
th { color: var(--text-muted); font-size: 0.85rem; }
tbody tr:hover { background: rgba(240, 240, 244, 0.03); }
@media (max-width: 1040px) { .summary-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 820px) { .filters, .filter-fields { grid-template-columns: 1fr; display: grid; } }
@media (max-width: 640px) { .summary-grid { grid-template-columns: 1fr; } }
</style>
