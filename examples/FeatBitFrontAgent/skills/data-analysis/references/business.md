# Business Analytics Recipes

## Trend analysis

```python
import pandas as pd

# Growth rates (period-over-period)
df['pct_change'] = df['revenue'].pct_change()
df['abs_change'] = df['revenue'].diff()

# Year-over-Year (assuming datetime index)
df['yoy_revenue'] = df['revenue'] / df['revenue'].shift(12) - 1

# Month-over-Month
df['mom_revenue'] = df['revenue'] / df['revenue'].shift(1) - 1

# Moving average trend
df['ma_trend_7'] = df['revenue'].rolling(7).mean()
df['ma_trend_30'] = df['revenue'].rolling(30).mean()

# Compound Annual Growth Rate (CAGR)
def cagr(start_value, end_value, periods):
    return (end_value / start_value) ** (1 / periods) - 1

cagr_3y = cagr(df['revenue'].iloc[0], df['revenue'].iloc[-1], 3)
```

## Cohort analysis

```python
# Create cohort groups (e.g., by signup month)
df['cohort'] = df['signup_date'].dt.to_period('M')
df['period'] = (df['activity_date'] - df['signup_date']).dt.days // 30

# Cohort retention table
cohort_data = df.groupby(['cohort', 'period']).size().unstack()

# Normalize to see retention rates
cohort_size = cohort_data.iloc[:, 0]
retention = cohort_data.divide(cohort_size, axis=0) * 100

# Output
print(retention.round(2))
```

## Funnel analysis

```python
# Define funnel steps
funnel_steps = ['page_view', 'add_to_cart', 'checkout', 'purchase']

# Count users at each step
funnel_counts = {step: df[df[step] == True]['user_id'].nunique()
                 for step in funnel_steps}

# Calculate conversion rates
funnel_df = pd.DataFrame(list(funnel_counts.items()),
                         columns=['step', 'users'])
funnel_df['conversion_rate'] = (funnel_df['users'] /
                                funnel_df['users'].iloc[0] * 100)
funnel_df['step_rate'] = funnel_df['users'].pct_change() * 100

# Drop NaN for first step
funnel_df['step_rate'] = funnel_df['step_rate'].fillna(100)
```

## User behavior analysis

```python
# Active users (DAU, WAU, MAU)
dau = df.groupby('date')['user_id'].nunique().mean()
wau = df.resample('W', on='date')['user_id'].nunique().mean()
mau = df.resample('M', on='date')['user_id'].nunique().mean()

# Stickiness ratio
stickiness = dau / mau

# User segmentation by activity
user_activity = df.groupby('user_id').agg({
    'actions': 'sum',
    'session_duration': 'mean',
    'last_active': 'max'
})

# Segment users
def segment_user(row):
    if row['actions'] > 100:
        return 'power'
    elif row['actions'] > 20:
        return 'active'
    else:
        return 'casual'

user_activity['segment'] = user_activity.apply(segment_user, axis=1)
```

## A/B testing

```python
from scipy import stats

# Group A and Group B data
group_a = df[df['variant'] == 'A']['conversion']
group_b = df[df['variant'] == 'B']['conversion']

# Conversion rates
rate_a = group_a.mean()
rate_b = group_b.mean()

# Statistical significance (t-test)
t_stat, p_value = stats.ttest_ind(
    df[df['variant'] == 'A']['conversion'],
    df[df['variant'] == 'B']['conversion']
)

# Chi-square test for proportions
contingency_table = pd.crosstab(df['variant'], df['conversion'])
chi2, p_value, dof, expected = stats.chi2_contingency(contingency_table)

# Confidence interval for difference
def ci_difference(p1, p2, n1, n2, confidence=0.95):
    se = np.sqrt(p1*(1-p1)/n1 + p2*(1-p2)/n2)
    z = stats.norm.ppf(1 - (1-confidence)/2)
    diff = p1 - p2
    return (diff - z*se, diff + z*se)
```

## RFM analysis (Recency, Frequency, Monetary)

```python
# Calculate RFM metrics
analysis_date = df['date'].max()

rfm = df.groupby('user_id').agg({
    'date': lambda x: (analysis_date - x.max()).days,  # Recency
    'order_id': 'count',                                # Frequency
    'amount': 'sum'                                     # Monetary
}).rename(columns={
    'date': 'recency',
    'order_id': 'frequency',
    'amount': 'monetary'
})

# Score each metric (1-5, higher is better)
rfm['R_score'] = pd.qcut(rfm['recency'], 5, labels=[5,4,3,2,1])
rfm['F_score'] = pd.qcut(rfm['frequency'].rank(method='first'), 5, labels=[1,2,3,4,5])
rfm['M_score'] = pd.qcut(rfm['monetary'].rank(method='first'), 5, labels=[1,2,3,4,5])

# Combine scores
rfm['RFM_score'] = rfm['R_score'].astype(str) + rfm['F_score'].astype(str) + rfm['M_score'].astype(str)

# Segment customers
def segment_rfm(row):
    if row['R_score'] >= 4 and row['F_score'] >= 4:
        return 'champions'
    elif row['R_score'] >= 3 and row['F_score'] >= 3:
        return 'loyal'
    elif row['R_score'] >= 3 and row['F_score'] <= 2:
        return 'at_risk'
    else:
        return 'lost'

rfm['segment'] = rfm.apply(segment_rfm, axis=1)
```

## Churn analysis

```python
# Define churn (e.g., no activity in last 30 days)
churn_threshold = 30
df['is_churned'] = (df['last_activity'].max() - df['last_activity']).dt.days > churn_threshold

# Churn rate
churn_rate = df['is_churned'].mean()

# Churn by segment
churn_by_segment = df.groupby('segment')['is_churned'].mean()

# Predict churn indicators
from sklearn.ensemble import RandomForestClassifier
from sklearn.model_selection import train_test_split

features = ['activity_count', 'session_duration', 'days_since_last', 'support_tickets']
X = df[features]
y = df['is_churned']

X_train, X_test, y_train, y_test = train_test_split(X, y, test_size=0.2)

model = RandomForestClassifier()
model.fit(X_train, y_train)

# Feature importance
importance = pd.DataFrame({
    'feature': features,
    'importance': model.feature_importances_
}).sort_values('importance', ascending=False)
```
