<script setup lang="ts">
import { computed } from 'vue'
import LocaleSwitcher from '../components/LocaleSwitcher.vue'
import { useI18n } from '../i18n'
import { useUserStore } from '../stores/user'

const userStore = useUserStore()
const { t } = useI18n()

const primaryRoute = computed(() => (userStore.isAuthenticated ? '/overview' : '/auth'))

const featureCards = computed(() =>[
  {
    title: t('landing.featureCurrent'),
    body: t('landing.featureCurrentBody'),
  },
  {
    title: t('landing.featureBilling'),
    body: t('landing.featureBillingBody'),
  },
  {
    title: t('landing.featureUsage'),
    body: t('landing.featureUsageBody'),
  },
])

const metrics = computed(() =>[
  { value: t('landing.metricsUsers'), label: t('landing.metricsUsersLabel') },
  { value: t('landing.metricsQuota'), label: t('landing.metricsQuotaLabel') },
  { value: t('landing.metricsUsage'), label: t('landing.metricsUsageLabel') },
])
</script>

<template>
  <div class="landing">
    <div class="noise" />

    <nav class="navbar glass-panel">
      <div class="brand-block">
        <span class="brand-dot" />
        <div>
          <p class="brand-kicker">VoiceLinkVR</p>
          <strong>{{ t('brand.portal') }}</strong>
        </div>
      </div>

      <div class="nav-actions">
        <LocaleSwitcher />
        <router-link to="/auth" class="btn btn-outline">{{ t('common.signIn') }}</router-link>
        <router-link :to="primaryRoute" class="btn btn-primary">{{ t('common.openPortal') }}</router-link>
      </div>
    </nav>

    <main class="hero-wrap">
      <section class="hero-copy">
        <div class="badge-chip">{{ t('landing.badge') }}</div>
        <h1 class="section-title">
          {{ t('landing.title') }}
          <span class="text-gradient">{{ t('landing.titleAccent') }}</span>
        </h1>
        <p class="section-copy">
          {{ t('landing.copy') }}
        </p>

        <div class="hero-actions">
          <router-link :to="primaryRoute" class="btn btn-primary">{{ t('landing.primary') }}</router-link>
          <router-link to="/auth" class="btn btn-outline">{{ t('landing.secondary') }}</router-link>
          <a
            href="https://github.com/VoiceLinkVR/VRCLS"
            target="_blank"
            rel="noreferrer"
            class="btn btn-outline"
          >
            {{ t('landing.github') }}
          </a>
        </div>

        <div class="hero-metrics glass-panel">
          <div v-for="metric in metrics" :key="metric.label" class="metric-item">
            <strong>{{ metric.value }}</strong>
            <span>{{ metric.label }}</span>
          </div>
        </div>
      </section>

      <section class="hero-card glass-panel">
        <div class="card-surface" />
        <div class="terminal-head">
          <span />
          <span />
          <span />
        </div>
        <div class="terminal-body">
          <div class="terminal-line">
            <span class="prompt">[Link]</span>
            <strong>{{ t('landing.terminalSession') }}</strong>
          </div>
          <div class="terminal-line secondary">
            <span class="prompt">[Sync]</span>
            <strong>{{ t('landing.terminalProfile') }}</strong>
          </div>
          <div class="terminal-line accent">
            <span class="prompt">[Cloud]</span>
            <strong>{{ t('landing.terminalBilling') }}</strong>
          </div>
        </div>
        <div class="signal-ring signal-a" />
        <div class="signal-ring signal-b" />
      </section>
    </main>

    <section class="feature-section">
      <div class="feature-header">
        <p class="feature-tag">{{ t('brand.portal') }}</p>
        <h2>{{ t('landing.featureTitle') }}</h2>
      </div>

      <div class="feature-grid">
        <article v-for="card in featureCards" :key="card.title" class="feature-card glass-panel">
          <h3>{{ card.title }}</h3>
          <p>{{ card.body }}</p>
        </article>
      </div>
    </section>
  </div>
</template>

<style scoped>
.landing {
  position: relative;
  min-height: 100vh;
  overflow: hidden;
  padding: 24px;
}

.noise {
  position: absolute;
  inset: 0;
  opacity: 0.15;
  pointer-events: none;
  background-image:
    linear-gradient(rgba(0, 229, 255, 0.04) 1px, transparent 1px),
    linear-gradient(90deg, rgba(157, 78, 221, 0.05) 1px, transparent 1px);
  background-size: 48px 48px;
  mask-image: radial-gradient(circle at center, black 34%, transparent 82%);
}

