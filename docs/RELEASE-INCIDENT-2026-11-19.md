# Release 工作流失败分析 — 2026-11-19

## 1. 失败概况

- 工作流：`Release` #14（run id `44564568275`），`workflow_dispatch`，提交 `ea4935e`（版本 1.0.3.6）。
- 时间：2026-11-19 16:18–16:24 UTC（北京时间 2026-11-20 00:18–00:24）。
- 结论：`APPX / Check` 失败；`Installer / Check`、`Installer / x64`、`Installer / arm64` 成功；
  `APPX / Package` 与 `Publish` 被跳过，因此**没有生成或发布 1.0.3.6 Release**。

## 2. 失败点

`APPX / Check` 的 `Run unit and conformance tests` 步骤退出码 1。日志中的唯一失败用例：

```
SurgeLink.Agent.Tests.AgentExperienceTests
    .OversizedDirectoryReadReturnsAnActionableTransportError [FAIL]
System.IO.EndOfStreamException: Unable to read beyond the end of the stream.
  at SurgeLink.Agent.AgentWire.ReadAsync[T](Stream stream, CancellationToken ct)
     in src/SurgeLink.Agent/AgentWire.cs:line 29
  at SurgeLink.Agent.AgentClient.SendAsync(...) in AgentClient.cs:line 16
```

其余测试程序集全部通过（该次运行 Agent 122/123，其余 Core、SSH、Terminal、Conformance、Telnet、Serial 均通过）。
同一提交的 `Code checks` #33 和 `Installer / Check` 都通过，说明这是并发/时序型问题，不是编译或功能回归。

## 3. 根因

`AgentPipeServer.ServeAsync` 在写响应前创建了一个 **2 秒**的 reply 截止时间：

```csharp
using var reply = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
reply.CancelAfter(TimeSpan.FromSeconds(2));
try { await AgentWire.WriteAsync(pipe, response, reply.Token); }
catch (AgentFrameTooLargeException) when (pipe.IsConnected && !reply.IsCancellationRequested) { ... }
```

`AgentWire.WriteAsync` 先做 `JsonSerializer.SerializeToUtf8Bytes`，再做 1 MiB 帧大小检查。该用例构造的
`files.list` 响应约 5.5 MB（500 条 × 900 个 CJK 字符的 Name/FullPath），在负载较高的 windows-2025 runner 上
序列化本身可能接近或超过 2 秒。序列化完成后才抛出 `AgentFrameTooLargeException`，此时 `reply` 已被取消：

- `catch` 的 `!reply.IsCancellationRequested` 过滤条件不再成立，原始的 `output_limit` 错误响应没有写出；
- 或错误响应刚写完 4 字节帧头就被取消，客户端在读取帧体时收到 EOF。

所以测试期望的 `output_limit` 变成 `EndOfStreamException`。这是一处真实的服务器端 bug：**在慢机器上，
超大结果可能返回不完整帧而不是有界的 output_limit 错误**；测试只是把它暴露出来。

`Code checks` #32（1.0.3.2）另有一次 Agent 测试因 `dotnet` 冷启动超过 15 秒超时而失败：
`McpAdapterTests.StdioHostDiscoversToolsAndUsesSameDesktopGrantAsCli`，属于独立的测试超时问题。

另外，Release #9（2026-10-24）的 `APPX / Check` 曾因
`PerformanceTests.OneMegabyteOfMixedAnsiIsFedQuickly` 实测 260 ms 超过 200 ms 预算而失败。
该测试同样是"共享 runner 上单次墙钟采样"的结构性不稳定：并发任务、JIT 与杀毒扫描都会污染样本。

## 4. 已实施的修复

| 文件 | 修复 |
|---|---|
| `src/SurgeLink.Agent/AgentWire.cs` | 拆出 `Serialize<T>`（只序列化并做 1 MiB 校验）与 `WriteFrameAsync`（只写已序列化帧）；`WriteAsync` 组合两者，行为不变 |
| `src/SurgeLink.Agent/AgentPipeServer.cs` | 先 `AgentWire.Serialize(response)`，再启动 reply 截止时间；`AgentFrameTooLargeException` 总是用新的 10 秒预算写出 `output_limit`；普通响应与异常路径的 reply 预算从 2 秒提高到 10 秒 |
| `tests/SurgeLink.Agent.Tests/AgentExperienceTests.cs` | 新增“超帧在写入前被拒绝”的回归测试；保留原有 output_limit 端到端用例 |
| `tests/SurgeLink.Agent.Tests/McpAdapterTests.cs` | MCP stdio host 的测试总超时从 15 秒提高到 30 秒，避免冷启动 `dotnet` 吃掉全部预算 |
| `tests/SurgeLink.Terminal.Tests/PerformanceTests.cs` | 1 MB 吞吐测试改为预热后取 3 次最好成绩，预算仍为 200 ms，消除共享 runner 单次采样抖动 |
| `docs/AGENT-CLI.md`、`docs/AGENT-SECURITY-BOUNDARY.md` | 同步回复预算说明 |

修复后 `OversizedDirectoryReadReturnsAnActionableTransportError` 在 Release 下连续运行 20 次全部通过；
Agent 测试 128 项全部通过；全量测试 1545 项全部通过。

## 5. 下一步

1. 在 GitHub Actions 重新运行 `Release`（workflow_dispatch，版本 1.0.3.6），修复已包含在本次提交中。
2. 失败运行没有发布任何 Release 资产；重新运行会从干净状态构建 EXE/APPX。
3. 若只想临时验证旧提交，可以 Re-run failed jobs，但旧代码仍有该时序缺陷，不保证不复现。
