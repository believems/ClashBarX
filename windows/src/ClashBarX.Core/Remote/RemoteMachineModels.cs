using System.Text.Json.Serialization;

namespace ClashBarX.Core.Remote;

/// <summary>远程机器（源：Models/RemoteMachine.swift；controllerAddress https 时带 scheme）。</summary>
public sealed record RemoteMachine(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("secret")] string? Secret,
    [property: JsonPropertyName("useHttps")] bool UseHttps)
{
    /// <summary>控制器地址（源：controllerAddress——https 显式带 scheme，http 依赖调用方补）。</summary>
    public string ControllerAddress => UseHttps ? $"https://{Host}:{Port}" : $"{Host}:{Port}";

    public string DisplayAddress => $"{Host}:{Port}";
}

public enum MachineConnectionKind
{
    Unknown,
    Checking,
    Connected,
    Failed,
}

/// <summary>连通状态（源：MachineConnectionStatus——connected 携带版本、failed 携带原因）。</summary>
public sealed record MachineConnectionStatus(MachineConnectionKind Kind, string? Version, string? Reason)
{
    public static MachineConnectionStatus Unknown { get; } = new(MachineConnectionKind.Unknown, null, null);

    public static MachineConnectionStatus Checking { get; } = new(MachineConnectionKind.Checking, null, null);

    public static MachineConnectionStatus Connected(string version) =>
        new(MachineConnectionKind.Connected, version, null);

    public static MachineConnectionStatus Failed(string reason) =>
        new(MachineConnectionKind.Failed, null, reason);

    public bool IsConnected => Kind == MachineConnectionKind.Connected;
}

/// <summary>机器目标（源：MachineTarget——local 或 remote(machine)）。</summary>
public readonly record struct MachineTarget(Guid? MachineId)
{
    public static MachineTarget Local { get; } = new(null);

    public static MachineTarget Remote(RemoteMachine machine) => new(machine.Id);

    public bool IsLocal => MachineId is null;
}
