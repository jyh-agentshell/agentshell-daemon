# AgentShell Daemon

[![MIT](https://img.shields.io/badge/license-MIT-green)](./LICENSE)

**AgentShell 守护进程** — 运行在用户云端 Linux 服务器上，监控 AI 编码代理（Codex、Claude Code 等）的 tmux 会话状态，通过 HTTPS 上报到 AgentShell 网关。

## 核心功能

### 已实现（Phase 1）

- **多路复用器监控**: 通过 `IMonitorTarget` 接口适配 tmux（screen/zellij/pty 预留接口）
- **双路径状态检测**: ANSI OSC 结构化标记（优先） + 正则回退
- **配置系统**: `agentshell.toml` + Tomlyn 解析；缺失或无效的主机身份配置会拒绝启动
- **零依赖部署**: `dotnet publish --self-contained` 编译为单文件二进制

### 已实现（Phase 2）

- **安全绑定**: Ed25519 密钥对、一次性注册令牌、公钥预登记与挑战-应答验证设备所有权
- **HTTPS 上报**: `HttpApiReporter` 对状态信封签名，服务端按主机身份和协议窗口验证

### 规划中（Phase 4+）

- **局域网直连**: 内嵌 Kestrel HTTP Server + mDNS 广播
- **自更新**: GitHub Releases HTTPS + SHA256 校验 + rename-and-restart

## 安装

```bash
curl -fL https://raw.githubusercontent.com/jyh-agentshell/agentshell-daemon/main/install.sh -o install.sh
less install.sh
sudo bash install.sh
```

安装脚本做三件事：
1. 从 GitHub Releases 下载最新二进制
2. SHA256 校验
3. 写入 systemd service 并启动

脚本仅用于首次安装。检测到既有二进制时会失败关闭，绝不把下载覆盖伪装成更新；后续更新须使用专门的校验、原子替换与重启流程。

## 配置

配置文件: `~/.agentshell/agentshell.toml`

```toml
[monitor]
type = "tmux"
session_pattern = "*"
poll_interval_ms = 500

[reporting]
# 主机唯一 UUID。请使用 --generate-config 生成，勿在多台主机间复用。
host_id = "由 --generate-config 自动生成"
api_base_url = "https://api.agentshell.dev/v1"
report_interval_ms = 1000

[lan]
enabled = true
port = 11920

[binding]
key_path = "~/.agentshell/agent.key"
```

## 构建

```bash
# 本地开发构建
dotnet build src/AgentShell.Daemon/

# 自包含发布（Linux x64）
dotnet publish src/AgentShell.Daemon/ \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  --output ./publish/
```

## 技术栈

- .NET 10 Console + `IHostedService`
- 引用 `AgentShell.Protocol` 协议库（当前本地 ProjectReference，协议包发布后切 NuGet）
- `Microsoft.Extensions.Logging` 结构化日志

## 许可

MIT — 用户服务器安装零顾虑，企业可自由集成。
