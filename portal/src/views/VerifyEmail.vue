<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { useUserStore } from '../stores/user'

const route = useRoute()
const userStore = useUserStore()

const token = ref(typeof route.query.token === 'string' ? route.query.token : '')
const code = ref('')
const loading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

async function handleVerify() {
  if (!token.value.trim()) {
    errorMessage.value = '缺少验证 token。'
    return
  }

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.verifyEmail(token.value.trim(), code.value.trim())
    successMessage.value = response.message
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '邮箱验证失败。'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="single-page">
    <section class="glass-panel single-card">
      <p class="eyebrow">Verify Email</p>
      <h1>确认邮箱验证码</h1>

      <label class="input-group">
        <span>验证 token</span>
        <input v-model="token" class="input-field" type="text" required />
      </label>
      <label class="input-group">
        <span>邮箱验证码</span>
        <input v-model="code" class="input-field" type="text" minlength="4" maxlength="16" required />
      </label>

      <button class="btn btn-primary" type="button" :disabled="loading" @click="handleVerify">
        {{ loading ? '处理中...' : '确认验证邮箱' }}
      </button>

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

.input-group {
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
