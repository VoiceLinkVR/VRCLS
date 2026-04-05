<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LocaleSwitcher from '../components/LocaleSwitcher.vue'
import { useI18n } from '../i18n'
import { useUserStore } from '../stores/user'

type Mode = 'login' | 'register'

const router = useRouter()
const route = useRoute()
const userStore = useUserStore()
const { t } = useI18n()

const authMode = ref<Mode>('login')
const form = ref({
  displayName: '',
  email: '',
  password: '',
})

const title = computed(() =>
  authMode.value === 'register' ? t('auth.titleRegister') : t('auth.titleLogin'),
)

const submitLabel = computed(() =>
  authMode.value === 'register' ? t('auth.submitRegister') : t('auth.submitLogin'),
)

const redirectTarget = computed(() => {
  const redirect = route.query.redirect
  return typeof redirect === 'string' ? redirect : '/app/overview'
})

async function handleSubmit() {
  if (userStore.loading) {
    return
  }

  try {
    if (authMode.value === 'register') {
      await userStore.register(
        form.value.email.trim(),
        form.value.password,
        form.value.displayName.trim() || form.value.email.split('@')[0] || 'VoiceLinkVR User',
      )
    } else {
      await userStore.login(form.value.email.trim(), form.value.password)
    }
  } catch {
    return
  }

  router.push(redirectTarget.value)
}
</script>

<template>
  <div class="auth-page">
    <div class="orb orb-a" />
    <div class="orb orb-b" />

    <section class="auth-shell glass-panel">
      <div class="auth-aside">
        <div class="aside-head">
          <div>
            <p class="aside-tag">{{ t('auth.asideTag') }}</p>
            <h1>{{ t('auth.introTitle') }}</h1>
          </div>
          <LocaleSwitcher />
        </div>
        <p>
          {{ t('auth.introCopy') }}
        </p>

        <div class="aside-points">
          <div class="aside-card glass-panel">
            <strong>{{ t('auth.sessionTitle') }}</strong>
            <span>{{ t('auth.sessionBody') }}</span>
          </div>
          <div class="aside-card glass-panel">
            <strong>{{ t('auth.billingTitle') }}</strong>
            <span>{{ t('auth.billingBody') }}</span>
          </div>
        </div>
      </div>

      <div class="auth-box">
        <div class="auth-head">
          <p class="auth-kicker">{{ t('brand.portal') }}</p>
          <h2>{{ title }}</h2>
        </div>

        <div class="mode-switch">
          <button type="button" :class="{ active: authMode === 'login' }" @click="authMode = 'login'">
            {{ t('auth.loginMode') }}
          </button>
          <button type="button" :class="{ active: authMode === 'register' }" @click="authMode = 'register'">
            {{ t('auth.registerMode') }}
          </button>
        </div>

        <form class="form-area" @submit.prevent="handleSubmit">
          <label v-if="authMode === 'register'" class="input-group">
            <span>{{ t('common.displayName') }}</span>
            <input
              v-model="form.displayName"
              type="text"
              class="input-field"
              :placeholder="t('auth.displayNamePlaceholder')"
              maxlength="100"
            />
          </label>

          <label class="input-group">
            <span>{{ t('common.email') }}</span>
            <input
              v-model="form.email"
              type="email"
              class="input-field"
              :placeholder="t('auth.emailPlaceholder')"
              required
            />
          </label>

          <label class="input-group">
            <span>{{ t('common.password') }}</span>
            <input
              v-model="form.password"
              type="password"
              class="input-field"
              :placeholder="t('auth.passwordPlaceholder')"
              minlength="6"
              required
            />
          </label>

          <p v-if="userStore.authError" class="error-copy">{{ userStore.authError }}</p>

          <button type="submit" class="btn btn-primary w-full" :disabled="userStore.loading">
            {{ userStore.loading ? t('auth.submitting') : submitLabel }}
          </button>
        </form>

        <router-link to="/" class="back-home">{{ t('common.backHome') }}</router-link>
      </div>
    </section>
  </div>
</template>

<style scoped>
.auth-page {
  position: relative;
  min-height: 100vh;
  display: grid;
  place-items: center;
  padding: 24px;
  overflow: hidden;
}

.orb {
  position: absolute;
  border-radius: 50%;
  filter: blur(30px);
  opacity: 0.5;
}

.orb-a {
  width: 240px;
  height: 240px;
  top: 8%;
  left: 12%;
  background: rgba(0, 229, 255, 0.16);
}

.orb-b {
  width: 300px;
  height: 300px;
  right: 10%;
  bottom: 6%;
  background: rgba(157, 78, 221, 0.16);
}

.auth-shell {
  position: relative;
  z-index: 1;
  width: min(100%, 1180px);
  display: grid;
  grid-template-columns: minmax(0, 1.06fr) minmax(340px, 0.94fr);
  overflow: hidden;
  background:
    linear-gradient(135deg, rgba(255, 255, 255, 0.02), transparent 35%),
    rgba(14, 18, 28, 0.88);
}

.auth-aside {
  padding: 56px;
  border-right: 1px solid var(--border-color);
  background:
    radial-gradient(circle at top left, rgba(0, 229, 255, 0.08), transparent 30%),
    rgba(10, 14, 22, 0.7);
}

.aside-head {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  align-items: flex-start;
}

.aside-tag,
.auth-kicker {
  color: var(--primary);
  font-size: 0.82rem;
  letter-spacing: 0.2em;
  text-transform: uppercase;
}

.auth-aside h1 {
  font-size: clamp(2.2rem, 5vw, 3.3rem);
  line-height: 1.05;
  margin: 20px 0 18px;
}

.auth-aside > p {
  color: var(--text-muted);
  max-width: 460px;
  line-height: 1.9;
}

.aside-points {
  display: grid;
  gap: 16px;
  margin-top: 34px;
}

.aside-card {
  padding: 20px;
}

.aside-card strong {
  display: block;
  margin-bottom: 8px;
}

.aside-card span {
  color: var(--text-muted);
  line-height: 1.8;
}

.auth-box {
  padding: 48px;
}

.auth-head h2 {
  margin-top: 12px;
  font-size: 2rem;
}

.mode-switch {
  margin: 28px 0 24px;
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 8px;
  padding: 6px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.mode-switch button {
  min-height: 46px;
  border: 0;
  border-radius: 14px;
  color: var(--text-muted);
  background: transparent;
  cursor: pointer;
  transition:
    background 0.22s ease,
    color 0.22s ease,
    transform 0.22s ease;
}

.mode-switch button.active {
  color: var(--text-main);
  background: rgba(255, 255, 255, 0.08);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
}

.form-area {
  margin-top: 8px;
}

.input-group {
  display: grid;
  gap: 10px;
  margin-bottom: 18px;
}

.input-group span {
  color: var(--text-muted);
  font-size: 0.9rem;
}

.error-copy {
  margin-bottom: 16px;
  color: #ffb4b4;
}

.back-home {
  display: inline-flex;
  margin-top: 18px;
  color: var(--text-muted);
}

@media (max-width: 980px) {
  .auth-shell {
    grid-template-columns: 1fr;
  }

  .auth-aside {
    border-right: 0;
    border-bottom: 1px solid var(--border-color);
  }
}

@media (max-width: 720px) {
  .aside-head {
    flex-direction: column;
  }
}

@media (max-width: 640px) {
  .auth-page {
    padding: 14px;
  }

  .auth-aside,
  .auth-box {
    padding: 28px 20px;
  }
}
</style>
