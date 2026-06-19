using System.Collections.Specialized;
using System.Net;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveMail.Persistence;
using static Omnipotent.Profiles.KMProfileManager;

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
            await service.CreateAPIRoute("/klivemail/stats", HandleStats, HttpMethod.Get, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/mailboxes", HandleListMailboxes, HttpMethod.Get, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/mailboxes/create", HandleCreateMailbox, HttpMethod.Post, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/mailboxes/delete", HandleDeleteMailbox, HttpMethod.Post, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/messages", HandleListMessages, HttpMethod.Get, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/messages/detail", HandleMessageDetail, HttpMethod.Get, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/search", HandleSearch, HttpMethod.Get, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/messages/mark-read", HandleMarkRead, HttpMethod.Post, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/messages/mark-unread", HandleMarkUnread, HttpMethod.Post, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/messages/delete", HandleDelete, HttpMethod.Post, KMPermissions.Klives);
            await service.CreateAPIRoute("/klivemail/attachments/download", HandleAttachmentDownload, HttpMethod.Get, KMPermissions.Klives);
        }

        private async Task HandleStats(UserRequest req)
        {
            try
            {
                var (total, unread, trash) = await Repo.GetStatsAsync();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { total, unread, trash }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleListMailboxes(UserRequest req)
        {
            try
            {
                var mailboxes = await Repo.ListMailboxesAsync();
                var (total, unread, trash) = await Repo.GetStatsAsync();
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    all = new { total, unread },
                    trash,
                    mailboxes
                }));
            }
            catch (Exception ex) { await Fail(req, ex); }
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
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = ok }));
            }
            catch (Exception ex) { await Fail(req, ex); }
        }

        private async Task HandleListMessages(UserRequest req)
        {
            try
            {
                var mailbox = NormalizeMailboxFilter(req.userParameters.Get("mailbox"));
                bool unread = ParseBool(req.userParameters.Get("unread"));
                bool hasAttachment = ParseBool(req.userParameters.Get("hasAttachment"));
                bool trash = ParseBool(req.userParameters.Get("trash"));
                int page = ParseInt(req.userParameters.Get("page"), 1);
                int pageSize = ParseInt(req.userParameters.Get("pageSize"), 50);

                var list = await Repo.ListMessagesAsync(mailbox, unread, hasAttachment, trash, page, pageSize);
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

                if (!msg.IsRead) await Repo.SetReadAsync(id, true);
                var thread = await Repo.GetThreadAsync(msg.ThreadId);

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
                    IsRead = true,
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
                if (string.IsNullOrWhiteSpace(q))
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(Array.Empty<object>()));
                    return;
                }
                var list = await Repo.SearchAsync(q, page, pageSize);
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

        // ── helpers ──

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
