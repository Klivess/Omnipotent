using System.Buffers;
using System.Text.RegularExpressions;
using MimeKit;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveMail.Models;
using Omnipotent.Services.KliveMail.Persistence;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Omnipotent.Services.KliveMail.Smtp
{
    // Receives the raw DATA buffer for each accepted message, parses it with MimeKit, and persists
    // one row per @klive.dev envelope recipient (catch-all). Attachments are written to disk.
    public sealed class KliveMailMessageStore : MessageStore
    {
        private readonly KliveMail service;
        private readonly KliveMailRepository repo;

        public KliveMailMessageStore(KliveMail service, KliveMailRepository repo)
        {
            this.service = service;
            this.repo = repo;
        }

        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                await using var ms = new MemoryStream();
                var position = buffer.GetPosition(0);
                while (buffer.TryGet(ref position, out var memory))
                    await ms.WriteAsync(memory, cancellationToken);
                long rawSize = ms.Length;
                ms.Position = 0;

                var message = await MimeMessage.LoadAsync(ms, cancellationToken);

                // Envelope recipients (RCPT TO) that are @klive.dev.
                var recipients = transaction.To
                    .Select(m => ($"{m.User}@{m.Host}").ToLowerInvariant())
                    .Where(a => a.EndsWith("@" + KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                // Fall back to header To, then a catch-all bucket, so nothing is silently lost.
                if (recipients.Count == 0)
                {
                    recipients = message.To.Mailboxes
                        .Select(m => m.Address.ToLowerInvariant())
                        .Where(a => a.EndsWith("@" + KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase))
                        .Distinct()
                        .ToList();
                }
                if (recipients.Count == 0)
                    recipients.Add("catchall@" + KliveMailRepository.MailDomain);

                var fromMb = message.From.Mailboxes.FirstOrDefault();
                string fromAddress = (fromMb?.Address
                    ?? (transaction.From != null ? $"{transaction.From.User}@{transaction.From.Host}" : "unknown@unknown")).ToLowerInvariant();
                string? fromName = fromMb?.Name;

                string threadId = DeriveThreadId(message);

                foreach (var rcpt in recipients)
                {
                    var stored = new StoredMessage
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ToAddress = rcpt,
                        FromAddress = fromAddress,
                        FromName = fromName,
                        Subject = message.Subject,
                        DateUtc = message.Date != default ? message.Date.UtcDateTime : (DateTime?)null,
                        ReceivedUtc = DateTime.UtcNow,
                        MessageId = message.MessageId,
                        InReplyTo = message.InReplyTo,
                        ReferencesRaw = (message.References != null && message.References.Count > 0) ? string.Join(' ', message.References) : null,
                        ThreadId = threadId,
                        BodyText = message.TextBody,
                        BodyHtml = message.HtmlBody,
                        RawSize = rawSize
                    };

                    stored.Attachments = await SaveAttachmentsAsync(message, stored.Id, cancellationToken);
                    stored.HasAttachments = stored.Attachments.Count > 0;

                    await repo.InsertMessageAsync(stored, cancellationToken);
                }

                await service.NotifyMailReceived(recipients, fromAddress, message.Subject ?? "(no subject)");
                return SmtpResponse.Ok;
            }
            catch (Exception ex)
            {
                await service.LogStoreError(ex);
                // Transient failure: ask the sender to retry rather than dropping the message.
                return SmtpResponse.TransactionFailed;
            }
        }

        private static string DeriveThreadId(MimeMessage message)
        {
            if (message.References != null && message.References.Count > 0)
                return message.References[0];
            if (!string.IsNullOrEmpty(message.InReplyTo))
                return message.InReplyTo;
            if (!string.IsNullOrEmpty(message.MessageId))
                return message.MessageId;

            var subj = (message.Subject ?? "").Trim();
            subj = Regex.Replace(subj, "^(?:(?:re|fwd|fw)\\s*:\\s*)+", "", RegexOptions.IgnoreCase).Trim();
            return string.IsNullOrEmpty(subj) ? Guid.NewGuid().ToString("N") : "subj:" + subj.ToLowerInvariant();
        }

        private async Task<List<StoredAttachment>> SaveAttachmentsAsync(MimeMessage message, string messageId, CancellationToken ct)
        {
            var result = new List<StoredAttachment>();
            var attachmentsDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveMailAttachmentsDirectory), messageId);

            foreach (var entity in message.Attachments)
            {
                try
                {
                    bool isInline = entity.ContentDisposition?.Disposition == ContentDisposition.Inline;
                    string? contentId = (entity as MimePart)?.ContentId ?? (entity as MessagePart)?.ContentId;
                    string? fileName;
                    string? contentType;

                    if (entity is MimePart mp)
                    {
                        fileName = mp.FileName;
                        contentType = mp.ContentType?.MimeType;
                    }
                    else
                    {
                        fileName = entity.ContentDisposition?.FileName;
                        contentType = entity.ContentType?.MimeType;
                    }

                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = $"attachment-{Guid.NewGuid():N}";
                    fileName = SanitizeFileName(fileName);

                    Directory.CreateDirectory(attachmentsDir);
                    var attId = Guid.NewGuid().ToString("N");
                    var storagePath = Path.Combine(attachmentsDir, attId + "_" + fileName);

                    await using (var fs = File.Create(storagePath))
                    {
                        if (entity is MimePart mimePart)
                            await mimePart.Content.DecodeToAsync(fs, ct);
                        else if (entity is MessagePart msgPart)
                            await msgPart.Message.WriteToAsync(fs, ct);
                    }

                    result.Add(new StoredAttachment
                    {
                        Id = attId,
                        MessageId = messageId,
                        FileName = fileName,
                        ContentType = contentType ?? "application/octet-stream",
                        SizeBytes = new FileInfo(storagePath).Length,
                        StoragePath = storagePath,
                        IsInline = isInline,
                        ContentId = contentId
                    });
                }
                catch (Exception ex)
                {
                    await service.LogStoreError(ex);
                }
            }
            return result;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            if (name.Length > 180) name = name.Substring(name.Length - 180);
            return name;
        }
    }
}
