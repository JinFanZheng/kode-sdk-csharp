# Visualization Templates

## Template 1: Time series dashboard

```python
#!/usr/bin/env python3
"""
Time series visualization dashboard
"""

import pandas as pd
import matplotlib.pyplot as plt
import seaborn as sns

def create_timeseries_dashboard(df, output_file='dashboard.png'):
    """Create a time series dashboard"""
    fig = plt.figure(figsize=(14, 10))
    gs = fig.add_gridspec(3, 2, hspace=0.3, wspace=0.3)

    # Main chart (top row)
    ax1 = fig.add_subplot(gs[0, :])
    ax1.plot(df['date'], df['value'], linewidth=2, color='#1f77b4')
    ax1.set_title('Main Metric Over Time', fontsize=14, fontweight='bold')
    ax1.set_ylabel('Value')
    ax1.grid(True, alpha=0.3)

    # Moving average comparison
    ax2 = fig.add_subplot(gs[1, 0])
    ax2.plot(df['date'], df['value'], label='Value', alpha=0.5)
    ax2.plot(df['date'], df['ma_7'], label='7-day MA', linewidth=2)
    ax2.plot(df['date'], df['ma_30'], label='30-day MA', linewidth=2)
    ax2.set_title('Moving Averages')
    ax2.legend()
    ax2.grid(True, alpha=0.3)

    # Distribution
    ax3 = fig.add_subplot(gs[1, 1])
    ax3.hist(df['value'], bins=30, edgecolor='black', alpha=0.7)
    ax3.axvline(df['value'].mean(), color='red', linestyle='--', label=f"Mean: {df['value'].mean():.2f}")
    ax3.set_title('Distribution')
    ax3.legend()
    ax3.set_xlabel('Value')

    # Monthly aggregation
    ax4 = fig.add_subplot(gs[2, 0])
    monthly = df.set_index('date').resample('M').mean()
    ax4.bar(range(len(monthly)), monthly['value'], edgecolor='black')
    ax4.set_xticks(range(len(monthly)))
    ax4.set_xticklabels([d.strftime('%Y-%m') for d in monthly.index], rotation=45)
    ax4.set_title('Monthly Average')
    ax4.set_ylabel('Value')

    # Box plot by day of week
    ax5 = fig.add_subplot(gs[2, 1])
    df['dow'] = pd.to_datetime(df['date']).dt.day_name()
    order = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
    df.boxplot(column='value', by='dow', ax=ax5, order=order)
    ax5.set_title('By Day of Week')
    ax5.set_xlabel('Day')
    plt.suptitle('')  # Remove default title

    plt.savefig(output_file, dpi=300, bbox_inches='tight')
    plt.close()

if __name__ == "__main__":
    df = pd.read_csv('data.csv')
    df['date'] = pd.to_datetime(df['date'])
    df['ma_7'] = df['value'].rolling(7).mean()
    df['ma_30'] = df['value'].rolling(30).mean()
    create_timeseries_dashboard(df)
```

## Template 2: Financial chart with indicators

```python
#!/usr/bin/env python3
"""
Financial visualization with technical indicators
"""

import pandas as pd
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle

def create_financial_chart(df, output_file='financial_chart.png'):
    """Create financial chart with indicators"""
    fig = plt.figure(figsize=(14, 10))
    gs = fig.add_gridspec(3, 1, height_ratios=[3, 1, 1], hspace=0.3)

    # Price and MA (main chart)
    ax1 = fig.add_subplot(gs[0])
    ax1.plot(df.index, df['close'], label='Close', linewidth=2, color='black')
    ax1.plot(df.index, df['ma_20'], label='20 MA', linewidth=1.5, color='blue')
    ax1.plot(df.index, df['ma_50'], label='50 MA', linewidth=1.5, color='orange')
    ax1.fill_between(df.index, df['bb_upper'], df['bb_lower'], alpha=0.2, color='gray', label='Bollinger Bands')
    ax1.set_title('Price with Technical Indicators')
    ax1.legend(loc='upper left')
    ax1.grid(True, alpha=0.3)

    # Volume
    colors = ['green' if close >= open_ else 'red'
              for open_, close in zip(df['open'], df['close'])]
    ax2 = fig.add_subplot(gs[1], sharex=ax1)
    ax2.bar(df.index, df['volume'], color=colors, alpha=0.5, edgecolor='none')
    ax2.set_ylabel('Volume')
    ax2.grid(True, alpha=0.3)

    # RSI
    ax3 = fig.add_subplot(gs[2], sharex=ax1)
    ax3.plot(df.index, df['rsi'], color='purple', linewidth=2)
    ax3.axhline(70, color='red', linestyle='--', alpha=0.5)
    ax3.axhline(30, color='green', linestyle='--', alpha=0.5)
    ax3.set_ylabel('RSI')
    ax3.set_ylim(0, 100)
    ax3.grid(True, alpha=0.3)

    # Hide x labels for top charts
    plt.setp(ax1.get_xticklabels(), visible=False)
    plt.setp(ax2.get_xticklabels(), visible=False)

    plt.savefig(output_file, dpi=300, bbox_inches='tight')
    plt.close()

if __name__ == "__main__":
    df = pd.read_csv('price_data.csv', parse_dates=['date'], index_col='date')
    create_financial_chart(df)
```

