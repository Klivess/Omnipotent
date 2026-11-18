namespace Omnipotent.Services.KliveMail.Models
{
    // Full message (detail view).
    public sealed class StoredMessage
    {
        public string Id { get; set; } = "";
        public string ToAddress { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string? FromName { get; set; }
        public string? Subject { get; set; }
        public DateTime? DateUtc { get; set; }
        public DateTime ReceivedUtc { get; set; }
        public string? MessageId { get; set; }
        public string? InReplyTo { get; set; }
        public string? ReferencesRaw { get; set; }
        public string ThreadId { get; set; } = "";
        public string? BodyText { get; set; }
        public string? BodyHtml { get; set; }
        public bool HasAttachments { get; set; }
        public long RawSize { get; set; }
        public bool IsRead { get; set; }
        public bool IsDeleted { get; set; }
        public List<StoredAttachment> Attachments { get; set; } = new();
    }

    public sealed class StoredAttachment
    {
        public string Id { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
        public string StoragePath { get; set; } = "";
        public bool IsInline { get; set; }
        public string? ContentId { get; set; }
    }

    // Lightweight row for list views.
    public sealed class MessageSummary
    {
        public string Id { get; set; } = "";
        public string ToAddress { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string? FromName { get; set; }
        public string? Subject { get; set; }
        public DateTime ReceivedUtc { get; set; }
        public DateTime? DateUtc { get; set; }
        public string ThreadId { get; set; } = "";
        public bool HasAttachments { get; set; }
        public bool IsRead { get; set; }
        public string? Snippet { get; set; }
        /// <summary>The sign-in or verification code the message carries, if it has one (KliveMailDigest).</summary>
        public string? Code { get; set; }
    }

    public sealed class MailboxInfo
    {
        public string Address { get; set; } = "";
        public string? DisplayName { get; set; }
        public int Total { get; set; }
        public int Unread { get; set; }
        public bool Pinned { get; set; }
        public DateTime? LastReceivedUtc { get; set; }
    }

    /// <summary>What the list views filter on. Mailbox null means every address.</summary>
    public sealed class MessageQuery
    {
        public string? Mailbox { get; set; }
        public bool UnreadOnly { get; set; }
        public bool HasAttachmentOnly { get; set; }
        public bool CodesOnly { get; set; }
        public bool Trash { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    /// <summary>Counts for the whole store, read in one pass over the mailbox index.</summary>
    public sealed class MailSummary
    {
        public int Total { get; set; }
        public int Unread { get; set; }
        public int Trash { get; set; }
        public DateTime? LastReceivedUtc { get; set; }
        public int Received24h { get; set; }
        public int Received7d { get; set; }
        /// <summary>Pinned addresses, alphabetical, each with its counts.</summary>
        public List<MailboxInfo> Mailboxes { get; set; } = new();
        /// <summary>Addresses with mail that nobody pinned: catch-all arrivals that only showed in All mail.</summary>
        public List<MailboxInfo> Others { get; set; } = new();
        /// <summary>Arrivals per UTC day, oldest first, days without mail included as 0.</summary>
        public List<DayCount> Daily { get; set; } = new();
    }

    public sealed class DayCount
    {
        public string D { get; set; } = "";
        public int N { get; set; }
    }
}
