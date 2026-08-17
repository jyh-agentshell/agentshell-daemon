# 生产维护窗口操作手册

> 本文档描述在用户主机上部署、回滚 daemon 的完整步骤。
> daemon 以普通用户 systemd 服务运行，所有操作不需要 root 权限（除非安装时使用了 sudo）。

## 前置准备

- [ ] 目标版本 GitHub Release 已发布（参见 `docs/release-procedure.md`）
- [ ] SHA256 校验文件已下载并验证
- [ ] 确认当前 daemon 版本：`agentshell-daemon --version`
- [ ] 确认回滚版本二进制已保留（`~/.agentshell/agentshell-daemon.bak`）
- [ ] 如有活跃 Agent 会话，通知用户维护窗口时间

## 部署步骤

```bash
# ─── 1. 下载新二进制 + 校验文件 ───
cd /tmp
RELEASE_URL="https://github.com/<OWNER>/agentshell-daemon/releases/download/v<VERSION>"
curl -fLO "${RELEASE_URL}/agentshell-daemon-linux-x64"
curl -fLO "${RELEASE_URL}/agentshell-daemon-linux-x64.sha256"

# ─── 2. 校验 SHA256 ───
sha256sum -c agentshell-daemon-linux-x64.sha256
# 预期输出：agentshell-daemon-linux-x64: OK

# ─── 3. 停止服务 ───
systemctl --user stop agentshell-daemon
# 如果安装时使用了 sudo：sudo systemctl stop agentshell-daemon

# ─── 4. 备份当前二进制 ───
INSTALL_DIR="${HOME}/.agentshell"
cp "${INSTALL_DIR}/agentshell-daemon" "${INSTALL_DIR}/agentshell-daemon.bak"

# ─── 5. 替换二进制 ───
mv /tmp/agentshell-daemon-linux-x64 "${INSTALL_DIR}/agentshell-daemon"
chmod +x "${INSTALL_DIR}/agentshell-daemon"

# ─── 6. 启动服务 ───
systemctl --user start agentshell-daemon

# ─── 7. 验证 ───
systemctl --user status agentshell-daemon
agentshell-daemon --version
journalctl --user -u agentshell-daemon -n 20 --no-pager
```

### 部署验证清单

- [ ] SHA256 校验通过
- [ ] `systemctl status` 显示 `active (running)`
- [ ] `--version` 输出新版本号
- [ ] 日志无异常（无 Ed25519 错误、无认证失败、无 tmux socket 错误）
- [ ] daemon 正常上报（检查 Server 端 `/diagnostics/notifications` 或会话列表）

## 回滚步骤

```bash
# ─── 1. 停止服务 ───
systemctl --user stop agentshell-daemon

# ─── 2. 恢复备份 ───
INSTALL_DIR="${HOME}/.agentshell"
mv "${INSTALL_DIR}/agentshell-daemon.bak" "${INSTALL_DIR}/agentshell-daemon"
chmod +x "${INSTALL_DIR}/agentshell-daemon"

# ─── 3. 启动 ───
systemctl --user start agentshell-daemon

# ─── 4. 验证 ───
systemctl --user status agentshell-daemon
agentshell-daemon --version
journalctl --user -u agentshell-daemon -n 20 --no-pager
```

### 回滚验证清单

- [ ] `systemctl status` 显示 `active (running)`
- [ ] `--version` 输出回滚版本号
- [ ] 日志无异常
- [ ] daemon 正常上报

## systemd 约束

daemon 的 systemd unit 由 `install.sh` 生成，关键配置：

| 配置项 | 值 | 原因 |
|--------|-----|------|
| `User` | 目标 SSH 用户 | 非 root 运行 |
| `PrivateTmp` | `no` | 需要访问 `/tmp/tmux-<uid>` socket |
| `ProtectHome` | `read-only` | 只读 home，除 `ReadWritePaths` |
| `ReadWritePaths` | `~/.agentshell` | 配置、密钥、token 存储 |
| `Restart` | `always` | 崩溃自动重启 |
| `RestartSec` | `5` | 5 秒后重启 |
| `NoNewPrivileges` | `yes` | 安全加固 |

维护窗口中替换二进制时**无需修改 systemd unit 文件**，只需确保：
- 二进制文件名不变（`agentshell-daemon`）
- 文件权限保持 `+x`
- 替换完成后 `systemctl restart` 即可

## 注意事项

- 维护窗口建议在**无活跃 Agent 会话**时执行，避免中断正在运行的审批流程
- 备份二进制（`.bak`）在确认新版本稳定后可手动清理：`rm ~/.agentshell/agentshell-daemon.bak`
- 如果 daemon 未绑定（`~/.agentshell/binding_code` 不存在），启动后只等待绑定，不扫描 tmux 也不上报
- 裁剪后的单文件二进制首次启动可能较慢（需要解压到临时目录），后续启动使用缓存
- ARM64 主机使用 `agentshell-daemon-linux-arm64` 替代 x64 版本
