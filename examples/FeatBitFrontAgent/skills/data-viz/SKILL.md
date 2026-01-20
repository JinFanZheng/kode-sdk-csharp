---
name: data-viz
description: Data visualization for charts and graphs. Use when user needs "画图/图表/可视化". Creates static PNG or interactive HTML charts from data.
---

## Mental Model

Visualization is **choosing the right chart to answer a specific question**. Chart type depends on data relationship, not aesthetics.

## Chart Selection

| Question | Chart Type |
|----------|------------|
| Trends over time? | Line chart |
| Compare categories? | Bar chart |
| Show distribution? | Histogram, box plot |
| Relationship between variables? | Scatter plot |
| Parts of whole? | Pie, stacked bar |
| 2D patterns? | Heatmap |
| Financial data? | Candlestick, OHLC |

## Anti-Patterns (NEVER)

- Don't use Chinese characters anywhere in charts - use English for labels, titles, legends, data labels
- Don't use Chinese characters in filenames (ASCII only)
- Don't pick wrong chart type for the question
- Don't overload with data → aggregate or sample
- Don't forget labels, title, legend
- Don't use poor colors (colorblind-safe palettes)

**Chart language: Always use English** (titles, axes, legends, labels) to avoid font rendering issues.

## Output Formats

- **PNG**: Static, high-quality for reports
- **HTML**: Interactive (zoom, pan, hover)
- **SVG**: Vector for editing

Filename: `{chart_type}_{timestamp}.{ext}` (ASCII only)

## Workflow

1. **Ask**: What's the question? What story to tell?
2. **Load data** from CSV/JSON (or data-analysis output)
3. **Choose chart type** based on question
4. **Create** Python script and **execute using virtual environment**:
   ```
   .venv/bin/python script.py
   ```
5. **Return** file path to user

## Python Environment

**Auto-initialize virtual environment if needed, then execute:**

```bash
# Navigate to skill directory
cd skills/data-viz

# Auto-create venv if not exists
if [ ! -f ".venv/bin/python" ]; then
    echo "Creating Python environment..."
    ./setup.sh
fi

# Execute script
.venv/bin/python your_script.py
```

The setup script auto-installs: matplotlib, seaborn, plotly, pandas with Chinese font support.

## References (load on demand)

For chart APIs and code templates, load: `references/REFERENCE.md`, `references/templates.md`
