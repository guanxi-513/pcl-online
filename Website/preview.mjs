import { randomBytes } from 'node:crypto';
import { createApplication } from './server.mjs';

const password = randomBytes(18).toString('hex');
const port = Number(process.env.PORT ?? 3210);
const origin = `http://127.0.0.1:${port}`;
const server = createApplication({ password, origin, database: ':memory:' });
server.listen(port, '127.0.0.1', () => {
  console.log(`Local preview: ${origin}`);
  console.log(`Temporary administrator password: ${password}`);
  console.log('Preview data is kept in memory and disappears on shutdown.');
});
