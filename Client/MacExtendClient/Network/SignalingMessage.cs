using System.Text.Json.Serialization;

namespace MacExtendClient.Network;

/// <summary>
/// Mensaje de señalización WebRTC (SDP offer/answer, candidato ICE), como una línea
/// de JSON compacto sobre la misma conexión TCP de control que antes solo detectaba
/// connect/disconnect. El Server tiene el mismo shape en Swift (SignalingMessage.swift).
/// </summary>
sealed class SignalingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("sdp")]
    public string? Sdp { get; set; }

    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; set; }

    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; set; }
}
