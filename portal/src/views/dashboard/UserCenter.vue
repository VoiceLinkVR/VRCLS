<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { formatDateTime } from '../../i18n'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const profileDisplayName = ref(userStore.profile?.name ?? '')
const profileLoading = ref(false)
const profileErrorMessage = ref('')
const profileSuccessMessage = ref('')

const email = ref(userStore.profile?.email ?? '')
const verificationToken = ref('')
const verificationCode = ref('')
const emailLoading = ref(false)
const verifyLoading = ref(false)
const emailErrorMessage = ref('')
const emailSuccessMessage = ref('')

const passwordVerificationToken = ref('')
const passwordVerificationCode = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const passwordLoading = ref(false)
const passwordSending = ref(false)
const passwordErrorMessage = ref('')
const passwordSuccessMessage = ref('')

const legacyUsername = ref('')
const legacyPassword = ref('')
const legacyLoading = ref(false)
const legacyErrorMessage = ref('')
const legacySuccessMessage = ref('')

watch(
  () => userStore.profile,
  (profile) => {
    profileDisplayName.value = profile?.name ?? ''
    email.value = profile?.email ?? ''
  },
  { immediate: true },
)

const emailVerified = computed(() => Boolean(userStore.profile?.email && userStore.profile?.emailVerified))
const canUseEmailSecurity = computed(() => emailVerified.value)
const hasLinkedLegacyAccount = computed(() => Boolean(userStore.profile?.legacyUsername))
const showLegacyForm = computed(() => userStore.isMigrationPending || !hasLinkedLegacyAccount.value)

async function saveProfile() {
  if (profileLoading.value) {
    return
  }

  profileLoading.value = true
  profileErrorMessage.value = ''
  profileSuccessMessage.value = ''

  try {
    const nextProfile = await userStore.updateProfile({ displayName: profileDisplayName.value.trim() })
    profileDisplayName.value = nextProfile?.name ?? profileDisplayName.value.trim()
    profileSuccessMessage.value = '基础资料已更新。'
  } catch (error) {
    profileErrorMessage.value = error instanceof Error ? error.message : '基础资料更新失败。'
  } finally {
    profileLoading.value = false
  }
}

async function sendEmailCode() {
  if (emailLoading.value) {
    return
  }

  emailLoading.value = true
  emailErrorMessage.value = ''
  emailSuccessMessage.value = ''

  try {
    const response = await userStore.bindEmail(email.value.trim())
    verificationToken.value = response.token
    emailSuccessMessage.value = '验证码已发送，请检查目标邮箱。'
  } catch (error) {
    emailErrorMessage.value = error instanceof Error ? error.message : '发送邮箱验证码失败。'
  } finally {
    emailLoading.value = false
  }
}

async function verifyEmailCode() {
  if (verifyLoading.value) {
    return
  }
  if (!verificationToken.value) {
    emailErrorMessage.value = '请先发送验证码。'
    return
  }

  verifyLoading.value = true
  emailErrorMessage.value = ''
  emailSuccessMessage.value = ''

  try {
    const response = await userStore.verifyEmail(verificationToken.value, verificationCode.value.trim())
    verificationCode.value = ''
    verificationToken.value = ''
    emailSuccessMessage.value = response.message || '邮箱已验证。'
  } catch (error) {
    emailErrorMessage.value = error instanceof Error ? error.message : '邮箱验证失败。'
  } finally {
    verifyLoading.value = false
  }
}

async function sendPasswordCode() {
  if (passwordSending.value) {
    return
  }

  passwordSending.value = true
  passwordErrorMessage.value = ''
  passwordSuccessMessage.value = ''

  try {
    const response = await userStore.sendChangePasswordCode()
    passwordVerificationToken.value = response.token
    passwordSuccessMessage.value = '验证码已发送，请检查已验证邮箱。'
  } catch (error) {
    passwordErrorMessage.value = error instanceof Error ? error.message : '发送验证码失败。'
  } finally {
    passwordSending.value = false
  }
}

