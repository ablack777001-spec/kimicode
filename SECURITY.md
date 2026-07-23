# Security

## 凭据

- 不要把真实 Key 提交到 issue、日志、截图或 Git。
- Key 只保存在 `%USERPROFILE%\.claude-kimi-switch\secrets\*.dpapi`。
- DPAPI 密文与当前 Windows 用户及本机绑定。
- Key 只在启动 Claude 子进程时注入该进程环境，不进入命令行；完整终端不保留含 Key 的中间 PowerShell 进程。
- 保存 Key 不会触发配置修复；配置修复保留用户级环境变量，仅清理应用管理的 Claude 配置冲突项。
- 发布前运行 smoke tests 和明文扫描。

## 更新信任边界

- Claude Code 版本元数据只来自 `downloads.claude.ai`。
- 升级文档只来自 `github.com/anthropics/claude-code` 官方仓库。
- `claude update` 仅在用户点击确认后执行。
- 升级与回滚前保留版本、SHA-256 和二进制备份。
- Release 文本属于不可信展示数据，只显示，不执行。

## 发布签名

- 普通 CI 只上传文件名和 Artifact 名均含 `UNSIGNED` 的开发产物。
- 标准发布名称只能由 `build.ps1 -Release` 生成；缺少可信代码签名证书、RFC 3161 时间戳、SignTool 或验签失败时立即停止。
- 私钥和证书密码不得提交到仓库、写入参数、日志或构建产物；当前实现只从 `CurrentUser\My` 按指纹选择带私钥的证书。
- SHA-256 哈希在签名及复制完成后生成，防止哈希对应未签名前的二进制。

## 卸载边界

- 系统卸载默认保留 DPAPI Key、设置、日志和 CLI 备份。
- 彻底清理必须在卸载窗口再次确认，且只允许删除应用验证过的 `%APPDATA%\KClaudeDesktop` 与 `%USERPROFILE%\.claude-kimi-switch`。
- 卸载不删除 Claude Code CLI、`.claude.json`、`.claude/settings.json` 或其他共享 Claude 数据。

## 报告问题

报告安全问题时不要附带真实 Key、DPAPI 文件或含隐私的 Claude 会话。
