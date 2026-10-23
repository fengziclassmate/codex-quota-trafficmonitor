# Codex Quota for TrafficMonitor

在 Windows 任务栏显示 ChatGPT 订阅中的 Codex 剩余额度、重置倒计时和任务状态。鼠标悬停可查看重置次数、重置权益到期时间、数据更新时间与下次查询时间。

![真实任务运行中的任务栏显示](docs/live-running.png)

本项目是独立的 TrafficMonitor 插件，不是 OpenAI 官方产品。

## 下载与安装

从 [Releases](https://github.com/fengziclassmate/codex-quota-trafficmonitor/releases/latest) 下载：

- `CodexQuota-TrafficMonitor-x64.zip`：包含 TrafficMonitor 1.86 x64 Lite、插件及源码，解压后运行 `TrafficMonitor.exe`。
- `CodexQuota-plugin-x64.zip`：已有 TrafficMonitor 的用户使用；关闭宿主，将其中 `plugins` 目录内三个文件复制到宿主的 `plugins` 目录，再在任务栏“显示设置”中勾选 Codex 项目。如需状态监听，也将两个 `.cmd` 文件复制到 `TrafficMonitor.exe` 同目录，从该目录启用监听。

运行环境：Windows x64、x64 TrafficMonitor、.NET Framework 4.6 或更新版本，以及已通过 ChatGPT 账户登录的 Codex CLI 或 Codex Desktop。本次实测环境为 Windows build 26300、TrafficMonitor 1.86、Codex CLI 0.163.0-alpha.11.2；其他系统版本的任务栏兼容性由 TrafficMonitor 决定。

首次启动默认显示三个项目：剩余额度、任务状态、重置倒计时。单击项目打开详情，鼠标悬停查看完整信息，插件右键菜单可手动刷新额度。

## 额度配置

插件通过本机 `codex app-server` 的 `account/rateLimits/read` 查询额度，默认间隔 120 秒。查询不创建模型任务。

它首先寻找 Codex Desktop 的本地 CLI，然后检查 PATH。如果未找到，在 `plugins/CodexQuota.ini` 中填写实际路径：

```ini
[plugin]
codex_path=C:\path\to\codex.exe
```

周期长度使用账户返回值，不固定成“5 小时额度 + 每周额度”。返回两个周期时依次显示两个剩余比例。倒计时到零后显示“待同步”，等待服务器确认，不自行将额度恢复为 100%。

重置权益按服务器的 `availableCount` 显示可用次数；返回明细时显示各项权益的到期时间。接口未提供明细或时间时明确标记未知。插件只展示权益，不兑换或消耗重置次数。

## 启用状态监听

1. 将插件放在固定位置，再运行 `Enable-Status.cmd`。
2. 安装器合并用户级 `hooks.json`，保留其他 Hooks，并在覆盖前生成备份。默认位置为 `%USERPROFILE%\.codex\hooks.json`；设置了 `CODEX_HOME` 时使用该目录。
3. 打开 Codex CLI，输入 `/hooks`，审阅并信任指向本插件 `CodexQuotaBridge.exe` 的 8 个监听项。
4. 重新打开需要监测的聊天，或启动新任务，让新配置生效。插件随宿主的监测周期更新，便携包默认约 1 秒。

安装器不会写入或伪造 Codex 信任记录。更改监听定义或移动插件路径后，需要重新审阅。可运行 `Disable-Status.cmd` 移除当前路径对应的监听，其他 Hooks 保留。

| 状态 | 含义 |
| --- | --- |
| 思考中 | 模型处理阶段的概括：有用户请求，当前没有已观测到的本地工具在运行。 |
| 运行中 | 本地工具开始后尚未返回；并行调用分别记录。 |
| 等待输入 | 发出了可观测到的用户输入请求。异步提问在下次用户事件或任务结束时清除。 |
| 批准/执行 | 批准请求已发出，工具尚未返回。Hooks 缺少独立的“批准后开始执行”事件，因此合并显示。 |
| 已完成 | 收到 Stop 事件，短暂显示约 60 秒。 |
| 已中断 | 收到明确的 Interrupt 事件。 |
| 空闲 | 监听连接存在，当前无进行中的任务。 |
| 未连接 | 尚无有效事件，或对应监测进程已退出。 |

同时有多个主聊天时，优先显示等待交互，其次运行、思考，详情提供各状态数量。子代理的独立轮次不与主聊天混合统计。托管工具及部分专用路径不发出本地工具 Hooks，因此不能用 Hooks 精确区分其执行阶段。

## 监测安装前已启动的聊天

本次真实测试中，已运行的桌面聊天没有加载新 Hooks。为这种情况提供只读兼容模式：

```powershell
.\plugins\CodexQuotaBridge.exe watch 'C:\path\to\rollout.jsonl' 12345
```

将路径替换为当前聊天的实际 JSONL 会话事件文件，将 `12345` 替换为对应 Codex 进程的 PID。该模式只读取事件，不发送提示词、不重新执行任务，也不修改会话。观察器持续跟踪同一聊天，单次任务完成后继续等待新任务；对应 Codex 进程退出后结束。发现同一会话的 Hooks 状态记录时，会让 Hooks 接管。

兼容模式已使用当前真实任务验证“思考中”和“运行中”的切换：

![真实任务思考中的任务栏显示](docs/live-thinking.png)

此模式依赖当前验证过的本地 JSONL 格式，Codex 升级后需要重新核对；它不能区分批准等待与工具执行，也不保证识别所有中断路径。正常使用优先重新打开聊天并使用官方 Hooks。

## 数据与隐私

- 本地缓存保存在 `%LOCALAPPDATA%\CodexQuota`。
- 监听只持久化会话/轮次标识、工具名称、状态、时间、进程标识和状态来源。
- 不保存提示词、工具参数、工具输出或推理正文。兼容模式在内存中解析会话记录，只保留状态元数据。
- 插件不读取、上传或展示登录令牌，不建立遥测服务器。额度请求由已有 Codex 登录完成。
- `*` 表示查询失败或数据超过 5 分钟未更新，详情显示具体原因。

## 构建与验证

源码在 `src`。构建需要 Zig 0.15.2 和 Windows 自带的 .NET Framework C# 编译器：

```powershell
.\src\build.ps1 -ZigPath 'C:\path\to\zig.exe'
```

先退出 TrafficMonitor。脚本运行逻辑检查，再生成 Microsoft C++ ABI 的 x64 DLL 和 x64 .NET 查询/监听程序，输出到 `plugins`。中间产物位于 `src/work/build`，不会进入版本控制。

已完成 20 项额度、权益和事件解析检查，以及安装配置保留/重复执行/停用检查、连续缓存刷新、真实账户查询、TrafficMonitor 实际加载验证。当前任务的兼容监测已取得真实“思考中/运行中”截图。尚未结束的同一任务，其完成状态应在任务结束后由真实完成事件更新。

## 常见问题

- **额度显示 ERR**：确认 CLI 使用 ChatGPT 登录，网络可用，必要时设置 `codex_path`。若通过 API Key 使用 Codex，本插件不展示 API 余额。
- **状态未连接**：检查 `/hooks` 是否已信任、聊天是否已重新打开；已有任务可按需使用兼容模式。
- **状态停在批准/执行**：这是等待批准与批准后执行的合并阶段，工具返回后清除。
- **没有显示任务栏数字**：确认宿主和插件同为 x64，在 TrafficMonitor 的显示设置中勾选项目，并检查宿主与当前 Windows 版本的兼容性。
- **迁移目录**：先从旧目录停用监听，移动整个文件夹，再从新目录启用并重新信任。

## 来源与许可

TrafficMonitor 与 `PluginInterface.h` 原作者为 Zhong Yang，使用 [Anti 996 License](LICENSE)。本项目保留原版权声明和许可；便携包中的宿主为上游发布的原版。

- [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)
- [TrafficMonitor 插件开发文档](https://github.com/zhongyang219/TrafficMonitor/wiki/Plugin-Development-Guide)
- [OpenAI：账户额度接口](https://learn.chatgpt.com/docs/app-server)
- [OpenAI：Hooks、事件覆盖和信任机制](https://learn.chatgpt.com/docs/hooks)
