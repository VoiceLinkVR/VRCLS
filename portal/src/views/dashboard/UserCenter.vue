<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()

const profileUsername = ref('')
const email = ref('')
const emailVerificationCode = ref('')
const passwordVerificationCode = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const legacyUsername = ref('')
const legacyPassword = ref('')
const emailEditing = ref(false)

const loading = ref({ profile: false, email: false, verify: false, pwdSend: false, pwd: false, legacy: false })
const msgs = ref({ profile: '', email: '', pwd: '', legacy: '' })
const errs = ref({ profile: '', email: '', pwd: '', legacy: '' })
const verificationToken = ref('')
const pwdToken = ref('')
const activeUsage = computed(() => userStore.usageSnapshot ?? userStore.usage)

watch(
  () => userStore.profile,
  (profile) => {
    if (!profile) return
    profileUsername.value = profile.username
    email.value = profile.email
    if (!profile.emailVerified) {
      emailEditing.value = true
    }
  },
  { immediate: true },
)

const isEmailVerified = computed(() => Boolean(userStore.profile?.emailVerified))
const currentEmail = computed(() => userStore.profile?.email?.trim() || '')
const trimmedEmail = computed(() => email.value.trim())
const isEmailChanged = computed(() => trimmedEmail.value !== currentEmail.value)
const canSendEmailCode = computed(() => Boolean(trimmedEmail.value) && (!isEmailVerified.value || isEmailChanged.value))
const showEmailVerifyForm = computed(() => Boolean(verificationToken.value))
const currentPlanLabel = computed(() => activeUsage.value?.plan_name || userStore.profile?.planName || '暂无数据')
const currentDailyQuotaLabel = computed(() => {
  const dailyLimit = activeUsage.value?.daily_limit
  return dailyLimit === null || dailyLimit === undefined ? '暂无数据' : `${dailyLimit}/day`
})

function formatRateLimitRule(limitRule: string | null | undefined): string {
  const parts = String(limitRule || '')
    .split(';')
    .map((part) => part.trim())
    .filter((part) => part && !part.startsWith('plan_id=') && !part.toLowerCase().endsWith('/day'))

  return parts.join(' · ')
}

const currentRateLimitLabel = computed(() => {
  const usage = activeUsage.value
  if (!usage) {
    return '暂无数据'
  }
  if (usage.is_test_user) {
    return formatRateLimitRule(usage.limit_rule) || '按测试用户默认限速规则执行'
  }

  const planPrice = Number(usage.plan_price ?? userStore.profile?.planPrice ?? 0)
  if (planPrice > 0) {
    return '当前付费额度不限制速'
  }

  return formatRateLimitRule(usage.limit_rule) || '当前无额外限速规则'
})

function resetEmailFlow(resetDraft = false) {
  verificationToken.value = ''
  emailVerificationCode.value = ''
  errs.value.email = ''
  msgs.value.email = ''
  if (resetDraft) {
    email.value = currentEmail.value
  }
}

function startEmailEdit() {
  emailEditing.value = true
  resetEmailFlow()
}

function cancelEmailEdit() {
  emailEditing.value = false
  resetEmailFlow(true)
}

async function saveProfile() {
  loading.value.profile = true
  errs.value.profile = ''
  msgs.value.profile = ''
  try {
    await userStore.updateProfile({
      username: profileUsername.value.trim(),
    })
    msgs.value.profile = '资料已保存。'
    window.setTimeout(() => {
      msgs.value.profile = ''
    }, 3000)
  } catch (error: any) {
    errs.value.profile = error.message
  } finally {
    loading.value.profile = false
  }
}

async function sendEmailCode() {
  if (!canSendEmailCode.value) {
    errs.value.email = isEmailVerified.value ? '请先输入新的邮箱地址。' : '请输入有效的邮箱地址。'
    return
  }

  loading.value.email = true
  errs.value.email = ''
  msgs.value.email = ''
  try {
    const response = await userStore.bindEmail(trimmedEmail.value)
    verificationToken.value = response.token
    msgs.value.email = '验证码已发送到新邮箱，请完成验证。'
  } catch (error: any) {
    errs.value.email = error.message
  } finally {
    loading.value.email = false
  }
}

