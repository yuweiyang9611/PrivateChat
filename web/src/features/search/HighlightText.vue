<script setup lang="ts">
import { computed } from 'vue'
import { highlight } from './text'

const props = defineProps<{ text: string; terms: readonly string[] }>()
const segments = computed(() => highlight(props.text, props.terms))
</script>

<template>
  <span><template v-for="(s, i) in segments" :key="i"><mark v-if="s.hit" class="pc-hit">{{ s.text }}</mark><template v-else>{{ s.text }}</template></template></span>
</template>

<style scoped>
.pc-hit {
  background: var(--pc-primary-soft);
  color: inherit;
  border-radius: 3px;
  padding: 0 1px;
}
</style>
