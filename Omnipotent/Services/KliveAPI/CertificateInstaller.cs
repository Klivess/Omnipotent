using System.Reflection;
using System.Management;
using System.Management.Automation;
using Omnipotent.Data_Handling;
using Microsoft.AspNetCore.Components.Web;
using Newtonsoft.Json;
using static System.Net.WebRequestMethods;
using System.Security.Cryptography;
using Certes;
using Certes.Acme;
using DSharpPlus;
using System.Net;
using Certes.Acme.Resource;

namespace Omnipotent.Services.KliveAPI
{
    public class CertificateInstaller
    {
        KliveAPI parent;
        public static string saveDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesAPICertificateDirectory);
        public static string pemSaveDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesACMEAPICertificateDirectory);
        public string rootAuthorityCrtPath = Path.Combine(saveDir, "KliveAPI.crt");
        public string rootAuthorityPfxPath = Path.Combine(saveDir, "KliveAPI.pfx");
        public string myGatewayCrtPath = Path.Combine(saveDir, "KliveAPIGateway.crt");
        public string myGatewayPfxPath = Path.Combine(saveDir, "KliveAPIGateway.pfx");
        public string myPemPath = Path.Combine(pemSaveDir, "klivedev.gmail.com(klive.dev).txt");

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
            if (OmniPaths.CheckIfOnServer())
            {
                await InstallProductionCert(expDateYears, password, issuedBy);
            }
            else
            {
                await InstallLocalCert(expDateYears, password, issuedBy);
            }
        }

        public async Task InstallLocalCert(int expDateYears, string password, string issuedBy)
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
                rootAuthorityCrtPath = Path.Combine(saveDir, "KliveAPILocal.crt");
                rootAuthorityPfxPath = Path.Combine(saveDir, "KliveAPILocal.pfx");

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
        public async Task InstallProductionCert(int expDateYears, string password, string issuedBy)
        {
            IAccountContext account = null;
            AcmeContext acme = null;

            if (!System.IO.File.Exists(myPemPath))
            {
                parent.ServiceLog("Creating ACME account and context.");
                acme = new AcmeContext(WellKnownServers.LetsEncryptStagingV2);
                account = await acme.NewAccount("klivesdev@gmail.com", true);

                // Save the account key for later use
                var pemKey = acme.AccountKey.ToPem();

                await parent.GetDataHandler().WriteToFile(myPemPath, pemKey);
                parent.ServiceLog("Done making ACME account and context.");
            }
            else
            {
                try
                {
                    parent.ServiceLog("Loading ACME account and context from file.");
                    string pem = await parent.GetDataHandler().ReadDataFromFile(myPemPath);

                    // Load the saved account key
                    var accountKey = KeyFactory.FromPem(pem);
                    acme = new AcmeContext(WellKnownServers.LetsEncryptStagingV2, accountKey);
                    account = await acme.Account();
                    parent.ServiceLog("Done loading ACME account and context.");
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "Asking Klives on what to do next.");
                    Dictionary<string, ButtonStyle> dict = new Dictionary<string, ButtonStyle>
                        {
                            {"Yes", ButtonStyle.Primary },
                            {"No", ButtonStyle.Danger },
                        };
                    string resp = await parent.serviceManager.GetNotificationsService().SendButtonsPromptToKlivesDiscord("Error loading PEM key for Let's Encrypt.",
                        "Should I delete the existing PEM key and create another?", dict, TimeSpan.FromDays(7));
                    if (resp == "Yes")
                    {
                        System.IO.File.Delete(myPemPath);
                        await InstallProductionCert(expDateYears, password, issuedBy);
                        return;
                    }
                    else
                    {
                        await InstallLocalCert(expDateYears, password, issuedBy);
                        return;
                    }
                }
            }

            //Order
            parent.ServiceLog("Creating ACME order.");
            string pathOfOrder = Path.Combine(OmniPaths.GlobalPaths.KlivesACMEAPICertificateDirectory, "currentActiveChallenge.txt");



            IChallengeContext dnsChallenge = null;
            IOrderContext order = await acme.NewOrder(new[] { "*.klive.dev" });
            parent.ServiceLog("Creating new ACME challenge.");
            var authz = (await order.Authorizations()).First();
            dnsChallenge = await authz.Dns();
            var dnsTxt = acme.AccountKey.DnsTxt(dnsChallenge.Token);
            parent.ServiceLog("Getting Klives to fulfill DNS record.");

            //Get Klives to confirm
            try
            {
                Dictionary<string, ButtonStyle> dict = new Dictionary<string, ButtonStyle>
                        {
                            {"Done", ButtonStyle.Primary },
                        };
                string addedDNSText = await parent.serviceManager.GetNotificationsService().SendButtonsPromptToKlivesDiscord($"Add a DNS record to {KliveAPI.domainName}.",
                    "Please add a new DNS record so that Omnipotent can solve this challenge and acquire the certificate needed for HTTPS.\n\n" +
                    "Type: TXT\n" +
                    $"Name: _acme-challenge.{KliveAPI.domainName}\n" +
                    $"Value: {dnsTxt}", dict, TimeSpan.FromDays(7));
                parent.ServiceLog("Klives has done creating the DNS record.");
            }
            catch (TimeoutException ex)
            {
                await InstallLocalCert(expDateYears, password, issuedBy);
                return;
            }

            //Wait for DNS to propagate
            await parent.serviceManager.GetKliveBotDiscordService().SendMessageToKlives("Waiting 1 hour for DNS to propagate...");

            //Ask Klives if to wait for DNS to propagate
            try
            {
                Dictionary<string, ButtonStyle> dict = new Dictionary<string, ButtonStyle>
                        {
                            {"Yes, wait 1 hour.", ButtonStyle.Primary },
                            {"No, don't wait.", ButtonStyle.Primary }
                        };
                string addedDNSText = await parent.serviceManager.GetNotificationsService().SendButtonsPromptToKlivesDiscord($"Should I wait 1 hour for DNS to propagate?",
                    $"Click No if the dns TXT value '{dnsTxt}' is already propagated.", dict, TimeSpan.FromDays(7));
                if (addedDNSText == "Yes, wait 1 hour.")
                {
                    parent.ServiceLog("Klives has chosen to wait 1 hour for the DNS to propagate.");
                    await Task.Delay(TimeSpan.FromHours(1));
                }
            }
            catch (TimeoutException ex)
            {
                await InstallLocalCert(expDateYears, password, issuedBy);
                return;
            }

            //Validate
            parent.ServiceLog("Validating that challenge is solved.");
            await parent.serviceManager.GetKliveBotDiscordService().SendMessageToKlives("Validating Challenge for KliveAPI...");
            var challengeResult = await dnsChallenge.Validate();

            //challengeResult.Status.Value always returns "pending" for some reason, so we have to wait for it to change
            await Task.Delay(TimeSpan.FromSeconds(30));

            challengeResult = await dnsChallenge.Resource();

            var attempts = 10;

            while (attempts > 0 && challengeResult.Status == ChallengeStatus.Pending || challengeResult.Status == ChallengeStatus.Processing)
            {
                challengeResult = await dnsChallenge.Resource();
                await Task.Delay(500);
                attempts--;
            }

            if (challengeResult.Status == Certes.Acme.Resource.ChallengeStatus.Invalid)
            {
                await parent.ServiceLogError(new Exception("DNS challenge failed to validate."));
                await parent.serviceManager.GetKliveBotDiscordService().SendMessageToKlives("DNS challenge failed to validate...");
                await InstallLocalCert(expDateYears, password, issuedBy);
                return;
            }
            else if (challengeResult.Status == Certes.Acme.Resource.ChallengeStatus.Valid)
            {
                try
                {
                    parent.ServiceLog("Challenge is solved, creating certificate.");
                    await parent.serviceManager.GetKliveBotDiscordService().SendMessageToKlives("Challenge is solved, creating certificate.");
                    //Generate certificate
                    var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
                    var cert = await order.Generate(new CsrInfo
                    {
                        CountryName = "UK",
                        State = "NoState",
                        Locality = "London",
                        Organization = "KlivesManagement",
                        OrganizationUnit = "Klives",
                        CommonName = "*.klive.dev",
                    }, privateKey);

                    var pfxBuilder = cert.ToPfx(privateKey);
                    var pfx = pfxBuilder.Build("klive.devKliveAPI", password);
                    //Save .pfx to file
                    System.IO.File.Create(rootAuthorityPfxPath).Close();
                    await parent.GetDataHandler().WriteBytesToFile(rootAuthorityPfxPath, pfx);
                    parent.ServiceLog("ACME Certificate created for !" + KliveAPI.domainName);
                    await parent.serviceManager.GetKliveBotDiscordService().SendMessageToKlives("ACME Certificate created for !" + KliveAPI.domainName);
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError(ex);
                    await InstallLocalCert(expDateYears, password, issuedBy);
                    return;
                }
            }
            else
            {
                await parent.serviceManager.GetKliveBotDiscordService().SendMessageToKlives($"ACME challenge validation was neither valid or invalid?? Challenge Info:\n{JsonConvert.SerializeObject(challengeResult)}");
                parent.ServiceLog($"ACME challenge validation was neither valid or invalid?? Challenge Info:\n{JsonConvert.SerializeObject(challengeResult)}");
                await InstallLocalCert(expDateYears, password, issuedBy);
            }
        }
    }
}