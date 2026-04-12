<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const verificationToken = ref('')
const verificationCode = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const legacyUsername = ref('')
const legacyPassword = ref('')
const loading = ref(false)
const sending = ref(false)
const linking = ref(false)
const errorMessage = ref('')
const successMessage = ref('')
const legacyErrorMessage = ref('')
const legacySuccessMessage = ref('')

const canUseEmailSecurity = computed(() => Boolean(userStore.profile?.email) && userStore.profile?.emailVerified)
const hasLinkedLegacyAccount = computed(() => Boolean(userStore.profile?.legacyUsername))

async function sendCode() {
  if (sending.value) {
    return
  }
  sending.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.sendChangePasswordCode()
    verificationToken.value = response.token
    successMessage.value = '验证码已发送，请检查已验证邮箱。'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '验证码发送失败。'
  } finally {
    sending.value = false
  }
}

async function handlePasswordSubmit() {
  if (loading.value) {
    return
  }
  if (newPassword.value.length < 6) {
    errorMessage.value = '新密码至少需要 6 位。'
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    errorMessage.value = '两次输入的新密码不一致。'
    return
  }
  if (!verificationToken.value) {
    errorMessage.value = '请先发送验证码。'
    return
  }

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.changePasswordWithCode(
      verificationToken.value,
      verificationCode.value.trim(),
      newPassword.value,
    )
    successMessage.value = response.message
    verificationCode.value = ''
    newPassword.value = ''
    confirmPassword.value = ''
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '修改密码失败。'
  } finally {
    loading.value = false
  }
}

async function handleLegacyLink() {
  if (linking.value) {
    return
  }

  linking.value = true
  legacyErrorMessage.value = ''
  legacySuccessMessage.value = ''

  try {
    const response = await userStore.legacyLink(legacyUsername.value.trim(), legacyPassword.value)
    legacySuccessMessage.value = response.message || '旧账号已成功绑定到当前邮箱账号。'
    legacyPassword.value = ''
    await userStore.bootstrap(true)
  } catch (error) {
    legacyErrorMessage.value = error instanceof Error ? error.message : '旧账号绑定失败。'
  } finally {
    linking.value = false
  }
}
</script>

<template>
  <section class="page-grid">
    <section class="glass-panel panel-section migration-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Migration</p>
          <h2>账户迁移与身份状态</h2>
        </div>
      </div>

      <div class="migration-grid">
        <article class="glass-panel info-card">
          <span>当前显示名</span>
          <strong>{{ userStore.profile?.name || '未登录' }}</strong>
        </article>
        <article class="glass-panel info-card">
          <span>业务主标识</span>
          <strong>{{ userStore.profile?.shortUserUuid || userStore.migrationSession?.legacyUsername || '--' }}</strong>
        </article>
        <article class="glass-panel info-card">
          <span>旧账号来源</span>
          <strong>{{ userStore.profile?.legacyUsername || userStore.migrationSession?.legacyUsername || '未绑定' }}</strong>
        </article>
      </div>

      <p class="panel-copy">
        v2 主身份已经切换到 `user_uuid + email`。旧用户名只保留为迁移来源或兼容信息，不再作为长期主登录方式。
      </p>

      <div v-if="userStore.isMigrationPending || !canUseEmailSecurity" class="migration-banner glass-panel">
        <div>
          <strong>迁移中的账号需要先完成邮箱绑定</strong>
          <p>
            在邮箱完成绑定和验证之前，账户会保持迁移态或受限态，无法使用完整的计费和安全能力。
          </p>
        </div>
        <RouterLink class="btn btn-primary" to="/email">前往绑定邮箱</RouterLink>
      </div>
    </section>

    <section class="glass-panel panel-section form-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Security</p>
          <h2>邮箱验证码改密</h2>
        </div>
      </div>

      <template v-if="!canUseEmailSecurity">
        <p class="panel-copy">
          当前账号还没有绑定并验证邮箱。为了启用找回密码、验证码改密和安全通知，请先完成邮箱绑定。
        </p>
        <RouterLink class="btn btn-primary" to="/email">前往绑定邮箱</RouterLink>
      </template>

      <template v-else>
        <p class="panel-copy">
          修改密码不再依赖当前密码，系统会把验证码发送到已验证邮箱
          <strong>{{ userStore.profile?.email }}</strong>。
        </p>

        <form class="form-grid" @submit.prevent="handlePasswordSubmit">
          <div class="inline-row">
            <label>
              <span>邮箱验证码</span>
              <input v-model="verificationCode" class="input-field" type="text" minlength="4" maxlength="16" required />
            </label>
            <button class="btn btn-outline send-btn" type="button" :disabled="sending" @click="sendCode">
              {{ sending ? '发送中...' : '发送验证码' }}
            </button>
          </div>
          <label>
            <span>新密码</span>
            <input v-model="newPassword" class="input-field" type="password" autocomplete="new-password" minlength="6" required />
          </label>
          <label>
            <span>确认新密码</span>
            <input v-model="confirmPassword" class="input-field" type="password" autocomplete="new-password" minlength="6" required />
          </label>

          <p v-if="errorMessage" class="error-text">{{ errorMessage }}</p>
          <p v-if="successMessage" class="success-text">{{ successMessage }}</p>

          <button class="btn btn-primary" type="submit" :disabled="loading">
            {{ loading ? '提交中...' : '更新密码' }}
          </button>
        </form>
      </template>
    </section>

    <section class="glass-panel panel-section form-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Legacy Link</p>
          <h2>绑定旧账号</h2>
        </div>
      </div>

      <p class="panel-copy">
        已登录邮箱账号可以在这里绑定一个旧账号，用于迁移旧订单、旧额度和历史资产。旧账号只能绑定一次，不会覆盖当前邮箱账号的主身份。
      </p>

      <p v-if="hasLinkedLegacyAccount" class="success-text">
        当前账户已记录旧账号来源：{{ userStore.profile?.legacyUsername }}
      </p>

      <form class="form-grid" @submit.prevent="handleLegacyLink">
        <label>
          <span>旧用户名</span>
          <input v-model="legacyUsername" class="input-field" type="text" autocomplete="username" required />
        </label>
        <label>
          <span>旧密码</span>
          <input v-model="legacyPassword" class="input-field" type="password" autocomplete="current-password" required />
        </label>

        <p v-if="legacyErrorMessage" class="error-text">{{ legacyErrorMessage }}</p>
        <p v-if="legacySuccessMessage" class="success-text">{{ legacySuccessMessage }}</p>

        <button class="btn btn-primary" type="submit" :disabled="linking">
          {{ linking ? '绑定中...' : '绑定旧账号' }}
        </button>
      </form>
    </section>
  </section>
</template>

<style scoped>
.migration-card,
.form-card {
  max-width: 860px;
}

.migration-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 20px;
}

.info-card {
  padding: 18px;
  display: grid;
  gap: 8px;
}

.info-card span {
  color: var(--text-muted);
}

.migration-banner {
  margin-top: 20px;
  padding: 18px 20px;
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: center;
  border-color: rgba(255, 196, 91, 0.28);
}

.migration-banner p,
.panel-copy {
  color: var(--text-muted);
  line-height: 1.8;
}

.panel-copy {
  margin-bottom: 24px;
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

@media (max-width: 720px) {
  .migration-grid,
  .inline-row {
    grid-template-columns: 1fr;
  }

  .migration-banner {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
