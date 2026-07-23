# Changelog

## 1.1.0

- 普通构建只生成带 `-unsigned` 后缀的开发产物；正式发布必须提供当前用户证书库中的代码签名证书和 RFC 3161 时间戳服务。
- 正式发布使用 SignTool 进行 SHA-256 Authenticode 签名，并在生成标准文件名和哈希前验证信任链、时间戳和签名证书指纹。
- 当前用户安装注册到 Windows“已安装的应用”，重复安装会原位更新同一注册项。
- 新增标准卸载窗口；默认保留 DPAPI Key、设置和备份，彻底清理需再次确认且不触及 Claude CLI 或共享 Claude 配置。
- 卸载清理在受限临时目录中等待主程序退出，使用绝对字面路径清理自有文件；失败时保留注册项和错误日志以便重试。
- 新增发布契约、安装幂等、卸载边界、PATH 精确移除和伪 Claude CLI 无付费集成检查。

## 1.0.1

- 会员通道固定为启动默认值，API 按量通道在 GUI 和完整终端入口统一二次确认，且不再跨启动记忆。
- 完整终端改为直接启动 Claude 子进程，避免含 Key 的 PowerShell 父进程在 Claude 退出后继续存活。
- 保存 Key 与配置修复解耦；修复流程不再删除用户级环境变量。
- 权限模式与 Claude CLI 当前契约对齐，移除不受支持的 `manual` 和第三方 Kimi 不安全的 `auto`。
- `kclaude api` 命令入口也要求按量计费二次确认；Claude CLI 安装优先使用 WinGet 官方包，无 WinGet 时仅打开官方说明，不再一键执行远程 PowerShell 脚本。
- 取消 CLI 升级或回滚时终止对应进程树，并在更新期间禁用并发操作。
- 单文件布局检测兼容 .NET single-file publish；构建脚本可在 Windows 应用控制拦截测试宿主时安全回退到单文件测试。
- 新增计费、权限、终端凭据边界、环境变量保留、取消清理和单文件布局回归测试。

## 1.0.0

- Kimi Code 会员与 Kimi API 按量双通道。
- WPF 流式 Claude Code GUI 和完整交互终端入口。
- DPAPI Key 录入、单独更新与 ACL 收紧。
- 当前用户安装、开始菜单和 `kclaude` 命令。
- Anthropic 官方版本检查、manifest 与 GitHub Release 文档同步。
- 手动 Claude Code 升级、SHA-256 备份和可逆回滚。
- GitHub Actions 构建与无外部测试框架的 smoke tests。
