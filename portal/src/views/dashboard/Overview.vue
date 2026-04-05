<script setup lang="ts">
import { computed } from 'vue'
import { formatDateTime, formatNumber, useI18n } from '../../i18n'
import { useUserStore } from '../../stores/user'
import { formatPlanLabel, translateAccountStatus } from '../../utils/portal'

const userStore = useUserStore()
const { t } = useI18n()

const stats = computed(() => [
  {
    key: 'requests',
    title: t('overview.requestsTitle'),
    caption: t('overview.requestsCaption'),
    value: `${formatNumber(userStore.requestSuccess)} / ${formatNumber(userStore.requestTotal)}`,
    percent: userStore.requestSuccessPercent,
    tone: 'primary',
  },
  {
    key: 'quota',
    title: t('overview.quotaTitle'),
    caption: t('overview.quotaCaption'),
    value: `${formatNumber(userStore.usedQuota)} / ${formatNumber(userStore.dailyLimit)}`,
    percent: userStore.quotaUsagePercent,
    tone: userStore.shouldUpgrade ? 'warning' : 'accent',
  },
  {
    key: 'addon',
    title: t('overview.addonTitle'),
    caption: t('overview.addonCaption'),
    value: formatNumber(userStore.usage?.addon_resource_balance ?? userStore.billingGuide?.addonResourceBalance ?? 0),
    percent: 0,
    tone: 'neutral',
  },
])

const quickFacts = computed(() => [
  {
    label: t('overview.factPlan'),
    value: formatPlanLabel(userStore.currentPlanPrice),
    detail: userStore.billingGuide?.displayResourceSummary || t('common.noData'),
  },
  {
    label: t('overview.factQuota'),
    value: `${formatNumber(userStore.remainingQuota)} / ${formatNumber(userStore.dailyLimit)}`,
    detail: userStore.shouldUpgrade ? t('overview.factQuotaLow') : t('overview.factQuotaHealthy'),
  },
  {
    label: t('overview.factStatus'),
    value: translateAccountStatus(userStore.profile?.accountStatus || ''),
    detail:
      userStore.usage?.expiration_utc
        ? `${t('usage.summaryExpiration')}: ${formatDateTime(userStore.usage.expiration_utc)}`
        : t('common.noData'),
  },
  {
    label: t('overview.factUsage'),
    value:
      userStore.usage?.query_start_date && userStore.usage?.query_end_date
        ? `${userStore.usage.query_start_date} ~ ${userStore.usage.query_end_date}`
        : t('common.noData'),
    detail: t('overview.factUsageDetail'),
  },
])

const events = computed(() => [
  {
    title: t('overview.timelineBilling'),
    copy: t('overview.timelineBillingBody'),
  },
  {
    title: t('overview.timelineUsage'),
    copy: t('overview.timelineUsageBody'),
  },
])
</script>

<template>
  <div class="overview panel-grid">
    <section v-if="userStore.shouldUpgrade" class="alert-banner glass-panel">
      <div class="alert-copy">
        <p class="alert-tag">{{ t('overview.alertTag') }}</p>
        <h3>
          {{ t('overview.alertTitle') }}
          <span>{{ Math.round(userStore.quotaUsagePercent) }}%</span>
        </h3>
        <p>
          {{ t('overview.alertBody') }}
        </p>
      </div>

          <router-link to="/billing" class="btn btn-primary">{{ t('overview.alertAction') }}</router-link>
    </section>

    <section class="stats-grid">
      <article v-for="item in stats" :key="item.key" class="stat-card glass-panel" :class="item.tone">
        <div class="stat-head">
          <div>
            <p>{{ item.title }}</p>
            <small>{{ item.caption }}</small>
          </div>
        </div>

        <div class="stat-value">
          {{ item.value }}
        </div>

        <div v-if="item.key !== 'addon'" class="progress-track">
          <div class="progress-bar" :style="{ width: `${item.percent}%` }" />
        </div>

        <p class="stat-foot" v-if="item.key !== 'addon'">{{ Math.round(item.percent) }}%</p>
      </article>
    </section>

    <section class="fact-grid">
      <article v-for="fact in quickFacts" :key="fact.label" class="fact-card glass-panel">
        <p>{{ fact.label }}</p>
        <strong>{{ fact.value }}</strong>
        <span>{{ fact.detail }}</span>
      </article>
    </section>

    <section class="insight-grid">
      <article class="insight-card glass-panel">
        <div class="insight-head">
          <h3>{{ t('overview.directionTitle') }}</h3>
          <router-link to="/usage">{{ t('overview.gotoUsage') }}</router-link>
        </div>
        <p class="insight-copy">
          {{ t('overview.directionBody') }}
        </p>
        <ul class="insight-list">
          <li>{{ t('overview.directionItemPlan') }}</li>
          <li>{{ t('overview.directionItemBilling') }}</li>
          <li>{{ t('overview.directionItemUsage') }}</li>
        </ul>
      </article>

      <article class="timeline-card glass-panel">
        <h3>{{ t('overview.timelineTitle') }}</h3>
        <div v-for="event in events" :key="event.title" class="timeline-item">
          <span class="timeline-dot" />
          <div>
            <strong>{{ event.title }}</strong>
            <p>{{ event.copy }}</p>
          </div>
        </div>
      </article>
    </section>
  </div>
