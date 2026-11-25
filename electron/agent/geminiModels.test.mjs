/**
 * Unit tests for geminiModels (mocked ListModels / error parsing).
 * Run: node --test electron/agent/geminiModels.test.mjs
 */
import { describe, it } from 'node:test'
import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'
import path from 'node:path'
import { register } from 'node:module'

// Compile-free: load via ts transpile? Use dynamic import of compiled JS after build,
// or duplicate pure helpers inline. Prefer building first — for CI we import from dist.
const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..')

async function load() {
  // Prefer dist-electron after tsc; fall back to compiling via tsx-less eval of source helpers
  const dist = path.join(root, 'dist-electron/agent/geminiModels.js')
  try {
    return await import(pathToFileURL(dist).href)
  } catch {
    // Inline minimal re-export path: run tsc first in npm test
    throw new Error('Build dist-electron first (npm run build:electron)')
  }
}

describe('geminiModels', async () => {
  const m = await load()

  it('parseSuggestedModel extracts recommended id from 404 text', () => {
    const msg =
      'This model models/gemini-2.0-flash is no longer available. Please update your code to use models/gemini-2.5-flash'
    assert.equal(m.parseSuggestedModel(msg), 'gemini-2.5-flash')
  })

  it('pickBestFlash prefers stable 2.5 over preview', () => {
    const models = [
      { id: 'gemini-2.0-flash', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
      { id: 'gemini-2.5-flash-preview-05-20', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
      { id: 'gemini-2.5-flash', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
      { id: 'gemini-2.5-flash-lite', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
    ]
    assert.equal(m.pickBestFlash(models), 'gemini-2.5-flash')
  })

  it('nextFlashFallbacks skips current and ranks lite after flash', () => {
    const models = [
      { id: 'gemini-2.5-flash', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
      { id: 'gemini-2.5-flash-lite', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
      { id: 'gemini-3.8-flash', supportsGenerateContent: true, supportsVision: true, supportsTools: true, displayName: '' },
    ]
    const next = m.nextFlashFallbacks(models, 'gemini-3.8-flash')
    assert.ok(next.includes('gemini-2.5-flash'))
    assert.ok(next.includes('gemini-2.5-flash-lite'))
    assert.ok(!next.includes('gemini-3.8-flash'))
    assert.ok(next.indexOf('gemini-2.5-flash') < next.indexOf('gemini-2.5-flash-lite'))
  })

  it('listGeminiModels filters generateContent and strips models/', async () => {
    const fakeFetch = async () =>
      ({
        ok: true,
        json: async () => ({
          models: [
            {
              name: 'models/gemini-2.5-flash',
              displayName: 'Gemini 2.5 Flash',
              supportedGenerationMethods: ['generateContent', 'countTokens'],
            },
            {
              name: 'models/embedding-001',
              supportedGenerationMethods: ['embedContent'],
            },
            {
              name: 'models/gemini-2.5-flash-lite',
              supportedGenerationMethods: ['generateContent'],
            },
          ],
        }),
      })
    const list = await m.listGeminiModels('test-key', fakeFetch)
    assert.deepEqual(
      list.map((x) => x.id),
      ['gemini-2.5-flash', 'gemini-2.5-flash-lite'],
    )
  })

  it('resolveGeminiModel migrates retired 2.0-flash', async () => {
    const fakeFetch = async () =>
      ({
        ok: true,
        json: async () => ({
          models: [
            {
              name: 'models/gemini-2.5-flash',
              supportedGenerationMethods: ['generateContent'],
            },
            {
              name: 'models/gemini-2.5-flash-lite',
              supportedGenerationMethods: ['generateContent'],
            },
          ],
        }),
      })
    const r = await m.resolveGeminiModel('k', 'gemini-2.0-flash', fakeFetch)
    assert.equal(r.model, 'gemini-2.5-flash')
    assert.equal(r.migrated, true)
  })

  it('classifyGeminiError distinguishes auth / model / network', () => {
    const auth = m.classifyGeminiError(Object.assign(new Error('API key not valid'), { status: 400 }))
    assert.equal(auth.kind, 'auth')
    const model = m.classifyGeminiError(
      Object.assign(
        new Error('This model models/gemini-2.0-flash is no longer available. use models/gemini-2.5-flash'),
        { status: 404 },
      ),
    )
    assert.equal(model.kind, 'model')
    assert.equal(model.suggestedModel, 'gemini-2.5-flash')
    const net = m.classifyGeminiError(Object.assign(new Error('fetch failed'), { status: 0 }))
    assert.equal(net.kind, 'network')
    const busy = m.classifyGeminiError(
      Object.assign(new Error('model is currently experiencing high demand'), { status: 503 }),
    )
    assert.equal(busy.kind, 'model')
    assert.ok(busy.userMessage.includes('перегружена'))
  })

  it('isRetiredOrMissing flags empty and 2.0-flash', () => {
    assert.equal(m.isRetiredOrMissing('gemini-2.0-flash', ['gemini-2.5-flash']), true)
    assert.equal(m.isRetiredOrMissing('gemini-2.5-flash', ['gemini-2.5-flash']), false)
    assert.equal(m.isRetiredOrMissing('', ['gemini-2.5-flash']), true)
  })
})
