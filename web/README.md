# PrivateChat 前端

Vue 3 + TypeScript + Vite + Element Plus + Tailwind CSS 4 + PWA。设计见 ../docs/DESIGN.md 第 5 节。

- `npm run dev`：开发（/api、/hubs 等代理到 http://localhost:8080）
- `npm run build`：产物在 dist/，带 hash 的文件在 dist/assets/
- `npm run type-check`：vue-tsc
- `npm run gen:api`：后端起来后从 /openapi/v1.json 生成 src/api/schema.d.ts
