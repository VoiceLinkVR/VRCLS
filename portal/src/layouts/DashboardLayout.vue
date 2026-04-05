<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LocaleSwitcher from '../components/LocaleSwitcher.vue'
import { formatNumber, useI18n } from '../i18n'
import { useUserStore } from '../stores/user'
import { formatPlanLabel } from '../utils/portal'

type MenuItem = {
  to: string
  label: string
  icon: string
}

const route = useRoute()
const router = useRouter()
const userStore = useUserStore()
const mobileMenuOpen = ref(false)
const { t } = useI18n()

const menuItems = computed<MenuItem[]>(() => [
  { to: '/app/overview', label: t('nav.overview'), icon: 'OV' },
  { to: '/app/sponsor', label: t('nav.billing'), icon: 'BI' },
  { to: '/app/orders', label: t('nav.orders'), icon: 'OR' },
  { to: '/app/usage', label: t('nav.usage'), icon: 'US' },
])

const currentTitle = computed(() =>
  typeof route.meta.titleKey === 'string' ? t(route.meta.titleKey) : t('route.overview'),
)
const currentPlanLabel = computed(() =>
  `${t('common.currentPlan')}: ${formatPlanLabel(userStore.currentPlanPrice)}`,
)
const remainingQuotaText = computed(() => formatNumber(userStore.remainingQuota))

function closeMenu() {
  mobileMenuOpen.value = false
}

function toggleMenu() {
  mobileMenuOpen.value = !mobileMenuOpen.value
}

async function logout() {
  await userStore.logout()
  closeMenu()
  router.push('/')
}
</script>

<template>
  <div class="dashboard-shell">
    <div v-if="mobileMenuOpen" class="mobile-mask" @click="closeMenu" />

    <aside class="sidebar glass-panel" :class="{ open: mobileMenuOpen }">
      <div class="sidebar-head">
        <div>
          <p class="brand-tag">{{ t('brand.app') }}</p>
          <h1 class="brand-name">{{ t('brand.portal') }}</h1>
        </div>
        <button class="btn btn-ghost mobile-close" type="button" @click="closeMenu">
          {{ t('common.close') }}
        </button>
      </div>

      <div class="status-card glass-panel">
        <span class="status-chip">{{ t('nav.live') }}</span>
        <strong>{{ userStore.profile?.name }}</strong>
        <p>{{ userStore.profile?.email }}</p>
      </div>

      <LocaleSwitcher />

      <nav class="menu">
        <router-link
          v-for="item in menuItems"
          :key="item.to"
          :to="item.to"
          class="menu-link"
          :class="{ active: route.path === item.to }"
          @click="closeMenu"
        >
          <span class="menu-icon">{{ item.icon }}</span>
          <span>{{ item.label }}</span>
        </router-link>
      </nav>

      <div class="sidebar-footer">
        <div class="sponsor-promo glass-panel">
          <p>{{ remainingQuotaText }} / {{ formatNumber(userStore.dailyLimit) }}</p>
          <span>{{ t('nav.promoTitle') }} {{ t('nav.promoBody') }}</span>
          <router-link to="/app/sponsor" class="btn btn-primary w-full" @click="closeMenu">
            {{ t('common.openBilling') }}
          </router-link>
        </div>

        <button class="btn btn-outline w-full logout-btn" type="button" @click="logout">
          {{ t('common.signOut') }}
        </button>
      </div>
    </aside>

    <main class="main-content">
      <header class="topbar glass-panel">
        <div class="topbar-main">
          <button class="btn btn-ghost menu-toggle" type="button" @click="toggleMenu">
            {{ t('nav.menu') }}
          </button>
          <div>
            <p class="eyebrow">{{ t('nav.center') }}</p>
            <h2 class="page-title">{{ currentTitle }}</h2>
          </div>
        </div>

        <div class="user-info">
          <span class="plan-tag">{{ currentPlanLabel }}</span>
          <div class="avatar">{{ userStore.profile?.name?.slice(0, 1) ?? 'V' }}</div>
        </div>
      </header>

      <section class="page-container">
        <RouterView />
      </section>
    </main>
  </div>
</template>

<style scoped>
.dashboard-shell {
  position: relative;
  display: flex;
  min-height: 100vh;
  padding: 18px;
  gap: 18px;
  overflow: hidden;
}

.mobile-mask {
  position: fixed;
  inset: 0;
  background: rgba(3, 7, 12, 0.62);
  backdrop-filter: blur(6px);
  z-index: 40;
}

