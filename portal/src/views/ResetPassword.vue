<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useUserStore } from '../stores/user'

const router = useRouter()
const userStore = useUserStore()

const email = ref('')
const token = ref('')
const code = ref('')
const password = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')
const redirectCountdown = ref(0)

let redirectTimer: number | null = null

function clearRedirectTimer() {
  if (redirectTimer) {
    window.clearInterval(redirectTimer)
    redirectTimer = null
  }
}

function goToAuth() {
  clearRedirectTimer()
  router.push({ path: '/auth', query: { notice: 'password-reset' } })
}

function startRedirectCountdown() {
  clearRedirectTimer()
  redirectCountdown.value = 2

  redirectTimer = window.setInterval(() => {
    if (redirectCountdown.value <= 1) {
      goToAuth()
      return
    }
    redirectCountdown.value -= 1
  }, 1000)
}

async function requestCode() {
  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.forgotPassword(email.value.trim())
    token.value = response.token
    successMessage.value = '验证码已发送。如果邮箱存在且已验证，请检查收件箱。'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '验证码发送失败。'
  } finally {
    loading.value = false
  }
}

async function resetPassword() {
  if (password.value.length < 6) {
    errorMessage.value = '新密码至少需要 6 位。'
    return
  }
  if (password.value !== confirmPassword.value) {
    errorMessage.value = '两次输入的新密码不一致。'
    return
  }
  if (!token.value) {
    errorMessage.value = '请先发送验证码。'
    return
  }

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  clearRedirectTimer()

  try {
    const response = await userStore.resetPassword(token.value, code.value.trim(), password.value)
    successMessage.value = response.message || 'Password reset complete'
    startRedirectCountdown()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '重置密码失败。'
  } finally {
    loading.value = false
  }
}

onBeforeUnmount(() => {
  clearRedirectTimer()
})
</script>

<template>
  <div class="single-page">
    <section class="glass-panel single-card">
      <p class="eyebrow">Reset Password</p>
      <h1>通过邮箱验证码重置密码</h1>

      <form class="form-grid" @submit.prevent="resetPassword">
        <label>
          <span>账户邮箱</span>
          <input v-model="email" class="input-field" type="email" autocomplete="email" required />
        </label>
        <button class="btn btn-outline" type="button" :disabled="loading" @click="requestCode">
          {{ loading ? '处理中...' : '发送验证码' }}
        </button>
        <label>
          <span>邮箱验证码</span>
          <input v-model="code" class="input-field" type="text" minlength="4" maxlength="16" required />
        </label>
        <label>
          <span>新密码</span>
          <input v-model="password" class="input-field" type="password" minlength="6" required />
        </label>
        <label>
          <span>确认新密码</span>
          <input v-model="confirmPassword" class="input-field" type="password" minlength="6" required />
        </label>
        <button class="btn btn-primary" type="submit" :disabled="loading">
          {{ loading ? '处理中...' : '提交新密码' }}
        </button>
      </form>

      <p v-if="errorMessage" class="error-text">{{ errorMessage }}</p>
      <div v-if="successMessage" class="success-block">
        <p class="success-text">{{ successMessage }}</p>
        <p v-if="redirectCountdown > 0" class="helper-text">
          {{ redirectCountdown }} 秒后自动返回登录页。
        </p>
        <button type="button" class="btn btn-outline" @click="goToAuth">立即前往登录</button>
      </div>
    </section>
  </div>
</template>

<style scoped>
.single-page {
  min-height: 100vh;
  padding: 24px;
  display: grid;
  place-items: center;
}

.single-card {
  width: min(100%, 720px);
  padding: 32px;
  display: grid;
  gap: 20px;
}

.form-grid {
  display: grid;
  gap: 16px;
}

.form-grid label {
  display: grid;
  gap: 8px;
}

.error-text {
  color: #ffb4b4;
}

.success-block {
  display: grid;
  gap: 12px;
}

.success-text {
  color: #9af0c8;
}

.helper-text {
  color: var(--text-muted);
}
</style>
