# Codex request fixture attribution

The tool definitions and native instruction/context text in
`codex-cli-0.155.1-request.json` are derived from OpenAI Codex CLI 0.155.1,
published by OpenAI and its contributors under the Apache License, Version 2.0.
The license text is included in `CODEX-LICENSE.txt`.

Release information: https://developers.openai.com/codex/changelog/

Local modifications: credentials, machine paths, identities, model, prompt, date,
and transport-derived values have been normalized into fixture placeholders.
The browser request builder supplies fresh request identities and appends
browser-specific capability instructions; it does not implement CLI tool execution.
