# Security

## 凭据

- 不要把真实 Key 提交到 issue、日志、截图或 Git。
- Key 只保存在 `%USERPROFILE%\.claude-kimi-switch\secrets\*.dpapi`。
- DPAPI 密文与当前 Windows 用户及本机绑定。
- 发布前运行 smoke tests 和明文扫描。

## 更新信任边界

- Claude Code 版本元数据只来自 `downloads.claude.ai`。
- 升级文档只来自 `github.com/anthropics/claude-code` 官方仓库。
- `claude update` 仅在用户点击确认后执行。
- 升级与回滚前保留版本、SHA-256 和二进制备份。
- Release 文本属于不可信展示数据，只显示，不执行。

## 报告问题

报告安全问题时不要附带真实 Key、DPAPI 文件或含隐私的 Claude 会话。