.sidebar {
  position: relative;
  z-index: 60;
  width: 310px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: 18px;
  padding: 24px;
  background:
    radial-gradient(circle at top, rgba(0, 229, 255, 0.1), transparent 38%),
    var(--bg-panel-strong);
}

.sidebar-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
}

.brand-tag {
  color: var(--primary);
  font-size: 0.8rem;
  letter-spacing: 0.2em;
  text-transform: uppercase;
  margin-bottom: 6px;
}

.brand-name {
  font-size: 1.9rem;
  line-height: 1;
  letter-spacing: -0.05em;
}

.mobile-close,
.menu-toggle {
  display: none;
}

.status-card {
  padding: 18px;
  background: rgba(255, 255, 255, 0.03);
}

.status-card strong {
  display: block;
  font-size: 1.05rem;
  margin-bottom: 4px;
}

.status-card p {
  color: var(--text-muted);
  font-size: 0.92rem;
}

.status-chip {
  display: inline-flex;
  align-items: center;
  padding: 5px 10px;
  margin-bottom: 14px;
  border-radius: 999px;
  background: rgba(0, 229, 255, 0.12);
  color: var(--primary);
  font-size: 0.76rem;
  font-weight: 700;
  animation: pulse-glow 2.6s ease-in-out infinite;
}

.menu {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
}

.menu-link {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid transparent;
  border-radius: 16px;
  color: var(--text-muted);
  transition:
    transform 0.22s ease,
    background 0.22s ease,
    color 0.22s ease,
    border-color 0.22s ease;
}

.menu-link:hover {
  color: var(--text-main);
  border-color: rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.05);
  transform: translateX(3px);
}

.menu-link.active {
  color: var(--text-main);
  border-color: var(--border-strong);
  background: linear-gradient(90deg, rgba(0, 229, 255, 0.14), rgba(157, 78, 221, 0.08));
  box-shadow: inset 3px 0 0 var(--primary);
}

.menu-icon {
  width: 24px;
  text-align: center;
  font-size: 0.72rem;
  font-weight: 700;
}

.sidebar-footer {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.sponsor-promo {
  padding: 18px;
  background:
    radial-gradient(circle at top right, rgba(0, 229, 255, 0.12), transparent 42%),
    rgba(0, 229, 255, 0.04);
  border-color: var(--border-strong);
}

.sponsor-promo p {
  font-size: 1rem;
  font-weight: 700;
  margin-bottom: 8px;
}

.sponsor-promo span {
  display: block;
  margin-bottom: 16px;
  color: var(--text-muted);
  font-size: 0.88rem;
  line-height: 1.7;
}

.logout-btn:hover {
  color: var(--danger);
  border-color: rgba(255, 94, 94, 0.24);
  background: rgba(255, 94, 94, 0.08);
}

.main-content {
  min-width: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.topbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  padding: 20px 28px;
  background: rgba(15, 20, 31, 0.78);
}

.topbar-main {
  display: flex;
  align-items: center;
  gap: 16px;
}

.eyebrow {
  color: var(--text-muted);
  font-size: 0.82rem;
  text-transform: uppercase;
  letter-spacing: 0.18em;
  margin-bottom: 4px;
}

.page-title {
  font-size: 1.45rem;
  line-height: 1.1;
}

.user-info {
  display: flex;
  align-items: center;
  gap: 14px;
}

.plan-tag {
  padding: 8px 14px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.04);
  color: var(--text-soft);
  font-size: 0.88rem;
}

.avatar {
  width: 42px;
  height: 42px;
  display: grid;
  place-items: center;
  border-radius: 50%;
  background: linear-gradient(135deg, var(--primary) 0%, var(--accent) 100%);
  color: #021810;
  font-weight: 900;
}

.page-container {
  min-height: 0;
  flex: 1;
  overflow: auto;
  padding-right: 4px;
}

@media (max-width: 1080px) {
  .dashboard-shell {
    padding: 14px;
  }

  .sidebar {
    position: fixed;
    inset: 14px auto 14px 14px;
    height: calc(100vh - 28px);
    transform: translateX(calc(-100% - 24px));
    transition: transform 0.24s ease;
  }

  .sidebar.open {
    transform: translateX(0);
  }

  .mobile-close,
  .menu-toggle {
    display: inline-flex;
  }

  .topbar {
    padding: 18px 20px;
  }
}

@media (max-width: 720px) {
  .dashboard-shell {
    padding: 12px;
  }

  .topbar {
    flex-direction: column;
    align-items: stretch;
  }

  .topbar-main,
  .user-info {
    justify-content: space-between;
  }

  .plan-tag {
    max-width: calc(100vw - 120px);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
}
</style>
