using System.Collections.Specialized;
using System.Net;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveMail.Persistence;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.KliveMail
{
#pragma warning disable CS4014
    public sealed class KliveMailRoutes
    {
        private readonly KliveMail service;
        public KliveMailRoutes(KliveMail service) { this.service = service; }

        private KliveMailRepository Repo => service.Repo;

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/klivemail/stats", HandleStats, HttpMethod.Get, KliveMailPerms.OverviewView);
            await service.CreateAPIRoute("/klivemail/mailboxes", HandleListMailboxes, HttpMethod.Get, KliveMailPerms.MailboxesRead);
            await service.CreateAPIRoute("/klivemail/mailboxes/create", HandleCreateMailbox, HttpMethod.Post, KliveMailPerms.MailboxesManage);
            await service.CreateAPIRoute("/klivemail/mailboxes/delete", HandleDeleteMailbox, HttpMethod.Post, KliveMailPerms.MailboxesManage);
            await service.CreateAPIRoute("/klivemail/messages", HandleListMessages, HttpMethod.Get, KliveMailPerms.MessagesRead);
            await service.CreateAPIRoute("/klivemail/messages/detail", HandleMessageDetail, HttpMethod.Get, KliveMailPerms.MessagesRead);
            await service.CreateAPIRoute("/klivemail/search", HandleSearch, HttpMethod.Get, KliveMailPerms.MessagesRead);
            await service.CreateAPIRoute("/klivemail/messages/mark-read", HandleMarkRead, HttpMethod.Post, KliveMailPerms.MessagesAct);
            await service.CreateAPIRoute("/klivemail/messages/mark-unread", HandleMarkUnread, HttpMethod.Post, KliveMailPerms.MessagesAct);
            await service.CreateAPIRoute("/klivemail/messages/delete", HandleDelete, HttpMethod.Post, KliveMailPerms.MessagesDelete);
            await service.CreateAPIRoute("/klivemail/attachments/download", HandleAttachmentDownload, HttpMethod.Get, KliveMailPerms.MessagesRead);
            // Many messages at once (the inbox held 268 unread across 19 addresses, marked one at a
            // time), and the way back out of the trash, which had no route at all.
            await service.CreateAPIRoute("/klivemail/messages/bulk-read", HandleBulkRead, HttpMethod.Post, KliveMailPerms.MessagesAct);
            await service.CreateAPIRoute("/klivemail/messages/bulk-delete", HandleBulkDelete, HttpMethod.Post, KliveMailPerms.MessagesDelete);
        }

        private async Task HandleStats(UserRequest req)
        {
            string? json = service.StatsSnapshotJson;
            await req.ReturnResponse(json ?? "{\"pending\":true}",
                code: json == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }

        private async Task HandleListMailboxes(UserRequest req)
        {
            string? json = service.MailboxesSnapshotJson;
            await req.ReturnResponse(json ?? "{\"pending\":true}",
                code: json == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }

        private async Task HandleCreateMailbox(UserRequest req)
        {
            try
            {
                var address = req.userParameters.Get("address");
                var displayName = req.userParameters.Get("displayName");
                if (string.IsNullOrWhiteSpace(address))
                {
                    await req.ReturnResponse("address is required.", code: HttpStatusCode.BadRequest);
                    return;
                }
                var ok = await Repo.CreateMailboxAsync(address, displayName);
                if (ok) service.QueueStatsRefresh();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = ok, address = KliveMailRepository.NormalizeAddress(address) }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleDeleteMailbox(UserRequest req)
        {
            try
            {
                var address = req.userParameters.Get("address");
                if (string.IsNullOrWhiteSpace(address))
                {
                    await req.ReturnResponse("address is required.", code: HttpStatusCode.BadRequest);
                    return;
                }
                var ok = await Repo.DeleteMailboxAsync(address);
                if (ok) service.QueueStatsRefresh();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = ok }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleListMessages(UserRequest req)
        {
            try
            {
                var list = await Repo.ListMessagesAsync(new Models.MessageQuery
                {
                    Mailbox = NormalizeMailboxFilter(req.userParameters.Get("mailbox")),
                    UnreadOnly = ParseBool(req.userParameters.Get("unread")),
                    HasAttachmentOnly = ParseBool(req.userParameters.Get("hasAttachment")),
                    CodesOnly = ParseBool(req.userParameters.Get("codes")),
                    Trash = ParseBool(req.userParameters.Get("trash")),
                    Page = ParseInt(req.userParameters.Get("page"), 1),
                    PageSize = ParseInt(req.userParameters.Get("pageSize"), 50),
                });
                await req.ReturnResponse(JsonConvert.SerializeObject(list));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleMessageDetail(UserRequest req)
        {
            try
            {
                var id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    await req.ReturnResponse("id is required.", code: HttpStatusCode.BadRequest);
                    return;
                }

                var msg = await Repo.GetMessageAsync(id);
                if (msg == null)
                {
                    await req.ReturnResponse("Message not found.", code: HttpStatusCode.NotFound);
                    return;
                }

                // peek=1 reads without marking read (previews, audits); the page opens normally.
                bool peek = ParseBool(req.userParameters.Get("peek"));
                bool wasRead = msg.IsRead;
                if (!peek && !msg.IsRead && await Repo.SetReadAsync(id, true)) service.QueueStatsRefresh();
                var thread = await Repo.GetThreadAsync(msg.ThreadId);
                string? code = await Repo.GetCodeAsync(id) ?? KliveMailDigest.FindCode(msg.Subject, msg.BodyText, msg.BodyHtml);
                // The links a sign-up or sign-in mail wants clicked, best first; footer noise is left out.
                var links = KliveMailDigest.RankLinks(msg.BodyHtml, msg.BodyText, 12)
                    .Where(link => link.Score > 0).Take(3)
                    .Select(link => new { url = link.Url, host = HostOf(link.Url) });

                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    msg.Id,
                    msg.ToAddress,
                    msg.FromAddress,
                    msg.FromName,
                    msg.Subject,
                    msg.DateUtc,
                    msg.ReceivedUtc,
                    msg.ThreadId,
                    msg.BodyText,
                    msg.BodyHtml,
                    msg.HasAttachments,
                    msg.RawSize,
                    IsRead = !peek || wasRead,
                    WasRead = wasRead,
                    msg.IsDeleted,
                    Code = code,
                    Links = links,
                    Attachments = msg.Attachments.Select(a => new
                    {
                        a.Id,
                        a.FileName,
                        a.ContentType,
                        a.SizeBytes,
                        a.IsInline,
                        a.ContentId
                    }),
                    Thread = thread
                }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleSearch(UserRequest req)
        {
            try
            {
                var q = req.userParameters.Get("q");
                int page = ParseInt(req.userParameters.Get("page"), 1);
                int pageSize = ParseInt(req.userParameters.Get("pageSize"), 50);
                string? mailbox = req.userParameters.Get("mailbox");
                string? from = req.userParameters.Get("from");
                if (string.IsNullOrWhiteSpace(q))
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(Array.Empty<object>()));
                    return;
                }
                var list = await Repo.SearchAsync(q, page, pageSize, mailbox, from);
                await req.ReturnResponse(JsonConvert.SerializeObject(list));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleMarkRead(UserRequest req) => await SetReadFromRequest(req, true);
        private async Task HandleMarkUnread(UserRequest req) => await SetReadFromRequest(req, false);

        private async Task SetReadFromRequest(UserRequest req, bool read)
        {
            try
            {
                var id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    await req.ReturnResponse("id is required.", code: HttpStatusCode.BadRequest);
                    return;
                }
                var ok = await Repo.SetReadAsync(id, read);
                if (ok) service.QueueStatsRefresh();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = ok }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleDelete(UserRequest req)
        {
            try
            {
                var id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    await req.ReturnResponse("id is required.", code: HttpStatusCode.BadRequest);
                    return;
                }
                var ok = await Repo.SoftDeleteAsync(id);
                if (ok) service.QueueStatsRefresh();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = ok }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleAttachmentDownload(UserRequest req)
        {
            try
            {
                var id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    await req.ReturnResponse("id is required.", code: HttpStatusCode.BadRequest);
                    return;
                }

                var att = await Repo.GetAttachmentAsync(id);
                if (att == null || string.IsNullOrEmpty(att.StoragePath) || !File.Exists(att.StoragePath))
                {
                    await req.ReturnResponse("Attachment not found.", code: HttpStatusCode.NotFound);
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(att.StoragePath);
                var headers = new NameValueCollection
                {
                    { "Content-Disposition", $"attachment; filename=\"{att.FileName}\"" }
                };
                await req.ReturnBinaryResponse(bytes, att.ContentType ?? "application/octet-stream", HttpStatusCode.OK, headers);
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        // Body or query: { "ids": [...] } / ids=a,b,c; plus read=0 (bulk-read) or restore=1 (bulk-delete).
        private static List<string> ReadIds(UserRequest req)
        {
            var ids = new List<string>();
            string? fromQuery = req.userParameters.Get("ids");
            if (!string.IsNullOrWhiteSpace(fromQuery)) ids.AddRange(fromQuery.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            string? body = req.userMessageContent;
            if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{"))
            {
                try
                {
                    var parsed = JsonConvert.DeserializeObject<BulkBody>(body);
                    if (parsed?.Ids != null) ids.AddRange(parsed.Ids.Where(id => !string.IsNullOrWhiteSpace(id)));
                }
                catch (JsonException) { }
            }
            return ids;
        }

        private sealed class BulkBody { public List<string>? Ids { get; set; } }

        private async Task HandleBulkRead(UserRequest req)
        {
            try
            {
                bool read = req.userParameters.Get("read") is not { } r || ParseBool(r);
                int changed;
                if (ParseBool(req.userParameters.Get("all")))
                {
                    if (!read)
                    {
                        await req.ReturnResponse("all=1 only marks mail read.", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    changed = await Repo.MarkAllReadAsync(NormalizeMailboxFilter(req.userParameters.Get("mailbox")));
                }
                else
                {
                    var ids = ReadIds(req);
                    if (ids.Count == 0 || ids.Count > KliveMailRepository.MaxBulkIds)
                    {
                        await req.ReturnResponse($"Provide 1–{KliveMailRepository.MaxBulkIds} ids, or all=1.", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    changed = await Repo.SetReadManyAsync(ids, read);
                }
                if (changed > 0) service.QueueStatsRefresh();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, changed }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleBulkDelete(UserRequest req)
        {
            try
            {
                var ids = ReadIds(req);
                if (ids.Count == 0 || ids.Count > KliveMailRepository.MaxBulkIds)
                {
                    await req.ReturnResponse($"Provide 1–{KliveMailRepository.MaxBulkIds} ids.", code: HttpStatusCode.BadRequest);
                    return;
                }
                bool restore = ParseBool(req.userParameters.Get("restore"));
                int changed = await Repo.SetDeletedManyAsync(ids, deleted: !restore);
                if (changed > 0) service.QueueStatsRefresh();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, changed, restored = restore }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        // ── helpers ──

        private static string? HostOf(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

        private static async Task Fail(UserRequest req, Exception ex)
            => await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);

        private static string? NormalizeMailboxFilter(string? mailbox)
        {
            if (string.IsNullOrWhiteSpace(mailbox)) return null;
            if (mailbox.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                mailbox.Equals("__all__", StringComparison.OrdinalIgnoreCase)) return null;
            return mailbox;
        }

        private static bool ParseBool(string? value)
            => value != null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

        private static int ParseInt(string? value, int fallback)
            => int.TryParse(value, out var n) ? n : fallback;
    }
#pragma warning restore CS4014
}
