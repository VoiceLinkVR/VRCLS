<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore } from '../stores/user'

const route = useRoute()
const router = useRouter()
const userStore = useUserStore()

const email = ref('')
const token = ref(typeof route.query.token === 'string' ? route.query.token : '')
const password = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const hasToken = computed(() => Boolean(token.value.trim()))

async function requestToken() {
  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.forgotPassword(email.value.trim())
    successMessage.value = response.message
    if (response.token) {
      token.value = response.token
      await router.replace({ path: '/reset-password', query: { token: response.token } })
    }
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '生成重置链接失败。'
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

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.resetPassword(token.value.trim(), password.value)
    successMessage.value = response.message
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '重置密码失败。'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="single-page">
    <section class="glass-panel single-card">
      <p class="eyebrow">Reset Password</p>
      <h1>重置密码</h1>

      <form v-if="!hasToken" class="form-grid" @submit.prevent="requestToken">
        <label>
          <span>账户邮箱</span>
          <input v-model="email" class="input-field" type="email" autocomplete="email" required />
        </label>
        <button class="btn btn-primary" type="submit" :disabled="loading">
          {{ loading ? '处理中...' : '生成重置链接' }}
        </button>
      </form>

      <form v-else class="form-grid" @submit.prevent="resetPassword">
        <label>
          <span>重置 token</span>
          <input v-model="token" class="input-field" type="text" required />
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
      <p v-if="successMessage" class="success-text">{{ successMessage }}</p>
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

.success-text {
  color: #9af0c8;
}
</style>
