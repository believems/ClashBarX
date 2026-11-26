using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashBarX.Core.Models;

/// <summary>mihomo 版本（源：Runtime.swift 的 VersionInfo，version 非可选，缺失即解码失败）。</summary>
public sealed record VersionInfo(
    [property: JsonPropertyName("version"), JsonRequired] string Version);

/// <summary>内核升级响应（源：Runtime.swift 的 CoreUpgradeResponse，两字段均可缺省）。</summary>
public sealed record CoreUpgradeResponse(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("message")] string? Message);
