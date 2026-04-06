<script setup lang="ts">
import { computed, ref } from 'vue'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const email = ref(userStore.profile?.email ?? '')
const verificationToken = ref('')
const verificationCode = ref('')
const loading = ref(false)
const verifyLoading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const currentEmail = computed(() => userStore.profile?.email || '未绑定')
const emailVerified = computed(() => userStore.profile?.emailVerified ?? false)

async function sendCode() {
  if (loading.value) {
    return
  }
  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.bindEmail(email.value.trim())
    verificationToken.value = response.token
    successMessage.value = '验证码已发送，请检查目标邮箱。'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '发送验证码失败。'
  } finally {
    loading.value = false
  }
}

async function verifyCode() {
  if (verifyLoading.value) {
    return
  }
  if (!verificationToken.value) {
    errorMessage.value = '请先发送验证码。'
    return
  }

  verifyLoading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.verifyEmail(verificationToken.value, verificationCode.value.trim())
    successMessage.value = response.message
    verificationCode.value = ''
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '邮箱验证失败。'
  } finally {
    verifyLoading.value = false
  }
}
</script>

<template>
  <section class="page-grid">
    <section class="glass-panel panel-section email-card">
      <div class="section-head">
        <div>
          <p class="eyebrow">Email</p>
          <h2>邮箱绑定与验证</h2>
        </div>
      </div>

      <div class="summary-row">
        <article class="glass-panel summary-card">
          <span>当前邮箱</span>
          <strong>{{ currentEmail }}</strong>
        </article>
        <article class="glass-panel summary-card">
          <span>验证状态</span>
          <strong>{{ emailVerified ? '已验证' : '未验证' }}</strong>
        </article>
      </div>

      <p class="panel-copy">
        旧用户如果还没有绑定邮箱，后续无法使用验证码注册找回、验证码改密和邮箱安全通知。请先完成绑定并验证。
      </p>

      <form class="form-grid" @submit.prevent="verifyCode">
        <label>
          <span>目标邮箱地址</span>
          <input v-model="email" class="input-field" type="email" autocomplete="email" required />
        </label>

        <div class="inline-row">
          <label>
            <span>邮箱验证码</span>
            <input v-model="verificationCode" class="input-field" type="text" minlength="4" maxlength="16" required />
          </label>
          <button class="btn btn-outline send-btn" type="button" :disabled="loading" @click="sendCode">
            {{ loading ? '发送中...' : '发送验证码' }}
          </button>
        </div>

        <p v-if="errorMessage" class="error-text">{{ errorMessage }}</p>
        <p v-if="successMessage" class="success-text">{{ successMessage }}</p>

        <button class="btn btn-primary" type="submit" :disabled="verifyLoading">
          {{ verifyLoading ? '验证中...' : '确认绑定邮箱' }}
        </button>
      </form>
    </section>
  </section>
</template>

<style scoped>
.email-card {
  max-width: 860px;
}

.summary-row {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 20px;
}

.summary-card {
  padding: 18px;
  display: grid;
  gap: 6px;
}

.summary-card span {
  color: var(--text-muted);
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
  .summary-row,
  .inline-row {
    grid-template-columns: 1fr;
  }
}
</style>
