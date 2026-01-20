# Plotly Interactive Visualization Guide

## Getting started

```python
import plotly.express as px
import plotly.graph_objects as go
import pandas as pd

# Express: Quick charts
fig = px.line(df, x='date', y='value', title='My Chart')
fig.write_html('chart.html')

# Graph Objects: Custom charts
fig = go.Figure()
fig.add_trace(go.Scatter(x=df['date'], y=df['value'], name='Series'))
fig.update_layout(title='My Chart')
fig.write_html('chart.html')
```

## Time series charts

```python
# Simple line chart
fig = px.line(df, x='date', y='price', title='Price Over Time')
fig.update_layout(hovermode='x unified')
fig.write_html('price_chart.html')

# Multiple lines with selectors
fig = px.line(df, x='date', y='value', color='category',
              title='Values by Category',
              labels={'value': 'Price', 'date': 'Date'})

# Add range selector buttons
fig.update_layout(
    xaxis=dict(
        rangeselector=dict(
            buttons=list([
                dict(count=1, label='1m', step='month', stepmode='backward'),
                dict(count=6, label='6m', step='month', stepmode='backward'),
                dict(count=1, label='YTD', step='year', stepmode='todate'),
                dict(count=1, label='1y', step='year', stepmode='backward'),
                dict(step='all')
            ])
        ),
        rangeslider=dict(visible=True),
        type='date'
    )
)
fig.write_html('interactive_timeseries.html')

# Area chart
fig = px.area(df, x='date', y='value', color='category',
              title='Area Chart')
fig.write_html('area_chart.html')

# Candlestick (financial)
fig = go.Figure(data=[go.Candlestick(
    x=df.index,
    open=df['open'],
    high=df['high'],
    low=df['low'],
    close=df['close']
)])
fig.update_layout(title='Candlestick Chart')
fig.write_html('candlestick.html')

# OHLC with volume
from plotly.subplots import make_subplots
fig = make_subplots(rows=2, cols=1, shared_xaxes=True,
                    vertical_spacing=0.03,
                    row_heights=[0.7, 0.3])

fig.add_trace(go.Candlestick(
    x=df.index, open=df['open'], high=df['high'],
    low=df['low'], close=df['close'], name='OHLC'
), row=1, col=1)

colors = ['green' if close >= open else 'red'
          for open, close in zip(df['open'], df['close'])]
fig.add_trace(go.Bar(
    x=df.index, y=df['volume'],
    name='Volume', marker_color=colors
), row=2, col=1)

fig.write_html('ohlc_volume.html')
```

## Distribution charts

```python
# Histogram
fig = px.histogram(df, x='value', nbins=30,
                   title='Distribution',
                   marginal='box')  # Add box plot on top
fig.write_html('histogram.html')

# Density plot
fig = px.density_contour(df, x='x', y='y',
                         title='Density Contour')
fig.write_html('density.html')

# Box plot
fig = px.box(df, x='category', y='value',
             title='Box Plot by Category',
             color='category')
fig.write_html('boxplot.html')

# Violin plot
fig = px.violin(df, x='category', y='value',
                title='Violin Plot',
                box=True, points='all')
fig.write_html('violin.html')

# ECDF (Empirical Cumulative Distribution)
fig = px.ecdf(df, x='value', color='category',
              title='ECDF')
fig.write_html('ecdf.html')
```

## Scatter and correlation

```python
# Basic scatter
fig = px.scatter(df, x='x', y='y',
                 title='Scatter Plot',
                 color='category',
                 size='size',
                 hover_data=['value'])
fig.write_html('scatter.html')

# Scatter with marginal histograms
fig = px.scatter(df, x='x', y='y',
                 color='category',
                 marginal_x='histogram',
                 marginal_y='histogram')
fig.write_html('scatter_marginal.html')

# 3D scatter
fig = px.scatter_3d(df, x='x', y='y', z='z',
                    color='category',
                    title='3D Scatter')
fig.write_html('scatter3d.html')

# Scatter matrix
fig = px.scatter_matrix(df,
                        dimensions=['col1', 'col2', 'col3', 'col4'],
                        color='category')
fig.write_html('scatter_matrix.html')

# Correlation heatmap
corr = df.corr()
fig = px.imshow(corr,
                text_auto=True,
                color_continuous_scale='RdBu_r',
                title='Correlation Matrix')
fig.write_html('heatmap.html')
```

## Composition charts

