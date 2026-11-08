<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useConversationsStore } from '@/stores/conversations'
import { useDriveStore, type NodeDto } from '@/stores/drive'

const props = defineProps<{ node: NodeDto | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const router = useRouter()
const drive = useDriveStore()
const conversations = useConversationsStore()

const open = computed({ get: () => props.node !== null, set: (v) => { if (!v) emit('close') } })
const keyword = ref('')
const picked = ref<string | null>(null)
const busy = ref(false)
const list = computed(() => conversations.sorted.filter((c) => !keyword.value || (c.title ?? '').includes(keyword.value)))

watch(() => props.node, (n) => {
  if (!n) return
  picked.value = null
  keyword.value = ''
  if (!conversations.loaded) void conversations.load()
})

async function send(goToChat: boolean): Promise<void> {
  if (!props.node || !picked.value) return
  busy.value = true
  try {
    await drive.sendToChat(props.node.id, picked.value)
    ElMessage.success(t('drive.sent'))
    const target = picked.value
    emit('close')
    if (goToChat) await router.push(`/messages/${target}`)
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <el-dialog v-model="open" :title="t('drive.sendTitle', { name: node?.name ?? '' })" width="min(92vw, 420px)" append-to-body data-testid="send-dialog">
    <el-input v-model="keyword" :placeholder="t('chat.search')" clearable class="mb-2" />
    <ul class="max-h-[50vh] overflow-y-auto">
      <li v-for="c in list" :key="c.id">
        <button
          type="button"
          class="flex w-full items-center gap-3 rounded px-2 py-2 text-left"
          :style="picked === c.id ? 'background: var(--pc-primary-soft)' : ''"
          @click="picked = c.id"
        >
          <el-avatar :size="32" style="background: var(--pc-primary)">{{ (c.title ?? '?').slice(0, 1) }}</el-avatar>
          <span class="truncate">{{ c.title }}</span>
        </button>
      </li>
    </ul>
    <template #footer>
      <el-button :disabled="!picked" :loading="busy" @click="send(false)">{{ t('chat.send') }}</el-button>
      <el-button type="primary" :disabled="!picked" :loading="busy" data-testid="send-and-open" @click="send(true)">{{ t('drive.sendAndOpen') }}</el-button>
    </template>
  </el-dialog>
</template>
