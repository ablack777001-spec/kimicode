# KClaude Desktop

KClaude Desktop 是一个面向 Windows 的 Claude Code 图形界面，专门支持 Kimi K3 双通道：

- 默认使用 Kimi Code 会员额度；
- 只有用户手动选择并再次确认时才使用 Kimi API 开放平台按量计费；
- GUI 内直接发送 Claude Code 任务并显示流式输出；
- 支持项目目录、会话继续、新会话、权限模式和完整交互式终端；
- 在界面内安全录入或更换两把 Key；
- 单文件 EXE 可便携运行，也能安装到当前 Windows 用户；
- 支持手动检查、升级和回滚 Claude Code CLI；
- 项目可直接放入 GitHub，使用 Actions 或本机生成发布 EXE。

## 安全原则

1. Key 使用 Windows DPAPI 加密，绑定当前 Windows 用户和本机。
2. Key 不进入命令行、日志、项目、Git、`.claude.json` 或 `settings.json`。
3. GUI 提示词通过 Claude CLI 标准输入发送，不进入进程命令行。
4. 会员通道只设置 `ANTHROPIC_API_KEY`；按量通道只设置 `ANTHROPIC_AUTH_TOKEN`。
5. 默认通道永久锁定为会员，应用不会因额度不足而自动切换按量 API。
6. GUI 不提供 `--dangerously-skip-permissions` 开关。
7. 发布包和源码不包含真实 Key；复制到新电脑后必须重新录入。

## 使用发布版 EXE

### 便携运行

直接运行 `KClaudeDesktop.exe`。应用首次启动会检查：

- Claude Code CLI 是否存在；
- 两把 DPAPI Key 是否已录入；
- 第三方模型配置是否初始化；
- Git Bash 和冲突环境变量状态。

### 安装到另一台电脑

1. 复制 `KClaudeDesktop-Setup.exe` 到目标 Windows x64 电脑并运行。
2. 在“电脑安装”页点击“安装到当前 Windows 用户”。
3. 如未安装 Claude Code，点击“安装官方 Claude Code CLI”。该按钮只会在用户确认后运行 Anthropic 官方安装器。
4. 在“Key 与通道”页录入目标电脑自己的两把 Key。
5. 运行诊断，选择可信任的项目目录后开始使用。

安装不需要管理员权限。程序安装到：

```text
%LOCALAPPDATA%\Programs\KClaude Desktop\
```

并创建开始菜单入口及 `%USERPROFILE%\bin\kclaude.cmd`。

## GUI 与完整终端

GUI 使用 Claude Code 的 `--print --output-format stream-json` 接口，实现流式输出和会话恢复。默认权限模式为 `plan`。需要修改项目时，可手动切换为 `acceptEdits` 或 `auto`。

非交互 print 模式无法呈现所有终端式权限询问。需要完整 slash command、逐项权限确认或终端交互时，点击“打开完整交互终端”。

## 版本检查、升级文档与回滚

版本检查是只读操作，不会升级：

1. 从 Anthropic 官方 `https://downloads.claude.ai/claude-code-releases/latest` 获取最新版本号；
2. 获取该版本的官方 `manifest.json`、commit 和构建时间；
3. 如存在，同步 `https://github.com/anthropics/claude-code/releases/tag/v<版本>` 的官方 Release 文档；
4. 文档仅按纯文本展示，不执行其中任何内容。

默认每天首次启动只检查一次。发现新版本时显示徽标，但绝不自动升级。

手动升级流程：

1. 用户点击确认；
2. 备份当前 `claude.exe`；
3. 记录版本、时间和 SHA-256；
4. 执行官方 `claude update`；
5. 重新验证版本及 GUI 所需参数。

回滚时先验证备份 SHA-256，再备份当前版本并原子恢复所选版本。备份不会自动删除：

```text
%APPDATA%\KClaudeDesktop\cli-backups\
```

## 从 GitHub clone 构建

要求：Windows x64、Git、.NET 10 SDK。

```powershell
git clone https://github.com/ablack777001-spec/kimicode.git
cd kimicode
.\build.ps1
```

同时测试在线版本与官方升级文档：

```powershell
.\build.ps1 -OnlineChecks
```

输出位于 `artifacts\`：

- `KClaudeDesktop.exe`
- `KClaudeDesktop-Setup.exe`
- `SHA256SUMS.txt`

两个 EXE 是同一份自包含单文件程序；Setup 文件名会让应用首次打开时直接引导安装。

## 开发验证

```powershell
dotnet build KClaudeDesktop.slnx -c Debug
dotnet run --project tests\KClaudeDesktop.SmokeTests\KClaudeDesktop.SmokeTests.csproj
```

测试使用虚拟 Key，不调用模型，不产生会员或 API 费用。

## 已知边界

- 当前发布目标为 Windows x64。
- DPAPI 密文不能跨电脑或跨 Windows 用户迁移。
- GUI 依赖 Claude Code 的 `stream-json`、session 和 permission-mode 参数；每次更新后会重新检查这些能力。
- 应用自身的新版由本仓库构建和 GitHub Actions 发布；没有配置仓库地址前，不会自行下载未知来源的应用更新。
