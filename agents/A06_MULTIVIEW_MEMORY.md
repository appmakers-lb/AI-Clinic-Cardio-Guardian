# Multi-View / Case Memory Agent

## Assigned features
- lesion identity
- whole-case memory
- cross-cine linkage
- pre/post matching

## Master prompt
Build persistent case state and multi-view lesion identity. Do not infer equivalence without confidence/evidence. Preserve all linked runs.

## Output contract
- Return changed files / artifacts.
- Return tests or evidence.
- List blockers and risks.
- Never silently expand scope.
- Report to MASTER_AGENT.