async function verifyEmailCode() {
  loading.value.verify = true
  errs.value.email = ''
  msgs.value.email = ''
  try {
    await userStore.verifyEmail(verificationToken.value, emailVerificationCode.value.trim())
    msgs.value.email = '邮箱已更新并验证成功。'
    emailEditing.value = false
    resetEmailFlow()
    window.setTimeout(() => {
      msgs.value.email = ''
    }, 3000)
  } catch (error: any) {
    errs.value.email = error.message
  } finally {
    loading.value.verify = false
  }
}

async function sendPwdCode() {
  loading.value.pwdSend = true
  errs.value.pwd = ''
  msgs.value.pwd = ''
  try {
    const response = await userStore.sendChangePasswordCode()
    pwdToken.value = response.token
    msgs.value.pwd = '验证码已发送至当前邮箱。'
  } catch (error: any) {
    errs.value.pwd = error.message
  } finally {
    loading.value.pwdSend = false
  }
}

async function updatePassword() {
  if (newPassword.value !== confirmPassword.value) {
    errs.value.pwd = '两次密码输入不一致。'
    return
  }

  loading.value.pwd = true
  errs.value.pwd = ''
  msgs.value.pwd = ''
  try {
    await userStore.changePasswordWithCode(pwdToken.value, passwordVerificationCode.value.trim(), newPassword.value)
    msgs.value.pwd = '密码已更新。'
    newPassword.value = ''
    confirmPassword.value = ''
    passwordVerificationCode.value = ''
    pwdToken.value = ''
    window.setTimeout(() => {
      msgs.value.pwd = ''
    }, 3000)
  } catch (error: any) {
    errs.value.pwd = error.message
  } finally {
    loading.value.pwd = false
  }
}

async function bindLegacy() {
  loading.value.legacy = true
  errs.value.legacy = ''
  msgs.value.legacy = ''
  try {
    await userStore.legacyLink(legacyUsername.value.trim(), legacyPassword.value)
    msgs.value.legacy = '旧账号绑定成功。'
    await userStore.bootstrap(true)
  } catch (error: any) {
    errs.value.legacy = error.message
  } finally {
    loading.value.legacy = false
  }
}
</script>