## Template 3: Comparison chart

```python
#!/usr/bin/env python3
"""
Comparison visualization for multiple groups
"""

import pandas as pd
import matplotlib.pyplot as plt
import seaborn as sns

def create_comparison_chart(df, output_file='comparison.png'):
    """Create comparison chart"""
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))

    # Bar chart comparison
    ax1 = axes[0, 0]
    categories = df['category'].unique()
    x = range(len(categories))
    width = 0.35

    group_a = df[df['group'] == 'A'].groupby('category')['value'].mean()
    group_b = df[df['group'] == 'B'].groupby('category')['value'].mean()

    ax1.bar([i - width/2 for i in x], group_a, width, label='Group A', alpha=0.8)
    ax1.bar([i + width/2 for i in x], group_b, width, label='Group B', alpha=0.8)
    ax1.set_xticks(x)
    ax1.set_xticklabels(categories, rotation=45)
    ax1.set_title('Average Value by Category')
    ax1.legend()
    ax1.grid(True, alpha=0.3, axis='y')

    # Box plot comparison
    ax2 = axes[0, 1]
    df.boxplot(column='value', by='group', ax=ax2)
    ax2.set_title('Distribution by Group')
    plt.suptitle('')

    # Time series comparison
    ax3 = axes[1, 0]
    for group in df['group'].unique():
        data = df[df['group'] == group].groupby('date')['value'].mean()
        ax3.plot(data.index, data.values, label=group, linewidth=2)
    ax3.set_title('Trend Comparison')
    ax3.legend()
    ax3.grid(True, alpha=0.3)
    ax3.tick_params(axis='x', rotation=45)

    # Heatmap
    ax4 = axes[1, 1]
    pivot = df.pivot_table(values='value', index='category', columns='group', aggfunc='mean')
    sns.heatmap(pivot, annot=True, fmt='.2f', cmap='RdYlGn', ax=ax4)
    ax4.set_title('Average Value Heatmap')

    plt.tight_layout()
    plt.savefig(output_file, dpi=300, bbox_inches='tight')
    plt.close()

if __name__ == "__main__":
    df = pd.read_csv('comparison_data.csv')
    df['date'] = pd.to_datetime(df['date'])
    create_comparison_chart(df)
```

## Template 4: Interactive report (Plotly)

```python
#!/usr/bin/env python3
"""
Interactive dashboard with Plotly
"""

import pandas as pd
import plotly.express as px
from plotly.subplots import make_subplots
import plotly.graph_objects as go

def create_interactive_dashboard(df, output_file='dashboard.html'):
    """Create interactive dashboard"""

    # Create subplots
    fig = make_subplots(
        rows=2, cols=2,
        subplot_titles=('Time Series', 'Distribution', 'Bar Chart', 'Scatter'),
        specs=[[{'secondary_y': True}, {'type': 'domain'}],
               [{}, {}]],
        vertical_spacing=0.15,
        horizontal_spacing=0.1
    )

    # Time series
    fig.add_trace(
        go.Scatter(x=df['date'], y=df['value'], name='Value', line=dict(color='blue')),
        row=1, col=1
    )
    fig.add_trace(
        go.Scatter(x=df['date'], y=df['ma'], name='MA', line=dict(color='orange', dash='dot')),
        row=1, col=1
    )

    # Pie chart
    pie_data = df.groupby('category')['value'].sum().reset_index()
    fig.add_trace(
        go.Pie(labels=pie_data['category'], values=pie_data['value'], name='Distribution'),
        row=1, col=2
    )

    # Bar chart
    bar_data = df.groupby('category')['value'].mean().reset_index()
    fig.add_trace(
        go.Bar(x=bar_data['category'], y=bar_data['value'], name='Average'),
        row=2, col=1
    )

    # Scatter
    fig.add_trace(
        go.Scatter(x=df['x'], y=df['y'], mode='markers',
                   marker=dict(size=df['size'], color=df['value'], showscale=True),
                   name='Scatter'),
        row=2, col=2
    )

    # Update layout
    fig.update_layout(
        title_text='Interactive Dashboard',
        showlegend=False,
        height=800
    )

    # Add range selector to time series
    fig.update_xaxes(
        rangeselector=dict(
            buttons=list([
                dict(count=1, label='1m', step='month', stepmode='backward'),
                dict(count=6, label='6m', step='month', stepmode='backward'),
                dict(count=1, label='YTD', step='year', stepmode='todate'),
                dict(step='all')
            ])
        ),
        row=1, col=1
    )

    fig.write_html(output_file)

if __name__ == "__main__":
    df = pd.read_csv('data.csv')
    df['date'] = pd.to_datetime(df['date'])
    create_interactive_dashboard(df)
```

