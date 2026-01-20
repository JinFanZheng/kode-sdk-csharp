# Financial Analysis Recipes

## Return calculations

```python
import pandas as pd
import numpy as np

# Simple return
df['return'] = df['price'].pct_change()

# Log return
df['log_return'] = np.log(df['price'] / df['price'].shift(1))

# Cumulative return
df['cum_return'] = (1 + df['return']).cumprod()

# Total return
total_return = (df['price'].iloc[-1] / df['price'].iloc[0]) - 1
```

## Risk metrics

```python
# Volatility (annualized)
volatility_daily = df['return'].std()
volatility_annual = volatility_daily * np.sqrt(252)

# Sharpe ratio (assuming risk-free rate = 0)
sharpe = df['return'].mean() / df['return'].std() * np.sqrt(252)

# Sortino ratio (downside deviation)
downside = df['return'][df['return'] < 0]
sortino = df['return'].mean() / downside.std() * np.sqrt(252)

# Maximum drawdown
cummax = df['price'].cummax()
drawdown = (df['price'] - cummax) / cummax
max_drawdown = drawdown.min()

# Value at Risk (VaR) at 95% confidence
var_95 = df['return'].quantile(0.05)

# Conditional VaR (expected shortfall)
cvar_95 = df['return'][df['return'] <= var_95].mean()
```

## Technical indicators

```python
# Moving averages
df['ma_5'] = df['price'].rolling(5).mean()
df['ma_20'] = df['price'].rolling(20).mean()
df['ma_50'] = df['price'].rolling(50).mean()

# Exponential moving average
df['ema_12'] = df['price'].ewm(span=12).mean()
df['ema_26'] = df['price'].ewm(span=26).mean()

# MACD
df['macd'] = df['ema_12'] - df['ema_26']
df['macd_signal'] = df['macd'].ewm(span=9).mean()

# RSI (Relative Strength Index)
def calculate_rsi(prices, period=14):
    delta = prices.diff()
    gain = (delta.where(delta > 0, 0)).rolling(period).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(period).mean()
    rs = gain / loss
    rsi = 100 - (100 / (1 + rs))
    return rsi

df['rsi'] = calculate_rsi(df['price'])

# Bollinger Bands
df['bb_middle'] = df['price'].rolling(20).mean()
df['bb_std'] = df['price'].rolling(20).std()
df['bb_upper'] = df['bb_middle'] + 2 * df['bb_std']
df['bb_lower'] = df['bb_middle'] - 2 * df['bb_std']
```

## Portfolio analysis

```python
# Portfolio return
weights = np.array([0.4, 0.3, 0.3])
returns = df[['asset1', 'asset2', 'asset3']].pct_change()
portfolio_return = returns.dot(weights)

# Portfolio volatility
cov_matrix = returns.cov()
portfolio_variance = weights.T @ cov_matrix @ weights
portfolio_volatility = np.sqrt(portfolio_variance) * np.sqrt(252)

# Efficient frontier (using scipy)
from scipy.optimize import minimize

def portfolio_performance(weights, returns):
    port_return = returns.mean().dot(weights) * 252
    port_vol = np.sqrt(weights.T @ returns.cov() @ weights) * np.sqrt(252)
    return port_return, port_vol

def minimize_volatility(weights, returns):
    return portfolio_performance(weights, returns)[1]

constraints = ({'type': 'eq', 'fun': lambda w: np.sum(w) - 1})
bounds = tuple((0, 1) for _ in range(len(weights)))
result = minimize(minimize_volatility, weights, args=(returns,),
                  method='SLSQP', bounds=bounds, constraints=constraints)
```

## Backtesting

```python
# Simple moving average crossover
df['signal'] = np.where(df['ma_5'] > df['ma_20'], 1, -1)
df['strategy_return'] = df['signal'].shift(1) * df['return']

# Strategy performance
strategy_total_return = (1 + df['strategy_return']).prod() - 1
buy_and_hold_return = (1 + df['return']).prod() - 1

# Sharpe ratio comparison
strategy_sharpe = df['strategy_return'].mean() / df['strategy_return'].std() * np.sqrt(252)
buy_hold_sharpe = df['return'].mean() / df['return'].std() * np.sqrt(252)
```
