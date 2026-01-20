# Data Visualization - Quick API Reference

## Chinese Font Configuration (IMPORTANT)

**Always configure Chinese fonts before creating charts with Chinese text:**

```python
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

# Method 1: Use system font (recommended for macOS/Linux)
plt.rcParams['font.sans-serif'] = ['Arial Unicode MS', 'PingFang SC', 'Heiti SC', 'STHeiti', 'SimHei']
plt.rcParams['axes.unicode_minus'] = False  # Fix minus sign display

# Method 2: For Linux without Chinese fonts, download and install
import os
import urllib.request
font_dir = os.path.expanduser('~/.local/share/fonts')
os.makedirs(font_dir, exist_ok=True)
font_url = 'https://github.com/StellarCN/scp_zh/raw/master/fonts/SimHei.ttf'
font_path = os.path.join(font_dir, 'SimHei.ttf')
if not os.path.exists(font_path):
    urllib.request.urlretrieve(font_url, font_path)
    fm.fontManager.addfont(font_path)
plt.rcParams['font.sans-serif'] = ['SimHei']
plt.rcParams['axes.unicode_minus'] = False

# Method 3: For plotly (interactive charts)
import plotly.graph_objects as go
fig = go.Figure()
# Plotly auto-detects Chinese characters in most cases
```

## Matplotlib basics

```python
import matplotlib.pyplot as plt
import numpy as np

# Configure Chinese fonts FIRST (if needed)
# See section above

# Create figure
plt.figure(figsize=(10, 6))

# Line chart
plt.plot(x, y, label='Series 1')
plt.plot(x, y2, label='Series 2')

# Scatter plot
plt.scatter(x, y, s=100, alpha=0.5)

# Bar chart
plt.bar(categories, values)

# Histogram
plt.hist(data, bins=30, edgecolor='black')

# Add labels and title
plt.xlabel('X Label')
plt.ylabel('Y Label')
plt.title('Chart Title')
plt.legend()

# Save
plt.savefig('chart.png', dpi=300, bbox_inches='tight')
plt.close()  # Free memory
```

## Seaborn basics

```python
import seaborn as sns
import matplotlib.pyplot as plt

# Set style
sns.set_style('whitegrid')
sns.set_palette('husl')

# Distribution plot
sns.histplot(data=df, x='value', kde=True)
sns.kdeplot(data=df, x='value')
sns.boxplot(data=df, x='category', y='value')

# Relationship plot
sns.scatterplot(data=df, x='x', y='y', hue='category')
sns.lineplot(data=df, x='date', y='value', hue='group')

# Categorical plot
sns.barplot(data=df, x='category', y='value')
sns.countplot(data=df, x='category')

# Matrix plot
sns.heatmap(df.corr(), annot=True, cmap='coolwarm')

# Pair plot
sns.pairplot(df, hue='category')

plt.savefig('chart.png')
plt.close()
```

## Plotly basics

```python
import plotly.express as px
import plotly.graph_objects as go

# Express (quick)
fig = px.line(df, x='date', y='value', title='Title')
fig = px.bar(df, x='category', y='value', color='group')
fig = px.scatter(df, x='x', y='y', color='category', size='size')
fig = px.histogram(df, x='value', nbins=30)

# Graph Objects (custom)
fig = go.Figure()
fig.add_trace(go.Scatter(x=x, y=y, name='Series 1'))
fig.add_trace(go.Scatter(x=x, y2, name='Series 2'))

# Layout
fig.update_layout(
    title='Chart Title',
    xaxis_title='X Axis',
    yaxis_title='Y Axis',
    hovermode='x unified'
)

# Save
fig.write_html('chart.html')
fig.write_image('chart.png', scale=2)
```

## Common chart types

### Line chart (time series)
```python
# matplotlib
plt.plot(df['date'], df['value'])
plt.gcf().autofmt_xdate()  # Rotate dates

# seaborn
sns.lineplot(data=df, x='date', y='value')

# plotly
fig = px.line(df, x='date', y='value')
```

### Bar chart (comparison)
```python
# matplotlib
plt.bar(df['category'], df['value'])

# seaborn
sns.barplot(data=df, x='category', y='value')

# plotly
fig = px.bar(df, x='category', y='value')
```

### Scatter plot (correlation)
```python
# matplotlib
plt.scatter(df['x'], df['y'])

# seaborn
sns.scatterplot(data=df, x='x', y='y', hue='category')

# plotly
fig = px.scatter(df, x='x', y='y', color='category')
```

### Heatmap (correlation matrix)
```python
# seaborn
sns.heatmap(df.corr(), annot=True, cmap='RdYlGn', center=0)

# plotly
fig = px.imshow(df.corr(), text_auto=True, color_continuous_scale='RdBu_r')
```

### Box plot (distribution)
```python
# matplotlib
plt.boxplot([df[df['cat']==c]['value'] for c in df['cat'].unique()])

# seaborn
sns.boxplot(data=df, x='category', y='value')

# plotly
fig = px.box(df, x='category', y='value')
```

## Styling

### Colors
```python
# Named colors
plt.plot(x, y, color='blue', color='red')

# Hex colors
plt.plot(x, y, color='#1f77b4')

# Colormaps
plt.scatter(x, y, c=z, cmap='viridis')
plt.colorbar()

# Seaborn palettes
sns.set_palette('husl')        # Sequential
sns.set_palette('Set2')        # Categorical
sns.set_palette('RdBu_r')      # Diverging
```

### Figure size and DPI
```python
plt.figure(figsize=(12, 6), dpi=100)
plt.savefig('chart.png', dpi=300, bbox_inches='tight')
```

### Fonts
```python
plt.rcParams['font.size'] = 12
plt.rcParams['font.family'] = 'sans-serif'
plt.rcParams['axes.titlesize'] = 16
plt.rcParams['axes.labelsize'] = 14
```

## Subplots

```python
# matplotlib
fig, axes = plt.subplots(2, 2, figsize=(12, 10))

axes[0, 0].plot(x, y1)
axes[0, 1].bar(categories, values)
axes[1, 0].scatter(x, y)
axes[1, 1].hist(data)

plt.tight_layout()
plt.savefig('subplots.png')

# plotly
from plotly.subplots import make_subplots
fig = make_subplots(rows=2, cols=2)
fig.add_trace(go.Scatter(x=x, y=y1), row=1, col=1)
```

## Annotations

```python
# matplotlib
plt.annotate('Peak', xy=(x_peak, y_peak), xytext=(x_peak+1, y_peak+1),
             arrowprops=dict(arrowstyle='->'))
plt.axhline(y=threshold, color='r', linestyle='--', label='Threshold')
plt.axvline(x=date, color='g', linestyle=':', label='Event')

# plotly
fig.add_vline(x=date, line_dash='dot', annotation_text='Event')
fig.add_hrect(y0=lower, y1=upper, fillcolor='green', opacity=0.1)
```
