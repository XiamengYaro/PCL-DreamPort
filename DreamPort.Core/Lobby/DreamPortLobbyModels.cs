using DreamPort.Core.Api;

namespace DreamPort.Core.Lobby;

public sealed record DreamPortLobbyRoom(
    string Code,
    string Host,
    string Name,
    string Visibility,
    int MaxPlayers,
    string McVersion,
    string ModLoader,
    string ModHash,
    IReadOnlyList<string> ModList,
    string Status,
    long CreatedAtMs,
    int? Occupancy,
    bool HostGone);

public sealed record DreamPortLobbyRoomsResult(
    bool Ok,
    IReadOnlyList<DreamPortLobbyRoom> Rooms,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLobbyMyRoomResult(
    bool Ok,
    DreamPortLobbyRoom? Room,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLobbyCreateResult(
    bool Ok,
    DreamPortLobbyRoom? Room,
    string? Ticket,
    int TicketTtlSeconds,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLobbyJoinDiff(
    bool VersionMismatch,
    string RoomVersion,
    string MyVersion);

public sealed record DreamPortLobbyJoinResult(
    bool Ok,
    DreamPortLobbyRoom? Room,
    string? Ticket,
    int TicketTtlSeconds,
    DreamPortLobbyJoinDiff? Diff,
    string? Reason = null,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLobbyInviteItem(
    long Id,
    string Code,
    string Inviter,
    string Invitee,
    string? Message,
    long CreatedAtMs,
    long ExpiresAtMs,
    bool RoomOpen,
    string? RoomName,
    string? McVersion,
    int? MaxPlayers);

public sealed record DreamPortLobbyInvitesResult(
    bool Ok,
    IReadOnlyList<DreamPortLobbyInviteItem> Incoming,
    IReadOnlyList<DreamPortLobbyInviteItem> Outgoing,
    int TtlSeconds,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLobbyAcceptInviteResult(
    bool Ok,
    string? RoomCode,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortFriendsResult(
    bool Ok,
    IReadOnlyList<string> Friends,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortRelayEndpoint(
    string Host,
    int Port,
    bool Tls);

public sealed record DreamPortLobbyMember(
    string PeerId,
    string PlayerName,
    bool IsHost,
    bool Kicked,
    long JoinedAtMs);

public sealed record DreamPortLobbyTunnelEvent(
    string Kind,
    string Message,
    string? PeerId = null);

public sealed record DreamPortLobbyTunnelStats(
    int ActiveDataConnections,
    long BytesIn,
    long BytesOut);

public sealed class DreamPortRelayRejectedException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}
