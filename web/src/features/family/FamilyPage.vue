<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { MoreFilled, Plus } from '@element-plus/icons-vue'
import { useFamilyStore, type InviteDto, type MemberDto, type UserRole } from '@/stores/family'
import { useSessionStore } from '@/stores/session'
import { online } from '@/net/online'
import InviteDialog from './InviteDialog.vue'

/**
 * 家庭：成员名单（所有人都能看）。管理员另外可以邀请邮箱、撤销还没用的邀请，
 * 改成员的名字和角色，停用 / 恢复成员。
 */
const { t } = useI18n()
const family = useFamilyStore()
const session = useSessionStore()

const inviting = ref(false)
const loadError = ref('')
const busy = ref<string | null>(null)

const me = computed(() => session.user?.id)
const isAdmin = computed(() => session.isAdmin)
/** 在用的排前面，停用的沉底；各自按加入先后 */
const members = computed(() => [...family.members].sort((a, b) => Number(a.disabled) - Number(b.disabled)))
const ROLES: UserRole[] = ['Admin', 'Member', 'Child']

onMounted(load)
// 开着这一页时被设为管理员：邀请列表这时才拉得到
watch(isAdmin, (admin) => {
  if (admin) void load()
})

async function load(): Promise<void> {
  loadError.value = ''
  try {
    await Promise.all([family.reloadMembers(), isAdmin.value ? family.loadInvites() : Promise.resolve()])
  } catch (e) {
    loadError.value = (e as Error).message
  }
}

function nameOf(m: MemberDto): string {
  return m.displayName || m.username
}

function roleTag(role: UserRole): 'warning' | 'info' | 'success' {
  return role === 'Admin' ? 'warning' : role === 'Child' ? 'success' : 'info'
}

function expired(i: InviteDto): boolean {
  return !!i.expiresAt && new Date(i.expiresAt).getTime() <= Date.now()
}

function expiryText(i: InviteDto): string {
  if (!i.expiresAt) return ''
  if (expired(i)) return t('family.inviteExpired')
  // 留一分钟的余量：这台设备的钟比服务器慢一点点时，刚发出的 30 天邀请不该显示成"31 天后过期"
  const days = Math.max(1, Math.ceil((new Date(i.expiresAt).getTime() - Date.now() - 60_000) / 86_400_000))
  return t('family.inviteExpiresIn', { n: days })
}

async function run(key: string, action: () => Promise<unknown>, done?: string): Promise<void> {
  busy.value = key
  try {
    await action()
    if (done) ElMessage.success(done)
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = null
  }
}

async function confirm(message: string, title: string, action: string): Promise<boolean> {
  try {
    await ElMessageBox.confirm(message, title, { type: 'warning', confirmButtonText: action, cancelButtonText: t('common.cancel') })
    return true
  } catch {
    return false
  }
}

async function rename(m: MemberDto): Promise<void> {
  let value: string
  try {
    ;({ value } = await ElMessageBox.prompt(t('family.renamePrompt'), t('family.rename'), {
      inputValue: nameOf(m),
      inputValidator: (v: string) => (v?.trim() ? v.trim().length <= 32 || t('family.nameTooLong') : t('family.nameRequired')),
      confirmButtonText: t('common.confirm'),
      cancelButtonText: t('common.cancel'),
    }))
  } catch {
    return
  }
  const displayName = value.trim()
  if (displayName !== m.displayName) await run(m.id, () => family.patchMember(m.id, { displayName }))
}

async function setRole(m: MemberDto, role: UserRole): Promise<void> {
  const name = nameOf(m)
  // 给出管理权限、收回成人权限前确认一下；其它改动直接生效
  if (role === 'Admin' && !(await confirm(t('family.makeAdminConfirm', { name }), t('family.setRole', { role: t('family.roles.Admin') }), t('common.confirm')))) return
  await run(m.id, () => family.patchMember(m.id, { role }), t('family.roleChanged', { name, role: t(`family.roles.${role}`) }))
}

async function setDisabled(m: MemberDto, disabled: boolean): Promise<void> {
  const name = nameOf(m)
  if (disabled && !(await confirm(t('family.disableConfirm', { name }), t('family.disable'), t('family.disable')))) return
  await run(m.id, () => family.patchMember(m.id, { disabled }), disabled ? t('family.disabledDone', { name }) : t('family.enabledDone', { name }))
}

/** 替这位家人开 / 关长辈模式：跟着对方的账号走，对方下次打开应用时生效（对方在自己设备的设置里另拨过的，以那台设备为准） */
async function setElder(m: MemberDto, elderMode: boolean): Promise<void> {
  const name = nameOf(m)
  await run(m.id, () => family.patchMember(m.id, { elderMode }), t(elderMode ? 'family.elderOnDone' : 'family.elderOffDone', { name }))
}

function onCommand(m: MemberDto, command: string): void {
  if (command === 'rename') void rename(m)
  else if (command === 'elder:on') void setElder(m, true)
  else if (command === 'elder:off') void setElder(m, false)
  else if (command === 'disable') void setDisabled(m, true)
  else if (command === 'enable') void setDisabled(m, false)
  else if (command.startsWith('role:')) void setRole(m, command.slice(5) as UserRole)
}

async function revoke(i: InviteDto): Promise<void> {
  if (!(await confirm(t('family.revokeConfirm', { email: i.email }), t('family.revoke'), t('family.revoke')))) return
  await run(i.id, () => family.revokeInvite(i.id))
}

async function renew(i: InviteDto): Promise<void> {
  if (!i.email) return
  await run(i.id, () => family.invite(i.email!, i.role), t('family.invited', { email: i.email }))
}
</script>

