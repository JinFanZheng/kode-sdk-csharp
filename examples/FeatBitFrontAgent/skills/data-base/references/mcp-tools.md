# Chrome DevTools MCP - Quick Guide

## Overview

The chrome-devtools MCP server provides browser automation capabilities through Chrome DevTools Protocol. Ideal for:
- Dynamic/JavaScript-rendered pages
- Pages requiring interaction (click, scroll, fill)
- Quick extraction without writing code

## Basic workflow

```javascript
// 1. Navigate to page
chrome_navigate("https://example.com")

// 2. Wait for content (evaluate with timeout)
chrome_evaluate("""
  await new Promise(r => setTimeout(r, 2000));
  document.querySelector('.content')
""")

// 3. Extract data
chrome_evaluate("Array.from(document.querySelectorAll('.item')).map(el => el.textContent)")

// 4. Verify (optional)
chrome_screenshot({path: 'verify.png'})
```

## Common operations

### Pagination

```javascript
chrome_evaluate("""
  const items = [];
  for (let i = 0; i < 5; i++) {
    document.querySelector('.next-page').click();
    await new Promise(r => setTimeout(r, 1000));
    items.push(...Array.from(document.querySelectorAll('.item')));
  }
  items
""")
```

### Infinite scroll

```javascript
chrome_evaluate("""
  for (let i = 0; i < 10; i++) {
    window.scrollTo(0, document.body.scrollHeight);
    await new Promise(r => setTimeout(r, 1500));
  }
  document.querySelectorAll('.loaded-item')
""")
```

### Form submission

```javascript
chrome_fill('#search-input', 'query')
chrome_click('#search-button')
chrome_evaluate("await new Promise(r => setTimeout(r, 2000)); document.querySelector('.results')")
```

### Handle authentication

```javascript
chrome_evaluate("""
  document.querySelector('#username').value = 'user';
  document.querySelector('#password').value = 'pass';
  document.querySelector('#login').click();
""")
```

## Tips

- Use `chrome_screenshot` to debug page state
- Wrap async operations in `chrome_evaluate` with `await`
- Always wait for elements before extraction
- Check `navigator.userAgent` if blocks occur
