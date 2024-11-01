using System.Reflection;
using System.Management;
using System.Management.Automation;
using Omnipotent.Data_Handling;
using Microsoft.AspNetCore.Components.Web;
using Newtonsoft.Json;
using static System.Net.WebRequestMethods;

namespace Omnipotent.Services.KliveAPI
{
    public class CertificateInstaller
    {
        KliveAPI parent;
        public static string saveDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesAPICertificateDirectory);
        public string rootAuthorityCrtPath = Path.Combine(saveDir, "KliveAPI.crt");
        public string rootAuthorityPfxPath = Path.Combine(saveDir, "KliveAPI.pfx");
        public string myGatewayCrtPath = Path.Combine(saveDir, "KliveAPIGateway.crt");
        public string myGatewayPfxPath = Path.Combine(saveDir, "KliveAPIGateway.pfx");
        public CertificateInstaller(KliveAPI service)
        {
            parent = service;
        }

        public async Task<bool> IsCertbotInstalled()
        {
            var powershell = PowerShell.Create();
            powershell.AddScript("certbot --help");
            var result = (await powershell.InvokeAsync()).ToList();
            return result.Any();
        }
        public async Task InstallCertBot()
        {
            const string url = "https://github.com/certbot/certbot/releases/tag/v2.11.0";

        }
        public async Task<bool> IsCertificateCreated()
        {
            if (!System.IO.File.Exists(rootAuthorityCrtPath) || !System.IO.File.Exists(rootAuthorityPfxPath) || !System.IO.File.Exists(myGatewayCrtPath) || !System.IO.File.Exists(myGatewayPfxPath))
            {
                return false;
            }
            else
            {
                return true;
            }
        }
        public async Task CreateInstallCert(int expDateYears, string password, string issuedBy)
        {
            // Create/install certificate
            using (var powerShell = System.Management.Automation.PowerShell.Create())
            {
                var notAfter = DateTime.Now.AddYears(expDateYears).ToLongDateString();
                var assemPath = Assembly.GetCallingAssembly().Location;
                var fileInfo = new FileInfo(assemPath);
                // This adds certificate to Personal and Intermediate Certification Authority
                var rootAuthorityName = "KliveAPI";
                string rootFriendlyName = "Klive API";
                System.IO.File.Create(rootAuthorityCrtPath).Close();
                System.IO.File.Create(rootAuthorityPfxPath).Close();
                System.IO.File.Create(myGatewayCrtPath).Close();
                System.IO.File.Create(myGatewayPfxPath).Close();

                // Create the root certificate
                var rootAuthorityScript =
                    $"$rootAuthority = New-SelfSignedCertificate" +
                    $" -DnsName '{rootAuthorityName}'" +
                    $" -NotAfter '{notAfter}'" +
                    $" -CertStoreLocation cert:\\LocalMachine\\My" +
                    $" -FriendlyName '{rootFriendlyName}'" +
                    $" -KeyUsage DigitalSignature,CertSign";
                powerShell.AddScript(rootAuthorityScript);

                // Export CRT file
                var exportAuthorityCrtScript =
                    $"$rootAuthorityPath = 'cert:\\localMachine\\my\\' + $rootAuthority.thumbprint;" +
                    $"Export-Certificate" +
                    $" -Cert $rootAuthorityPath" +
                    $" -FilePath {rootAuthorityCrtPath}";
                powerShell.AddScript(exportAuthorityCrtScript);

                // Export PFX file
                var exportAuthorityPfxScript =
                    $"$pwd = ConvertTo-SecureString -String '{password}' -Force -AsPlainText;" +
                    $"Export-PfxCertificate" +
                    $" -Cert $rootAuthorityPath" +
                    $" -FilePath '{rootAuthorityPfxPath}'" +
                    $" -Password $pwd";
                powerShell.AddScript(exportAuthorityPfxScript);

                // Create the self-signed certificate, signed using the above certificate
                var gatewayAuthorityName = "KliveAPIService";
                var gatewayFriendlyName = "Klive API Service";
                var gatewayAuthorityScript =
                    $"$rootcert = ( Get-ChildItem -Path $rootAuthorityPath );" +
                    $"$gatewayCert = New-SelfSignedCertificate" +
                    $" -DnsName '{gatewayAuthorityName}'" +
                    $" -NotAfter '{notAfter}'" +
                    $" -certstorelocation cert:\\localmachine\\my" +
                    $" -Signer $rootcert" +
                    $" -FriendlyName '{gatewayFriendlyName}'" +
                    $" -KeyUsage KeyEncipherment,DigitalSignature";
                powerShell.AddScript(gatewayAuthorityScript);

                // Export new certificate public key as a CRT file
                var exportCrtScript =
                    $"$gatewayCertPath = 'cert:\\localMachine\\my\\' + $gatewayCert.thumbprint;" +
                    $"Export-Certificate" +
                    $" -Cert $gatewayCertPath" +
                    $" -FilePath {myGatewayCrtPath}";
                powerShell.AddScript(exportCrtScript);

                // Export the new certificate as a PFX file
                var exportPfxScript =
                    $"Export-PfxCertificate" +
                    $" -Cert $gatewayCertPath" +
                    $" -FilePath {myGatewayPfxPath}" +
                    $" -Password $pwd"; // Use the previous password
                powerShell.AddScript(exportPfxScript);



                var output = await powerShell.InvokeAsync();

                parent.ServiceLog("Created and installed certificate.");
            }
        }
    }
}