import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Configuração isolada dos testes: não herda o build (outDir em wwwroot, publicDir em /assets).
export default defineConfig( {
    plugins: [ react() ],
    test: {
        environment: 'jsdom',
        globals: false,
        setupFiles: [ './src/test/setup.ts' ],
        include: [ 'src/**/*.test.{ts,tsx}' ],
        css: false,
        restoreMocks: true,
        coverage: {
            provider: 'v8',
            include: [ 'src/**/*.{ts,tsx}' ],
            exclude: [ 'src/test/**', 'src/main.tsx', 'src/vite-env.d.ts' ],
        },
    },
} );
