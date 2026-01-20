---
name: data-analysis
description: >-
  Data analysis and statistical computation. Use when user needs "数据分析/统计/计算指标/数据洞察".
  Supports general analysis, financial data (stocks, returns), business data (sales, users), and scientific research.
  Uses pandas/numpy/scikit-learn for processing. Automatically activates data-base for data acquisition.
license: MIT
compatibility: Requires Python 3.10+, pandas, numpy, scipy
allowed-tools: BashRun FsRead FsWrite
metadata:
  category: data
  tier: analysis
  version: 1.0.0
  author: Kode SDK
  scenarios:
    - general
    - financial
    - business
    - scientific
---

# Data Analysis - Statistical Computing & Insights

## When to use this skill

Activate this skill when:
- User mentions "数据分析", "统计", "计算指标", "数据洞察"
- Need to analyze structured data (CSV, JSON, database)
- Calculate statistics, trends, patterns
- Financial analysis (returns, volatility, technical indicators)
- Business analytics (sales, user behavior, KPIs)
- Scientific data processing and hypothesis testing

## Workflow

### 1. Get data

**⚠️ IMPORTANT: File naming requirements**
- **File names MUST NOT contain Chinese characters or non-ASCII characters**
- Use only English letters, numbers, underscores, and hyphens
- Examples: `data.csv`, `sales_report_2025.xlsx`, `analysis_results.json`
- ❌ Invalid: `销售数据.csv`, `数据文件.xlsx`, `報表.json`
- This ensures compatibility across different systems and prevents encoding issues

**If data already exists:**
- Read from file (CSV, JSON, Excel)
- Query database if available

**If file names contain Chinese characters:**
- Ask the user to rename the file to English/ASCII characters
- Or rename the file when saving it to the agent directory

**If no data:**
- Automatically activate `data-base` skill
- Scrape/collect required data
- Save to structured format

### 2. Understand requirements

Ask the user:
- What questions do you want to answer?
- What metrics are important?
- What format for results? (summary, chart, report)
- Any specific statistical methods?

### 3. Analyze

**General analysis:**
- Descriptive statistics (mean, median, std, percentiles)
- Distribution analysis (histograms, box plots)
- Correlation analysis
- Group comparisons

**Financial analysis:**
- Return calculation (simple, log, cumulative)
- Risk metrics (volatility, VaR, Sharpe ratio)
- Technical indicators (MA, RSI, MACD)
- Portfolio analysis

**Business analysis:**
- Trend analysis (growth rates, YoY, MoM)
- Cohort analysis
- Funnel analysis
- A/B testing

**Scientific analysis:**
- Hypothesis testing (t-test, chi-square, ANOVA)
- Regression analysis
- Time series analysis
- Statistical significance

### 4. Output

Generate results in:
- **Summary statistics**: Tables with key metrics
- **Charts**: Save as PNG files
- **Report**: Markdown with findings
- **Data**: Processed CSV/JSON for further use

## Python Environment

**Auto-initialize virtual environment if needed, then execute:**

```bash
cd skills/data-analysis

if [ ! -f ".venv/bin/python" ]; then
    echo "Creating Python environment..."
    ./setup.sh
fi

.venv/bin/python your_script.py
```

The setup script auto-installs: pandas, numpy, scipy, scikit-learn, statsmodels, with Chinese font support.

## Analysis scenarios

### General data
```python
import pandas as pd

# Load and summarize
df = pd.read_csv('data.csv')
summary = df.describe()
correlations = df.corr()
```

### Financial data
```python
# Calculate returns
df['return'] = df['price'].pct_change()

# Risk metrics
volatility = df['return'].std() * (252 ** 0.5)
sharpe = df['return'].mean() / df['return'].std() * (252 ** 0.5)
```

### Business data
```python
# Group by category
grouped = df.groupby('category').agg({
    'revenue': ['sum', 'mean', 'count']
})

# Growth rate
df['growth'] = df['revenue'].pct_change()
```

### Scientific data
```python
from scipy import stats

# T-test
t_stat, p_value = stats.ttest_ind(group_a, group_b)

# Regression
from sklearn.linear_model import LinearRegression
model = LinearRegression()
model.fit(X, y)
```

## File path conventions

### Temporary output (session-scoped)
Files written to the current directory will be stored in the session directory:
```python
import time
from datetime import datetime

# Use timestamp for unique filenames (avoid conflicts)
timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')

# Charts and temporary files
plt.savefig(f'analysis_{timestamp}.png')      # → $KODE_AGENT_DIR/analysis_20250115_143022.png
df.to_csv(f'results_{timestamp}.csv')        # → $KODE_AGENT_DIR/results_20250115_143022.csv
```

**Always use unique filenames** to avoid conflicts when running multiple analyses:
- Use timestamps: `analysis_20250115_143022.png`
- Use descriptive names + timestamps: `sales_report_q1_2025.csv`
- Use random suffix for scripts: `script_{random.randint(1000,9999)}.py`

### User data (persistent)
Use `$KODE_USER_DIR` for persistent user data:
```python
import os
user_dir = os.getenv('KODE_USER_DIR')

# Save to user memory
memory_file = f"{user_dir}/.memory/facts/preferences.jsonl"

# Read from knowledge base
knowledge_dir = f"{user_dir}/.knowledge/docs"
```

### Environment variables
- `KODE_AGENT_DIR`: Session directory for temporary output (charts, analysis results)
- `KODE_USER_DIR`: User data directory for persistent storage (memory, knowledge, config)

## Best practices

- **File names MUST be ASCII-only**: No Chinese or non-ASCII characters in filenames
- **Always inspect data first**: `df.head()`, `df.info()`, `df.describe()`
- **Handle missing values**: Drop or impute based on context
- **Check assumptions**: Normality, independence, etc.
- **Visualize**: Charts reveal patterns tables hide
- **Document findings**: Explain metrics and their implications
- **Use correct paths**: Temporary outputs to current dir, persistent data to `$KODE_USER_DIR`

## Quick reference

- [REFERENCE.md](references/REFERENCE.md) - pandas/numpy API reference
- [references/financial.md](references/financial.md) - Financial analysis recipes
- [references/business.md](references/business.md) - Business analytics recipes
- [references/scientific.md](references/scientific.md) - Statistical testing methods
- [references/templates.md](references/templates.md) - Code templates

## Environment setup

This skill uses Python scripts. To set up the environment:

```bash
# Navigate to the skill directory
cd apps/assistant/skills/data-analysis

# Run the setup script (creates venv and installs dependencies)
./setup.sh

# Activate the environment
source .venv/bin/activate
```

The setup script will:
- Create a Python virtual environment in `.venv/`
- Install required packages (pandas, numpy, scipy, scikit-learn, statsmodels)

To run Python scripts with the skill environment:
```bash
# Use the virtual environment's Python
.venv/bin/python script.py

# Or activate first, then run normally
source .venv/bin/activate
python script.py
```
