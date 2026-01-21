# NuGet 发布指南

本指南介绍如何将 Kode Agent SDK 发布到 **NuGet.org** 和 **GitHub Packages**。

> **💡 提示**: 项目会自动发布到两个平台，用户可以从任意平台安装。详见 [GitHub Packages 使用指南](.github/GITHUB_PACKAGES_GUIDE.md)。

## 📦 包列表

本项目包含 6 个 NuGet 包，按依赖顺序如下：

1. **Kode.Agent.SourceGenerator** - Roslyn 源代码生成器（独立）
2. **Kode.Agent.Sdk** - 核心 SDK（依赖 SourceGenerator）
3. **Kode.Agent.Tools.Builtin** - 内置工具集（依赖 Sdk）
4. **Kode.Agent.Store.Json** - JSON 文件存储（依赖 Sdk）
5. **Kode.Agent.Store.Redis** - Redis 分布式存储（依赖 Sdk）
6. **Kode.Agent.Mcp** - MCP 协议集成（依赖 Sdk）

## 🚀 发布方式

### 方式一：自动发布（推荐）

#### 前置准备

1. **NuGet.org 配置**
   - 在 [nuget.org](https://www.nuget.org/) 创建账号
   - 生成 API Key（需要 `Push new packages` 权限）

2. **GitHub Secrets 配置**
   - 进入仓库 `Settings` → `Secrets and variables` → `Actions`
   - 点击 `New repository secret`
   - 添加 Secret：
     - 名称：`NUGET_API_KEY`
     - 值：你的 NuGet.org API Key

> **📝 注意**: GitHub Packages 发布会自动使用 `GITHUB_TOKEN`，无需额外配置。

#### 发布步骤

```bash
# 1. 提交所有更改
git add .
git commit -m "chore: prepare for NuGet release v0.1.0"
git push origin main

# 2. 创建版本标签（自动触发发布）
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions 会自动：
- ✅ 构建所有项目
- ✅ 运行测试
- ✅ 打包 NuGet 包
- ✅ 按正确顺序发布到 **NuGet.org**
- ✅ 按正确顺序发布到 **GitHub Packages**
- ✅ 创建 GitHub Release（包含所有 .nupkg 文件）

#### 查看发布进度

访问 [Actions](https://github.com/JinFanZheng/kode-sdk-csharp/actions) 页面查看工作流运行状态。

---

### 方式二：手动发布

#### 1. 本地构建和打包

```bash
cd /path/to/kode-sdk-csharp

# 清理并构建
dotnet clean
dotnet restore
dotnet build --configuration Release

# 运行测试
dotnet test --configuration Release --no-build

# 打包
dotnet pack --configuration Release --no-build --output ./nupkgs
```

#### 2. 验证包内容

```bash
# 列出生成的包
ls -lh ./nupkgs/

# 检查包内容（可选）
unzip -l ./nupkgs/Kode.Agent.Sdk.0.1.0.nupkg
```

预期输出：
```
-rw-r--r--  237K  Kode.Agent.Sdk.0.1.0.nupkg
-rw-r--r--  171K  Kode.Agent.Sdk.0.1.0.snupkg
-rw-r--r--   20K  Kode.Agent.SourceGenerator.0.1.0.nupkg
-rw-r--r--   46K  Kode.Agent.Tools.Builtin.0.1.0.nupkg
-rw-r--r--   36K  Kode.Agent.Tools.Builtin.0.1.0.snupkg
-rw-r--r--   31K  Kode.Agent.Store.Json.0.1.0.nupkg
-rw-r--r--   18K  Kode.Agent.Store.Json.0.1.0.snupkg
-rw-r--r--   34K  Kode.Agent.Store.Redis.0.1.0.nupkg
-rw-r--r--   19K  Kode.Agent.Store.Redis.0.1.0.snupkg
-rw-r--r--   23K  Kode.Agent.Mcp.0.1.0.nupkg
-rw-r--r--   17K  Kode.Agent.Mcp.0.1.0.snupkg
```

#### 3. 发布到 NuGet.org

**重要：必须按依赖顺序发布！**

```bash
# 设置 API Key（或直接在命令中使用）
export NUGET_API_KEY="your-api-key-here"

# 1. 首先发布 SourceGenerator
dotnet nuget push ./nupkgs/Kode.Agent.SourceGenerator.0.1.0.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

# 等待 1-2 分钟，确保包已被索引
echo "Waiting for SourceGenerator to be indexed..."
sleep 90

# 2. 发布核心 Sdk
dotnet nuget push ./nupkgs/Kode.Agent.Sdk.0.1.0.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

# 等待 2-3 分钟，确保 Sdk 已被索引
echo "Waiting for Sdk to be indexed..."
sleep 120

# 3. 批量发布其他包
dotnet nuget push ./nupkgs/Kode.Agent.Tools.Builtin.0.1.0.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

dotnet nuget push ./nupkgs/Kode.Agent.Store.Json.0.1.0.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

dotnet nuget push ./nupkgs/Kode.Agent.Store.Redis.0.1.0.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

dotnet nuget push ./nupkgs/Kode.Agent.Mcp.0.1.0.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

echo "All packages published successfully!"
```

---

## 🔄 版本更新

### 更新版本号

编辑 `Directory.Build.props`：

```xml
<PropertyGroup>
  <Version>0.2.0</Version>
  <AssemblyVersion>0.2.0.0</AssemblyVersion>
  <FileVersion>0.2.0.0</FileVersion>
  <PackageReleaseNotes>Release notes for v0.2.0...</PackageReleaseNotes>
</PropertyGroup>
```

### 语义化版本规范

遵循 [SemVer 2.0](https://semver.org/) 规范：

- **主版本号（Major）**：不兼容的 API 变更
  - 例：`1.0.0` → `2.0.0`
- **次版本号（Minor）**：向后兼容的功能新增
  - 例：`1.0.0` → `1.1.0`
- **修订号（Patch）**：向后兼容的问题修复
  - 例：`1.0.0` → `1.0.1`
- **预发布版本**：开发阶段使用
  - 例：`1.0.0-alpha.1`、`1.0.0-beta.2`、`1.0.0-rc.1`

---

## ✅ 发布检查清单

### 发布前

- [ ] 所有测试通过 (`dotnet test`)
- [ ] 代码审查完成
- [ ] 更新 CHANGELOG.md
- [ ] 更新版本号
- [ ] 更新 README.md（如有 API 变更）
- [ ] 确认没有提交敏感信息（API Keys、密码等）

### 发布后

- [ ] 在 [nuget.org](https://www.nuget.org/packages?q=Kode.Agent) 确认所有包已发布
- [ ] 测试从 NuGet 安装包
- [ ] 创建 GitHub Release（如使用手动发布）
- [ ] 更新文档网站（如有）
- [ ] 发布社区公告

---

## 🛠️ 故障排查

### 问题 1: 包依赖解析失败

**症状**：发布后，用户无法安装包，提示找不到依赖。

**原因**：依赖包尚未被 NuGet.org 索引。

**解决方案**：
1. 严格按照依赖顺序发布
2. 在发布依赖包后等待足够时间（2-5 分钟）
3. 访问 NuGet.org 确认包已上线后再发布下一个

### 问题 2: GitHub Actions 构建失败

**症状**：GitHub Actions 显示 "Process completed with exit code 1"。

**原因**：.NET 10.0 是预览版本，需要特殊配置。

**解决方案**：已在工作流中添加 `dotnet-quality: 'preview'` 配置。

### 问题 3: 符号包上传失败

**症状**：`.snupkg` 文件上传失败。

**解决方案**：
```bash
# 单独上传符号包
dotnet nuget push ./nupkgs/Kode.Agent.Sdk.0.1.0.snupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json
```

### 问题 4: API Key 权限不足

**症状**：上传时提示 403 Forbidden。

**解决方案**：
1. 确认 API Key 有效且未过期
2. 确认 API Key 有 "Push new packages" 权限
3. 如果是首次发布，确认包名未被占用

---

## 📚 相关资源

- [NuGet 官方文档](https://docs.microsoft.com/en-us/nuget/)
- [创建 NuGet 包](https://docs.microsoft.com/en-us/nuget/create-packages/creating-a-package-msbuild)
- [发布 NuGet 包](https://docs.microsoft.com/en-us/nuget/nuget-org/publish-a-package)
- [语义化版本规范](https://semver.org/)
- [GitHub Actions 文档](https://docs.github.com/en/actions)

---

## 💡 最佳实践

1. **始终在本地测试打包**
   ```bash
   dotnet pack --configuration Release
   # 解压检查包内容
   unzip -l ./nupkgs/*.nupkg
   ```

2. **使用 CI/CD 自动化发布**
   - 避免手动错误
   - 保证发布流程一致性
   - 自动生成 Release Notes

3. **保持版本号一致性**
   - 所有包使用相同版本号
   - 在 `Directory.Build.props` 统一管理

4. **包含完整的文档**
   - README.md
   - 代码示例
   - API 文档链接

5. **提供符号包**
   - 便于调试
   - 提升开发体验

---

## 🔐 安全提示

- **永远不要**在代码中硬编码 API Key
- **永远不要**将 API Key 提交到版本控制
- **定期轮换** NuGet API Key
- **使用最小权限**原则配置 API Key
- **启用双因素认证**保护 NuGet.org 账号
