# GitHub Packages 使用指南

本项目同时发布到 **NuGet.org** 和 **GitHub Packages**，为用户提供多种安装方式。

## 📦 包源选择

| 包源 | URL | 优势 | 适用场景 |
|------|-----|------|---------|
| **NuGet.org** | https://www.nuget.org/packages?q=Kode.Agent | ✅ 公开访问<br>✅ 无需认证<br>✅ CDN 加速 | **推荐**用于生产环境 |
| **GitHub Packages** | https://github.com/JinFanZheng?tab=packages | ✅ 与代码仓库集成<br>✅ 版本管理统一<br>✅ 企业私有部署 | 内部开发、预发布版本 |

---

## 🚀 从 GitHub Packages 安装

### 方式一：使用命令行配置（推荐）

**步骤 1**: 创建 Personal Access Token

1. 访问 https://github.com/settings/tokens
2. 点击 `Generate new token` → `Generate new token (classic)`
3. 勾选权限：
   - ✅ `read:packages` - 安装包
   - ✅ `write:packages` - 发布包（如需）
4. 生成并复制 Token

**步骤 2**: 配置 GitHub 包源

```bash
# 添加 GitHub Packages 源
dotnet nuget add source \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_TOKEN \
  --store-password-in-clear-text \
  --name github \
  "https://nuget.pkg.github.com/JinFanZheng/index.json"

# 验证配置
dotnet nuget list source
```

**步骤 3**: 安装包

```bash
# 从 GitHub Packages 安装
dotnet add package Kode.Agent.Sdk --source github

# 或在 .csproj 中添加
<PackageReference Include="Kode.Agent.Sdk" Version="0.1.0" />
```

---

### 方式二：使用 nuget.config 文件

**步骤 1**: 复制配置模板

```bash
cp nuget.config.github.example nuget.config
```

**步骤 2**: 编辑 `nuget.config`

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github" value="https://nuget.pkg.github.com/JinFanZheng/index.json" />
  </packageSources>
  
  <packageSourceCredentials>
    <github>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="YOUR_GITHUB_TOKEN" />
    </github>
  </packageSourceCredentials>
</configuration>
```

**⚠️ 安全提示**: 不要将包含 Token 的 `nuget.config` 提交到版本控制！

**步骤 3**: 安装包

```bash
dotnet restore
```

---

### 方式三：在 GitHub Actions 中使用

GitHub Actions 自动可以访问 GitHub Packages（无需配置 Token）：

```yaml
- name: Restore dependencies
  run: |
    dotnet nuget add source \
      --username ${{ github.actor }} \
      --password ${{ secrets.GITHUB_TOKEN }} \
      --store-password-in-clear-text \
      --name github \
      "https://nuget.pkg.github.com/JinFanZheng/index.json"
    
    dotnet restore
```

---

## 🔒 安全最佳实践

### 1. 使用环境变量存储 Token

**Windows (PowerShell)**:
```powershell
$env:GITHUB_TOKEN = "your_token_here"

dotnet nuget add source `
  --username YOUR_USERNAME `
  --password $env:GITHUB_TOKEN `
  --store-password-in-clear-text `
  --name github `
  "https://nuget.pkg.github.com/JinFanZheng/index.json"
```

**Linux/macOS**:
```bash
export GITHUB_TOKEN="your_token_here"

dotnet nuget add source \
  --username YOUR_USERNAME \
  --password "$GITHUB_TOKEN" \
  --store-password-in-clear-text \
  --name github \
  "https://nuget.pkg.github.com/JinFanZheng/index.json"
```

### 2. 使用 .gitignore 排除敏感文件

```gitignore
# NuGet 配置文件（包含 Token）
nuget.config
NuGet.Config

# 但保留示例文件
!nuget.config.example
!nuget.config.github.example
```

### 3. 使用 dotnet user-secrets (ASP.NET Core)

```bash
# 初始化 user secrets
dotnet user-secrets init

# 存储 GitHub Token
dotnet user-secrets set "GitHub:Token" "your_token_here"
```

---

## 📋 可用的包

以下包已发布到 GitHub Packages：

