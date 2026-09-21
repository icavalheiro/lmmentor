import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// O build de produção é emitido direto no wwwroot do backend, que o serve estaticamente.
const configDir = dirname( fileURLToPath( import.meta.url ) );
const backendRoot = resolve( configDir, '../LMMentor.Backend' );

export default defineConfig( {
  plugins: [ react() ],
  // O AdminUI é servido pelo backend a partir de /admin.
  base: '/admin/',
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