```python
# Bar chart
fig = px.bar(df, x='category', y='value',
             color='group', title='Bar Chart',
             barmode='group')  # or 'stack', 'overlay'
fig.write_html('barchart.html')

# Stacked bar
fig = px.bar(df, x='category', y='value',
             color='group', title='Stacked Bar')
fig.write_html('stacked_bar.html')

# Pie chart
sizes = df.groupby('category')['value'].sum().reset_index()
fig = px.pie(sizes, values='value', names='category',
             title='Distribution',
             hole=0.3)  # Donut chart
fig.write_html('piechart.html')

# Sunburst chart (hierarchical)
fig = px.sunburst(df, path=['level1', 'level2', 'level3'],
                  values='value',
                  title='Sunburst')
fig.write_html('sunburst.html')

# Treemap
fig = px.treemap(df, path=['level1', 'level2'],
                 values='value',
                 color='value',
                 title='Treemap')
fig.write_html('treemap.html')
```

## Maps and geographic

```python
# Scatter mapbox (requires mapbox token or openstreetmap)
fig = px.scatter_mapbox(df, lat='latitude', lon='longitude',
                        color='value', size='size',
                        mapbox_style='open-street-map',
                        title='Map',
                        zoom=10)
fig.write_html('map.html')

# Choropleth
fig = px.choropleth(df, locations='country_code',
                    color='value',
                    hover_name='country',
                    title='World Map',
                    color_continuous_scale='Viridis')
fig.write_html('world_map.html')

# US states choropleth
fig = px.choropleth(df, locations='state_code',
                    locationmode='USA-states',
                    color='value',
                    scope='usa',
                    title='US Map')
fig.write_html('us_map.html')
```

## Statistical charts

```python
# Error bars
fig = px.scatter(df, x='category', y='value',
                 color='group',
                 error_y='std',
                 title='Error Bars')
fig.write_html('error_bars.html')

# Facet plots (small multiples)
fig = px.scatter(df, x='x', y='y',
                 facet_col='category',
                 facet_row='group',
                 trendline='ols')
fig.write_html('faceted.html')

# Parallel coordinates
fig = px.parallel_coordinates(df, color='value',
                              title='Parallel Coordinates')
fig.write_html('parallel_coordinates.html')

# Parallel categories
fig = px.parallel_categories(df, color='value',
                             title='Parallel Categories')
fig.write_html('parallel_categories.html')
```

## Advanced interactivity

```python
# Dropdown for filtering
import plotly.graph_objects as go

fig = go.Figure()

# Add traces for each category
for category in df['category'].unique():
    data = df[df['category'] == category]
    fig.add_trace(go.Scatter(
        x=data['date'], y=data['value'],
        name=category, visible=True
    ))

# Add dropdown
buttons = []
for i, category in enumerate(df['category'].unique()):
    buttons.append(dict(
        label=category,
        method='update',
        args=[{'visible': [i == j for j in range(len(df['category'].unique()))]},
              {'title': f'{category} Data'}]
    ))

fig.update_layout(
    updatemenus=[dict(
        type='dropdown',
        direction='down',
        showactive=True,
        x=0.1, xanchor='left',
        y=1.15, yanchor='top',
        buttons=buttons
    )]
)
fig.write_html('dropdown_filter.html')

# Slider for time range
fig = go.Figure()
fig.add_trace(go.Scatter(x=df['date'], y=df['value']))

fig.update_layout(
    sliders=[dict(
        steps=[dict(args=[{'x': [df['date'].min(), date]}],
                    label=str(date)[:10],
                    method='relayout')
               for date in df['date'][::30]],  # Every 30th date
        active=0,
        currentvalue={'prefix': 'Date from: '}
    )]
)
fig.write_html('time_slider.html')

# Custom hover templates
fig = px.scatter(df, x='x', y='y', color='category')
fig.update_traces(
    hovertemplate='<b>%{fullData.name}</b><br>' +
                  'X: %{x:.2f}<br>' +
                  'Y: %{y:.2f}<br>' +
                  '<extra></extra>'
)
fig.write_html('custom_hover.html')
```

## Styling and themes

```python
# Built-in themes
fig = px.line(df, x='date', y='value')
fig.update_layout(template='plotly_dark')  # or plotly, ggplot2, seaborn, simple_white
fig.write_html('themed.html')

# Custom layout
fig = px.line(df, x='date', y='value', color='category')
fig.update_layout(
    title=dict(text='Custom Title', font=dict(size=20, color='blue')),
    xaxis_title='X Axis Label',
    yaxis_title='Y Axis Label',
    legend_title='Categories',
    font=dict(family='Arial', size=12),
    plot_bgcolor='white',
    paper_bgcolor='lightgray'
)
fig.write_html('custom_layout.html')

# Color sequences
fig = px.scatter(df, x='x', y='y', color='category',
                 color_discrete_sequence=px.colors.qualitative.Plotly)
fig.write_html('custom_colors.html')
```