.navbar {
  position: relative;
  z-index: 2;
  width: min(100%, var(--content-width));
  margin: 0 auto;
  padding: 16px 20px;
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 20px;
}

.brand-block {
  display: flex;
  align-items: center;
  gap: 14px;
}

.brand-dot {
  width: 14px;
  height: 14px;
  border-radius: 50%;
  background: radial-gradient(circle, #ffffff 0%, var(--primary) 40%, rgba(157, 78, 221, 0.55) 100%);
  box-shadow: 0 0 18px var(--primary-glow-strong);
}

.brand-kicker {
  color: var(--primary);
  font-size: 0.78rem;
  letter-spacing: 0.22em;
  text-transform: uppercase;
}

.brand-block strong {
  font-size: 1.1rem;
}

.nav-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
}

.hero-wrap {
  position: relative;
  z-index: 1;
  width: min(100%, var(--content-width));
  margin: 56px auto 0;
  display: grid;
  grid-template-columns: minmax(0, 1.08fr) minmax(320px, 0.92fr);
  gap: 32px;
  align-items: center;
}

.hero-copy {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 24px;
  animation: fade-up 0.75s ease both;
}

.hero-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
}

.hero-metrics {
  width: 100%;
  padding: 24px;
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 18px;
}

.metric-item {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.metric-item strong {
  font-size: clamp(1.5rem, 4vw, 2.2rem);
  color: #fff;
}

.metric-item span {
  color: var(--text-muted);
  font-size: 0.9rem;
}

.hero-card {
  position: relative;
  min-height: 460px;
  padding: 26px;
  overflow: hidden;
  background:
    radial-gradient(circle at top, rgba(6, 182, 212, 0.1), transparent 35%),
    rgba(17, 10, 35, 0.88);
  animation: fade-up 0.9s ease both;
}

.card-surface {
  position: absolute;
  inset: auto 8% 6% 8%;
  height: 170px;
  border-radius: 50%;
  background: radial-gradient(circle, rgba(157, 78, 221, 0.24), transparent 68%);
  filter: blur(20px);
}

.terminal-head {
  display: flex;
  gap: 8px;
  margin-bottom: 32px;
}

.terminal-head span {
  width: 11px;
  height: 11px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.22);
}

.terminal-body {
  position: relative;
  z-index: 1;
  display: flex;
  flex-direction: column;
  gap: 18px;
  margin-top: 42px;
}

.terminal-line {
  padding: 18px 20px;
  border: 1px solid var(--border-color);
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.03);
}

.terminal-line strong {
  display: block;
  font-size: 1.16rem;
  margin-top: 8px;
  color: #fff;
}

.terminal-line.secondary strong {
  color: var(--accent);
}

.terminal-line.accent strong {
  color: var(--primary);
}

.prompt {
  color: var(--text-muted);
  font-size: 0.84rem;
  letter-spacing: 0.08em;
}

.signal-ring {
  position: absolute;
  border: 1px solid rgba(0, 229, 255, 0.16);
  border-radius: 50%;
}

.signal-a {
  width: 180px;
  height: 180px;
  top: -42px;
  right: -42px;
}

.signal-b {
  width: 300px;
  height: 300px;
  bottom: -140px;
  left: -90px;
  border-color: rgba(6, 182, 212, 0.2);
}

.feature-section {
  width: min(100%, var(--content-width));
  margin: 84px auto 40px;
}

.feature-header {
  margin-bottom: 24px;
}

.feature-tag {
  color: var(--primary);
  font-size: 0.82rem;
  letter-spacing: 0.24em;
  text-transform: uppercase;
  margin-bottom: 12px;
}

.feature-header h2 {
  max-width: 780px;
  font-size: clamp(1.8rem, 4vw, 2.7rem);
  line-height: 1.12;
}

.feature-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 22px;
}

.feature-card {
  padding: 28px;
  transition:
    transform 0.22s ease,
    border-color 0.22s ease,
    background 0.22s ease;
}

.feature-card:hover {
  transform: translateY(-6px);
  border-color: var(--border-strong);
  background: var(--bg-panel-hover);
}

.feature-card h3 {
  margin-bottom: 12px;
  font-size: 1.18rem;
}

.feature-card p {
  color: var(--text-muted);
  line-height: 1.8;
}

@media (max-width: 1080px) {
  .hero-wrap,
  .feature-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 720px) {
  .landing {
    padding: 16px;
  }

  .navbar {
    flex-direction: column;
    align-items: stretch;
  }

  .nav-actions {
    flex-direction: column;
    align-items: stretch;
  }

  .hero-wrap {
    margin-top: 34px;
  }

  .hero-metrics {
    grid-template-columns: 1fr;
  }
}
</style>
