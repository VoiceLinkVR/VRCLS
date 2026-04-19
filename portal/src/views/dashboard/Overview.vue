<script setup lang="ts">
import { computed } from 'vue'
import { formatDateTime, formatNumber } from '../../i18n'
import { useUserStore } from '../../stores/user'
import { translateAccountStatus } from '../../utils/portal'

const userStore = useUserStore()
const overviewUsage = computed(() => userStore.usageSnapshot ?? userStore.usage)

function formatQuotaValue(value: number | null | undefined, suffix: string) {
  return value === null || value === undefined ? '暂无数据' : `${formatNumber(value)} ${suffix}`
}

function translatePricingMode(mode: string | null | undefined) {
  const normalized = (mode || '').trim().toLowerCase()
  if (normalized === 'ladder') return '阶梯档'
  if (normalized === 'fixed_tiers') return '固定档'
  if (normalized === 'free') return '免费档'
  return '暂无数据'
}

const todayUsageRow = computed(() => {
  const rows = overviewUsage.value?.daily_stats ?? []
  if (rows.length === 0) return null

  const todayUtc = new Date().toISOString().slice(0, 10)
  return (
    rows.find((row) => row.date === todayUtc) ??
    rows.find((row) => row.date === overviewUsage.value?.query_end_date) ??
    rows[rows.length - 1] ??
    null
  )
})

const todaySuccessCount = computed(() => overviewUsage.value?.today_success_count ?? todayUsageRow.value?.daily_success_count ?? 0)
const todayTotalCount = computed(() => overviewUsage.value?.today_total_count ?? todayUsageRow.value?.daily_total_count ?? todaySuccessCount.value)
const todayResourceUsed = computed(
  () =>
    overviewUsage.value?.today_daily_resource_used ??
    todayUsageRow.value?.daily_subscription_resource_cost ??
    todayUsageRow.value?.daily_resource_cost ??
    userStore.usedQuota,
)
const todayRemainingQuota = computed(() => overviewUsage.value?.today_remaining_daily ?? overviewUsage.value?.remaining_daily)
const currentPricingMode = computed(() => overviewUsage.value?.pricing_mode ?? '')
const currentPlanLabel = computed(() => overviewUsage.value?.plan_name || '暂无数据')
const overviewAnnouncements = computed(() =>
  [...userStore.announcements]
    .filter((item) => item.displayMode === 'banner' && item.showInOverview)
    .sort((left, right) => left.sortOrder - right.sortOrder),
)

const metrics = computed(() => [
  {
    label: '今日请求数',
    value: formatNumber(todaySuccessCount.value),
    sub: `总请求数：${formatNumber(todayTotalCount.value)}`,
  },
  {
    label: '今日资源消耗',
    value: formatNumber(todayResourceUsed.value),
    sub: `今日剩余资源：${formatQuotaValue(todayRemainingQuota.value, '单位')}`,
    danger: userStore.shouldUpgrade,
  },
  {
    label: '加油包余额',
    value: formatNumber(overviewUsage.value?.addon_resource_balance ?? 0),
    sub: `累计总量：${formatQuotaValue(overviewUsage.value?.addon_resource_total, '单位')}`,
  },
])

const identityFacts = computed(() => [
  { label: '用户名', value: userStore.profile?.username || '暂无数据' },
  { label: '用户标识', value: userStore.profile?.shortUserUuid || '暂无数据' },
  { label: '账户状态', value: translateAccountStatus(userStore.profile?.accountStatus || '') },
  { label: '旧版账号', value: userStore.profile?.legacyUsername || '无' },
])

const mergedSubscriptionFacts = computed(() => {
  const usage = overviewUsage.value

  return [
    { label: '当前订阅模式', value: translatePricingMode(currentPricingMode.value) },
    { label: '当前套餐', value: currentPlanLabel.value },
    { label: '当前剩余资源', value: formatQuotaValue(usage?.today_remaining_daily ?? usage?.remaining_resource, '单位') },
    { label: '日资源上限', value: formatQuotaValue(usage?.daily_limit, '单位/日') },
    { label: '到期时间', value: formatDateTime(usage?.expiration_utc) },
  ]
})

