using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>
    /// Paths nobody legitimately requests from KliveAPI: CMS admin panels, leaked config
    /// files, VCS metadata, exploit endpoints. KliveAPI serves none of these, so a hit is
    /// a probe by definition — the question is only how many and how varied.
    /// </summary>
    public static class ProbePatterns
    {
        public enum ProbeKind { None, Cms, Secrets, Vcs, AdminPanel, Exploit, Shell, Traversal }

        private static readonly (Regex Pattern, ProbeKind Kind)[] Patterns =
        {
            (Rx(@"/(wp-(login|admin|content|includes|json|config)|xmlrpc\.php|wordpress/|wp/)"), ProbeKind.Cms),
            (Rx(@"/(administrator/|joomla|drupal|magento|typo3|umbraco|sites/default/files)"), ProbeKind.Cms),
            (Rx(@"(^|/)\.(env|aws|ssh|npmrc|pypirc|docker|kube|htpasswd|htaccess|DS_Store|vscode|idea)(\b|/|\.|$)"), ProbeKind.Secrets),
            (Rx(@"/(config\.(json|php|yml|yaml|js)|settings\.py|web\.config|appsettings(\.\w+)?\.json|credentials|secrets?\.(json|ya?ml)|database\.yml|id_rsa|\.pem$|backup\.(sql|zip|tar)|dump\.sql|db\.sql)"), ProbeKind.Secrets),
            (Rx(@"(^|/)\.(git|svn|hg|bzr)(/|$)"), ProbeKind.Vcs),
            (Rx(@"/(phpmyadmin|pma|myadmin|adminer|phpinfo|server-status|server-info|manager/html|jmx-console|web-console|solr/|actuator|_profiler|telescope|horizon|console/|elmah\.axd|trace\.axd)"), ProbeKind.AdminPanel),
            (Rx(@"/(cgi-bin/|boaform|hnap1|goform/|setup\.cgi|shell\?|vendor/phpunit|eval-stdin\.php|_ignition|autodiscover|owa/|ecp/|remote/login|dana-na|\+cscoe\+|global-protect|sslvpn|ReportServer|mgmt/tm|api/v1/pods|containers/json|druid/|nacos|struts|\.action$|invoker/|jenkins|hudson|geoserver|confluence|jira/secure)"), ProbeKind.Exploit),
            (Rx(@"(\.(php|asp|aspx|jsp|cgi|pl)$)|/(shell|cmd|c99|r57|webshell|alfa|wso)\.?"), ProbeKind.Shell),
            (Rx(@"(\.\./|\.\.%2f|%2e%2e|/etc/passwd|/proc/self|win\.ini|boot\.ini)"), ProbeKind.Traversal),
        };

        private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static ProbeKind Classify(string? route, string? query = null)
        {
            if (string.IsNullOrEmpty(route)) return ProbeKind.None;
            foreach (var (pattern, kind) in Patterns)
            {
                if (pattern.IsMatch(route)) return kind;
            }
            if (!string.IsNullOrEmpty(query) && Patterns[^1].Pattern.IsMatch(query)) return ProbeKind.Traversal;
            return ProbeKind.None;
        }

        public static bool IsProbe(string? route, string? query = null) => Classify(route, query) != ProbeKind.None;
    }
}
