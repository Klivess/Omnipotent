using System.Reflection;
using System.Management;
using System.Management.Automation;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveAPI
{
    public class CertificateInstaller
    {
        public static void CreateInstallCert(int expDateYears, string password, string issuedBy)
        {
            // Create/install certificate
            using (var powerShell = System.Management.Automation.PowerShell.Create())
            {
                var notAfter = DateTime.Now.AddYears(expDateYears).ToLongDateString();
                var assemPath = Assembly.GetCallingAssembly().Location;
                var fileInfo = new FileInfo(assemPath);
                var saveDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesAPICertificateDirectory);
                // This adds certificate to Personal and Intermediate Certification Authority
                var rootAuthorityName = "KliveAPI";
                var rootFriendlyName = "Klive API";


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
                var rootAuthorityCrtPath = Path.Combine(saveDir, "KliveAPI.crt");
                var exportAuthorityCrtScript =
                    $"$rootAuthorityPath = 'cert:\\localMachine\\my\\' + $rootAuthority.thumbprint;" +
                    $"Export-Certificate" +
                    $" -Cert $rootAuthorityPath" +
                    $" -FilePath {rootAuthorityCrtPath}";
                powerShell.AddScript(exportAuthorityCrtScript);

                // Export PFX file
                var rootAuthorityPfxPath = Path.Combine(saveDir, "KliveAPI.pfx");
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
                var myGatewayCrtPath = Path.Combine(saveDir, "KliveAPIGateway.crt");
                var exportCrtScript =
                    $"$gatewayCertPath = 'cert:\\localMachine\\my\\' + $gatewayCert.thumbprint;" +
                    $"Export-Certificate" +
                    $" -Cert $gatewayCertPath" +
                    $" -FilePath {myGatewayCrtPath}";
                powerShell.AddScript(exportCrtScript);

                // Export the new certificate as a PFX file
                var myGatewayPfxPath = Path.Combine(saveDir, "KliveAPIGateway.pfx");
                var exportPfxScript =
                    $"Export-PfxCertificate" +
                    $" -Cert $gatewayCertPath" +
                    $" -FilePath {myGatewayPfxPath}" +
                    $" -Password $pwd"; // Use the previous password
                powerShell.AddScript(exportPfxScript);

                powerShell.Invoke();
            }
        }
    }
}