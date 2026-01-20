---
name: data-files
description: File router that routes uploaded files to appropriate analysis skills. CSV/Excel/JSON → data-analysis, Images → data-viz, PDF/HTML → data-base.
---

## Mental Model

Files are **routed by type to specialized processors**. This skill is the router - detection and delegation, not processing itself.

## File Routing

| Extension | Routes To | For What |
|-----------|-----------|----------|
| `.csv`, `.xlsx`, `.json` | data-analysis | Statistics, aggregation, patterns |
| `.png`, `.jpg`, `.gif` | data-viz | Chart recognition, visualization |
| `.pdf`, `.html` | data-base | Text extraction, scraping |

## Anti-Patterns (NEVER)

- Don't accept non-ASCII filenames (require rename)
- Don't process unsupported file types
- Don't bypass file size limits (50MB default)

## Upload Flow

1. User uploads file via Web UI
2. Detect type by extension/MIME
3. Inject metadata into message: `[File: name.csv (id) - Type: CSV, Skill: data-analysis]`
4. Activate appropriate skill
5. Return results to user

## Critical Constraints

**Filenames MUST be ASCII-only** (no Chinese/non-ASCII):
- ✅ Good: `data.csv`, `report_2025.xlsx`
- ❌ Bad: `数据.csv`, `報表.pdf`

If non-ASCII filename detected, ask user to rename before processing.
