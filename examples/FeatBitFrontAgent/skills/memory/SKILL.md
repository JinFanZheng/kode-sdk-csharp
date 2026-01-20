---
name: memory
description: Persistent memory for cross-session personalization. Trigger when user shares identity, preferences, relationships, or facts worth remembering.
---

## Mental Model

Memory is for **information with recurring value across conversations**. If you'll need it tomorrow/next week, save it. If it's ephemeral (today's weather, casual greeting), don't.

## What to Remember (DO)

| Category | Examples | File |
|----------|----------|------|
| Identity | Name, age, location, occupation | `facts/people.jsonl` |
| Preferences | Languages, frameworks, work style | `facts/preferences.jsonl` |
| Relationships | Colleagues, family, team members | `facts/people.jsonl` |
| Decisions | Conclusions from discussions | `facts/projects.jsonl` |
| Context | Project details, work environment | `facts/projects.jsonl` |

## What NOT to Remember (NEVER)

- Ephemeral greetings ("你好", "hi")
- Temporary states ("今天很忙", "现在在外面")
- One-time questions without context
- Duplicate information already stored
- **Credentials** (passwords, API keys, tokens - even if user shares)

## Action Pattern

When user shares memorable info:

1. **Immediately** call `fs_write` - don't acknowledge first, don't batch
2. Extract structured fields from casual speech
3. Use importance score: 0.9-1.0 (identity), 0.7-0.8 (preferences), 0.5-0.6 (context)

Example:
```
User: "我叫张三，在深圳做后端开发"
→ fs_write path=".memory/facts/people.jsonl" content='{"id":"mem_1704628800000","ts":"2026-01-07T12:00:00.000Z","type":"fact","category":"person","content":"张三，深圳，后端开发","tags":["name","location","occupation"],"importance":0.95}'
```

## Storage Map

```
.memory/
├── profile.json           # Read on session start for context
├── facts/
│   ├── people.jsonl       # Identity, relationships
│   ├── preferences.jsonl  # Tech stack, work style
│   └── projects.jsonl     # Work context, decisions
└── conversations/
    └── YYYY-MM-DD.jsonl   # Session summaries
```

## Entry Schema

```json
{"id":"mem_{{timestamp}}","ts":"{{ISO8601}}","type":"fact","category":"{{person|preference|project}}","content":"{{concise content in user's language}}","tags":["{{retrieval keywords}}"],"importance":{{0.5-1.0}}
```

## Retrieval

Session start: `fs_read` `profile.json`
Search: `fs_grep` pattern="{{keyword}}" path=".memory/"

