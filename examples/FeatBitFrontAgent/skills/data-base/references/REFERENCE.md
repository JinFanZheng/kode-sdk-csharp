# Data Base - Technical Reference

## Chrome DevTools MCP API

### Available Tools

| Tool | Description | Example |
|------|-------------|---------|
| `chrome_navigate` | Navigate to URL | Navigate to target page |
| `chrome_evaluate` | Execute JavaScript | Extract data using DOM APIs |
| `chrome_screenshot` | Capture screenshot | Verify page state |
| `chrome_click` | Click element | Handle interactions |
| `chrome_fill` | Fill form inputs | Submit forms |

### Common patterns

```javascript
// Extract all links
const links = Array.from(document.querySelectorAll('a')).map(a => ({
  text: a.textContent.trim(),
  href: a.href
}));

// Extract table data
const rows = Array.from(document.querySelectorAll('table tr')).map(tr =>
  Array.from(tr.querySelectorAll('td, th')).map(td => td.textContent)
);

// Wait for element (using chrome_evaluate with await)
await new Promise(resolve => setTimeout(resolve, 2000));
document.querySelector('.loaded-content');
```

## Python Libraries

### requests + BeautifulSoup

```python
import requests
from bs4 import BeautifulSoup

# Basic request
response = requests.get(url, headers={'User-Agent': 'Mozilla/5.0'})
soup = BeautifulSoup(response.text, 'html.parser')

# Find elements
title = soup.find('h1').text
items = soup.find_all('div', class_='item')
```

### Playwright (dynamic pages)

```python
from playwright.sync_api import sync_playwright

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page()
    page.goto(url)
    page.wait_for_selector('.data-loaded')
    data = page.evaluate('() => extractData()')
    browser.close()
```

## Data formats

### JSON output

```python
import json
with open('output.json', 'w') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
```

### CSV output

```python
import csv
with open('output.csv', 'w') as f:
    writer = csv.DictWriter(f, fieldnames=['name', 'price', 'url'])
    writer.writeheader()
    writer.writerows(data)
```

## Rate limiting

```python
import time

# Simple delay
time.sleep(1)

# With retry
from tenacity import retry, stop_after_attempt, wait_fixed

@retry(stop=stop_after_attempt(3), wait=wait_fixed(2))
def fetch_with_retry(url):
    return requests.get(url)
```

## Robots.txt checker

```python
import urllib.robotparser
rp = urllib.robotparser.RobotFileParser()
rp.set_url("https://example.com/robots.txt")
rp.read()

if rp.can_fetch("*", url):
    # Proceed with scraping
    pass
```
