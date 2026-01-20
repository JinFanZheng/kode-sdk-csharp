# Matplotlib Patterns Guide

## Time series visualization

```python
import matplotlib.pyplot as plt
import pandas as pd

# Single line
plt.figure(figsize=(12, 6))
plt.plot(df['date'], df['price'], linewidth=2)
plt.title('Price Over Time')
plt.xlabel('Date')
plt.ylabel('Price ($)')
plt.grid(True, alpha=0.3)
plt.gcf().autofmt_xdate()
plt.savefig('price_trend.png', dpi=300)
plt.close()

# Multiple lines with legend
plt.figure(figsize=(12, 6))
for category in df['category'].unique():
    data = df[df['category'] == category]
    plt.plot(data['date'], data['value'], label=category, linewidth=2)
plt.legend(title='Category')
plt.title('Values by Category Over Time')
plt.savefig('multi_line.png', dpi=300)
plt.close()

# With moving average
fig, ax = plt.subplots(figsize=(12, 6))
ax.plot(df['date'], df['price'], label='Price', alpha=0.7)
ax.plot(df['date'], df['ma_20'], label='20-day MA', linewidth=2)
ax.plot(df['date'], df['ma_50'], label='50-day MA', linewidth=2)
ax.legend()
ax.set_title('Price with Moving Averages')
ax.set_xlabel('Date')
ax.set_ylabel('Price ($)')
plt.savefig('ma_chart.png', dpi=300)
plt.close()
```

## Financial charts

```python
# Candlestick chart
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle
import pandas as pd

def plot_candlestick(df, ax):
    """Plot candlestick chart"""
    width = 0.6
    width2 = 0.1

    up = df[df['close'] >= df['open']]
    down = df[df['close'] < df['open']]

    # Up bars
    ax.bar(up.index, up['close'] - up['open'], width, bottom=up['open'],
           color='g', edgecolor='none')
    ax.bar(up.index, up['high'] - up['close'], width2, bottom=up['close'],
           color='g', edgecolor='none')
    ax.bar(up.index, up['low'] - up['open'], width2, bottom=up['open'],
           color='g', edgecolor='none')

    # Down bars
    ax.bar(down.index, down['close'] - down['open'], width, bottom=down['open'],
           color='r', edgecolor='none')
    ax.bar(down.index, down['high'] - down['open'], width2, bottom=down['open'],
           color='r', edgecolor='none')
    ax.bar(down.index, down['low'] - down['close'], width2, bottom=down['close'],
           color='r', edgecolor='none')

fig, ax = plt.subplots(figsize=(14, 7))
plot_candlestick(df.tail(50), ax)
ax.set_title('Candlestick Chart')
ax.set_xlabel('Date')
ax.set_ylabel('Price')
plt.xticks(rotation=45)
plt.tight_layout()
plt.savefig('candlestick.png', dpi=300)
plt.close()

# OHLC with volume
fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(14, 8),
                               gridspec_kw={'height_ratios': [3, 1]})
# Plot OHLC on ax1
# Plot volume on ax2
colors = ['g' if close >= open else 'r' for open, close in zip(df['open'], df['close'])]
ax2.bar(df.index, df['volume'], color=colors, alpha=0.5)
ax2.set_ylabel('Volume')

plt.tight_layout()
plt.savefig('ohlc_volume.png', dpi=300)
plt.close()
```

## Distribution visualization

```python
# Histogram with KDE
fig, ax = plt.subplots(figsize=(10, 6))
ax.hist(df['value'], bins=30, density=True, alpha=0.7, edgecolor='black')
df['value'].plot.kde(ax=ax, color='red', linewidth=2)
ax.set_title('Distribution with KDE')
ax.set_xlabel('Value')
ax.set_ylabel('Density')
plt.savefig('distribution.png', dpi=300)
plt.close()

# Box plot by category
fig, ax = plt.subplots(figsize=(10, 6))
categories = df['category'].unique()
data = [df[df['category'] == cat]['value'] for cat in categories]
bp = ax.boxplot(data, labels=categories, patch_artist=True)
for patch, color in zip(bp['boxes'], plt.cm.Set2.colors):
    patch.set_facecolor(color)
ax.set_title('Distribution by Category')
ax.set_ylabel('Value')
plt.xticks(rotation=45)
plt.savefig('boxplot.png', dpi=300)
plt.close()

# Violin plot
fig, ax = plt.subplots(figsize=(10, 6))
positions = range(1, len(categories) + 1)
vp = ax.violinplot(data, positions=positions, showmeans=True, showmedians=True)
ax.set_xticks(positions)
ax.set_xticklabels(categories)
ax.set_title('Violin Plot by Category')
plt.savefig('violin.png', dpi=300)
plt.close()
```

## Correlation and relationships

