---
name: config-update
description: config_update 工具完整参考——所有 action 的参数、用法示例、注意事项
---

# config_update 工具参考

## Action 列表

| action | 必需参数 | 说明 |
|--------|---------|------|
| `list` | 无 | 查看所有已配置的账户和模型 |
| `add` | provider, modelId, apiKey | 新建账户并添加模型 |
| `add_model` | endpointId, modelId | 向已有账户追加模型 |
| `set_default` | endpointId（modelId） | 切换默认模型 |
| `delete` | endpointId（accountId） | 删除账户及其所有模型 |
| `update_model` | endpointId（modelId） | 修改模型参数（可选：displayName, contextWindowSize, maxOutputTokens, capabilities, isReasoning, supportsToolCalling, enabled） |
| `update_account` | endpointId（accountId） | 修改账户参数（可选：displayName, baseUrl, enabled, apiKey） |

## 关键规则

- update 操作只修改传入的非空字段，未传入的保持不变
- API Key 存储在 OS keychain，修改直接更新密钥
- contextWindowSize 支持 `128k`、`1m`、`2m` 格式和纯数字
- capabilities 逗号分隔：`Text,Image,Video,Audio,File`，默认 `Text`
- provider 值：`Anthropic` | `AnthropicCompatible` | `OpenAI` | `OpenAICompatible`

## 示例

```python
# 修改模型 thinking 模式
config_update(action="update_model", endpointId="model-xxx", isReasoning=true)

# 修改上下文窗口
config_update(action="update_model", endpointId="model-xxx", contextWindowSize="1m")

# 轮换 API Key
config_update(action="update_account", endpointId="account-xxx", apiKey="new-key")

# 启用/禁用模型
config_update(action="update_model", endpointId="model-xxx", enabled=false)
```
