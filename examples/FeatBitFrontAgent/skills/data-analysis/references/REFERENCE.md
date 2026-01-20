# Data Analysis - Pandas & NumPy Reference

## Data loading

```python
import pandas as pd
import numpy as np

# CSV
df = pd.read_csv('file.csv')
df.to_csv('output.csv', index=False)

# JSON
df = pd.read_json('file.json')
df.to_json('output.json', orient='records')

# Excel
df = pd.read_excel('file.xlsx')
df.to_excel('output.xlsx', index=False)

# From dict
df = pd.DataFrame(data)
```

## Data inspection

```python
# Basic info
df.head()           # First 5 rows
df.tail()           # Last 5 rows
df.info()           # Data types, non-null counts
df.describe()       # Summary statistics
df.shape            # (rows, columns)
df.columns          # Column names

# Missing values
df.isnull().sum()           # Count per column
df.isnull().mean()          # Percentage per column
df.dropna()                 # Drop rows with NaN
df.fillna(0)                # Fill with value
df.fillna(df.mean())        # Fill with mean

# Data types
df['col'].astype(int)               # Convert type
pd.to_datetime(df['date'])          # To datetime
pd.to_numeric(df['col'], errors='coerce')  # To number
```

## Filtering & selection

```python
# Select columns
df['column']           # Single column (Series)
df[['col1', 'col2']]   # Multiple columns (DataFrame)

# Filter rows
df[df['column'] > 5]                   # Condition
df[(df['a'] > 5) & (df['b'] < 10)]     # Multiple conditions
df.query('column > 5')                 # Query string

# Positional
df.iloc[0]              # First row
df.iloc[:, 0]           # First column
df.loc[row_label]       # By label
```

## Grouping & aggregation

```python
# Group by
grouped = df.groupby('category')
grouped.mean()                      # Mean per group
grouped.agg(['mean', 'sum', 'count'])  # Multiple aggregations

# Specific aggregations
df.groupby('category').agg({
    'revenue': 'sum',
    'users': 'count',
    'price': ['mean', 'median']
})

# Pivot table
df.pivot_table(values='value', index='row', columns='col', aggfunc='sum')
```

## Time series

```python
# Convert to datetime
df['date'] = pd.to_datetime(df['date'])

# Set as index
df.set_index('date', inplace=True)

# Resampling
df.resample('D').mean()      # Daily
df.resample('W').sum()       # Weekly
df.resample('M').last()      # Monthly

# Rolling windows
df['price'].rolling(7).mean()    # 7-day moving average
df['price'].rolling(30).std()    # 30-day volatility

# Shift/lag
df['price_lag1'] = df['price'].shift(1)
df['return'] = df['price'].pct_change()
```

## Merging & joining

```python
# Concatenate
pd.concat([df1, df2])           # Vertical stack
pd.concat([df1, df2], axis=1)   # Horizontal stack

# Merge (SQL join)
pd.merge(df1, df2, on='key')                    # Inner
pd.merge(df1, df2, on='key', how='left')        # Left join
pd.merge(df1, df2, left_on='key1', right_on='key2')

# Join on index
df1.join(df2, lsuffix='_left', rsuffix='_right')
```

## Statistical functions

```python
# Descriptive
df['col'].mean()           # Mean
df['col'].median()         # Median
df['col'].std()            # Standard deviation
df['col'].var()            # Variance
df['col'].min()            # Minimum
df['col'].max()            # Maximum
df['col'].quantile(0.25)   # 25th percentile

# Correlation
df.corr()                  # Correlation matrix
df['col1'].corr(df['col2'])  # Pairwise correlation

# Value counts
df['col'].value_counts()   # Unique values and counts
```

## NumPy operations

```python
import numpy as np

# Create arrays
arr = np.array([1, 2, 3])
zeros = np.zeros(10)
ones = np.ones(10)
range_arr = np.arange(0, 10, 2)
linspace = np.linspace(0, 1, 100)

# Operations
arr * 2                    # Element-wise multiply
arr + 10                   # Element-wise add
arr.mean()                 # Mean
arr.std()                  # Std dev
np.percentile(arr, 95)     # 95th percentile

# Matrix operations
np.dot(a, b)               # Dot product
np.matmul(a, b)            # Matrix multiply
arr.T                      # Transpose

# Random
np.random.seed(42)
np.random.randn(100)       # Normal distribution
np.random.randint(0, 10, 5)  # Random integers
```
