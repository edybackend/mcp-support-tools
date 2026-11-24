// PreToolUse hook: rules for the MCP surface. A tool is an API a model will call on its own, so its safety
// properties must be explicit and its authorization can never be dropped silently.
import { readFileSync } from 'node:fs'

const input = JSON.parse(readFileSync(0, 'utf8'))
const filePath = (input.tool_input?.file_path ?? '').replaceAll('\\', '/')
if (!/\/src\/.*\.cs$/.test(filePath)) process.exit(0)

const before = (() => {
  try {
    return readFileSync(filePath, 'utf8')
  } catch {
    return ''
  }
})()
const oldText = input.tool_input?.old_string
const after = oldText !== undefined ? before.replace(oldText, input.tool_input?.new_string ?? '') : (input.tool_input?.content ?? '')
const lines = after.split('\n')
const problems = []

lines.forEach((line, i) => {
  if (!/\[McpServerTool\(/.test(line)) return
  if (!/ReadOnly\s*=/.test(line)) problems.push(`line ${i + 1}: declare ReadOnly = true|false explicitly (and Destructive for writes)`)
  if (!lines.slice(i + 1, i + 3).some((l) => /\[Description\(/.test(l))) problems.push(`line ${i + 1}: add a [Description(...)] written for the model`)
})

// Each MCP type is checked on its own attribute block (the lines right around it), not the whole file.
lines.forEach((line, i) => {
  if (!/\[McpServer(Tool|Resource|Prompt)Type\]/.test(line)) return
  if (!lines.slice(Math.max(0, i - 2), i + 3).some((l) => /\[Authorize\(Policy\s*=/.test(l))) {
    problems.push(`line ${i + 1}: every MCP tool, resource or prompt type needs [Authorize(Policy = Scopes.…)]`)
  }
})

if (filePath.endsWith('/Program.cs') && /AddMcpServer\(/.test(after) && !/\.AddAuthorizationFilters\(\)/.test(after)) {
  problems.push('AddAuthorizationFilters() must stay: it is what enforces [Authorize] on tools')
}

if (problems.length > 0) {
  console.error(`Blocked: ${filePath}\n- ${problems.join('\n- ')}`)
  process.exit(2)
}
