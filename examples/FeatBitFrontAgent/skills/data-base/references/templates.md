# Python Script Templates

## Template 1: Basic scraping with requests

```python
#!/usr/bin/env python3
"""
Basic web scraper using requests + BeautifulSoup
"""

import requests
from bs4 import BeautifulSoup
import json
import time
from datetime import datetime

def scrape_page(url):
    """Scrape a single page"""
    headers = {
        'User-Agent': 'Mozilla/5.0 (compatible; DataBot/1.0)'
    }

    response = requests.get(url, headers=headers)
    response.raise_for_status()

    soup = BeautifulSoup(response.text, 'html.parser')

    # Extract data
    items = []
    for element in soup.select('.item-class'):
        items.append({
            'title': element.select_one('.title').text.strip(),
            'price': element.select_one('.price').text.strip(),
            'url': element.select_one('a')['href']
        })

    return items

def main():
    url = "https://example.com/data"
    data = scrape_page(url)

    # Save to JSON
    filename = f"data_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
    with open(filename, 'w') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    print(f"Saved {len(data)} items to {filename}")

if __name__ == "__main__":
    main()
```

## Template 2: Pagination handling

```python
#!/usr/bin/env python3
"""Scraper with pagination support"""

import requests
from bs4 import BeautifulSoup
import json
import time

def scrape_all_pages(base_url, max_pages=10):
    """Scrape multiple pages"""
    all_items = []

    for page in range(1, max_pages + 1):
        url = f"{base_url}?page={page}"
        print(f"Scraping page {page}...")

        response = requests.get(url)
        soup = BeautifulSoup(response.text, 'html.parser')

        items = soup.select('.item')
        if not items:
            break

        for item in items:
            all_items.append({
                'page': page,
                'title': item.select_one('.title').text.strip()
            })

        time.sleep(1)  # Rate limit

    return all_items

if __name__ == "__main__":
    data = scrape_all_pages("https://example.com/list")
    with open('all_pages.json', 'w') as f:
        json.dump(data, f, indent=2)
```

## Template 3: Playwright for dynamic pages

```python
#!/usr/bin/env python3
"""Scraper for JavaScript-rendered pages"""

from playwright.sync_api import sync_playwright
import json

def scrape_dynamic_page(url):
    """Scrape dynamic content using Playwright"""
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()

        page.goto(url)

        # Wait for content
        page.wait_for_selector('.data-loaded', timeout=10000)

        # Extract data
        items = page.evaluate('''() => {
            return Array.from(document.querySelectorAll('.item')).map(el => ({
                title: el.querySelector('.title')?.textContent,
                price: el.querySelector('.price')?.textContent
            }))
        }''')

        browser.close()
        return items

if __name__ == "__main__":
    data = scrape_dynamic_page("https://example.com/app")
    with open('dynamic_data.json', 'w') as f:
        json.dump(data, f, indent=2)
```

## Template 4: CSV export

```python
#!/usr/bin/env python3
"""Export scraped data to CSV"""

import csv
import json

def to_csv(json_file, csv_file):
    """Convert JSON to CSV"""
    with open(json_file) as f:
        data = json.load(f)

    if not data:
        return

    fieldnames = list(data[0].keys())

    with open(csv_file, 'w', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(data)

    print(f"Exported {len(data)} rows to {csv_file}")

if __name__ == "__main__":
    to_csv('data.json', 'data.csv')
```

## Template 5: Error handling & retry

```python
#!/usr/bin/env python3
"""Scraper with robust error handling"""

import requests
import time
from tenacity import retry, stop_after_attempt, wait_exponential

@retry(
    stop=stop_after_attempt(3),
    wait=wait_exponential(multiplier=1, min=2, max=10)
)
def fetch_with_retry(url):
    """Fetch with exponential backoff"""
    response = requests.get(url, timeout=30)
    response.raise_for_status()
    return response

def safe_scrape(url):
    """Scrape with error handling"""
    try:
        response = fetch_with_retry(url)
        # Process data...
        return {'status': 'success', 'data': []}
    except requests.RequestException as e:
        return {'status': 'error', 'message': str(e)}
    except Exception as e:
        return {'status': 'error', 'message': f'Unexpected: {e}'}
```
