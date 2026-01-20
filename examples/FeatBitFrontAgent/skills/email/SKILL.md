---
name: email
description: Email operations via IMAP/SMTP. Trigger for inbox checking, reading, drafting, or sending emails. Requires `.config/email.json` setup.
---

## Mental Model

Email is for **asynchronous communication with audit trail**. Unlike chat (instant, casual), emails are formal, searchable, and have recipients/CC/threads.

## Trigger Patterns

| User Intent | Action | Requires Approval |
|-------------|--------|-------------------|
| "Check email" / "Any new mail?" | `email_list` | No |
| "Read email from X" | `email_read` | No |
| "Send email to X" | `email_send` | **Yes** |
| "Draft email" | `email_draft` | No |
| "Archive/delete email" | `email_move` / `email_delete` | Delete: **Yes** |

## Pre-flight Checks (Before Sending)

**CRITICAL: Always confirm before `email_send`**

1. Show full email: To, CC, Subject, Body
2. Check for sensitive content (passwords, tokens, confidential)
3. Warn if body mentions "attachment" but none attached
4. Alert if sending outside business hours (user's timezone)

## Anti-Patterns (NEVER)

- Don't send without user confirmation
- Don't guess recipient addresses - ask if unclear
- Don't include passwords/API keys in emails
- Don't send empty emails or ones with placeholder text
- Don't delete without showing what will be deleted

## Common Queries

**Check unread:**
```
email_list unreadOnly=true limit=10
```

**From specific sender:**
```
email_list from="boss@company.com"
```

**Search by subject:**
```
email_list subject="Report"
```

**After sending important email, notify:**
```
notify_send title="Email Sent" content="Quarterly report sent to boss@company.com" priority="high"
```

## Setup Required

`.config/email.json` must exist (user provides credentials):
```json
{
  "imap": {"host": "imap.gmail.com", "port": 993, "auth": {"user": "...", "pass": "..."}},
  "smtp": {"host": "smtp.gmail.com", "port": 587, "auth": {"user": "...", "pass": "..."}}
}
```


