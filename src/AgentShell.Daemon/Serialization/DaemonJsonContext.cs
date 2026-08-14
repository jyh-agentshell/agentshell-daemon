using System.Text.Json.Serialization;
using AgentShell.Daemon.Auth;
using AgentShell.Protocol.Models;

namespace AgentShell.Daemon.Serialization;

/// <summary>裁剪发布所需的 JSON 类型元数据，禁止回退到反射序列化。</summary>
[JsonSerializable(typeof(BindingCodeStore.StoredBindingCode))]
[JsonSerializable(typeof(RegisterHostKeyRequest))]
[JsonSerializable(typeof(RenewResponse))]
[JsonSerializable(typeof(AgentStateEvent))]
[JsonSerializable(typeof(SessionLifecycleEvent))]
[JsonSerializable(typeof(ReportEnvelope))]
internal partial class DaemonJsonContext : JsonSerializerContext;