<template>
  <div class="page-grid">
    <div class="panel">
      <div class="panel-header">
        <h2>账户概览</h2>
        <p>您的账户基础信息与全局唯一标识。</p>
      </div>
      <div class="panel-body">
        <div class="desc-list">
          <div class="desc-row">
            <div class="desc-term">账户 ID (UUID)</div>
            <div class="desc-detail account-readonly">
              <code class="uuid-code">{{ userStore.profile?.userUuid || '--' }}</code>
              <p class="hint-line">系统自动分配，不可更改</p>
            </div>
          </div>

          <div class="desc-row">
            <div class="desc-term">用户名</div>
            <div class="desc-detail">
              <div class="flex-row inline-edit">
                <input v-model="profileUsername" class="input-field" style="max-width: 200px;" placeholder="输入用户名" />
                <button class="btn btn-primary" @click="saveProfile" :disabled="loading.profile">更新</button>
              </div>
            </div>
          </div>

          <div class="desc-row">
            <div class="desc-term">当前套餐</div>
            <div class="desc-detail">{{ currentPlanLabel }}</div>
          </div>

          <div class="desc-row">
            <div class="desc-term">日额度</div>
            <div class="desc-detail">{{ currentDailyQuotaLabel }}</div>
          </div>

          <div class="desc-row">
            <div class="desc-term">限速规则</div>
            <div class="desc-detail">{{ currentRateLimitLabel }}</div>
          </div>

          <span class="text-success text-left" v-if="msgs.profile">{{ msgs.profile }}</span>
          <span class="text-error text-left" v-if="errs.profile">{{ errs.profile }}</span>
        </div>
      </div>
    </div>

    <div class="panel">
      <div class="panel-header">
        <h2>账户安全</h2>
        <p>管理您的登录邮箱与密码凭证。</p>
      </div>
      <div class="panel-body">
        <div class="desc-list">
          <div class="desc-row">
            <div class="desc-term">
              邮箱地址
              <span v-if="isEmailVerified && !emailEditing" class="badge-chip ml-2 badge-success">已验证</span>
              <span v-else class="badge-chip ml-2 badge-warning">{{ emailEditing ? '待验证' : '未验证' }}</span>
            </div>
            <div class="desc-detail stack-form">
              <div class="flex-row inline-edit">
                <input
                  v-model="email"
                  class="input-field"
                  type="email"
                  :readonly="isEmailVerified && !emailEditing"
                  placeholder="name@example.com"
                />
                <button
                  v-if="isEmailVerified && !emailEditing"
                  class="btn btn-outline"
                  @click="startEmailEdit"
                >
                  修改邮箱
                </button>
                <button
                  v-else
                  class="btn btn-outline"
                  @click="sendEmailCode"
                  :disabled="loading.email || !canSendEmailCode"
                >
                  发送验证码
                </button>
              </div>

              <p class="hint-line" v-if="isEmailVerified && !emailEditing">
                当前登录邮箱。如需更换，请点击“修改邮箱”后提交新地址并完成验证。
              </p>
              <p class="hint-line" v-else>
                新邮箱需要完成验证码验证后才会正式生效。
              </p>

              <div class="flex-row inline-edit" v-if="showEmailVerifyForm">
                <input v-model="emailVerificationCode" class="input-field" type="text" placeholder="输入邮箱验证码" />
                <button class="btn btn-primary" @click="verifyEmailCode" :disabled="loading.verify">验证并保存</button>
              </div>

              <div class="flex-row inline-edit" v-if="emailEditing && isEmailVerified">
                <button class="btn btn-outline" @click="cancelEmailEdit">取消</button>
              </div>

              <span class="text-success text-left" v-if="msgs.email">{{ msgs.email }}</span>
              <span class="text-error text-left" v-if="errs.email">{{ errs.email }}</span>
            </div>
          </div>

          <div class="desc-row" v-if="isEmailVerified">
            <div class="desc-term">修改密码</div>
            <div class="desc-detail stack-form">
              <div class="flex-row inline-edit">
                <input v-model="passwordVerificationCode" class="input-field" type="text" placeholder="输入邮箱收到的验证码" />
                <button class="btn btn-outline" @click="sendPwdCode" :disabled="loading.pwdSend">获取验证码</button>
              </div>
              <input v-model="newPassword" class="input-field" type="password" placeholder="新密码（至少 6 位）" />
              <input v-model="confirmPassword" class="input-field" type="password" placeholder="确认新密码" />
              <button class="btn btn-primary align-self-end" @click="updatePassword" :disabled="!pwdToken || loading.pwd">提交修改</button>
              <span class="text-success text-left" v-if="msgs.pwd">{{ msgs.pwd }}</span>
              <span class="text-error text-left" v-if="errs.pwd">{{ errs.pwd }}</span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <div class="panel" v-if="userStore.isMigrationPending">
      <div class="panel-header">
        <h2>历史账号归并</h2>
        <p>将旧版订单与余额继承到当前账户。</p>
      </div>
      <div class="panel-body">
        <div class="desc-list">
          <div class="desc-row">
            <div class="desc-term">旧版凭证</div>
            <div class="desc-detail stack-form">
              <input v-model="legacyUsername" class="input-field" type="text" placeholder="旧账号用户名" />
              <input v-model="legacyPassword" class="input-field" type="password" placeholder="旧密码" />
              <button class="btn btn-outline align-self-end" @click="bindLegacy" :disabled="loading.legacy">绑定账号</button>
              <span class="text-success text-left" v-if="msgs.legacy">{{ msgs.legacy }}</span>
              <span class="text-error text-left" v-if="errs.legacy">{{ errs.legacy }}</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.page-grid {
  display: flex;
  flex-direction: column;
  gap: 32px;
}

.account-readonly {
  min-width: 280px;
}

.uuid-code {
  display: inline-flex;
  align-items: center;
  background: rgba(255, 255, 255, 0.05);
  padding: 4px 8px;
  border-radius: 4px;
  color: var(--text-soft);
  font-family: var(--font-mono);
  overflow-wrap: anywhere;
}

.hint-line {
  font-size: 12px;
  color: var(--text-soft);
  margin-top: 4px;
  text-align: left;
}

.inline-edit {
  align-items: center;
}

.stack-form {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: 100%;
  max-width: 420px;
}

.text-left {
  text-align: left;
  display: block;
  margin-top: 4px;
}

.align-self-end {
  align-self: flex-end;
}

.ml-2 {
  margin-left: 8px;
}

.badge-success {
  background: rgba(0, 230, 118, 0.12);
  color: #d7e4ee;
  border: 1px solid rgba(0, 230, 118, 0.22);
}

.badge-warning {
  background: rgba(0, 112, 243, 0.14);
  color: #d7e4ee;
  border: 1px solid rgba(0, 112, 243, 0.24);
}

@media (max-width: 768px) {
  .stack-form {
    max-width: 100%;
  }

  .inline-edit {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
