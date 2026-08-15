import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const templatePath = resolve(repoRoot, 'update.template.json')
const compatTemplatePath = resolve(repoRoot, 'update-0.107.template.json')
const modManifestPath = resolve(repoRoot, 'DrawAndGuessMod.json')
const outputPath = resolve(repoRoot, 'public', 'update.json')
const compatOutputPath = resolve(repoRoot, 'public', 'update-0.107.json')

const [templateText, compatTemplateText, modManifestText] = await Promise.all([
  readFile(templatePath, 'utf8'),
  readFile(compatTemplatePath, 'utf8'),
  readFile(modManifestPath, 'utf8'),
])

const template = JSON.parse(templateText)
const compatTemplate = JSON.parse(compatTemplateText)
const modManifest = JSON.parse(modManifestText)

if (typeof modManifest.version !== 'string' || modManifest.version.trim().length === 0) {
  throw new Error('DrawAndGuessMod.json must contain a non-empty version string.')
}

if (typeof compatTemplate.latest_version !== 'string' || compatTemplate.latest_version.trim().length === 0) {
  throw new Error('update-0.107.template.json must contain a non-empty latest_version string.')
}

const output = {
  ...template,
  latest_version: modManifest.version.trim(),
}

await mkdir(dirname(outputPath), { recursive: true })
await Promise.all([
  writeFile(outputPath, `${JSON.stringify(output, null, 2)}\n`, 'utf8'),
  writeFile(compatOutputPath, `${JSON.stringify(compatTemplate, null, 2)}\n`, 'utf8'),
])