async function updatePassword() {
  if (passwordLoading.value) {
    return
  }
  if (!passwordVerificationToken.value) {
    passwordErrorMessage.value = '请先发送验证码。'
    return
  }
  if (newPassword.value.length < 6) {
    passwordErrorMessage.value = '新密码至少需要 6 位。'
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    passwordErrorMessage.value = '两次输入的新密码不一致。'
    return
  }

  passwordLoading.value = true
  passwordErrorMessage.value = ''
  passwordSuccessMessage.value = ''

  try {
    const response = await userStore.changePasswordWithCode(
      passwordVerificationToken.value,
      passwordVerificationCode.value.trim(),
      newPassword.value,
    )
    passwordSuccessMessage.value = response.message || '密码已更新。'
    passwordVerificationCode.value = ''
    passwordVerificationToken.value = ''
    newPassword.value = ''
    confirmPassword.value = ''
  } catch (error) {
    passwordErrorMessage.value = error instanceof Error ? error.message : '修改密码失败。'
  } finally {
    passwordLoading.value = false
  }
}

async function bindLegacyAccount() {
  if (legacyLoading.value) {
    return
  }

  legacyLoading.value = true
  legacyErrorMessage.value = ''
  legacySuccessMessage.value = ''

  try {
    const response = await userStore.legacyLink(legacyUsername.value.trim(), legacyPassword.value)
    legacyPassword.value = ''
    legacySuccessMessage.value = response.message || '旧账号已绑定到当前邮箱账号。'
  } catch (error) {
    legacyErrorMessage.value = error instanceof Error ? error.message : '旧账号绑定失败。'
  } finally {
    legacyLoading.value = false
  }
}
</script>

