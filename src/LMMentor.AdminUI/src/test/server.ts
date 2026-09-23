import { setupServer } from 'msw/node';

// Servidor MSW compartilhado: cada teste registra os handlers de que precisa.
export const server = setupServer();
