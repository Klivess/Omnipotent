using DSharpPlus.Entities;
using InstagramApiSharp;
using InstagramApiSharp.API;
using InstagramApiSharp.Classes;
using InstagramApiSharp.Classes.Models;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Notifications;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramLoginHandler
    {
        private readonly OmniGram service;
        private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);
        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(2);

        private static readonly TimeSpan[] BackoffIntervals = new[]
        {
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(4),
            TimeSpan.FromHours(12)
        };

        public OmniGramLoginHandler(OmniGram service)
        {
            this.service = service;
        }

        public async Task<OmniGramLoginStatus> HandleLoginAsync(IInstaApi instaApi, OmniGramAccount account)
        {
            account.LastLoginAttemptTime = DateTime.UtcNow;
            try
            {
                await service.ServiceLog($"[OmniGram] Attempting login for {account.Username}...");
                var loginResult = await instaApi.LoginAsync();

                if (!loginResult.Succeeded)
                {
                    return await HandleLoginResult(instaApi, account, loginResult.Value, loginResult.Info?.Message);
                }

                return await OnLoginSuccess(instaApi, account);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Exception during login for {account.Username}");
                account.LoginStatus = OmniGramLoginStatus.Error;
                account.LoginErrorMessage = ex.Message;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.Error;
            }
        }

        private async Task<OmniGramLoginStatus> HandleLoginResult(IInstaApi instaApi, OmniGramAccount account, InstaLoginResult result, string message)
        {
            switch (result)
            {
                case InstaLoginResult.Success:
                    return await OnLoginSuccess(instaApi, account);

                case InstaLoginResult.TwoFactorRequired:
                    account.LoginStatus = OmniGramLoginStatus.Awaiting2FA;
                    return await Handle2FAAsync(instaApi, account);

                case InstaLoginResult.ChallengeRequired:
                    account.LoginStatus = OmniGramLoginStatus.AwaitingChallenge;
                    return await HandleChallengeAsync(instaApi, account);

                case InstaLoginResult.BadPassword:
                    account.LoginStatus = OmniGramLoginStatus.CredentialsInvalid;
                    account.LoginErrorMessage = "Bad password";
                    await NotifyDiscord($"OmniGram Login Failed: {account.Username}",
                        $"Bad password for account **{account.Username}**. Update credentials and re-add the account.",
                        DiscordColor.Red);
                    return OmniGramLoginStatus.CredentialsInvalid;

                case InstaLoginResult.InvalidUser:
                    account.LoginStatus = OmniGramLoginStatus.CredentialsInvalid;
                    account.LoginErrorMessage = "Invalid user / account not found";
                    await NotifyDiscord($"OmniGram Login Failed: {account.Username}",
                        $"Instagram user **{account.Username}** does not exist or is invalid.",
                        DiscordColor.Red);
                    return OmniGramLoginStatus.CredentialsInvalid;

                case InstaLoginResult.InactiveUser:
                    account.LoginStatus = OmniGramLoginStatus.AccountDisabled;
                    account.LoginErrorMessage = "Account is deactivated/disabled by Instagram";
                    await NotifyDiscord($"OmniGram Account Disabled: {account.Username}",
                        $"Instagram account **{account.Username}** is deactivated or disabled.",
                        DiscordColor.Orange);
                    return OmniGramLoginStatus.AccountDisabled;

                case InstaLoginResult.LimitError:
                    account.LoginStatus = OmniGramLoginStatus.RateLimited;
                    account.LoginErrorMessage = "Rate limited by Instagram";
                    await NotifyDiscord($"OmniGram Rate Limited: {account.Username}",
                        $"Instagram rate limited login for **{account.Username}**. Retrying in 30 minutes.",
                        DiscordColor.Orange);
                    await ScheduleRetry(account);
                    return OmniGramLoginStatus.RateLimited;

                case InstaLoginResult.CheckpointLoggedOut:
                    account.LoginStatus = OmniGramLoginStatus.CheckpointRequired;
                    return await HandleChallengeAsync(instaApi, account);

                case InstaLoginResult.Exception:
                default:
                    account.LoginStatus = OmniGramLoginStatus.Error;
                    account.LoginErrorMessage = message ?? "Unknown login error";
                    await service.ServiceLogError($"[OmniGram] Login error for {account.Username}: {message}");
                    await NotifyDiscord($"OmniGram Login Error: {account.Username}",
                        $"Login failed with error: {message ?? "Unknown"}. Scheduling retry.",
                        DiscordColor.Red);
                    await ScheduleRetry(account);
                    return OmniGramLoginStatus.Error;
            }
        }

        private async Task<OmniGramLoginStatus> Handle2FAAsync(IInstaApi instaApi, OmniGramAccount account)
        {
            try
            {
                var twoFactorInfoResult = await instaApi.GetTwoFactorInfoAsync();
                var twoFactorInfo = twoFactorInfoResult.Succeeded ? twoFactorInfoResult.Value : null;
                if (twoFactorInfo == null)
                {
                    await service.ServiceLogError($"[OmniGram] 2FA required for {account.Username} but no TwoFactorInfo available.");
                    account.LoginStatus = OmniGramLoginStatus.Error;
                    account.LoginErrorMessage = "2FA info unavailable";
                    return OmniGramLoginStatus.Error;
                }

                bool hasSms = !string.IsNullOrEmpty(twoFactorInfo.ObfuscatedPhoneNumber);
                bool hasTotp = false; // TOTP detection requires TwoFactorLogin from login response; default to SMS prompt
                int verifyMethod = 0; // 0 = authenticator, 3 = SMS

                string promptDesc;
                if (hasTotp && hasSms)
                {
                    var method = await PromptButtons(
                        $"OmniGram 2FA: {account.Username}",
                        $"Two-factor authentication required for **{account.Username}**.\nChoose verification method:",
                        new Dictionary<string, DiscordButtonStyle>
                        {
                            { "🔐 Authenticator App", DiscordButtonStyle.Primary },
                            { "📱 SMS", DiscordButtonStyle.Secondary }
                        });

                    if (method == null) return await On2FATimeout(account);
                    verifyMethod = method.Contains("SMS") ? 3 : 0;
                    promptDesc = verifyMethod == 3
                        ? $"SMS sent to {twoFactorInfo.ObfuscatedPhoneNumber}. Enter code for **{account.Username}**:"
                        : $"Enter authenticator app code for **{account.Username}**:";
                }
                else if (hasSms)
                {
                    verifyMethod = 3;
                    promptDesc = $"SMS code sent to {twoFactorInfo.ObfuscatedPhoneNumber}. Enter code for **{account.Username}**:";
                }
                else
                {
                    verifyMethod = 0;
                    promptDesc = $"Enter authenticator app code for **{account.Username}**:";
                }

                // First attempt
                var code = await PromptText($"OmniGram 2FA Code: {account.Username}", promptDesc, "2FA Code", "Enter 6-digit code");
                if (code == null) return await On2FATimeout(account);

                var twoFactorResult = await instaApi.TwoFactorLoginAsync(code.Trim(), verifyMethod);

                if (twoFactorResult.Succeeded)
                    return await OnLoginSuccess(instaApi, account);

                switch (twoFactorResult.Value)
                {
                    case InstaLoginTwoFactorResult.Success:
                        return await OnLoginSuccess(instaApi, account);

                    case InstaLoginTwoFactorResult.InvalidCode:
                        // One retry
                        var retryCode = await PromptText($"OmniGram 2FA Retry: {account.Username}",
                            $"Invalid code. Try again for **{account.Username}**:", "2FA Code", "Enter 6-digit code");
                        if (retryCode == null) return await On2FATimeout(account);

                        var retryResult = await instaApi.TwoFactorLoginAsync(retryCode.Trim(), verifyMethod);
                        if (retryResult.Succeeded || retryResult.Value == InstaLoginTwoFactorResult.Success)
                            return await OnLoginSuccess(instaApi, account);

                        account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                        account.LoginErrorMessage = "2FA code invalid after retry";
                        await ScheduleRetry(account);
                        return OmniGramLoginStatus.ChallengeTimedOut;

                    case InstaLoginTwoFactorResult.CodeExpired:
                        await NotifyDiscord($"OmniGram 2FA Expired: {account.Username}",
                            $"2FA code expired for **{account.Username}**. Retrying full 2FA flow.",
                            DiscordColor.Orange);
                        return await Handle2FAAsync(instaApi, account); // one recursive retry

                    case InstaLoginTwoFactorResult.ChallengeRequired:
                        account.LoginStatus = OmniGramLoginStatus.AwaitingChallenge;
                        return await HandleChallengeAsync(instaApi, account);

                    default:
                        account.LoginStatus = OmniGramLoginStatus.Error;
                        account.LoginErrorMessage = $"2FA failed: {twoFactorResult.Info?.Message}";
                        await ScheduleRetry(account);
                        return OmniGramLoginStatus.Error;
                }
            }
            catch (TimeoutException)
            {
                return await On2FATimeout(account);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] 2FA exception for {account.Username}");
                account.LoginStatus = OmniGramLoginStatus.Error;
                account.LoginErrorMessage = ex.Message;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.Error;
            }
        }

        private async Task<OmniGramLoginStatus> HandleChallengeAsync(IInstaApi instaApi, OmniGramAccount account)
        {
            try
            {
                // Reset any stale challenge state before requesting new verification methods
                try { await instaApi.ResetChallengeRequireVerifyMethodAsync(); }
                catch { /* not critical — may fail if no prior challenge was active */ }

                var challengeResult = await instaApi.GetChallengeRequireVerifyMethodAsync();
                if (!challengeResult.Succeeded)
                {
                    await service.ServiceLogError($"[OmniGram] Failed to get challenge methods for {account.Username}: {challengeResult.Info?.Message}");
                    account.LoginStatus = OmniGramLoginStatus.Error;
                    account.LoginErrorMessage = $"Challenge method retrieval failed: {challengeResult.Info?.Message}";
                    await ScheduleRetry(account);
                    return OmniGramLoginStatus.Error;
                }

                var challenge = challengeResult.Value;
                var stepName = challenge.StepName?.ToLowerInvariant() ?? string.Empty;

                if (challenge.SubmitPhoneRequired)
                {
                    return await HandleSubmitPhoneChallenge(instaApi, account);
                }

                return stepName switch
                {
                    "select_verify_method" => await HandleSelectVerifyMethodChallenge(instaApi, account, challenge),
                    "delta_login_review" => await HandleDeltaLoginReview(instaApi, account),
                    _ => await HandleUnknownChallenge(account, stepName, challenge)
                };
            }
            catch (TimeoutException)
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                account.LoginErrorMessage = "Challenge prompt timed out";
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Challenge exception for {account.Username}");
                account.LoginStatus = OmniGramLoginStatus.Error;
                account.LoginErrorMessage = ex.Message;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.Error;
            }
        }

        private async Task<OmniGramLoginStatus> HandleSelectVerifyMethodChallenge(
            IInstaApi instaApi, OmniGramAccount account, InstaChallengeRequireVerifyMethod challenge)
        {
            bool hasEmail = !string.IsNullOrEmpty(challenge.StepData?.Email);
            bool hasPhone = !string.IsNullOrEmpty(challenge.StepData?.PhoneNumber);

            bool useEmail;
            if (hasEmail && hasPhone)
            {
                var choice = await PromptButtons(
                    $"OmniGram Challenge: {account.Username}",
                    $"Instagram challenge for **{account.Username}**.\nChoose verification method:",
                    new Dictionary<string, DiscordButtonStyle>
                    {
                        { $"📧 Email ({challenge.StepData.Email})", DiscordButtonStyle.Primary },
                        { $"📱 Phone ({challenge.StepData.PhoneNumber})", DiscordButtonStyle.Secondary }
                    });

                if (choice == null)
                {
                    account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                    await ScheduleRetry(account);
                    return OmniGramLoginStatus.ChallengeTimedOut;
                }
                useEmail = choice.Contains("Email");
            }
            else
            {
                useEmail = hasEmail;
            }

            // Request verification code
            if (useEmail)
                await instaApi.RequestVerifyCodeToEmailForChallengeRequireAsync();
            else
                await instaApi.RequestVerifyCodeToSMSForChallengeRequireAsync();

            string destination = useEmail ? challenge.StepData?.Email : challenge.StepData?.PhoneNumber;
            var code = await PromptText(
                $"OmniGram Verification: {account.Username}",
                $"Verification code sent to **{destination}** for **{account.Username}**.\nEnter the code:",
                "Verification Code", "Enter code");

            if (code == null)
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }

            var verifyResult = await instaApi.VerifyCodeForChallengeRequireAsync(code.Trim());
            if (verifyResult.Succeeded && verifyResult.Value == InstaLoginResult.Success)
                return await OnLoginSuccess(instaApi, account);

            // First failure — offer resend
            var resendChoice = await PromptButtons(
                $"OmniGram Code Failed: {account.Username}",
                $"Verification code failed for **{account.Username}**.",
                new Dictionary<string, DiscordButtonStyle>
                {
                    { "🔄 Resend Code", DiscordButtonStyle.Primary },
                    { "❌ Cancel", DiscordButtonStyle.Danger }
                });

            if (resendChoice == null || resendChoice.Contains("Cancel"))
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }

            // Resend
            if (useEmail)
                await instaApi.RequestVerifyCodeToEmailForChallengeRequireAsync(replayChallenge: true);
            else
                await instaApi.RequestVerifyCodeToSMSForChallengeRequireAsync(replayChallenge: true);

            var retryCode = await PromptText(
                $"OmniGram Verification Retry: {account.Username}",
                $"New code sent to **{destination}**. Enter it for **{account.Username}**:",
                "Verification Code", "Enter code");

            if (retryCode == null)
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }

            var retryResult = await instaApi.VerifyCodeForChallengeRequireAsync(retryCode.Trim());
            if (retryResult.Succeeded && retryResult.Value == InstaLoginResult.Success)
                return await OnLoginSuccess(instaApi, account);

            account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
            account.LoginErrorMessage = "Challenge verification failed after retry";
            await ScheduleRetry(account);
            return OmniGramLoginStatus.ChallengeTimedOut;
        }

        private async Task<OmniGramLoginStatus> HandleDeltaLoginReview(IInstaApi instaApi, OmniGramAccount account)
        {
            var choice = await PromptButtons(
                $"OmniGram Suspicious Login: {account.Username}",
                $"Instagram flagged a suspicious login for **{account.Username}**. Was this you?",
                new Dictionary<string, DiscordButtonStyle>
                {
                    { "✅ It Was Me", DiscordButtonStyle.Success },
                    { "❌ It Wasn't Me", DiscordButtonStyle.Danger }
                });

            if (choice == null)
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }

            if (choice.Contains("Wasn't"))
            {
                account.LoginStatus = OmniGramLoginStatus.Error;
                account.LoginErrorMessage = "Login denied by user (suspicious login review)";
                await service.ServiceLog($"[OmniGram] Suspicious login denied by user for {account.Username}.");
                return OmniGramLoginStatus.Error;
            }

            var acceptResult = await instaApi.AcceptChallengeAsync();
            if (acceptResult.Succeeded)
                return await OnLoginSuccess(instaApi, account);

            account.LoginStatus = OmniGramLoginStatus.Error;
            account.LoginErrorMessage = $"Accept challenge failed: {acceptResult.Info?.Message}";
            await ScheduleRetry(account);
            return OmniGramLoginStatus.Error;
        }

        private async Task<OmniGramLoginStatus> HandleSubmitPhoneChallenge(IInstaApi instaApi, OmniGramAccount account)
        {
            var phone = await PromptText(
                $"OmniGram Phone Required: {account.Username}",
                $"Instagram requires a phone number for **{account.Username}**.\nEnter phone number (with country code, e.g. +44...):",
                "Phone Number", "+44...");

            if (phone == null)
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }

            var submitResult = await instaApi.SubmitPhoneNumberForChallengeRequireAsync(phone.Trim());
            if (!submitResult.Succeeded)
            {
                account.LoginStatus = OmniGramLoginStatus.Error;
                account.LoginErrorMessage = $"Phone submission failed: {submitResult.Info?.Message}";
                await NotifyDiscord($"OmniGram Phone Challenge Failed: {account.Username}",
                    $"Phone number submission failed for **{account.Username}**: {submitResult.Info?.Message}",
                    DiscordColor.Red);
                return OmniGramLoginStatus.Error;
            }

            // Now verify with SMS code
            var code = await PromptText(
                $"OmniGram SMS Verification: {account.Username}",
                $"SMS code sent to **{phone.Trim()}** for **{account.Username}**. Enter the code:",
                "SMS Code", "Enter code");

            if (code == null)
            {
                account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
                await ScheduleRetry(account);
                return OmniGramLoginStatus.ChallengeTimedOut;
            }

            var verifyResult = await instaApi.VerifyCodeForChallengeRequireAsync(code.Trim());
            if (verifyResult.Succeeded && verifyResult.Value == InstaLoginResult.Success)
                return await OnLoginSuccess(instaApi, account);

            account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
            account.LoginErrorMessage = "SMS verification failed after phone submission";
            await ScheduleRetry(account);
            return OmniGramLoginStatus.ChallengeTimedOut;
        }

        private async Task<OmniGramLoginStatus> HandleUnknownChallenge(OmniGramAccount account, string stepName, InstaChallengeRequireVerifyMethod challenge)
        {
            var details = JsonConvert.SerializeObject(challenge, Formatting.Indented);
            await service.ServiceLogError($"[OmniGram] Unknown challenge type '{stepName}' for {account.Username}. Details: {details}");
            await NotifyDiscord($"OmniGram Unknown Challenge: {account.Username}",
                $"Unknown Instagram challenge type **'{stepName}'** for **{account.Username}**.\nManual intervention required.\n```json\n{details.Substring(0, Math.Min(details.Length, 1500))}```",
                DiscordColor.Red);

            account.LoginStatus = OmniGramLoginStatus.Error;
            account.LoginErrorMessage = $"Unknown challenge type: {stepName}";
            return OmniGramLoginStatus.Error;
        }

        // ── Success & Retry Helpers ──

        private async Task<OmniGramLoginStatus> OnLoginSuccess(IInstaApi instaApi, OmniGramAccount account)
        {
            account.LoginStatus = OmniGramLoginStatus.LoggedIn;
            account.LastLoginTime = DateTime.UtcNow;
            account.LoginRetryCount = 0;
            account.LoginErrorMessage = null;

            // Persist device fingerprint so it survives session clears and re-logins
            try
            {
                var stateJson = instaApi.GetStateDataAsString();
                var stateObj = Newtonsoft.Json.Linq.JObject.Parse(stateJson);
                var deviceToken = stateObj["DeviceInfo"];
                if (deviceToken != null)
                    account.DeviceData = deviceToken.ToString(Formatting.None);
            }
            catch { /* non-critical */ }

            // Persist session
            try
            {
                var state = instaApi.GetStateDataAsStream();
                var sessionPath = Path.Combine(OmniPaths.GlobalPaths.OmniGramSessionsDirectory, $"{account.Username}.session");
                Directory.CreateDirectory(Path.GetDirectoryName(sessionPath));
                using var fileStream = File.Create(sessionPath);
                await state.CopyToAsync(fileStream);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Failed to persist session for {account.Username}");
            }

            // Update profile info
            try
            {
                var currentUser = await instaApi.GetCurrentUserAsync();
                if (currentUser.Succeeded)
                {
                    var userInfoResult = await instaApi.UserProcessor.GetUserInfoByIdAsync(currentUser.Value.Pk);
                    if (userInfoResult.Succeeded)
                    {
                        account.FollowerCount = (int)userInfoResult.Value.FollowerCount;
                        account.FollowingCount = (int)userInfoResult.Value.FollowingCount;
                        account.MediaCount = (int)userInfoResult.Value.MediaCount;
                    }
                }
            }
            catch { /* non-critical */ }

            await service.ServiceLog($"[OmniGram] Successfully logged in as {account.Username}.");
            return OmniGramLoginStatus.LoggedIn;
        }

        private async Task<OmniGramLoginStatus> On2FATimeout(OmniGramAccount account)
        {
            account.LoginStatus = OmniGramLoginStatus.ChallengeTimedOut;
            account.LoginErrorMessage = "2FA prompt timed out";
            await ScheduleRetry(account);
            return OmniGramLoginStatus.ChallengeTimedOut;
        }

        public async Task ScheduleRetry(OmniGramAccount account)
        {
            // Check if within retry window
            if (account.LastLoginAttemptTime.HasValue &&
                (DateTime.UtcNow - account.LastLoginAttemptTime.Value) < RetryWindow &&
                account.LoginRetryCount >= MaxRetries)
            {
                await service.ServiceLog($"[OmniGram] Max retries ({MaxRetries}) reached for {account.Username} within retry window. Requires manual intervention.");
                account.LoginStatus = OmniGramLoginStatus.Error;
                account.LoginErrorMessage = $"Max login retries ({MaxRetries}) exceeded";
                await NotifyDiscord($"OmniGram Login Failed: {account.Username}",
                    $"Max login retries exceeded for **{account.Username}**. Manual re-add required.",
                    DiscordColor.Red);
                return;
            }

            int retryIndex = Math.Min(account.LoginRetryCount, BackoffIntervals.Length - 1);
            var delay = BackoffIntervals[retryIndex];
            account.LoginRetryCount++;

            await service.ServiceLog($"[OmniGram] Scheduling login retry #{account.LoginRetryCount} for {account.Username} in {delay.TotalMinutes} minutes.");
            await service.ServiceCreateScheduledTask(
                DateTime.Now.Add(delay),
                $"OmniGramRetryLogin_{account.Username}",
                "OmniGram Login",
                $"Retry login for {account.Username} (attempt #{account.LoginRetryCount})",
                false,
                account.AccountId);
        }

        // ── Wrapper for runtime challenge handling on any API call ──

        public async Task<IResult<T>> ExecuteWithChallengeHandlingAsync<T>(
            IInstaApi instaApi, OmniGramAccount account, Func<Task<IResult<T>>> apiCall)
        {
            var result = await apiCall();

            if (result.Succeeded)
                return result;

            // Check if the failure is a challenge/login-required response
            var responseType = result.Info?.ResponseType ?? ResponseType.Unknown;
            var errorMsg = (result.Info?.Message ?? "").ToLowerInvariant();
            bool isCheckpoint = responseType == ResponseType.ChallengeRequired
                || responseType == ResponseType.LoginRequired
                || errorMsg.Contains("checkpoint_required")
                || errorMsg.Contains("challenge_required")
                || errorMsg.Contains("login_required");

            if (isCheckpoint)
            {
                await service.ServiceLog($"[OmniGram] Runtime challenge triggered for {account.Username} during API call. Entering challenge flow.");
                account.IsPaused = true;
                var loginStatus = await HandleLoginAsync(instaApi, account);
                if (loginStatus == OmniGramLoginStatus.LoggedIn)
                {
                    account.IsPaused = false;
                    // Retry the original call once
                    return await apiCall();
                }
            }

            return result;
        }

        // ── Discord Prompt Helpers ──

        private async Task NotifyDiscord(string title, string description, DiscordColor color)
        {
            try
            {
                var notificationsService = (NotificationsService)(await service.GetServicesByType<NotificationsService>())?[0];
                if (notificationsService == null) return;

                // Use buttons prompt with just an OK button to send a notification embed
                await service.ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>(
                    "SendMessageToKlives",
                    Omnipotent.Services.KliveBot_Discord.KliveBotDiscord.MakeSimpleEmbed(title, description, color));
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniGram] Failed to send Discord notification");
            }
        }

        private async Task<string> PromptText(string title, string description, string modalTitle, string placeholder)
        {
            try
            {
                var notificationsService = (NotificationsService)(await service.GetServicesByType<NotificationsService>())?[0];
                if (notificationsService == null) return null;
                return await notificationsService.SendTextPromptToKlivesDiscord(title, description, PromptTimeout, modalTitle, placeholder);
            }
            catch (TimeoutException)
            {
                await service.ServiceLog($"[OmniGram] Discord text prompt timed out: {title}");
                return null;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Discord text prompt failed: {title}");
                return null;
            }
        }

        private async Task<string> PromptButtons(string title, string description, Dictionary<string, DiscordButtonStyle> buttons)
        {
            try
            {
                var notificationsService = (NotificationsService)(await service.GetServicesByType<NotificationsService>())?[0];
                if (notificationsService == null) return null;
                return await notificationsService.SendButtonsPromptToKlivesDiscord(title, description, buttons, PromptTimeout);
            }
            catch (TimeoutException)
            {
                await service.ServiceLog($"[OmniGram] Discord button prompt timed out: {title}");
                return null;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Discord button prompt failed: {title}");
                return null;
            }
        }
    }
}
