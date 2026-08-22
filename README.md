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
api_base_url = "https://agentshell.servicelab.cn/api"
report_interval_ms = 1000
full_sync_interval_seconds = 30

[lan]
enabled = true
port = 11920

[binding]
key_path = "~/.agentshell/agent.key"
```

## 首次绑定与运行

首次启动时，daemon 会在 `~/.agentshell/agent.key` 生成 Ed25519 原始私钥（权限应为 `0600`）。不要使用 OpenSSL PEM 文件覆盖它；两种格式不兼容。

设备注册需要由 Android 客户端证明同一 SSH 主机的控制权。开始前，确保 Android 使用的是实际网关域名，并确保远程登录用户的 `PATH` 中可找到 `agentshell-daemon`。在 daemon 主机上生成一次性绑定码：

```bash
agentshell-daemon --generate-binding-code <SSH 主机名或地址>
```

将输出的 `agentshell://bind?...` 完整粘贴到 Android 的“绑定设备”页面。客户端会通过 HTTPS 获取一次性注册令牌，再经 SSH 依次调用 `register-key`、`bind-verify` 和 `--set-token`；私钥和访问令牌均不应离开主机。绑定成功后，daemon 将在下一轮自动扫描并上报完整初始状态。

未绑定或令牌不可用时，daemon 只等待绑定，不扫描 tmux 会话、不发送状态。这是正常的安全保护，不是服务故障。可使用以下命令检查：

```bash
sudo systemctl status agentshell-daemon --no-pager
sudo journalctl -u agentshell-daemon -n 50 --no-pager
```

### systemd 部署约束

守护进程应以目标 SSH 用户而非 root 运行，配置目录只授予该用户写入权限。可以启用 `NoNewPrivileges`、`ProtectSystem=strict`、受限地址族等 systemd 加固项；但**不能设置 `PrivateTmp=true`**，因为 daemon 需要访问该用户 tmux 的 `/tmp/tmux-<uid>` socket。

若二进制放在版本化发布目录，须为 SSH 客户端命令提供稳定路径：管理员安装优先创建 `/usr/local/bin/agentshell-daemon`；无 root 权限时可创建 `~/.local/bin/agentshell-daemon`。Android 绑定命令先查找 `PATH`，再回退到该用户级路径。

升级时先验证新二进制及 SHA256，再原子切换链接并重启服务。发布 CI 必须用裁剪后的 x64 单文件执行“生成绑定码 → 消费并验签”冒烟，防止反射 JSON 序列化在裁剪后失效。不得使用 `curl | bash` 方式更新。

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
