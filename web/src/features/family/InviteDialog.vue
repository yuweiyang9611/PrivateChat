<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useFamilyStore, type UserRole } from '@/stores/family'
import { online } from '@/net/online'

/** 邀请家人：登记邮箱和角色。对方用这个邮箱通过 Cloudflare 的验证后，第一次打开就自动建号。 */
const open = defineModel<boolean>({ required: true })
const { t } = useI18n()
const family = useFamilyStore()

const email = ref('')
const role = ref<UserRole>('Member')
const submitting = ref(false)
const error = ref('')

const roles: UserRole[] = ['Member', 'Child', 'Admin']
const trimmed = computed(() => email.value.trim().toLowerCase())
/** 和服务器一样的粗检查：真伪由 Cloudflare 的验证码去验 */
const valid = computed(() => /^[^\s@]+@[^\s@.]+(\.[^\s@.]+)+$/.test(trimmed.value))

watch(open, (v) => {
  if (!v) return
  email.value = ''
  role.value = 'Member'
  error.value = ''
})

async function submit(): Promise<void> {
  if (!valid.value || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    const invite = await family.invite(trimmed.value, role.value)
    ElMessage.success(t('family.invited', { email: invite.email }))
    open.value = false
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="t('family.invite')" width="min(92vw, 440px)" append-to-body data-testid="invite-dialog">
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('family.email')">
        <el-input v-model="email" type="email" inputmode="email" autocomplete="off" size="large" placeholder="name@example.com" data-testid="invite-email" @keyup.enter="submit" />
      </el-form-item>
      <el-form-item :label="t('family.role')">
        <el-radio-group v-model="role" class="!flex !flex-col !items-stretch gap-1" data-testid="invite-role">
          <el-radio v-for="r in roles" :key="r" :value="r" class="role-option !mr-0 !h-auto !items-start py-1" :data-testid="`invite-role-${r}`">
            <div>{{ t(`family.roles.${r}`) }}</div>
            <div class="text-xs whitespace-normal" style="color: var(--pc-text-muted)">{{ t(`family.roleDesc.${r}`) }}</div>
          </el-radio>
        </el-radio-group>
      </el-form-item>
    </el-form>
    <p class="text-xs leading-relaxed" style="color: var(--pc-text-muted)">{{ t('family.inviteHint') }}</p>
    <el-alert v-if="error" class="mt-3" type="error" :closable="false" :title="error" data-testid="invite-error" />
    <template #footer>
      <el-button @click="open = false">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!valid || !online" data-testid="invite-submit" @click="submit">{{ t('family.inviteSubmit') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
/* 选项有两行字：圆点对齐第一行，而不是整块的中间或最顶上 */
.role-option :deep(.el-radio__input) {
  margin-top: 4px;
}
</style>