```python
# Scatter plot with regression line
fig, ax = plt.subplots(figsize=(10, 6))
ax.scatter(df['x'], df['y'], alpha=0.5)
# Add regression line
z = np.polyfit(df['x'], df['y'], 1)
p = np.poly1d(z)
ax.plot(df['x'].sort_values(), p(df['x'].sort_values()),
        color='red', linewidth=2, label=f'Trend: y={z[0]:.2f}x+{z[1]:.2f}')
ax.set_xlabel('X Variable')
ax.set_ylabel('Y Variable')
ax.legend()
ax.set_title('Scatter Plot with Trend Line')
plt.savefig('scatter_trend.png', dpi=300)
plt.close()

# Heatmap
import seaborn as sns
fig, ax = plt.subplots(figsize=(10, 8))
sns.heatmap(df.corr(), annot=True, fmt='.2f', cmap='RdYlGn',
            center=0, square=True, linewidths=1, ax=ax)
ax.set_title('Correlation Matrix')
plt.tight_layout()
plt.savefig('heatmap.png', dpi=300)
plt.close()

# Pair plot
from pandas.plotting import scatter_matrix
fig, axes = scatter_matrix(df[['col1', 'col2', 'col3']],
                          figsize=(12, 12), diagonal='kde')
plt.tight_layout()
plt.savefig('pairplot.png', dpi=300)
plt.close()
```

## Composition charts

```python
# Stacked bar chart
categories = df['category'].unique()
groups = df['group'].unique()
bottom = np.zeros(len(categories))

fig, ax = plt.subplots(figsize=(10, 6))
for group in groups:
    values = [df[(df['category'] == cat) & (df['group'] == group)]['value'].sum()
              for cat in categories]
    ax.bar(categories, values, bottom=bottom, label=group)
    bottom += values

ax.set_title('Stacked Bar Chart')
ax.set_ylabel('Value')
ax.legend(title='Group')
plt.xticks(rotation=45)
plt.tight_layout()
plt.savefig('stacked_bar.png', dpi=300)
plt.close()

# Pie chart
fig, ax = plt.subplots(figsize=(8, 8))
sizes = df.groupby('category')['value'].sum()
labels = sizes.index
explode = [0.1 if i == 0 else 0 for i in range(len(sizes))]  # Emphasize first slice
ax.pie(sizes, explode=explode, labels=labels, autopct='%1.1f%%',
       startangle=90, colors=plt.cm.Set3.colors)
ax.set_title('Distribution by Category')
plt.savefig('piechart.png', dpi=300, bbox_inches='tight')
plt.close()
```

## Multiple charts (subplots)

```python
# Dashboard style
fig = plt.figure(figsize=(14, 10))
gs = fig.add_gridspec(3, 2)

# Main chart (top row)
ax1 = fig.add_subplot(gs[0, :])
ax1.plot(df['date'], df['value'], linewidth=2)
ax1.set_title('Main Metric Over Time')

# Sub charts (bottom row)
ax2 = fig.add_subplot(gs[1, 0])
ax2.bar(df['category'], df['value1'])
ax2.set_title('By Category')

ax3 = fig.add_subplot(gs[1, 1])
ax3.hist(df['value2'], bins=20)
ax3.set_title('Distribution')

ax4 = fig.add_subplot(gs[2, :])
ax4.plot(df['date'], df['metric1'], label='Metric 1')
ax4.plot(df['date'], df['metric2'], label='Metric 2')
ax4.legend()
ax4.set_title('Comparison')

plt.tight_layout()
plt.savefig('dashboard.png', dpi=300)
plt.close()
```

## Annotations and styling

```python
# Highlight key events
fig, ax = plt.subplots(figsize=(12, 6))
ax.plot(df['date'], df['value'], linewidth=2)

# Add vertical line for event
event_date = '2024-01-15'
ax.axvline(pd.to_datetime(event_date), color='red',
           linestyle='--', linewidth=2, label='Major Event')

# Add annotation
peak_idx = df['value'].idxmax()
ax.annotate(f"Peak: {df['value'].max():.2f}",
            xy=(peak_idx, df['value'].max()),
            xytext=(peak_idx, df['value'].max() * 1.1),
            arrowprops=dict(arrowstyle='->', color='red'),
            fontsize=12, fontweight='bold')

# Add shaded region
ax.axhspan(ymin=100, ymax=200, alpha=0.2, color='green',
           label='Target Range')

ax.legend()
ax.set_title('Chart with Annotations')
plt.savefig('annotated.png', dpi=300)
plt.close()

# Custom styling
plt.style.use('seaborn-v0_8-darkgrid')
fig, ax = plt.subplots(figsize=(10, 6))

colors = ['#1f77b4', '#ff7f0e', '#2ca02c', '#d62728']
for i, category in enumerate(categories):
    data = df[df['category'] == category]
    ax.plot(data['date'], data['value'],
            color=colors[i % len(colors)], linewidth=2.5,
            label=category, marker='o', markersize=4)

ax.set_title('Custom Styled Chart', fontsize=16, fontweight='bold')
ax.set_xlabel('Date', fontsize=12)
ax.set_ylabel('Value', fontsize=12)
ax.legend(loc='best', frameon=True, shadow=True)
ax.grid(True, alpha=0.3, linestyle='--')

plt.savefig('styled.png', dpi=300)
plt.close()
```
