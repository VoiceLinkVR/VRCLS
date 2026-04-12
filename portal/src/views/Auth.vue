<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LocaleSwitcher from '../components/LocaleSwitcher.vue'
import { useI18n } from '../i18n'
import { useUserStore } from '../stores/user'

type EmailMode = 'login' | 'register'
type EntryMode = 'email' | 'device' | 'legacy'

const router = useRouter()
const route = useRoute()
const userStore = useUserStore()
const { t } = useI18n()

const emailMode = ref<EmailMode>('login')
const entryMode = ref<EntryMode>('email')
const registerChallengeToken = ref('')
const registerChallengeExpiresIn = ref(0)
const challengeLoading = ref(false)
const challengeMessage = ref('')
const deviceCode = ref('')
const legacyMessage = ref('')

const emailForm = ref({
  email: '',
  password: '',
  verificationCode: '',
})

const legacyForm = ref({
  legacyUsername: '',
  legacyPassword: '',
})

const redirectTarget = computed(() => {
  const redirect = route.query.redirect
  return typeof redirect === 'string' ? redirect : '/overview'
})

const noticeMessage = computed(() => {
  const notice = route.query.notice
  if (notice === 'password-reset') {
    return '密码已重置，请使用新密码登录。'
  }
  return ''
})

const emailTitle = computed(() =>
  emailMode.value === 'register' ? '邮箱注册' : '邮箱登录',
)

watch(
  () => route.query.tab,
  (nextTab) => {
    if (nextTab === 'device' || nextTab === 'legacy' || nextTab === 'email') {
      entryMode.value = nextTab
    }
  },
  { immediate: true },
)

async function sendRegisterCode() {
  if (!emailForm.value.email.trim() || challengeLoading.value) {
    return
  }

  challengeLoading.value = true
  challengeMessage.value = ''
  try {
    const response = await userStore.sendRegisterCode(emailForm.value.email.trim())
    registerChallengeToken.value = response.verification_token
    registerChallengeExpiresIn.value = response.expires_in
    challengeMessage.value = '验证码已发送，请检查邮箱。'
  } catch (error) {
    challengeMessage.value = error instanceof Error ? error.message : '验证码发送失败。'
  } finally {
    challengeLoading.value = false
  }
}

async function handleEmailSubmit() {
  if (userStore.loading) {
    return
  }

  try {
    if (emailMode.value === 'register') {
      if (!registerChallengeToken.value) {
        challengeMessage.value = '请先发送注册验证码。'
        return
      }

      await userStore.register(
        emailForm.value.email.trim(),
        emailForm.value.password,
        registerChallengeToken.value,
        emailForm.value.verificationCode.trim(),
      )
    } else {
      await userStore.login(emailForm.value.email.trim(), emailForm.value.password)
    }
  } catch {
    return
  }

  router.push(redirectTarget.value)
}

async function handleLegacySubmit() {
  if (userStore.loading) {
    return
  }

  legacyMessage.value = ''

  try {
    await userStore.legacyLogin(
      legacyForm.value.legacyUsername.trim(),
      legacyForm.value.legacyPassword,
    )
    await userStore.bootstrap(true)

    legacyMessage.value = '旧账号验证成功，请继续完成邮箱绑定和验证。'

    if (userStore.isAuthenticated) {
      router.push('/center')
    }
  } catch (error) {
    legacyMessage.value = error instanceof Error ? error.message : '旧账号迁移登录失败。'
  }
}