const addonFacts = computed(() => {
  const usage = overviewUsage.value

  return [
    { label: '加油包状态', value: (usage?.addon_resource_balance ?? 0) > 0 ? '已启用' : '未启用' },
    { label: '剩余加油包资源', value: formatQuotaValue(usage?.addon_resource_balance, '单位') },
    { label: '加油包累计总量', value: formatQuotaValue(usage?.addon_resource_total, '单位') },
    { label: '到期时间', value: formatDateTime(usage?.addon_expiration_utc) },
  ]
})
</script>

<template>
  <div class="overview-page">
    <section v-if="overviewAnnouncements.length > 0" class="overview-announcement-stack">
      <article v-for="item in overviewAnnouncements" :key="item.id" class="panel overview-announcement-card">
        <div class="overview-announcement-head">
          <span class="overview-announcement-badge">总览公告</span>
          <strong>{{ item.title }}</strong>
        </div>
        <p class="overview-announcement-copy">{{ item.content }}</p>
      </article>
    </section>

    <div class="metrics-grid">
      <div v-for="m in metrics" :key="m.label" class="metric-card panel">
        <span class="metric-label">{{ m.label }}</span>
        <strong class="metric-value" :class="{ 'text-warning': m.danger }">{{ m.value }}</strong>
        <span class="metric-sub">{{ m.sub }}</span>
      </div>
    </div>

    <div class="info-grid">
      <div class="panel">
        <div class="panel-header">
          <h2>身份信息</h2>
          <p>基础账户识别信息。</p>
        </div>
        <div class="panel-body">
          <div class="desc-list">
            <div v-for="item in identityFacts" :key="item.label" class="desc-row">
              <span class="desc-term">{{ item.label }}</span>
              <span class="desc-detail">{{ item.value }}</span>
            </div>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel-header">
          <h2>订阅方案与限额</h2>
          <p>您当前服务实际生效的参数与资费详情。</p>
        </div>
        <div class="panel-body">
          <div class="desc-list">
            <div v-for="item in mergedSubscriptionFacts" :key="item.label" class="desc-row">
              <span class="desc-term">{{ item.label }}</span>
              <span class="desc-detail mono-wrap">{{ item.value }}</span>
            </div>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel-header">
          <h2>加油包</h2>
          <p>加油包资源与启用条件单独展示。</p>
        </div>
        <div class="panel-body">
          <div class="desc-list">
            <div v-for="item in addonFacts" :key="item.label" class="desc-row">
              <span class="desc-term">{{ item.label }}</span>
              <span class="desc-detail">{{ item.value }}</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overview-page { display: flex; flex-direction: column; gap: 32px; }
.overview-announcement-stack { display: flex; flex-direction: column; gap: 16px; }
.overview-announcement-card { padding: 20px 22px; }
.overview-announcement-head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.overview-announcement-badge { display: inline-flex; align-items: center; padding: 2px 8px; border-radius: 999px; background: rgba(0, 112, 243, 0.14); color: var(--text-main); font-size: 12px; font-weight: 600; }
.overview-announcement-copy { margin: 0; white-space: pre-line; line-height: 1.7; color: var(--text-soft); }
.metrics-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 24px; }
.metric-card { padding: 24px; display: flex; flex-direction: column; gap: 8px; }
.metric-label { color: var(--text-soft); font-size: 0.875rem; font-weight: 500; }
.metric-value { font-size: 2rem; font-weight: 600; line-height: 1.1; letter-spacing: -0.04em; }
.metric-sub { color: var(--text-muted); font-size: 0.8125rem; }
.text-warning { color: var(--warning); }
.info-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 24px; }
.mono-wrap { font-family: var(--font-mono); overflow-wrap: anywhere; word-break: break-word; }
</style>
