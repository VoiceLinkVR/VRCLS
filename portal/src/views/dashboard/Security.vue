<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const verificationToken = ref('')
const verificationCode = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const sending = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const canUseEmailSecurity = computed(() => Boolean(userStore.profile?.email) && userStore.profile?.emailVerified)

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
    successMessage.value = '验证码已发送，请检查绑定邮箱。'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '验证码发送失败。'
  } finally {
    sending.value = false
  }
}

async function handleSubmit() {
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
</script>

<template>
  <section class="page-grid">
    <section class="glass-panel panel-section form-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Security</p>
          <h2>邮箱验证码改密</h2>
        </div>
      </div>

      <template v-if="!canUseEmailSecurity">
        <p class="panel-copy">
          当前账号还没有绑定并验证邮箱。为了正式启用注册验证码、找回密码和安全改密流程，请先完成邮箱绑定。
        </p>
        <RouterLink class="btn btn-primary" to="/email">前往绑定邮箱</RouterLink>
      </template>

      <template v-else>
        <p class="panel-copy">
          改密码不再依赖当前密码，系统会把验证码发送到已验证邮箱
          <strong>{{ userStore.profile?.email }}</strong>。
        </p>

        <form class="form-grid" @submit.prevent="handleSubmit">
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
  </section>
</template>

<style scoped>
.form-card {
  max-width: 760px;
}

.panel-copy {
  margin-bottom: 24px;
  color: var(--text-muted);
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

@media (max-width: 720px) {
  .inline-row {
    grid-template-columns: 1fr;
  }
}
</style>
