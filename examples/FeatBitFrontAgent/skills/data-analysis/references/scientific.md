# Scientific Data Analysis & Statistical Testing

## Hypothesis testing

```python
from scipy import stats
import numpy as np

# One-sample t-test
# Test if sample mean differs from population mean
t_stat, p_value = stats.ttest_1samp(sample, population_mean)

# Two-sample t-test (independent)
# Test if two groups have different means
t_stat, p_value = stats.ttest_ind(group_a, group_b)
t_stat, p_value = stats.ttest_ind(group_a, group_b, equal_var=False)  # Welch's t-test

# Paired t-test
# Test if paired samples have different means
t_stat, p_value = stats.ttest_rel(before, after)

# One-way ANOVA
# Test if 3+ groups have different means
f_stat, p_value = stats.f_oneway(group1, group2, group3)

# Chi-square test of independence
# Test if two categorical variables are related
contingency_table = pd.crosstab(df['var1'], df['var2'])
chi2, p_value, dof, expected = stats.chi2_contingency(contingency_table)

# Kolmogorov-Smirnov test
# Test if sample follows a distribution
statistic, p_value = stats.kstest(sample, 'norm')
```

## Correlation analysis

```python
# Pearson correlation (linear)
corr, p_value = stats.pearsonr(x, y)

# Spearman correlation (monotonic, non-parametric)
corr, p_value = stats.spearmanr(x, y)

# Kendall's tau (ordinal data)
corr, p_value = stats.kendalltau(x, y)

# Correlation matrix with p-values
def correlation_with_pvalues(df):
    corr_matrix = df.corr()
    pval_matrix = df.corr(method=lambda x, y: stats.pearsonr(x, y)[1])
    return corr_matrix, pval_matrix
```

## Regression analysis

```python
from sklearn.linear_model import LinearRegression
from sklearn.preprocessing import PolynomialFeatures
import statsmodels.api as sm

# Simple linear regression
X = df[['feature']].values
y = df['target'].values

model = LinearRegression()
model.fit(X, y)
y_pred = model.predict(X)

# Get coefficients and intercept
slope = model.coef_[0]
intercept = model.intercept_
r_squared = model.score(X, y)

# Multiple regression
X = df[['feature1', 'feature2', 'feature3']]
model.fit(X, y)

# With statistical summary (using statsmodels)
X_with_const = sm.add_constant(X)
model_sm = sm.OLS(y, X_with_const).fit()
print(model_sm.summary())

# Polynomial regression
poly = PolynomialFeatures(degree=2)
X_poly = poly.fit_transform(X)
model_poly = LinearRegression()
model_poly.fit(X_poly, y)

# Logistic regression
from sklearn.linear_model import LogisticRegression
logistic_model = LogisticRegression()
logistic_model.fit(X, y_binary)
y_pred_prob = logistic_model.predict_proba(X)[:, 1]
```

## Time series analysis

```python
from statsmodels.tsa.stattools import adfuller
from statsmodels.tsa.arima.model import ARIMA

# Augmented Dickey-Fuller test (stationarity)
result = adfuller(df['value'])
print('ADF Statistic:', result[0])
print('p-value:', result[1])
print('Is stationary:', result[1] < 0.05)

# ARIMA model
model = ARIMA(df['value'], order=(1, 1, 1))
fitted = model.fit()
forecast = fitted.forecast(steps=10)

# Exponential smoothing
from statsmodels.tsa.holtwinters import ExponentialSmoothing
model = ExponentialSmoothing(df['value'], trend='add', seasonal='add', seasonal_periods=12)
fitted = model.fit()
forecast = fitted.forecast(steps=10)
```

## Distribution analysis

```python
# Test for normality
# Shapiro-Wilk test (small samples)
statistic, p_value = stats.shapiro(sample)

# Kolmogorov-Smirnov test
statistic, p_value = stats.kstest(sample, 'norm')

# Anderson-Darling test
result = stats.anderson(sample, dist='norm')

# Q-Q plot
from scipy.stats import probplot
import matplotlib.pyplot as plt
probplot(sample, dist='norm', plot=plt)
plt.savefig('qq_plot.png')

# Find distribution parameters
# Normal distribution
mu, sigma = stats.norm.fit(sample)

# Exponential distribution
loc, scale = stats.expon.fit(sample)

# Beta distribution
a, b, loc, scale = stats.beta.fit(sample)
```

## Non-parametric tests

```python
# Mann-Whitney U test (alternative to independent t-test)
statistic, p_value = stats.mannwhitneyu(group_a, group_b)

# Wilcoxon signed-rank test (alternative to paired t-test)
statistic, p_value = stats.wilcoxon(before, after)

# Kruskal-Wallis test (alternative to ANOVA)
statistic, p_value = stats.kruskal(group1, group2, group3)

# Friedman test (repeated measures ANOVA)
statistic, p_value = stats.friedmanchisquare(group1, group2, group3)
```

## Effect size

```python
# Cohen's d (for t-tests)
def cohens_d(group1, group2):
    n1, n2 = len(group1), len(group2)
    var1, var2 = group1.var(), group2.var()
    pooled_var = ((n1-1)*var1 + (n2-1)*var2) / (n1+n2-2)
    return (group1.mean() - group2.mean()) / np.sqrt(pooled_var)

# Pearson's r (correlation effect size)
# r = 0.1 (small), 0.3 (medium), 0.5 (large)

# R-squared (variance explained)
r_squared = model.score(X, y)

# Eta-squared (ANOVA effect size)
def eta_squared(f_stat, df_between, df_within):
    return (f_stat * df_between) / (f_stat * df_between + df_within)
```

## Power analysis

```python
from statsmodels.stats.power import ttest_ind

# Calculate sample size needed for t-test
# Effect size (Cohen's d), alpha, power, ratio
effect_size = 0.5
alpha = 0.05
power = 0.8
ratio = 1  # Equal group sizes

required_n = ttest_ind.solve_power(
    effect_size=effect_size,
    alpha=alpha,
    power=power,
    ratio=ratio
)

# Calculate achieved power
achieved_power = ttest_ind.power(
    effect_size=effect_size,
    nobs1=100,
    alpha=alpha,
    ratio=ratio
)
```

## Multiple testing correction

```python
from statsmodels.stats.multitest import multipletests

# Bonferroni correction
rejected, p_adjusted, _, _ = multipletests(p_values, alpha=0.05, method='bonferroni')

# Benjamini-Hochberg FDR correction
rejected, p_adjusted, _, _ = multipletests(p_values, alpha=0.05, method='fdr_bh')

# Holm-Bonferroni
rejected, p_adjusted, _, _ = multipletests(p_values, alpha=0.05, method='holm')
```
