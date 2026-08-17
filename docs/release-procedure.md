# Daemon 发布流程

> 本文档描述从代码冻结到 GitHub Release 验证的完整步骤。
> 所有命令均可由普通命令行执行，不依赖特定代理。

## 发布前检查

```bash
# 1. 确认版本号
grep '<Version>' Directory.Build.props

# 2. 确认 SDK 版本
cat global.json

# 3. 本地构建 + 测试
dotnet build src/AgentShell.Daemon/ --configuration Release
dotnet test tests/AgentShell.Daemon.Tests/

# 4. 确认 CI 绿色（main 分支 build job 通过）
gh run list --workflow=build.yml --limit 1
```

### 检查清单

- [ ] `Directory.Build.props` 版本号与目标 tag 一致
- [ ] Protocol 子模块引用指向稳定提交
- [ ] `dotnet build --configuration Release` 通过
- [ ] `dotnet test` 全部通过
- [ ] CI `main` 分支 build job 绿色

## 创建 Release

```bash
# 1. 创建注释 tag
git tag -a v<VERSION> -m "Release daemon <VERSION>"

# 2. 推送 tag
git push origin v<VERSION>
```

CI 自动触发以下 job：

1. **`build`**：restore + build + test
2. **`release`**（tag 触发）：
   - 矩阵构建 `linux-x64` + `linux-arm64`
   - 自包含、单文件、裁剪发布
   - x64 冒烟测试：`--version`、`--generate-config`、`--generate-binding-code`、`bind-verify` 往返
   - 生成 `.sha256` 校验文件
3. **`release-assets`**（tag 触发）：
   - 上传二进制 + SHA256 到 GitHub Release

### 等待 CI 完成

```bash
# 监控 release workflow 状态
gh run list --workflow=build.yml --limit 3
gh run view <RUN_ID> --log-failed
```

## 发布后验证

```bash
# 1. 确认 Release 页面资产齐全
gh release view v<VERSION>

# 2. 下载并校验
cd /tmp
gh release download v<VERSION> --pattern "*.sha256"
gh release download v<VERSION> --pattern "agentshell-daemon-linux-x64"
sha256sum -c agentshell-daemon-linux-x64.sha256

# 3. 功能验证
chmod +x agentshell-daemon-linux-x64
./agentshell-daemon-linux-x64 --version
./agentshell-daemon-linux-x64 --generate-binding-code localhost
```

### 验证清单

- [ ] GitHub Release 包含 `agentshell-daemon-linux-x64`、`agentshell-daemon-linux-arm64` 及对应 `.sha256`
- [ ] SHA256 校验通过
- [ ] `--version` 输出正确版本号
- [ ] `--generate-binding-code` 正常输出绑定 URI

## 版本号策略

- Daemon 使用独立 SemVer，与 Protocol/Server/Android 各自递增
- 路线图版本（如 server-v0.7）是工作计划标识，不直接对应 daemon 代码版本
- 组件 SemVer 是实际产物与兼容性标识
- 当前发布基线：Protocol `0.3.1`，daemon `0.3.1`
