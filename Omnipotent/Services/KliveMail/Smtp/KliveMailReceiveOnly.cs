using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Protocol;

namespace Omnipotent.Services.KliveMail.Smtp
{
    /// <summary>
    /// KliveMail only receives: mail for @klive.dev is accepted from anyone, and nothing is ever
    /// relayed (KliveMailMailboxFilter refuses other domains). So there is nothing to log in to.
    ///
    /// Port 25 nevertheless advertised "AUTH PLAIN LOGIN" on plaintext sessions (the endpoint allowed
    /// unsecured authentication, and the library's default authenticator was in charge of the
    /// answer). A login granted nothing, but an open AUTH on a public port invites credential
    /// stuffing and reads as a misconfigured relay to anyone scanning it. Now AUTH is not offered,
    /// on plaintext or after STARTTLS, and any login a client tries anyway is refused.
    /// </summary>
    public static class KliveMailReceiveOnly
    {
        /// <summary>Refuses every login; mail delivery never needs one.</summary>
        public static readonly IUserAuthenticator RefuseAllLogins = new DelegatingUserAuthenticator((_, _) => false);

        /// <summary>Builds the commands the server runs, with an EHLO that does not offer AUTH.</summary>
        public sealed class CommandFactory : SmtpCommandFactory
        {
            public override SmtpCommand CreateEhlo(string domainOrAddress) => new NoAuthEhloCommand(domainOrAddress);
        }

        public sealed class NoAuthEhloCommand : EhloCommand
        {
            public NoAuthEhloCommand(string domainOrAddress) : base(domainOrAddress) { }

            protected override IEnumerable<string> GetExtensions(ISessionContext context)
                => base.GetExtensions(context).Where(extension => !extension.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        }
    }
}
