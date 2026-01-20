# Data Analysis Templates

## Template 1: General data analysis script

```python
#!/usr/bin/env python3
"""
General data analysis workflow
"""

import pandas as pd
import numpy as np
import json
from datetime import datetime

def load_data(filepath):
    """Load data from file"""
    if filepath.endswith('.csv'):
        return pd.read_csv(filepath)
    elif filepath.endswith('.json'):
        return pd.read_json(filepath)
    else:
        raise ValueError(f"Unsupported file type: {filepath}")

def analyze_data(df):
    """Perform analysis"""
    # Basic info
    info = {
        'shape': df.shape,
        'columns': list(df.columns),
        'dtypes': df.dtypes.astype(str).to_dict(),
        'missing': df.isnull().sum().to_dict(),
        'memory_usage': df.memory_usage(deep=True).sum()
    }

    # Descriptive statistics
    numeric_cols = df.select_dtypes(include=[np.number]).columns
    stats = df[numeric_cols].describe().to_dict()

    # Correlations
    if len(numeric_cols) > 1:
        correlations = df[numeric_cols].corr().to_dict()
    else:
        correlations = {}

    return {
        'info': info,
        'statistics': stats,
        'correlations': correlations
    }

def save_results(results, output_file):
    """Save analysis results"""
    with open(output_file, 'w') as f:
        json.dump(results, f, indent=2, default=str)

def main():
    input_file = 'data.csv'
    output_file = f'analysis_{datetime.now().strftime("%Y%m%d_%H%M%S")}.json'

    df = load_data(input_file)
    results = analyze_data(df)
    save_results(results, output_file)

    print(f"Analysis complete. Results saved to {output_file}")

if __name__ == "__main__":
    main()
```

## Template 2: Financial analysis script

```python
#!/usr/bin/env python3
"""
Financial data analysis
"""

import pandas as pd
import numpy as np

def calculate_returns(prices):
    """Calculate returns"""
    df = pd.DataFrame({'price': prices})
    df['return'] = df['price'].pct_change()
    df['log_return'] = np.log(df['price'] / df['price'].shift(1))
    df['cum_return'] = (1 + df['return']).cumprod()
    return df

def calculate_risk_metrics(returns):
    """Calculate risk metrics"""
    metrics = {
        'volatility_daily': returns.std(),
        'volatility_annual': returns.std() * np.sqrt(252),
        'sharpe_ratio': returns.mean() / returns.std() * np.sqrt(252),
        'max_drawdown': calculate_max_drawdown(returns),
        'var_95': returns.quantile(0.05),
        'cvar_95': returns[returns <= returns.quantile(0.05)].mean()
    }
    return metrics

def calculate_max_drawdown(returns):
    """Calculate maximum drawdown"""
    cum_returns = (1 + returns).cumprod()
    cummax = cum_returns.cummax()
    drawdown = (cum_returns - cummax) / cummax
    return drawdown.min()

def calculate_technical_indicators(prices, window=20):
    """Calculate technical indicators"""
    df = pd.DataFrame({'price': prices})
    df['sma'] = df['price'].rolling(window).mean()
    df['std'] = df['price'].rolling(window).std()
    df['upper_band'] = df['sma'] + 2 * df['std']
    df['lower_band'] = df['sma'] - 2 * df['std']
    return df

def main():
    # Load price data
    prices = pd.read_csv('prices.csv')['close']

    # Calculate returns
    df = calculate_returns(prices)

    # Risk metrics
    risk = calculate_risk_metrics(df['return'].dropna())

    # Technical indicators
    indicators = calculate_technical_indicators(prices)

    print("Risk Metrics:")
    for k, v in risk.items():
        print(f"  {k}: {v:.4f}")

if __name__ == "__main__":
    main()
```

## Template 3: Statistical testing script

