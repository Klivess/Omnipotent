using Omnipotent.Services.KliveMail.Persistence;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Storage;

namespace Omnipotent.Services.KliveMail.Smtp
{
    // Catch-all for @klive.dev: accept any local part, reject other domains (we never relay).
    public sealed class KliveMailMailboxFilter : IMailboxFilter
    {
        public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox @from, int size, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox @from, CancellationToken cancellationToken)
            => Task.FromResult(string.Equals(to.Host, KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase));
    }
}
