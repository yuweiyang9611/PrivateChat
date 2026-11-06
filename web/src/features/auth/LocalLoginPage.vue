<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSessionStore } from '@/stores/session'
import { useConversationsStore } from '@/stores/conversations'

const { t } = useI18n()
const session = useSessionStore()
const conversations = useConversationsStore()
const account = ref('')
const password = ref('')
const submitting = ref(false)
const message = ref<string | null>(null)

async function submit(): Promise<void> {
  if (!account.value || !password.value) return
  submitting.value = true
  message.value = null
  const err = await session.localLogin(account.value.trim(), password.value)
  submitting.value = false
  if (err) {
    message.value = err
    return
  }
  await conversations.load()
}
</script>

<template>
  <div class="flex min-h-dvh items-center justify-center p-4" style="background: var(--pc-bg)">
    <el-card class="w-full max-w-sm" shadow="hover">
      <template #header>
        <div class="text-center">
          <div class="text-2xl font-semibold" style="color: var(--pc-primary)">{{ t('app.name') }}</div>
          <div class="mt-1 text-sm opacity-70">{{ t('login.subtitle') }}</div>
        </div>
      </template>
      <el-form label-position="top" @submit.prevent="submit">
        <el-form-item :label="t('login.account')">
          <el-input v-model="account" autocomplete="username" size="large" />
        </el-form-item>
        <el-form-item :label="t('login.password')">
          <el-input v-model="password" type="password" show-password autocomplete="current-password" size="large" @keyup.enter="submit" />
        </el-form-item>
        <el-alert v-if="message" :title="message" type="error" :closable="false" class="mb-3" />
        <el-button type="primary" size="large" class="w-full" :loading="submitting" @click="submit">
          {{ t('login.submit') }}
        </el-button>
      </el-form>
      <p class="mt-4 text-xs opacity-60">{{ t('login.hint') }}</p>
    </el-card>
  </div>
</template>
