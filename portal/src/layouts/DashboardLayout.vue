<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LocaleSwitcher from '../components/LocaleSwitcher.vue'
import { formatNumber } from '../i18n'
import { useUserStore } from '../stores/user'

const route = useRoute()
const router = useRouter()
const userStore = useUserStore()
const mobileMenuOpen = ref(false)
const announcementHistoryOpen = ref(false)
const DISMISSED_MODAL_STORAGE_KEY = 'vrcls.portal.dismissedModalAnnouncementIds'
const dismissedModalIds = ref<string[]>(readDismissedModalIds())

function readDismissedModalIds(): string[] {
  if (typeof window === 'undefined') {
    return []
  }
  try {
    const parsed = JSON.parse(window.localStorage.getItem(DISMISSED_MODAL_STORAGE_KEY) || '[]')
    return Array.isArray(parsed) ? parsed.map((value) => String(value)) : []
  } catch {
    return []
  }
}

function persistDismissedModalIds(ids: string[]) {
  if (typeof window === 'undefined') {
    return
  }
  window.localStorage.setItem(DISMISSED_MODAL_STORAGE_KEY, JSON.stringify(ids))
}

const profileLabel = computed(() => {
  if (userStore.profile?.username) {
    return userStore.profile.username
  }
  if (userStore.profile?.shortUserUuid) {
    return `用户 ${userStore.profile.shortUserUuid}`
  }
  return '未登录'
})
const sidebarRemainingQuotaText = computed(() => {
  const usage = userStore.usageSnapshot ?? userStore.usage
  const value = usage?.today_remaining_daily ?? usage?.remaining_daily
  return value === null || value === undefined ? '暂无数据' : formatNumber(value)
})

const menuItems = computed(() => [
  { to: '/overview', label: '总览' },
  { to: '/center', label: '账户设置' },
  { to: '/billing', label: '订阅充值' },
  { to: '/orders', label: '账单历史' },
  { to: '/usage', label: '用量监控' },
  { to: '/faq', label: '常见问题' },
])

const routeTitleMap: Record<string, string> = {
  auth: '登录',
  overview: '总览',
  billing: '订阅充值',
  orders: '账单历史',
  usage: '用量监控',
  faq: '常见问题',
  center: '账户设置',
  authorize: '控制台授权登录',
  'reset-password': '重置密码',
  'verify-email': '验证邮箱',
}

const currentTitle = computed(() => routeTitleMap[String(route.name || '')] || '控制台')
const needsEmailBinding = computed(() => !userStore.profile?.email || !userStore.profile?.emailVerified)
const publishedAnnouncementCount = computed(() => userStore.announcements.length)
const historyAnnouncements = computed(() =>
  [...userStore.announcements].sort((left, right) => {
    const leftTime = left.publishedAt ? new Date(left.publishedAt).getTime() : 0
    const rightTime = right.publishedAt ? new Date(right.publishedAt).getTime() : 0
    if (rightTime !== leftTime) {
      return rightTime - leftTime
    }
    return left.sortOrder - right.sortOrder
  }),
)
const activeModalAnnouncement = computed(() =>
  userStore.announcements.find(
    (item) => item.displayMode === 'modal' && !dismissedModalIds.value.includes(item.id),
  ) ?? null,
)

function closeMenu() {
  mobileMenuOpen.value = false
}

function toggleMenu() {
  mobileMenuOpen.value = !mobileMenuOpen.value
}

async function logout() {
  await userStore.logout()
  router.push('/auth')
}

function dismissModalAnnouncement(id: string) {
  if (!id || dismissedModalIds.value.includes(id)) {
    return
  }
  dismissedModalIds.value = [...dismissedModalIds.value, id]
  persistDismissedModalIds(dismissedModalIds.value)
}

function toggleAnnouncementHistory() {
  announcementHistoryOpen.value = !announcementHistoryOpen.value
}
</script>

