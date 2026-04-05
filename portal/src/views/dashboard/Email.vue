<script setup lang="ts">
import { computed, ref } from 'vue'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const email = ref(userStore.profile?.email ?? '')
const loading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')
const verificationUrl = ref('')

const currentEmail = computed(() => userStore.profile?.email || '未绑定')
const emailVerified = computed(() => (userStore.profile as { emailVerified?: boolean } | null)?.emailVerified ?? false)

async function handleBindEmail() {
  if (loading.value) {
    return
  }
  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  verificationUrl.value = ''
  try {
    const response = await userStore.bindEmail(email.value.trim())
    successMessage.value = response.message
    verificationUrl.value = response.nextUrl
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存邮箱失败。'
  } finally {
    loading.value = false
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
        当前后端会生成验证 token 并返回跳转地址，等正式邮件服务接入后，这里可以直接切到邮件投递模式。
      </p>

      <form class="form-grid" @submit.prevent="handleBindEmail">
        <label>
          <span>新邮箱地址</span>
          <input v-model="email" class="input-field" type="email" autocomplete="email" required />
        </label>

        <p v-if="errorMessage" class="error-text">{{ errorMessage }}</p>
        <p v-if="successMessage" class="success-text">{{ successMessage }}</p>

        <div class="action-row">
          <button class="btn btn-primary" type="submit" :disabled="loading">
            {{ loading ? '提交中...' : '保存并生成验证链接' }}
          </button>
          <RouterLink v-if="verificationUrl" class="btn btn-outline" :to="verificationUrl">
            立即验证邮箱
          </RouterLink>
        </div>
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

.action-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.error-text {
  color: #ffb4b4;
}

.success-text {
  color: #9af0c8;
}

@media (max-width: 720px) {
  .summary-row {
    grid-template-columns: 1fr;
  }
}
</style>