function openAuthorizePage() {
  const query = deviceCode.value.trim() ? { user_code: deviceCode.value.trim().toUpperCase() } : undefined
  router.push({ path: '/authorize', query })
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
            <strong>v2 主登录</strong>
            <span>新控制台默认使用邮箱密码登录，注册时不再要求昵称或用户名。</span>
          </div>
          <div class="aside-card glass-panel">
            <strong>旧账号迁移</strong>
            <span>旧用户名和旧密码仅作为迁移入口，验证后需要立即绑定并验证邮箱。</span>
          </div>
          <div class="aside-card glass-panel">
            <strong>控制台授权</strong>
            <span>桌面客户端推荐使用设备授权登录，不再把测试账号当成真实登录身份。</span>
          </div>
        </div>
      </div>

      <div class="auth-box">
        <div class="auth-head">
          <p class="auth-kicker">{{ t('brand.portal') }}</p>
          <h2>{{ entryMode === 'email' ? emailTitle : entryMode === 'device' ? '控制台授权登录' : '旧账号迁移' }}</h2>
          <p v-if="noticeMessage" class="helper-copy success-copy">{{ noticeMessage }}</p>
        </div>

        <div class="entry-switch">
          <button type="button" :class="{ active: entryMode === 'email' }" @click="entryMode = 'email'">
            邮箱登录
          </button>
          <button type="button" :class="{ active: entryMode === 'device' }" @click="entryMode = 'device'">
            控制台授权
          </button>
          <button type="button" :class="{ active: entryMode === 'legacy' }" @click="entryMode = 'legacy'">
            旧账号迁移
          </button>
        </div>

        <template v-if="entryMode === 'email'">
          <div class="mode-switch">
            <button type="button" :class="{ active: emailMode === 'login' }" @click="emailMode = 'login'">
              {{ t('auth.loginMode') }}
            </button>
            <button type="button" :class="{ active: emailMode === 'register' }" @click="emailMode = 'register'">
              {{ t('auth.registerMode') }}
            </button>
          </div>

          <form class="form-area" @submit.prevent="handleEmailSubmit">
            <label class="input-group">
              <span>{{ t('common.email') }}</span>
              <input
                v-model="emailForm.email"
                type="email"
                class="input-field"
                :placeholder="t('auth.emailPlaceholder')"
                required
              />
            </label>

            <label class="input-group">
              <span>{{ t('common.password') }}</span>
              <input
                v-model="emailForm.password"
                type="password"
                class="input-field"
                :placeholder="t('auth.passwordPlaceholder')"
                minlength="6"
                required
              />
            </label>

            <template v-if="emailMode === 'register'">
              <label class="input-group">
                <span>邮箱验证码</span>
                <div class="inline-row">
                  <input
                    v-model="emailForm.verificationCode"
                    type="text"
                    class="input-field"
                    placeholder="输入邮箱中的验证码"
                    minlength="4"
                    maxlength="16"
                    required
                  />
                  <button class="btn btn-outline" type="button" :disabled="challengeLoading" @click="sendRegisterCode">
                    {{ challengeLoading ? '发送中...' : '发送验证码' }}
                  </button>
                </div>
              </label>
              <p v-if="challengeMessage" class="helper-copy">{{ challengeMessage }}</p>
              <p v-if="registerChallengeExpiresIn" class="helper-copy">
                验证码有效期 {{ Math.floor(registerChallengeExpiresIn / 60) }} 分钟。
              </p>
            </template>

            <p v-if="userStore.authError" class="error-copy">{{ userStore.authError }}</p>

            <button type="submit" class="btn btn-primary w-full" :disabled="userStore.loading">
              {{ userStore.loading ? t('auth.submitting') : emailMode === 'register' ? '注册并进入控制台' : '登录控制台' }}
            </button>
          </form>

          <router-link to="/reset-password" class="back-home">忘记密码？</router-link>
        </template>

        <template v-else-if="entryMode === 'device'">
          <div class="entry-panel">
            <p class="panel-copy">
              客户端发起设备授权后，会显示一个授权码。你可以在这里输入授权码并进入确认页，也可以直接打开授权页后再粘贴。
            </p>

            <label class="input-group">
              <span>授权码</span>
              <input
                v-model="deviceCode"
                type="text"
                class="input-field"
                placeholder="例如 ABCD-EFGH"
                autocomplete="off"
              />
            </label>

            <button type="button" class="btn btn-primary w-full" @click="openAuthorizePage">
              打开控制台授权页
            </button>
          </div>
        </template>

        <template v-else>
          <form class="form-area" @submit.prevent="handleLegacySubmit">
            <p class="panel-copy">
              这里只用于旧账号升级，不是长期主登录方式。验证通过后，你需要立即绑定邮箱并完成邮箱验证。
            </p>

            <label class="input-group">
              <span>旧用户名</span>
              <input
                v-model="legacyForm.legacyUsername"
                type="text"
                class="input-field"
                placeholder="输入旧系统用户名"
                autocomplete="username"
                required
              />
            </label>

            <label class="input-group">
              <span>旧密码</span>
              <input
                v-model="legacyForm.legacyPassword"
                type="password"
                class="input-field"
                placeholder="输入旧系统密码"
                autocomplete="current-password"
                required
              />
            </label>

            <p v-if="legacyMessage" class="helper-copy" :class="{ 'error-copy': !userStore.isMigrationPending, 'success-copy': userStore.isMigrationPending }">
              {{ legacyMessage }}
            </p>

            <button type="submit" class="btn btn-primary w-full" :disabled="userStore.loading">
              {{ userStore.loading ? '验证中...' : '验证旧账号并开始迁移' }}
            </button>
          </form>
        </template>

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

.aside-card span,
.panel-copy {
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

.entry-switch,
.mode-switch {
  margin: 28px 0 24px;
  display: grid;
  gap: 8px;
  padding: 6px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.entry-switch {
  grid-template-columns: repeat(3, 1fr);
}

.mode-switch {
  grid-template-columns: repeat(2, 1fr);
}

.entry-switch button,
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

.entry-switch button.active,
.mode-switch button.active {
  color: var(--text-main);
  background: rgba(255, 255, 255, 0.08);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
}

.form-area,
.entry-panel {
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

.inline-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 12px;
}

.helper-copy {
  margin-bottom: 12px;
  color: var(--text-muted);
}

.error-copy {
  color: #ffb4b4;
}

.success-copy {
  color: #9af0c8;
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

  .inline-row,
  .entry-switch {
    grid-template-columns: 1fr;
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
