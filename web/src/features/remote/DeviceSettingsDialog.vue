<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useFamilyStore } from '@/stores/family'
import { useRemoteStore, type DeviceDto } from '@/stores/remote'
import { formatWhen } from './format'

const open = defineModel<boolean>({ required: true })
const props = defineProps<{ device: DeviceDto | null }>()

const { t } = useI18n()
const family = useFamilyStore()
const remote = useRemoteStore()

const name = ref('')
const allowed = ref<string[]>([])
const pin = ref('')
const clearPin = ref(false)
const busy = ref(false)
let initialAllowed: string[] = []

/** 能被加进名单的人：成年成员（孩子不能远程），不含主人自己 */
const candidates = computed(() => family.members.filter((m) => !m.disabled && m.role !== 'Child' && m.id !== props.device?.ownerId))
const pinValid = computed(() => pin.value === '' || /^\d{6,12}$/.test(pin.value))

watch(open, (v) => {
  if (!v || !props.device) return
  void family.load()
  name.value = props.device.name
  initialAllowed = [...(props.device.allowedControllers ?? [])]
  allowed.value = [...initialAllowed]
  pin.value = ''
  clearPin.value = false
}, { immediate: true })

async function save(): Promise<void> {
  const d = props.device
  if (!d || !pinValid.value || !name.value.trim()) return
  busy.value = true
  try {
    // 名单没动就不发（里面可能有后来被停用 / 降为孩子的人，界面上看不到也去不掉，发过去会被拒）；
    // 动了就只发界面上能选的人
    const changed = allowed.value.length !== initialAllowed.length || allowed.value.some((id) => !initialAllowed.includes(id))
    const selectable = new Set(candidates.value.map((m) => m.id))
    await remote.update(d.id, {
      name: name.value.trim(),
      allowedControllers: changed ? allowed.value.filter((id) => selectable.has(id)) : null,
      pin: pin.value || null,
      clearPin: clearPin.value && !pin.value ? true : null,
    })
    ElMessage.success(t('remote.saved'))
    open.value = false
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="t('remote.settingsTitle')" width="min(520px, 94vw)" append-to-body data-testid="device-settings-dialog">
    <el-form v-if="device" label-position="top" @submit.prevent="save">
      <el-form-item :label="t('remote.name')">
        <el-input v-model="name" maxlength="64" data-testid="device-name" />
      </el-form-item>
      <el-form-item :label="t('remote.allowed')">
        <div class="w-full">
          <el-checkbox-group v-model="allowed" class="flex flex-col" data-testid="device-allowed">
            <el-checkbox v-for="m in candidates" :key="m.id" :value="m.id" :label="m.displayName || m.username" />
          </el-checkbox-group>
          <div class="text-xs" style="color: var(--pc-text-muted)">{{ t('remote.allowedHint') }}</div>
        </div>
      </el-form-item>
      <el-form-item :label="`${t('remote.pin')}（${device.hasPin ? t('remote.pinSet') : t('remote.pinNone')}）`">
        <div class="w-full">
          <div class="flex items-center gap-3">
            <el-input v-model="pin" type="password" show-password inputmode="numeric" maxlength="12" :placeholder="t('remote.pinNew')" class="!w-60" data-testid="device-pin" />
            <el-checkbox v-if="device.hasPin" v-model="clearPin" :disabled="!!pin" :label="t('remote.pinClear')" />
          </div>
          <div v-if="device.pinLockedUntil" class="mt-1 text-xs" style="color: #e5484d">{{ t('remote.pinLocked', { time: formatWhen(device.pinLockedUntil) }) }}</div>
          <div class="mt-1 text-xs" style="color: var(--pc-text-muted)">{{ t('remote.pinHint') }}</div>
        </div>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="open = false">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="busy" :disabled="!pinValid || !name.trim()" data-testid="save-device" @click="save">{{ t('remote.save') }}</el-button>
    </template>
  </el-dialog>
</template>
