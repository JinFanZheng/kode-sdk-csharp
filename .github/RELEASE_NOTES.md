# Release Notes

## 如何创建新版本发布

### 1. 更新版本号

编辑 `Directory.Build.props`：

```xml
<PropertyGroup>
  <Version>0.2.0</Version>
  <AssemblyVersion>0.2.0.0</AssemblyVersion>
  <FileVersion>0.2.0.0</FileVersion>
  <PackageReleaseNotes>版本 0.2.0 更新说明...</PackageReleaseNotes>
</PropertyGroup>
```

### 2. 提交更改

```bash
git add Directory.Build.props
git commit -m "chore: bump version to 0.2.0"
git push origin main
```

### 3. 创建并推送标签

```bash
git tag v0.2.0
git push origin v0.2.0
```

这会自动触发：
- ✅ 构建和测试
- ✅ 打包 NuGet 包
- ✅ 发布到 NuGet.org
- ✅ 创建 GitHub Release

---

## 如果发布失败怎么办

### 情况 1: Release 已存在错误

如果看到 "Release already exists" 或 "Too many retries"：

```bash
# 删除远程 tag
git push --delete origin v0.2.0

# 删除本地 tag
git tag -d v0.2.0

# 在 GitHub 上手动删除 Release（如果已创建）
# 前往: https://github.com/JinFanZheng/kode-sdk-csharp/releases

# 重新创建 tag
git tag v0.2.0
git push origin v0.2.0
```

### 情况 2: NuGet 发布失败

如果 NuGet.org 发布失败：

1. 检查 NUGET_API_KEY 是否有效
2. 检查包版本是否已存在于 NuGet.org
3. 手动发布包（参考 PUBLISH_GUIDE.md）

### 情况 3: 测试失败

如果测试失败导致发布中止：

```bash
# 修复问题后
git add .
git commit -m "fix: resolve test issues"
git push origin main

# 删除并重新创建 tag
git push --delete origin v0.2.0
git tag -d v0.2.0
git tag v0.2.0
git push origin v0.2.0
```

---

## 版本号规范

遵循语义化版本 (SemVer 2.0):

- **主版本号 (Major)**: 不兼容的 API 变更
  - 例: `1.0.0` → `2.0.0`
  
- **次版本号 (Minor)**: 向后兼容的功能新增
  - 例: `1.0.0` → `1.1.0`
  
- **修订号 (Patch)**: 向后兼容的问题修复
  - 例: `1.0.0` → `1.0.1`

- **预发布版本**: 开发测试使用
  - 例: `1.0.0-alpha.1`, `1.0.0-beta.2`, `1.0.0-rc.1`

---

## 检查清单

发布前确认：

- [ ] 所有测试通过
- [ ] 代码审查完成
- [ ] 更新 CHANGELOG.md（如有）
- [ ] 版本号符合语义化规范
- [ ] Release Notes 已准备
- [ ] README 文档已更新（如有 API 变更）

发布后验证：

- [ ] GitHub Release 已创建
- [ ] NuGet.org 上所有 6 个包已发布
- [ ] 包版本号正确
- [ ] 从 NuGet 安装测试包可用
- [ ] CI/CD 徽章显示绿色通过

---

## 回滚版本

如果发现严重问题需要回滚：

### 从 NuGet.org 下架包

**警告**: NuGet.org 不允许删除包，只能"下架"(unlist)

```bash
# 下架所有 0.2.0 版本的包
dotnet nuget delete Kode.Agent.Sdk 0.2.0 --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json --non-interactive

# 对所有 6 个包重复此操作
```

### 发布修复版本

```bash
# 更新版本为 0.2.1（修复版本）
# 编辑 Directory.Build.props
git add .
git commit -m "fix: critical bug fix for v0.2.0"
git tag v0.2.1
git push origin v0.2.1
```

---

## 常见问题

### Q: 可以重新推送同一个 tag 吗？

A: 技术上可以，但**强烈不建议**。应该使用新的版本号。

如果必须重新推送：
```bash
git tag -d v0.2.0
git push --delete origin v0.2.0
# 修复后
git tag v0.2.0 -f
git push origin v0.2.0 -f
```

### Q: 如何创建预发布版本？

A: 使用预发布后缀：

```xml
<Version>0.2.0-beta.1</Version>
```

```bash
git tag v0.2.0-beta.1
git push origin v0.2.0-beta.1
```

### Q: 发布需要多长时间？

A: 
- GitHub Actions 构建: ~5 分钟
- NuGet.org 索引: 2-10 分钟
- 总计: 约 15 分钟

### Q: 如何只发布特定的包？

A: 自动发布会发布所有包。如需单独发布，使用手动方式（见 PUBLISH_GUIDE.md）。

---

## 参考资源

- [NuGet 文档](https://docs.microsoft.com/en-us/nuget/)
- [语义化版本规范](https://semver.org/lang/zh-CN/)
- [GitHub Releases 文档](https://docs.github.com/en/repositories/releasing-projects-on-github)
- [PUBLISH_GUIDE.md](../PUBLISH_GUIDE.md) - 完整发布指南