</template>

<style scoped>
.overview {
  padding-bottom: 24px;
}

.alert-banner {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 20px;
  padding: 26px 28px;
  background:
    radial-gradient(circle at left, rgba(251, 191, 36, 0.12), transparent 36%),
    rgba(251, 191, 36, 0.08);
  border-color: rgba(251, 191, 36, 0.22);
}

.alert-tag {
  color: var(--warning);
  font-size: 0.8rem;
  letter-spacing: 0.18em;
  text-transform: uppercase;
  margin-bottom: 10px;
}

.alert-copy h3 {
  font-size: 1.45rem;
  margin-bottom: 10px;
}

.alert-copy h3 span {
  color: var(--warning);
}

.alert-copy p:last-child {
  color: var(--text-muted);
  line-height: 1.8;
}

.stats-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 24px;
}

.stat-card {
  padding: 28px;
}

.stat-head {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-bottom: 26px;
}

.stat-head p {
  font-size: 1.08rem;
  font-weight: 700;
}

.stat-head small,
.stat-foot {
  color: var(--text-muted);
}

.stat-value {
  margin-bottom: 18px;
  font-size: clamp(1.8rem, 3vw, 2.5rem);
  font-weight: 900;
  letter-spacing: -0.04em;
}

.progress-track {
  height: 10px;
  overflow: hidden;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.05);
}

.progress-bar {
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, var(--primary), var(--accent));
  box-shadow: 0 0 18px var(--primary-glow);
}

.stat-card.accent .progress-bar {
  background: linear-gradient(90deg, var(--accent), var(--primary));
  box-shadow: 0 0 18px rgba(157, 78, 221, 0.28);
}

.stat-card.warning .stat-value {
  color: #ffd36b;
}

.stat-card.warning .progress-bar {
  background: linear-gradient(90deg, #fbbf24, #fde68a);
  box-shadow: 0 0 18px rgba(251, 191, 36, 0.28);
}

.stat-foot {
  margin-top: 14px;
}

.fact-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 24px;
}

.fact-card {
  padding: 24px;
}

.fact-card p {
  color: var(--text-muted);
  margin-bottom: 16px;
}

.fact-card strong {
  display: block;
  margin-bottom: 12px;
  font-size: 1.2rem;
}

.fact-card span {
  color: var(--text-muted);
  line-height: 1.8;
}

.insight-grid {
  display: grid;
  grid-template-columns: 1.2fr 0.8fr;
  gap: 24px;
}

.insight-card,
.timeline-card {
  padding: 28px;
}

.insight-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  margin-bottom: 18px;
}

.insight-head a {
  color: var(--primary);
}

.insight-copy {
  color: var(--text-muted);
  line-height: 1.9;
  margin-bottom: 20px;
}

.insight-list {
  list-style: none;
  display: grid;
  gap: 12px;
}

.insight-list li {
  padding: 14px 16px;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 16px;
  background: rgba(255, 255, 255, 0.03);
}

.timeline-card h3 {
  margin-bottom: 18px;
}

.timeline-item {
  display: flex;
  gap: 14px;
  padding: 14px 0;
}

.timeline-item + .timeline-item {
  border-top: 1px solid rgba(255, 255, 255, 0.06);
}

.timeline-dot {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  margin-top: 7px;
  border-radius: 50%;
  background: var(--primary);
  box-shadow: 0 0 12px var(--primary-glow-strong);
}

.timeline-item p {
  color: var(--text-muted);
  margin-top: 8px;
  line-height: 1.8;
}

@media (max-width: 1040px) {
  .stats-grid,
  .fact-grid,
  .insight-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 720px) {
  .alert-banner {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
