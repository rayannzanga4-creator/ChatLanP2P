using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace ChatLan.Core;

public class PeerHub : Hub
{
    private readonly PeerNode _node;
    public PeerHub(PeerNode node) { _node = node; }

    public Task<PeerInfoDto?> Hello(PeerInfoDto peer) =>
        _node.OnHelloAsync(Context.ConnectionId, peer, Context.GetHttpContext()?.Connection.RemoteIpAddress);

    // ----- messages -----
    public Task<bool> ReceiveMessage(WireMessage message) => _node.OnMessageAsync(Context.ConnectionId, message);
    public Task<bool> ReceiveGroupMessage(WireMessage message) => _node.OnGroupMessageAsync(Context.ConnectionId, message);
    public Task SyncMessages(WireMessage[] items) => _node.OnSyncAsync(Context.ConnectionId, items);
    public Task MessagesRead(string[] uids) => _node.OnReadAsync(Context.ConnectionId, uids);
    public Task Typing() => _node.OnTypingAsync(Context.ConnectionId);
    public Task DeleteForEveryone(string uid) => _node.OnDeleteAsync(Context.ConnectionId, uid);
    public Task EditMessage(string uid, string text, DateTime? editedAt) => _node.OnEditAsync(Context.ConnectionId, uid, text, editedAt);
    public Task ReactMessage(string uid, string emoji, bool remove) => _node.OnReactAsync(Context.ConnectionId, uid, emoji, remove);
    public Task ProfileChanged(PeerInfoDto peer) => _node.OnProfileAsync(Context.ConnectionId, peer);

    // ----- statuts / stories -----
    public Task<bool> ReceiveStatus(StatusDto status) => _node.OnStatusAsync(Context.ConnectionId, status);
    public Task SyncStatuses(StatusDto[] items) => _node.OnStatusSyncAsync(Context.ConnectionId, items);
    public Task StatusSeen(string statusId) => _node.OnStatusSeenAsync(Context.ConnectionId, statusId);
    public Task StatusReact(string statusId, string emoji) => _node.OnStatusReactAsync(Context.ConnectionId, statusId, emoji);
    public Task StatusDeleted(string statusId) => _node.OnStatusDeletedAsync(Context.ConnectionId, statusId);

    // ----- groupes -----
    public Task GroupUpdate(GroupDto group) => _node.OnGroupAsync(Context.ConnectionId, group);

    // ----- appels audio / vidéo -----
    public Task CallInvite(string callId, string fromName, bool video, CallPeerDto[] peers)
    {
        _node.OnCallInvite(Context.ConnectionId, callId, fromName, video, peers ?? Array.Empty<CallPeerDto>());
        return Task.CompletedTask;
    }

    public Task CallAccept(string callId)
    {
        _node.OnCallAccept(Context.ConnectionId, callId);
        return Task.CompletedTask;
    }

    public Task CallDecline(string callId)
    {
        _node.OnCallDecline(Context.ConnectionId, callId);
        return Task.CompletedTask;
    }

    public Task CallEnd(string callId)
    {
        _node.OnCallEnd(Context.ConnectionId, callId);
        return Task.CompletedTask;
    }

    public Task CallAudio(string callId, byte[] data, string fromPeerId)
    {
        _node.OnCallAudio(Context.ConnectionId, callId, data, fromPeerId);
        return Task.CompletedTask;
    }

    public Task CallVideo(string callId, byte[] data, string fromPeerId)
    {
        _node.OnCallVideo(Context.ConnectionId, callId, data, fromPeerId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _node.OnInboundClosed(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
