import { createCipheriv, createDecipheriv, createHash, randomBytes } from 'node:crypto';
import Redis from 'ioredis';

// Key/value backend for BFF sessions (US-021 follow-up). Values are AES-256-GCM encrypted and keys are hashes of the
// opaque ids, so a dump of Valkey yields neither usable cookies nor readable tokens (D06 §8, TM-006).
export interface KvStore {
  get(key: string): Promise<string | null>;
  set(key: string, value: string, ttlSeconds: number): Promise<void>;
  del(key: string): Promise<void>;
  /** Read and delete atomically (single-use values). */
  take(key: string): Promise<string | null>;
  /** Try to acquire a short lock; false if held by someone else. */
  lock(key: string, ttlMs: number): Promise<boolean>;
}

class MemoryStore implements KvStore {
  private readonly m = new Map<string, { v: string; exp: number }>();
  private live(key: string) {
    const e = this.m.get(key);
    if (e && e.exp < Date.now()) this.m.delete(key);
    return this.m.get(key);
  }
  async get(key: string) { return this.live(key)?.v ?? null; }
  async set(key: string, value: string, ttl: number) { this.m.set(key, { v: value, exp: Date.now() + ttl * 1000 }); }
  async del(key: string) { this.m.delete(key); }
  async take(key: string) { const v = this.live(key)?.v ?? null; this.m.delete(key); return v; }
  async lock(key: string, ttlMs: number) {
    if (this.live(key)) return false;
    this.m.set(key, { v: '1', exp: Date.now() + ttlMs });
    return true;
  }
}

class ValkeyStore implements KvStore {
  constructor(private readonly r: Redis) {}
  async get(key: string) { return this.r.get(key); }
  async set(key: string, value: string, ttl: number) { await this.r.set(key, value, 'EX', ttl); }
  async del(key: string) { await this.r.del(key); }
  async take(key: string) { return this.r.getdel(key); }
  async lock(key: string, ttlMs: number) { return (await this.r.set(key, '1', 'PX', ttlMs, 'NX')) === 'OK'; }
}

function encryptionKey(): Buffer {
  const raw = process.env.SESSION_ENC_KEY;
  if (raw) {
    const k = Buffer.from(raw, 'base64');
    if (k.length !== 32) throw new Error('SESSION_ENC_KEY must be 32 bytes, base64');
    return k;
  }
  if (process.env.SESSION_STORE === 'valkey') throw new Error('SESSION_ENC_KEY is required with SESSION_STORE=valkey');
  return randomBytes(32); // memory store: process-local key
}

const g = globalThis as unknown as { __rcStore?: KvStore; __rcKey?: Buffer };

export function store(): KvStore {
  if (!g.__rcStore) {
    if (process.env.SESSION_STORE === 'valkey') {
      const url = process.env.VALKEY_URL;
      if (!url) throw new Error('VALKEY_URL is required with SESSION_STORE=valkey');
      g.__rcStore = new ValkeyStore(new Redis(url, { maxRetriesPerRequest: 2, connectTimeout: 5000 }));
    } else {
      g.__rcStore = new MemoryStore();
    }
  }
  return g.__rcStore;
}

const key = () => (g.__rcKey ??= encryptionKey());

/** Storage key for an opaque id: prefix + SHA-256, never the id itself. */
export const storageKey = (prefix: string, id: string) =>
  `rc:bff:${prefix}:${createHash('sha256').update(id).digest('base64url')}`;

export function seal(plain: string, aad: string): string {
  const iv = randomBytes(12);
  const c = createCipheriv('aes-256-gcm', key(), iv);
  c.setAAD(Buffer.from(aad));
  const body = Buffer.concat([c.update(plain, 'utf8'), c.final()]);
  return `v1.${iv.toString('base64url')}.${body.toString('base64url')}.${c.getAuthTag().toString('base64url')}`;
}

export function open(sealed: string, aad: string): string | null {
  const [v, iv, body, tag] = sealed.split('.');
  if (v !== 'v1' || !iv || !body || !tag) return null;
  try {
    const d = createDecipheriv('aes-256-gcm', key(), Buffer.from(iv, 'base64url'));
    d.setAAD(Buffer.from(aad));
    d.setAuthTag(Buffer.from(tag, 'base64url'));
    return Buffer.concat([d.update(Buffer.from(body, 'base64url')), d.final()]).toString('utf8');
  } catch {
    return null; // tampered, or encrypted with another key
  }
}