<template>
  <div class="layout">
    <div v-if="mobileMenuOpen" class="mobile-mask" @click="closeMenu" />

    <aside class="sidebar" :class="{ open: mobileMenuOpen }">
      <div class="sidebar-header">
        <div class="brand">
          <div class="brand-logo">V</div>
          <strong>VoiceLinkVR</strong>
        </div>
      </div>

      <div class="sidebar-user">
        <strong>{{ profileLabel }}</strong>
        <p>{{ userStore.profile?.email || '未绑定邮箱' }}</p>
      </div>

      <nav class="nav-menu">
        <router-link
          v-for="item in menuItems"
          :key="item.to"
          :to="item.to"
          class="nav-item"
          :class="{ active: route.path.startsWith(item.to) }"
          @click="closeMenu"
        >
          {{ item.label }}
        </router-link>
      </nav>

      <div class="sidebar-footer">
        <div class="quota-mini">
          <span>剩余额度</span>
          <strong>{{ sidebarRemainingQuotaText }}</strong>
        </div>
        <button class="btn btn-outline w-full" @click="logout">退出登录</button>
      </div>
    </aside>

    <main class="main">
      <header class="topbar">
        <div class="topbar-left">
          <button class="menu-toggle" @click="toggleMenu">☰</button>
          <span class="breadcrumb">控制台 / <strong>{{ currentTitle }}</strong></span>
        </div>
        <div class="topbar-right">
          <button class="announcement-trigger" type="button" @click="toggleAnnouncementHistory">
            <span class="announcement-trigger-label">公告</span>
            <span v-if="publishedAnnouncementCount > 0" class="announcement-trigger-count">{{ publishedAnnouncementCount }}</span>
          </button>
          <LocaleSwitcher />
        </div>
      </header>

      <div class="content scroll-area">
        <div class="page-container">
          <div v-if="userStore.isMigrationPending" class="alert alert-warning">
            <div>
              <h3>账户迁移中</h3>
              <p>请先完成邮箱绑定或旧账号归并，以解锁完整的计费与安全能力。</p>
            </div>
            <router-link to="/center" class="btn btn-outline">前往设置</router-link>
          </div>

          <div v-if="needsEmailBinding" class="alert alert-warning">
            <div>
              <h3>需要验证邮箱</h3>
              <p>为了账户安全，请验证您的邮箱地址。</p>
            </div>
            <router-link to="/center" class="btn btn-outline">去验证</router-link>
          </div>

          <RouterView />
        </div>
      </div>
    </main>

    <div v-if="announcementHistoryOpen" class="announcement-modal-mask" @click.self="announcementHistoryOpen = false">
      <div class="announcement-history-panel panel">
        <div class="announcement-history-head">
          <div>
            <p class="announcement-history-eyebrow">公告中心</p>
            <h3>历史公告</h3>
          </div>
          <button class="btn btn-outline" type="button" @click="announcementHistoryOpen = false">关闭</button>
        </div>

        <div v-if="historyAnnouncements.length === 0" class="announcement-history-empty">
          当前还没有已发布公告。
        </div>

        <div v-else class="announcement-history-list">
          <article v-for="item in historyAnnouncements" :key="item.id" class="announcement-history-item">
            <div class="announcement-head">
              <span class="announcement-badge" :class="{ modal: item.displayMode === 'modal' }">
                {{ item.displayMode === 'modal' ? '弹窗公告' : '普通公告' }}
              </span>
              <strong>{{ item.title }}</strong>
            </div>
            <p class="announcement-history-meta">
              {{ item.publishedAt ? item.publishedAt.replace('T', ' ').replace('Z', ' UTC') : '未记录发布时间' }}
            </p>
            <p class="announcement-copy">{{ item.content }}</p>
          </article>
        </div>
      </div>
    </div>

    <div v-if="activeModalAnnouncement" class="announcement-modal-mask">
      <div class="announcement-modal panel">
        <div class="announcement-head">
          <span class="announcement-badge modal">弹窗公告</span>
          <strong>{{ activeModalAnnouncement.title }}</strong>
        </div>
        <p class="announcement-copy">{{ activeModalAnnouncement.content }}</p>
        <div class="announcement-actions">
          <button class="btn btn-primary" @click="dismissModalAnnouncement(activeModalAnnouncement.id)">我知道了</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.layout {
  display: flex;
  height: 100vh;
  overflow: hidden;
}

.mobile-mask {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.8);
  z-index: 40;
}

.sidebar {
  width: 260px;
  background: var(--bg-base);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  z-index: 50;
}

.sidebar-header {
  height: 64px;
  display: flex;
  align-items: center;
  padding: 0 24px;
  border-bottom: 1px solid var(--border);
}

.brand {
  display: flex;
  align-items: center;
  gap: 12px;
}

.brand-logo {
  width: 24px;
  height: 24px;
  background: var(--text-main);
  color: var(--bg-base);
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 800;
  font-size: 14px;
}

