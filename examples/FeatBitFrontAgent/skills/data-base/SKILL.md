---
name: data-base
description: Data acquisition for web scraping and data collection. Use when user needs "爬取数据/抓取网页/scrape data". Outputs structured JSON/CSV for analysis.
---

## Mental Model

Data acquisition is **converting unstructured web content into structured data**. Choose tool based on page complexity: JS-heavy → chrome-devtools MCP, static → Python requests.

## Tool Selection

| Page Type | Tool | When to Use |
|-----------|------|-------------|
| Dynamic (JS-rendered, SPAs) | chrome-devtools MCP | React/Vue apps, infinite scroll, login gates |
| Static HTML | Python requests | Blogs, news sites, simple pages |
| Complex/reusable logic | Python script | Multi-step scraping, rate limiting, proxies |

## Anti-Patterns (NEVER)

- Don't scrape without checking robots.txt
- Don't overload servers (default: 1 req/sec)
- Don't scrape personal data without consent
- Don't use Chinese characters in output filenames (ASCII only)
- Don't forget to identify bot with User-Agent

## Output Format

- **JSON**: Nested/hierarchical data
- **CSV**: Tabular data
- Filename: `{source}_{timestamp}.{ext}` (ASCII only, e.g., `news_20250115.csv`)

## Workflow

1. **Ask**: What data? Which sites? How much?
2. **Select tool** based on page type
3. **Extract** and save structured data
4. **Deliver** file path to user or pass to data-analysis

## Python Environment

**Auto-initialize virtual environment if needed, then execute:**

```bash
cd skills/data-base

if [ ! -f ".venv/bin/python" ]; then
    echo "Creating Python environment..."
    ./setup.sh
fi

.venv/bin/python your_script.py
```

The setup script auto-installs: requests, beautifulsoup4, pandas, web scraping tools.

## References (load on demand)

For detailed APIs and templates, load: `references/REFERENCE.md`, `references/templates.md`