```python
#!/usr/bin/env python3
"""
Statistical hypothesis testing
"""

import pandas as pd
from scipy import stats
import json

def test_group_differences(group_a, group_b, test='t-test'):
    """Test if two groups are different"""
    results = {}

    if test == 't-test':
        statistic, p_value = stats.ttest_ind(group_a, group_b)
        results['test'] = 'Independent t-test'
    elif test == 'mann-whitney':
        statistic, p_value = stats.mannwhitneyu(group_a, group_b)
        results['test'] = 'Mann-Whitney U test'
    else:
        raise ValueError(f"Unknown test: {test}")

    results['statistic'] = statistic
    results['p_value'] = p_value
    results['significant'] = p_value < 0.05

    # Effect size (Cohen's d)
    pooled_std = np.sqrt((group_a.std()**2 + group_b.std()**2) / 2)
    cohens_d = (group_a.mean() - group_b.mean()) / pooled_std
    results['cohens_d'] = cohens_d

    return results

def test_correlation(x, y):
    """Test correlation between two variables"""
    corr, p_value = stats.pearsonr(x, y)

    return {
        'correlation': corr,
        'p_value': p_value,
        'significant': p_value < 0.05
    }

def main():
    df = pd.read_csv('data.csv')

    # Example: Test difference between groups
    group_a = df[df['group'] == 'A']['value']
    group_b = df[df['group'] == 'B']['value']

    results = test_group_differences(group_a, group_b)

    print(f"{results['test']}:")
    print(f"  Statistic: {results['statistic']:.4f}")
    print(f"  P-value: {results['p_value']:.4f}")
    print(f"  Significant: {results['significant']}")
    print(f"  Cohen's d: {results['cohens_d']:.4f}")

if __name__ == "__main__":
    main()
```

## Template 4: Time series analysis

```python
#!/usr/bin/env python3
"""
Time series analysis
"""

import pandas as pd
import numpy as np
from statsmodels.tsa.stattools import adfuller
from statsmodels.tsa.arima.model import ARIMA

def test_stationarity(series):
    """Test if time series is stationary"""
    result = adfuller(series.dropna())

    return {
        'adf_statistic': result[0],
        'p_value': result[1],
        'is_stationary': result[1] < 0.05,
        'critical_values': result[4]
    }

def fit_arima(series, order=(1, 1, 1)):
    """Fit ARIMA model"""
    model = ARIMA(series, order=order)
    fitted = model.fit()

    return {
        'model': fitted,
        'aic': fitted.aic,
        'bic': fitted.bic,
        'summary': fitted.summary()
    }

def forecast_arima(model, steps=10):
    """Generate forecast"""
    forecast = model.forecast(steps=steps)

    # Calculate confidence intervals
    forecast_result = model.get_forecast(steps=steps)
    conf_int = forecast_result.conf_int()

    return {
        'forecast': forecast,
        'lower_bound': conf_int.iloc[:, 0],
        'upper_bound': conf_int.iloc[:, 1]
    }

def main():
    df = pd.read_csv('timeseries.csv', parse_dates=['date'], index_col='date')
    series = df['value']

    # Test stationarity
    stationarity = test_stationarity(series)

    # Fit model
    model_result = fit_arima(series)

    # Forecast
    forecast = forecast_arima(model_result['model'], steps=10)

    print(f"Stationary: {stationarity['is_stationary']}")
    print(f"AIC: {model_result['aic']:.2f}")
    print(f"Forecast: {forecast['forecast'].values}")

if __name__ == "__main__":
    main()
```

## Template 5: Data cleaning script

```python
#!/usr/bin/env python3
"""
Data cleaning utilities
"""

import pandas as pd
import numpy as np

def clean_data(df):
    """Clean data"""
    # Remove duplicates
    df = df.drop_duplicates()

    # Handle missing values
    for col in df.columns:
        if df[col].dtype in ['int64', 'float64']:
            df[col].fillna(df[col].median(), inplace=True)
        else:
            df[col].fillna(df[col].mode()[0], inplace=True)

    # Remove outliers (IQR method)
    numeric_cols = df.select_dtypes(include=[np.number]).columns
    for col in numeric_cols:
        Q1 = df[col].quantile(0.25)
        Q3 = df[col].quantile(0.75)
        IQR = Q3 - Q1
        df = df[(df[col] >= Q1 - 1.5*IQR) & (df[col] <= Q3 + 1.5*IQR)]

    # Standardize column names
    df.columns = df.columns.str.lower().str.replace(' ', '_')

    return df

def validate_data(df, rules):
    """Validate data against rules"""
    issues = []

    for rule in rules:
        if rule['type'] == 'range':
            col = rule['column']
            if not df[col].between(rule['min'], rule['max']).all():
                issues.append(f"{col} has values outside range [{rule['min']}, {rule['max']}]")

        elif rule['type'] == 'not_null':
            if df[rule['column']].isnull().any():
                issues.append(f"{rule['column']} has null values")

    return issues

def main():
    df = pd.read_csv('raw_data.csv')

    # Clean
    df_clean = clean_data(df)

    # Validate
    rules = [
        {'type': 'range', 'column': 'age', 'min': 0, 'max': 120},
        {'type': 'not_null', 'column': 'email'}
    ]
    issues = validate_data(df_clean, rules)

    if issues:
        print("Validation issues:")
        for issue in issues:
            print(f"  - {issue}")
    else:
        print("Data validated successfully")

    df_clean.to_csv('clean_data.csv', index=False)

if __name__ == "__main__":
    main()
```
