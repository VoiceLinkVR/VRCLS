<script setup lang="ts">
import { ref } from 'vue'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

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

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.changePassword(currentPassword.value, newPassword.value)
    successMessage.value = response.message
    currentPassword.value = ''
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
          <h2>修改登录密码</h2>
        </div>
      </div>

      <p class="panel-copy">
        控制台密码修改后，后续桌面端如果继续使用口令登录也必须同步更新。等客户端切到控制台授权登录后，密码输入会被进一步弱化。
      </p>

      <form class="form-grid" @submit.prevent="handleSubmit">
        <label>
          <span>当前密码</span>
          <input v-model="currentPassword" class="input-field" type="password" autocomplete="current-password" required />
        </label>
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

.error-text {
  color: #ffb4b4;
}

.success-text {
  color: #9af0c8;
}
</style>
