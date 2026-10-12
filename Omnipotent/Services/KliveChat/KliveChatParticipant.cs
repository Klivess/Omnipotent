using Omnipotent.Profiles;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>
    /// A seat in a room. It outlives any one socket: when a resumable participant's connection drops, the
    /// seat is held for <see cref="KliveChatRoom.ReconnectGrace"/> so a quick reconnect lands back in it and
    /// the other peers keep their media connections. Mutable state is guarded by the owning room's lock.
    /// </summary>
    public sealed class KliveChatParticipant
    {
        private readonly Queue<DateTime> recentChats = new();

        public KliveChatParticipant(KliveChatJoinRequest join, string name, DateTime nowUtc)
        {
            Id = join.ParticipantId;
            Name = name;
            UserId = string.IsNullOrWhiteSpace(join.UserId) ? null : join.UserId;
            Rank = join.Rank;
            IdentityKey = join.IdentityKey;
            Resumable = join.Resumable;
            JoinedUtc = nowUtc;
            LastSeenUtc = nowUtc;
            Refresh(join);
        }

        public string Id { get; }
        public string Name { get; internal set; }
        public string? UserId { get; }
        /// <summary>Hierarchy rank: who may moderate whom.</summary>
        public ProfileRank Rank { get; }
        public string IdentityKey { get; }
        public bool Resumable { get; }
        public DateTime JoinedUtc { get; }
        public bool IsGuest => UserId == null;

        public bool HoldsModeratePermission { get; private set; }
        public bool HoldsDeleteAnyPermission { get; private set; }
        public string? IpAddress { get; private set; }
        public int Protocol { get; private set; }

        public bool IsMuted { get; internal set; }
        public bool HasVideo { get; internal set; }
        public bool IsScreenSharing { get; internal set; }
        public bool HasScreenAudio { get; internal set; }

        internal KliveChatConnection? Connection { get; set; }
        internal DateTime? DisconnectedUtc { get; set; }
        internal DateTime LastSeenUtc { get; set; }

        /// <summary>A reconnect re-reads permissions and protocol: they may have changed since the seat was taken.</summary>
        internal void Refresh(KliveChatJoinRequest join)
        {
            HoldsModeratePermission = join.HoldsModeratePermission;
            HoldsDeleteAnyPermission = join.HoldsDeleteAnyPermission;
            IpAddress = join.IpAddress;
            Protocol = join.Protocol;
        }

        /// <summary>Sliding-window rate limit for chat: at most <paramref name="burst"/> messages per <paramref name="window"/>.</summary>
        internal bool AllowChat(DateTime nowUtc, int burst, TimeSpan window)
        {
            while (recentChats.Count > 0 && nowUtc - recentChats.Peek() > window) recentChats.Dequeue();
            if (recentChats.Count >= burst) return false;
            recentChats.Enqueue(nowUtc);
            return true;
        }
    }
}
