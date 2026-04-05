<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { useUserStore } from '../stores/user'

const route = useRoute()
const userStore = useUserStore()

const token = ref(typeof route.query.token === 'string' ? route.query.token : '')
const loading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')
const hasToken = computed(() => Boolean(token.value.trim()))

async function handleVerify() {
  if (!hasToken.value) {
    errorMessage.value = '缺少邮箱验证 token。'
    return
  }

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.verifyEmail(token.value.trim())
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
      <h1>验证邮箱</h1>
      <p class="lead">当前阶段使用 token 流程完成邮箱验证，后续接入邮件服务后，这里将直接承接邮件跳转。</p>

      <label class="input-group">
        <span>验证 token</span>
        <input v-model="token" class="input-field" type="text" required />
      </label>

      <button class="btn btn-primary" type="button" :disabled="loading || !hasToken" @click="handleVerify">
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

.lead {
  color: var(--text-muted);
  line-height: 1.8;
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