<template>
  <section class="page-grid">
    <section class="glass-panel panel-section center-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Profile</p>
          <h2>基础资料</h2>
        </div>
      </div>

      <div class="summary-grid">
        <article class="glass-panel summary-card">
          <span>用户昵称</span>
          <strong>{{ userStore.profile?.name || '--' }}</strong>
        </article>
        <article class="glass-panel summary-card">
          <span>业务标识</span>
          <strong>{{ userStore.profile?.shortUserUuid || '--' }}</strong>
        </article>
        <article class="glass-panel summary-card">
          <span>邮箱状态</span>
          <strong>{{ emailVerified ? '已验证' : '未验证' }}</strong>
        </article>
        <article class="glass-panel summary-card">
          <span>最近登录</span>
          <strong>{{ userStore.profile?.lastLoginAt ? formatDateTime(userStore.profile.lastLoginAt) : '--' }}</strong>
        </article>
      </div>

      <form class="form-grid" @submit.prevent="saveProfile">
        <label>
          <span>昵称 / Display Name</span>
          <input
            v-model="profileDisplayName"
            class="input-field"
            type="text"
            maxlength="100"
            placeholder="输入你希望在控制台展示的昵称"
          />
        </label>
        <label>
          <span>当前邮箱</span>
          <input class="input-field" :value="userStore.profile?.email || '未绑定邮箱'" type="text" readonly />
        </label>
        <label>
          <span>旧账号来源</span>
          <input class="input-field" :value="userStore.profile?.legacyUsername || '未绑定'" type="text" readonly />
        </label>

        <p v-if="profileErrorMessage" class="error-text">{{ profileErrorMessage }}</p>
        <p v-if="profileSuccessMessage" class="success-text">{{ profileSuccessMessage }}</p>

        <button class="btn btn-primary" type="submit" :disabled="profileLoading">
          {{ profileLoading ? '保存中...' : '保存基础资料' }}
        </button>
      </form>
    </section>

    <section class="glass-panel panel-section center-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Email</p>
          <h2>邮箱绑定与验证</h2>
        </div>
      </div>

      <p class="panel-copy">
        所有验证码安全能力都依赖已验证邮箱。旧用户完成邮箱验证后，就不再需要单独的迁移标签页。
      </p>

      <form class="form-grid" @submit.prevent="verifyEmailCode">
        <label>
          <span>目标邮箱地址</span>
          <input v-model="email" class="input-field" type="email" autocomplete="email" required />
        </label>
        <div class="inline-row">
          <label>
            <span>邮箱验证码</span>
            <input
              v-model="verificationCode"
              class="input-field"
              type="text"
              minlength="4"
              maxlength="16"
              required
            />
          </label>
          <button class="btn btn-outline send-btn" type="button" :disabled="emailLoading" @click="sendEmailCode">
            {{ emailLoading ? '发送中...' : '发送验证码' }}
          </button>
        </div>

        <p v-if="emailErrorMessage" class="error-text">{{ emailErrorMessage }}</p>
        <p v-if="emailSuccessMessage" class="success-text">{{ emailSuccessMessage }}</p>

        <button class="btn btn-primary" type="submit" :disabled="verifyLoading">
          {{ verifyLoading ? '验证中...' : '确认绑定邮箱' }}
        </button>
      </form>
    </section>

    <section class="glass-panel panel-section center-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Security</p>
          <h2>密码与账户安全</h2>
        </div>
      </div>

      <template v-if="!canUseEmailSecurity">
        <p class="panel-copy">
          当前账户还没有完成邮箱验证。请先在上方完成邮箱绑定与验证，再使用验证码改密功能。
        </p>
      </template>

      <template v-else>
        <form class="form-grid" @submit.prevent="updatePassword">
          <div class="inline-row">
            <label>
              <span>邮箱验证码</span>
              <input
                v-model="passwordVerificationCode"
                class="input-field"
                type="text"
                minlength="4"
                maxlength="16"
                required
              />
            </label>
            <button
              class="btn btn-outline send-btn"
              type="button"
              :disabled="passwordSending"
              @click="sendPasswordCode"
            >
              {{ passwordSending ? '发送中...' : '发送验证码' }}
            </button>
          </div>
          <label>
            <span>新密码</span>
            <input
              v-model="newPassword"
              class="input-field"
              type="password"
              autocomplete="new-password"
              minlength="6"
              required
            />
          </label>
          <label>
            <span>确认新密码</span>
            <input
              v-model="confirmPassword"
              class="input-field"
              type="password"
              autocomplete="new-password"
              minlength="6"
              required
            />
          </label>

          <p v-if="passwordErrorMessage" class="error-text">{{ passwordErrorMessage }}</p>
          <p v-if="passwordSuccessMessage" class="success-text">{{ passwordSuccessMessage }}</p>

          <button class="btn btn-primary" type="submit" :disabled="passwordLoading">
            {{ passwordLoading ? '提交中...' : '更新密码' }}
          </button>
        </form>
      </template>
    </section>

    <section class="glass-panel panel-section center-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Legacy</p>
          <h2>旧账号归并</h2>
        </div>
      </div>

      <p class="panel-copy">
        旧账号绑定能力现在收口在用户中心中，不再单独占一个迁移标签页。需要时可以在这里把历史订单和权益归并到当前邮箱账号。
      </p>

      <p v-if="hasLinkedLegacyAccount && !showLegacyForm" class="success-text">
        当前账号已记录旧账号来源：{{ userStore.profile?.legacyUsername }}
      </p>

      <form v-if="showLegacyForm" class="form-grid" @submit.prevent="bindLegacyAccount">
        <label>
          <span>旧用户名</span>
          <input v-model="legacyUsername" class="input-field" type="text" autocomplete="username" required />
        </label>
        <label>
          <span>旧密码</span>
          <input
            v-model="legacyPassword"
            class="input-field"
            type="password"
            autocomplete="current-password"
            required
          />
        </label>

        <p v-if="legacyErrorMessage" class="error-text">{{ legacyErrorMessage }}</p>
        <p v-if="legacySuccessMessage" class="success-text">{{ legacySuccessMessage }}</p>

        <button class="btn btn-primary" type="submit" :disabled="legacyLoading">
          {{ legacyLoading ? '绑定中...' : '绑定旧账号' }}
        </button>
      </form>
    </section>
  </section>
</template>

<style scoped>
.center-card {
  max-width: 920px;
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 24px;
}

.summary-card {
  display: grid;
  gap: 8px;
  padding: 18px;
}

.summary-card span,
.panel-copy {
  color: var(--text-muted);
}

.summary-card strong {
  font-size: 1.05rem;
  line-height: 1.5;
}

.panel-copy {
  margin-bottom: 24px;
  line-height: 1.8;
}

.form-grid {
  display: grid;
  gap: 16px;
}

.form-grid label {
  display: grid;
  gap: 8px;
}

.inline-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 12px;
  align-items: end;
}

.send-btn {
  min-height: 46px;
}

.error-text {
  color: #ffb4b4;
}

.success-text {
  color: #9af0c8;
}

@media (max-width: 1040px) {
  .summary-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 720px) {
  .summary-grid,
  .inline-row {
    grid-template-columns: 1fr;
  }
}
</style>