<template>
  <div class="h-full overflow-y-auto" data-testid="family-page">
    <div class="mx-auto flex max-w-2xl flex-col gap-4 p-4">
      <div class="flex items-center justify-between">
        <h1 class="text-lg font-semibold">{{ t('nav.family') }}</h1>
        <el-button v-if="isAdmin" type="primary" round :icon="Plus" :disabled="!online" data-testid="invite-open" @click="inviting = true">{{ t('family.invite') }}</el-button>
      </div>

      <el-alert v-if="loadError" type="error" :closable="false" :title="loadError">
        <el-button size="small" text @click="load">{{ t('common.retry') }}</el-button>
      </el-alert>

      <section>
        <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('family.members', { n: family.members.length }) }}</h2>
        <ul class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)" data-testid="family-members">
          <li
            v-for="m in members"
            :key="m.id"
            class="flex items-center gap-3 px-4 py-3"
            :class="{ 'opacity-60': m.disabled }"
            data-testid="family-member"
            :data-member-email="m.email"
          >
            <el-avatar :size="44" class="shrink-0" :style="m.disabled ? 'background: var(--pc-text-muted)' : 'background: var(--pc-primary)'">{{ nameOf(m).slice(0, 1) }}</el-avatar>
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                <span class="truncate font-medium" data-testid="member-name">{{ nameOf(m) }}</span>
                <span v-if="m.id === me" class="text-xs" style="color: var(--pc-text-muted)">{{ t('family.me') }}</span>
                <el-tag :type="roleTag(m.role)" size="small" round data-testid="member-role">{{ t(`family.roles.${m.role}`) }}</el-tag>
                <el-tag v-if="m.elderMode" type="info" size="small" round data-testid="member-elder">{{ t('family.elderTag') }}</el-tag>
                <el-tag v-if="m.disabled" type="danger" size="small" round data-testid="member-disabled">{{ t('family.disabledTag') }}</el-tag>
              </div>
              <!-- 邮箱是登录身份，只给管理员看 -->
              <div v-if="isAdmin" class="truncate text-sm" style="color: var(--pc-text-muted)">{{ m.email }}</div>
            </div>
            <el-dropdown v-if="isAdmin" trigger="click" :disabled="!online || busy === m.id" @command="(c: string) => onCommand(m, c)">
              <el-button :icon="MoreFilled" circle text :loading="busy === m.id" data-testid="member-more" />
              <template #dropdown>
                <el-dropdown-menu :data-menu-for="m.email">
                  <el-dropdown-item command="rename" data-testid="member-rename">{{ t('family.rename') }}</el-dropdown-item>
                  <el-dropdown-item v-if="m.elderMode" command="elder:off" data-testid="member-elder-off">{{ t('family.elderOff') }}</el-dropdown-item>
                  <el-dropdown-item v-else command="elder:on" data-testid="member-elder-on">{{ t('family.elderOn') }}</el-dropdown-item>
                  <!-- 自己的角色和停用不能自己改：至少留一个管理员 -->
                  <template v-if="m.id !== me">
                    <template v-for="r in ROLES" :key="r">
                      <el-dropdown-item v-if="r !== m.role" :command="`role:${r}`" :divided="r === ROLES.find((x) => x !== m.role)" :data-testid="`member-role-${r}`">
                        {{ t('family.setRole', { role: t(`family.roles.${r}`) }) }}
                      </el-dropdown-item>
                    </template>
                    <el-dropdown-item v-if="m.disabled" command="enable" divided data-testid="member-enable">{{ t('family.enable') }}</el-dropdown-item>
                    <el-dropdown-item v-else command="disable" divided data-testid="member-disable">{{ t('family.disable') }}</el-dropdown-item>
                  </template>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </li>
        </ul>
      </section>

      <section v-if="isAdmin">
        <h2 class="mb-2 text-sm font-medium" style="color: var(--pc-text-muted)">{{ t('family.pending') }}</h2>
        <div v-if="!family.invites.length" class="py-4 text-center text-sm" style="color: var(--pc-text-muted)" data-testid="family-invites-empty">{{ t('family.pendingEmpty') }}</div>
        <ul v-else class="overflow-hidden rounded-2xl border" style="border-color: var(--pc-border); background: var(--pc-surface)" data-testid="family-invites">
          <li v-for="i in family.invites" :key="i.id" class="flex items-center gap-3 px-4 py-3" data-testid="family-invite" :data-invite-email="i.email">
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                <span class="truncate font-medium">{{ i.email }}</span>
                <el-tag :type="roleTag(i.role)" size="small" round>{{ t(`family.roles.${i.role}`) }}</el-tag>
              </div>
              <div class="text-sm" :style="expired(i) ? 'color: var(--el-color-danger)' : 'color: var(--pc-text-muted)'" data-testid="invite-expiry">{{ expiryText(i) }}</div>
            </div>
            <el-button v-if="expired(i)" size="small" round type="primary" plain :disabled="!online" :loading="busy === i.id" data-testid="invite-renew" @click="renew(i)">{{ t('family.renew') }}</el-button>
            <el-button size="small" round :disabled="!online || busy === i.id" data-testid="invite-revoke" @click="revoke(i)">{{ t('family.revoke') }}</el-button>
          </li>
        </ul>
        <p class="mt-2 text-xs leading-relaxed" style="color: var(--pc-text-muted)">{{ t('family.pendingHint') }}</p>
      </section>
    </div>

    <InviteDialog v-model="inviting" />
  </div>
</template>
