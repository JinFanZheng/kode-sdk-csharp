---
name: knowledge
description: External knowledge capture and retrieval. Trigger when user wants to save notes, bookmarks, code snippets, or reference material for later access.
---

## Mental Model

Knowledge is for **deliberate capture of reference material**. Unlike memory (which extracts info from conversation), knowledge is explicit: user asks to save something.

## Trigger Patterns

| User Intent | Action | Destination |
|-------------|--------|-------------|
| "Save this link" | Store URL | `bookmarks.jsonl` |
| "Make a note about X" | Create note | `notes/YYYY-MM-DD_topic.md` |
| "Remember this code pattern" | Save snippet | `snippets/{lang}/` |

## What Goes Where

```
.knowledge/
├── bookmarks.jsonl           # URLs, articles, docs
├── notes/
│   └── YYYY-MM-DD_topic.md   # Meeting notes, ideas, summaries
└── snippets/
    ├── typescript/           # Language-specific patterns
    ├── python/
    └── shell/
```

## Entry Schemas

**Bookmark:**
```json
{"id":"bm_{{timestamp}}","url":"{{URL}}","title":"{{page title}}","tags":["{{keywords}}"],"savedAt":"{{ISO8601}}"}
```

**Note (Markdown + YAML):**
```markdown
---
title: {{Topic}}
date: {{YYYY-MM-DD}}
tags: [{{keywords}}]
---

# {{Topic}}

{{content}}
```

**Snippet:**
```typescript
// @title: {{Pattern Name}}
// @tags: {{keyword1}}, {{keyword2}}
// @created: {{YYYY-MM-DD}}

{{code}}
```

## Anti-Patterns (NEVER)

- Don't save transient information (today's weather, temporary URLs)
- Don't duplicate - check if already exists before saving
- Don't save without user's explicit request (use `memory` skill for that)

## Action Pattern

User says "保存这个链接" or "Save this":
1. Extract URL, title, context
2. Generate relevant tags
3. `fs_write` to appropriate destination
4. Confirm what was saved