## Template 5: Report generator

```python
#!/usr/bin/env python3
"""
Generate analysis report with charts
"""

import pandas as pd
import matplotlib.pyplot as plt
from datetime import datetime

def generate_report(df, output_prefix='report'):
    """Generate analysis report with multiple charts"""

    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')

    # Chart 1: Overview
    fig, axes = plt.subplots(2, 2, figsize=(12, 8))
    fig.suptitle('Data Overview', fontsize=16, fontweight='bold')

    # Summary statistics
    summary = df.describe()
    axes[0, 0].axis('off')
    axes[0, 0].table(cellText=summary.round(2).values,
                     rowLabels=summary.index,
                     colLabels=summary.columns,
                     loc='center', cellLoc='center')
    axes[0, 0].set_title('Summary Statistics')

    # Missing values
    missing = df.isnull().sum()
    axes[0, 1].bar(range(len(missing)), missing)
    axes[0, 1].set_xticks(range(len(missing)))
    axes[0, 1].set_xticklabels(missing.index, rotation=45)
    axes[0, 1].set_title('Missing Values')
    axes[0, 1].set_ylabel('Count')

    # Data types
    dtype_counts = df.dtypes.value_counts()
    axes[1, 0].pie(dtype_counts, labels=dtype_counts.index, autopct='%1.1f%%')
    axes[1, 0].set_title('Data Types')

    # Correlation heatmap
    numeric_df = df.select_dtypes(include=['number'])
    if len(numeric_df.columns) > 1:
        im = axes[1, 1].imshow(numeric_df.corr(), cmap='RdBu_r', aspect='auto', vmin=-1, vmax=1)
        axes[1, 1].set_xticks(range(len(numeric_df.columns)))
        axes[1, 1].set_yticks(range(len(numeric_df.columns)))
        axes[1, 1].set_xticklabels(numeric_df.columns, rotation=45)
        axes[1, 1].set_yticklabels(numeric_df.columns)
        axes[1, 1].set_title('Correlation')
        plt.colorbar(im, ax=axes[1, 1])

    plt.tight_layout()
    plt.savefig(f'{output_prefix}_overview_{timestamp}.png', dpi=300, bbox_inches='tight')
    plt.close()

    # Chart 2: Distributions
    numeric_cols = df.select_dtypes(include=['number']).columns
    n_cols = min(3, len(numeric_cols))
    n_rows = (len(numeric_cols) + n_cols - 1) // n_cols

    fig, axes = plt.subplots(n_rows, n_cols, figsize=(15, 4*n_rows))
    if n_rows == 1 and n_cols == 1:
        axes = [[axes]]
    elif n_rows == 1:
        axes = [axes]
    elif n_cols == 1:
        axes = [[ax] for ax in axes]

    for i, col in enumerate(numeric_cols):
        row, col_idx = i // n_cols, i % n_cols
        axes[row][col_idx].hist(df[col].dropna(), bins=30, edgecolor='black')
        axes[row][col_idx].set_title(f'Distribution: {col}')
        axes[row][col_idx].set_xlabel(col)
        axes[row][col_idx].set_ylabel('Frequency')
        axes[row][col_idx].grid(True, alpha=0.3)

    # Hide empty subplots
    for i in range(len(numeric_cols), n_rows * n_cols):
        row, col_idx = i // n_cols, i % n_cols
        axes[row][col_idx].axis('off')

    plt.tight_layout()
    plt.savefig(f'{output_prefix}_distributions_{timestamp}.png', dpi=300, bbox_inches='tight')
    plt.close()

    print(f"Report generated: {output_prefix}_overview_{timestamp}.png")
    print(f"Report generated: {output_prefix}_distributions_{timestamp}.png")

if __name__ == "__main__":
    df = pd.read_csv('data.csv')
    generate_report(df)
```
