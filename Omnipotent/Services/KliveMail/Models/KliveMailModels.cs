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
    }

    public sealed class MailboxInfo
    {
        public string Address { get; set; } = "";
        public string? DisplayName { get; set; }
        public int Total { get; set; }
        public int Unread { get; set; }
        public bool Pinned { get; set; }
    }
}
