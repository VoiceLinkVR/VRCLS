<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatDateTime, formatNumber, translateApiError, useI18n } from '../../i18n'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()
const { t } = useI18n()

const startDate = ref(userStore.usage?.query_start_date ?? '')
const endDate = ref(userStore.usage?.query_end_date ?? '')
const loading = ref(false)
const errorText = ref('')

const summaryCards = computed(() => [
  {
    title: '当前身份',
    value: userStore.profile?.name || t('common.noData'),
    detail: userStore.profile?.userUuid || t('common.noData'),
  },
  {
    title: t('usage.summaryRequests'),
    value: formatNumber(userStore.usage?.daily_success_count ?? 0),
    detail: `${t('usage.summaryTotal')}: ${formatNumber(userStore.usage?.daily_total_count ?? 0)}`,
  },
  {
    title: t('usage.summaryQuotaUsed'),
    value: formatNumber(userStore.usage?.resource_used ?? 0),
    detail: `${t('usage.summaryQuotaRemaining')}: ${formatNumber(userStore.remainingQuota)}`,
  },
  {
    title: t('usage.summaryAddonUsed'),
    value: formatNumber(userStore.usage?.addon_resource_used ?? 0),
    detail: `${t('usage.summaryAddonBalance')}: ${formatNumber(userStore.usage?.addon_resource_balance ?? 0)}`,
  },
  {
    title: '旧账号来源',
    value: userStore.profile?.legacyUsername || '未绑定',
    detail: userStore.profile?.legacyUsername ? '仅用于迁移说明，不作为主登录标识。' : '当前账户没有绑定旧账号。',
  },
  {
    title: t('usage.summaryExpiration'),
    value: formatDateTime(userStore.usage?.expiration_utc),
    detail:
      userStore.usage?.query_start_date && userStore.usage?.query_end_date
        ? `${userStore.usage.query_start_date} ~ ${userStore.usage.query_end_date}`
        : t('common.noData'),
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
    errorText.value =
      error instanceof Error
        ? translateApiError({ message: error.message }, 'errors.generic')
        : t('errors.generic')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="usage panel-grid">
    <section class="usage-hero glass-panel">
      <div>
        <p class="hero-tag">{{ t('usage.heroTag') }}</p>
        <h2>{{ t('usage.heroTitle') }}</h2>
        <p>{{ t('usage.heroBody') }}</p>
        <div class="identity-inline">
          <span>{{ userStore.profile?.name || '未登录' }}</span>
          <code v-if="userStore.profile?.shortUserUuid">{{ userStore.profile.shortUserUuid }}</code>
        </div>
      </div>
    </section>

    <section class="filters glass-panel">
      <div class="filter-fields">
        <label class="filter-item">
          <span>{{ t('common.startDate') }}</span>
          <input v-model="startDate" type="date" class="input-field" />
        </label>
        <label class="filter-item">
          <span>{{ t('common.endDate') }}</span>
          <input v-model="endDate" type="date" class="input-field" />
        </label>
      </div>

      <div class="filter-actions">
        <p v-if="errorText" class="error-copy">{{ errorText }}</p>
        <button type="button" class="btn btn-primary" :disabled="loading" @click="queryUsage">
          {{ loading ? t('common.loading') : t('common.query') }}
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
        <h3>{{ t('usage.detailsTitle') }}</h3>
      </div>

      <div v-if="!userStore.usage || userStore.usage.daily_stats.length === 0" class="empty-state">
        {{ t('usage.empty') }}
      </div>

      <div v-else class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>{{ t('usage.date') }}</th>
              <th>{{ t('usage.success') }}</th>
              <th>{{ t('usage.failed') }}</th>
              <th>{{ t('usage.limited') }}</th>
              <th>{{ t('usage.quotaUsed') }}</th>
              <th>{{ t('usage.dailyRemaining') }}</th>
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
.usage-hero,
.filters,
.usage-table {
  padding: 28px;
}

.hero-tag {
  color: var(--primary);
  font-size: 0.82rem;
  letter-spacing: 0.18em;
  text-transform: uppercase;
}

.usage-hero h2 {
  margin: 12px 0 14px;
  font-size: clamp(1.9rem, 4vw, 2.7rem);
}

.usage-hero p:last-child {
  max-width: 820px;
  color: var(--text-muted);
  line-height: 1.8;
}

.identity-inline {
  margin-top: 16px;
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
}

.identity-inline span {
  color: var(--text-main);
  font-weight: 700;
}

.identity-inline code {
  padding: 6px 10px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  color: var(--primary);
}

.filters {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  align-items: end;
}

.filter-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 240px));
  gap: 16px;
}

.filter-item {
  display: grid;
  gap: 10px;
}

.filter-item span {
  color: var(--text-muted);
}

.filter-actions {
  display: grid;
  gap: 10px;
}

.error-copy {
  color: #ffb4b4;
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 24px;
}

.summary-card {
  padding: 24px;
}

.summary-card span {
  display: block;
  color: var(--text-muted);
  margin-bottom: 12px;
}

.summary-card strong {
  display: block;
  margin-bottom: 12px;
  font-size: 1.24rem;
}

.summary-card small {
  color: var(--text-muted);
  line-height: 1.7;
}

.section-head {
  margin-bottom: 18px;
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

table {
  width: 100%;
  border-collapse: collapse;
  min-width: 760px;
}

th,
td {
  padding: 16px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  text-align: left;
  white-space: nowrap;
}

th {
  color: var(--text-muted);
  font-size: 0.85rem;
}

tbody tr:hover {
  background: rgba(255, 255, 255, 0.03);
}

@media (max-width: 1040px) {
  .summary-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 820px) {
  .filters,
  .filter-fields {
    grid-template-columns: 1fr;
    display: grid;
  }
}

@media (max-width: 640px) {
  .summary-grid {
    grid-template-columns: 1fr;
  }
}
</style>
