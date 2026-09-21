import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// O build de produção é emitido direto no wwwroot do backend, que o serve estaticamente.
const configDir = dirname( fileURLToPath( import.meta.url ) );
const backendRoot = resolve( configDir, '../LMMentor.Backend' );
// Logo, favicon e sprites vivem em /assets na raiz do repositório: uma única fonte de verdade
// compartilhada com o README, em vez de cópias dentro do AdminUI.
const assetsRoot = resolve( configDir, '../../assets' );

export default defineConfig( {
  plugins: [ react() ],
  // O AdminUI é servido pelo backend a partir de /admin.
  base: '/admin/',
  // publicDir padrão (./public) é substituído pelos assets compartilhados do repositório.
  publicDir: assetsRoot,
  server: {
    // Porta fixa para o proxy do backend em modo watch (sem fallback de porta).
    port: 5173,
    strictPort: true,
  },
  build: {
    outDir: resolve( backendRoot, 'wwwroot' ),
    emptyOutDir: true,
  },
} );
