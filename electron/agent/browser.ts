/** Isolated Chromium via Playwright — never touches the user's main profile. */
import fs from 'fs'
import path from 'path'
import os from 'os'
import { chromium, type BrowserContext, type Page } from 'playwright'

export class BrowserSession {
  private context: BrowserContext | null = null
  private page: Page | null = null
  private readonly profileDir: string

  constructor(userDataRoot?: string) {
    this.profileDir = path.join(
      userDataRoot || path.join(os.homedir(), '.lia-assistant'),
      'playwright-profile',
    )
  }

  private async ensure(): Promise<Page> {
    if (this.page) return this.page
    fs.mkdirSync(this.profileDir, { recursive: true })
    this.context = await chromium.launchPersistentContext(this.profileDir, {
      headless: false,
      viewport: { width: 1280, height: 800 },
      args: ['--disable-blink-features=AutomationControlled'],
    })
    this.page = this.context.pages()[0] || (await this.context.newPage())
    return this.page
  }

  async open(url: string) {
    const page = await this.ensure()
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 })
  }

  async click(selector: string) {
    const page = await this.ensure()
    await page.click(selector, { timeout: 15000 })
  }

  async type(selector: string, text: string) {
    const page = await this.ensure()
    await page.fill(selector, text)
  }

  async readText(): Promise<string> {
    const page = await this.ensure()
    return page.innerText('body')
  }

  async screenshot(outPath: string) {
    const page = await this.ensure()
    fs.mkdirSync(path.dirname(outPath), { recursive: true })
    await page.screenshot({ path: outPath, fullPage: false })
  }

  async close() {
    await this.context?.close()
    this.context = null
    this.page = null
  }
}