| 包名 | 描述 | 安装命令 |
|------|------|---------|
| `Kode.Agent.Sdk` | 核心 SDK | `dotnet add package Kode.Agent.Sdk --source github` |
| `Kode.Agent.SourceGenerator` | 源代码生成器 | `dotnet add package Kode.Agent.SourceGenerator --source github` |
| `Kode.Agent.Tools.Builtin` | 内置工具集 | `dotnet add package Kode.Agent.Tools.Builtin --source github` |
| `Kode.Agent.Store.Json` | JSON 存储 | `dotnet add package Kode.Agent.Store.Json --source github` |
| `Kode.Agent.Store.Redis` | Redis 存储 | `dotnet add package Kode.Agent.Store.Redis --source github` |
| `Kode.Agent.Mcp` | MCP 集成 | `dotnet add package Kode.Agent.Mcp --source github` |

---

## 🔍 查看已发布的包

访问以下 URL 查看所有已发布的包：
- **GitHub Packages**: https://github.com/JinFanZheng?tab=packages
- **NuGet.org**: https://www.nuget.org/profiles/JinFanZheng

---

## ❓ 常见问题

### Q1: 为什么需要 Personal Access Token？

A: GitHub Packages 需要身份验证才能安装包（即使是公开包）。Token 用于验证你的身份。

---

### Q2: Token 权限应该选择哪些？

A: 
- **仅安装包**: `read:packages`
- **发布包**: `read:packages` + `write:packages`
- **删除包**: `read:packages` + `write:packages` + `delete:packages`

---

### Q3: 如何在 CI/CD 中使用？

A: 在 GitHub Actions 中使用 `${{ secrets.GITHUB_TOKEN }}`，它会自动获得所需权限。

在其他 CI 系统中（如 Azure DevOps、GitLab CI），需要：
1. 创建 Personal Access Token
2. 将 Token 存储为 CI 系统的 Secret
3. 在构建脚本中引用 Secret

---

### Q4: 可以同时使用 NuGet.org 和 GitHub Packages 吗？

A: 可以！使用 `packageSourceMapping` 指定不同包的来源：

```xml
<packageSourceMapping>
  <packageSource key="github">
    <package pattern="Kode.Agent.*" />
  </packageSource>
  <packageSource key="nuget.org">
    <package pattern="*" />
  </packageSource>
</packageSourceMapping>
```

---

### Q5: GitHub Packages 和 NuGet.org 的包有区别吗？

A: 没有！两个源的包内容完全相同，只是托管位置不同。

---

### Q6: 如何删除 GitHub Packages 源？

```bash
dotnet nuget remove source github
```

---

## 🛠️ 故障排查

### 问题 1: 401 Unauthorized

**原因**: Token 无效或权限不足

**解决方案**:
1. 检查 Token 是否过期
2. 确认 Token 有 `read:packages` 权限
3. 重新生成 Token

---

### 问题 2: 404 Not Found

**原因**: 包名或版本号错误，或包未发布

**解决方案**:
1. 访问 https://github.com/JinFanZheng?tab=packages 确认包已发布
2. 检查包名拼写
3. 确认版本号正确

---

### 问题 3: NU1301 错误

**原因**: 包源配置冲突

**解决方案**:
```bash
# 清除缓存
dotnet nuget locals all --clear

# 重新配置源
dotnet nuget remove source github
dotnet nuget add source --name github "https://nuget.pkg.github.com/JinFanZheng/index.json"
```

---

## 📚 参考文档

- [GitHub Packages 官方文档](https://docs.github.com/zh/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [NuGet 配置文档](https://docs.microsoft.com/zh-cn/nuget/reference/nuget-config-file)
- [Personal Access Token 管理](https://docs.github.com/zh/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)

---

## 💡 推荐方案

根据不同场景，我们推荐：

| 场景 | 推荐包源 | 原因 |
|------|---------|------|
| **生产环境** | NuGet.org | 无需认证，稳定性高 |
| **企业内部** | GitHub Packages | 统一管理，访问控制 |
| **开发测试** | 两者都可 | 根据网络情况选择 |
| **CI/CD** | GitHub Packages | 与 GitHub Actions 集成 |

---

如有问题，请在 [GitHub Issues](https://github.com/JinFanZheng/kode-sdk-csharp/issues) 提问。
