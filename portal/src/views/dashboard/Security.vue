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

const migrationSummary = computed(() => [
  {
    label: '当前用户名',
    value: userStore.profile?.username || '未登录',
    detail: '控制台与订单页统一使用 username 作为主要展示名称。',
  },
  {
    label: '主身份标识',
    value: userStore.profile?.shortUserUuid || userStore.migrationSession?.legacyUsername || '--',
    detail: 'v2 体系已经切换到 user_uuid + email 组合身份。',
  },
  {
    label: '旧账号来源',
    value: userStore.profile?.legacyUsername || userStore.migrationSession?.legacyUsername || '未绑定',
    detail: '旧用户名仅保留为迁移来源或兼容说明，不再作为长期登录方式。',
  },
])

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
  <section class="page-grid security-page">
    <section class="panel">
      <div class="panel-header">
        <div>
          <h2>账户迁移与身份状态</h2>
          <p>把迁移背景、当前身份和旧账号来源收敛到一个统一面板里查看。</p>
        </div>
      </div>

      <div class="panel-body migration-stack">
        <div class="desc-list">
          <div v-for="item in migrationSummary" :key="item.label" class="desc-row">
            <div class="desc-term">{{ item.label }}</div>
            <div class="desc-detail">
              <strong>{{ item.value }}</strong>
              <span>{{ item.detail }}</span>
            </div>
          </div>
        </div>

        <div v-if="userStore.isMigrationPending || !canUseEmailSecurity" class="migration-callout">
          <div>
            <strong>迁移中的账号需要先完成邮箱绑定</strong>
            <p>在邮箱完成绑定和验证之前，账户会保持迁移态或受限态，无法使用完整的计费和安全能力。</p>
          </div>
          <RouterLink class="btn btn-primary" to="/center">前往用户中心</RouterLink>
        </div>
      </div>
    </section>

    <form class="panel" @submit.prevent="handlePasswordSubmit">
      <div class="panel-header">
        <div>
          <h2>邮箱验证码改密</h2>
          <p>验证码只发送到已验证邮箱，整个过程不再依赖当前密码。</p>
        </div>
      </div>

      <div v-if="!canUseEmailSecurity" class="panel-body">
        <p class="helper-text">
          当前账户还没有绑定并验证邮箱。为了启用找回密码、验证码改密和安全通知，请先完成邮箱绑定。
        </p>
      </div>

      <div v-else class="panel-body form-grid">
        <p class="helper-text">
          修改密码的验证码会发送到 <strong>{{ userStore.profile?.email }}</strong>。
        </p>

        <div class="dual-grid">
          <div class="input-group">
            <label for="security-code">邮箱验证码</label>
            <input
              id="security-code"
              v-model="verificationCode"
              class="input-field"
              type="text"
              minlength="4"
              maxlength="16"
              required
            />
          </div>

          <div class="input-group">
            <label for="security-password">新密码</label>
            <input
              id="security-password"
              v-model="newPassword"
              class="input-field"
              type="password"
              autocomplete="new-password"
              minlength="6"
              required
            />
          </div>
        </div>

        <div class="input-group">
          <label for="security-confirm-password">确认新密码</label>
          <input
            id="security-confirm-password"
            v-model="confirmPassword"
            class="input-field"
            type="password"
            autocomplete="new-password"
            minlength="6"
            required
          />
        </div>

        <p v-if="errorMessage" class="error-text">{{ errorMessage }}</p>
        <p v-if="successMessage" class="success-text">{{ successMessage }}</p>
      </div>

      <div v-if="canUseEmailSecurity" class="panel-footer">
        <button class="btn btn-outline" type="button" :disabled="sending" @click="sendCode">
          {{ sending ? '发送中...' : '发送验证码' }}
        </button>
        <button class="btn btn-primary" type="submit" :disabled="loading">
          {{ loading ? '提交中...' : '更新密码' }}
        </button>
      </div>
    </form>

    <form class="panel" @submit.prevent="handleLegacyLink">
      <div class="panel-header">
        <div>
          <h2>绑定旧账号</h2>
          <p>把历史订单、附加额度与旧资产归并到当前邮箱身份，不覆盖当前主账号。</p>
        </div>
      </div>

      <div class="panel-body form-grid">
        <div v-if="hasLinkedLegacyAccount" class="desc-list">
          <div class="desc-row">
            <div class="desc-term">已记录来源</div>
            <div class="desc-detail">
              <strong>{{ userStore.profile?.legacyUsername }}</strong>
              <span>当前账号已经保存旧账号来源信息。</span>
            </div>
          </div>
        </div>

        <div class="dual-grid">
          <div class="input-group">
            <label for="legacy-link-username">旧用户名</label>
            <input id="legacy-link-username" v-model="legacyUsername" class="input-field" type="text" autocomplete="username" required />
          </div>

          <div class="input-group">
            <label for="legacy-link-password">旧密码</label>
            <input
              id="legacy-link-password"
              v-model="legacyPassword"
              class="input-field"
              type="password"
              autocomplete="current-password"
              required
            />
          </div>
        </div>

        <p v-if="legacyErrorMessage" class="error-text">{{ legacyErrorMessage }}</p>
        <p v-if="legacySuccessMessage" class="success-text">{{ legacySuccessMessage }}</p>
      </div>

      <div class="panel-footer">
        <button class="btn btn-primary" type="submit" :disabled="linking">
          {{ linking ? '绑定中...' : '绑定旧账号' }}
        </button>
      </div>
    </form>
  </section>
</template>

<style scoped>
.security-page {
  max-width: 980px;
}

.migration-stack,
.form-grid {
  display: grid;
  gap: 20px;
}

.desc-detail {
  display: grid;
  gap: 4px;
}

.desc-detail strong {
  color: var(--text-main);
  font-size: 0.9375rem;
  font-weight: 600;
}

.desc-detail span {
  color: var(--text-soft);
  line-height: 1.6;
}

.migration-callout {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  padding: 16px;
  border: 1px solid rgba(245, 158, 11, 0.2);
  border-radius: var(--radius-md);
  background: rgba(245, 158, 11, 0.08);
}

.migration-callout strong {
  display: block;
  margin-bottom: 4px;
  font-size: 0.9375rem;
}

.migration-callout p {
  color: var(--text-soft);
  font-size: 0.875rem;
  line-height: 1.6;
}

.dual-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

@media (max-width: 800px) {
  .dual-grid {
    grid-template-columns: 1fr;
  }

  .migration-callout {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