.sidebar-user {
  padding: 24px;
  border-bottom: 1px solid var(--border);
}

.sidebar-user strong {
  display: block;
  font-size: 0.875rem;
}

.sidebar-user p {
  color: var(--text-soft);
  font-size: 0.8125rem;
  margin-top: 4px;
}

.nav-menu {
  padding: 24px 12px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex: 1;
}

.nav-item {
  padding: 8px 12px;
  border-radius: var(--radius-sm);
  color: var(--text-soft);
  font-size: 0.875rem;
  font-weight: 500;
}

.nav-item:hover {
  background: var(--bg-hover);
  color: var(--text-main);
}

.nav-item.active {
  background: var(--bg-panel);
  color: var(--text-main);
}

.sidebar-footer {
  padding: 24px;
  border-top: 1px solid var(--border);
}

.quota-mini {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
  font-size: 0.8125rem;
  color: var(--text-soft);
}

.quota-mini strong {
  color: var(--text-main);
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.topbar {
  height: 64px;
  border-bottom: 1px solid var(--border);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 32px;
  background: var(--bg-base);
}

.topbar-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

.announcement-trigger {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 38px;
  padding: 0 14px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: rgba(255, 255, 255, 0.03);
  color: var(--text-main);
  cursor: pointer;
}

.announcement-trigger:hover {
  background: rgba(255, 255, 255, 0.06);
}

.announcement-trigger-label {
  font-size: 13px;
  font-weight: 600;
}

.announcement-trigger-count {
  min-width: 20px;
  height: 20px;
  padding: 0 6px;
  border-radius: 999px;
  background: rgba(0, 112, 243, 0.16);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 12px;
  font-weight: 700;
}

.topbar-left {
  display: flex;
  align-items: center;
  gap: 16px;
}

.breadcrumb {
  font-size: 0.875rem;
  color: var(--text-soft);
}

.breadcrumb strong {
  color: var(--text-main);
  font-weight: 500;
}

.menu-toggle {
  display: none;
  background: none;
  border: none;
  color: var(--text-main);
  font-size: 1.25rem;
  cursor: pointer;
}

.content {
  flex: 1;
  padding: 40px 32px;
  overflow-y: auto;
}

.announcement-card,
.announcement-modal,
.announcement-history-panel {
  padding: 20px 22px;
}

.announcement-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;
}

.announcement-badge {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 999px;
  background: rgba(0, 112, 243, 0.14);
  color: var(--text-main);
  font-size: 12px;
  font-weight: 600;
}

.announcement-badge.modal {
  background: rgba(255, 196, 0, 0.14);
}

.announcement-copy {
  margin: 0;
  white-space: pre-line;
  line-height: 1.7;
  color: var(--text-soft);
}

.announcement-modal-mask {
  position: fixed;
  inset: 0;
  z-index: 90;
  background: rgba(10, 12, 18, 0.68);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
}

.announcement-modal {
  width: min(100%, 620px);
  box-shadow: 0 20px 80px rgba(0, 0, 0, 0.35);
}

.announcement-history-panel {
  width: min(100%, 760px);
  max-height: min(80vh, 820px);
  display: flex;
  flex-direction: column;
  gap: 18px;
  box-shadow: 0 20px 80px rgba(0, 0, 0, 0.35);
}

.announcement-history-head {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: start;
}

.announcement-history-eyebrow {
  margin: 0 0 6px;
  font-size: 12px;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--text-soft);
}

.announcement-history-head h3 {
  margin: 0;
}

.announcement-history-empty {
  color: var(--text-soft);
}

.announcement-history-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
  overflow: auto;
  padding-right: 4px;
}

.announcement-history-item {
  border: 1px solid var(--border);
  border-radius: 16px;
  padding: 16px;
  background: rgba(255, 255, 255, 0.02);
}

.announcement-history-meta {
  margin: 0 0 10px;
  font-size: 12px;
  color: var(--text-soft);
}

.announcement-actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 18px;
}

@media (max-width: 768px) {
  .sidebar {
    position: fixed;
    inset: 0 auto 0 0;
    transform: translateX(-100%);
    transition: transform 0.2s;
  }

  .sidebar.open {
    transform: translateX(0);
  }

  .menu-toggle {
    display: block;
  }

  .topbar {
    padding: 0 20px;
  }

  .content {
    padding: 24px 20px;
  }

  .announcement-modal-mask {
    padding: 16px;
  }
}
</style>
