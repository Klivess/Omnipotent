using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblrStore
    {
        private readonly OmniTumblr parent;

        public ConcurrentDictionary<string, OmniTumblrAccount> Accounts = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, OmniTumblrPostPlan> Posts = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, OmniTumblrCampaign> Campaigns = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, OmniTumblrServiceEvent> Events = new(StringComparer.OrdinalIgnoreCase);

        public OmniTumblrStore(OmniTumblr parent)
        {
            this.parent = parent;
        }

        public async Task Load()
        {
            await LoadAccounts();
            await LoadPosts();
            await LoadCampaigns();
            await LoadEvents();
        }

        private async Task LoadAccounts()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniTumblrAccount>(content);
                    if (obj != null)
                    {
                        Accounts[obj.AccountId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniTumblr failed loading account file: " + file);
                }
            }
        }

        private async Task LoadPosts()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniTumblrPostPlan>(content);
                    if (obj != null)
                    {
                        Posts[obj.PostId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniTumblr failed loading post file: " + file);
                }
            }
        }

        private async Task LoadCampaigns()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrCampaignsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniTumblrCampaign>(content);
                    if (obj != null)
                    {
                        Campaigns[obj.CampaignId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniTumblr failed loading campaign file: " + file);
                }
            }
        }

        private async Task LoadEvents()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrEventsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniTumblrServiceEvent>(content);
                    if (obj != null)
                    {
                        Events[obj.EventId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniTumblr failed loading event file: " + file);
                }
            }
        }

        public async Task SaveAccount(OmniTumblrAccount account)
        {
            Accounts[account.AccountId] = account;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory), account.AccountId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(account, Formatting.Indented));
        }

        public async Task SavePost(OmniTumblrPostPlan post)
        {
            Posts[post.PostId] = post;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory), post.PostId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(post, Formatting.Indented));
        }

        public async Task SaveCampaign(OmniTumblrCampaign campaign)
        {
            Campaigns[campaign.CampaignId] = campaign;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrCampaignsDirectory), campaign.CampaignId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(campaign, Formatting.Indented));
        }

        public async Task SaveCampaignSafe(OmniTumblrCampaign campaign)
        {
            if (campaign.PlannedPostIds.Count == 0)
            {
                Campaigns.TryRemove(campaign.CampaignId, out _);
                string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrCampaignsDirectory), campaign.CampaignId + ".json");
                await parent.GetDataHandler().DeleteFile(path);
                return;
            }

            await SaveCampaign(campaign);
        }

        public async Task SaveEvent(OmniTumblrServiceEvent serviceEvent)
        {
            Events[serviceEvent.EventId] = serviceEvent;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrEventsDirectory), serviceEvent.EventId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(serviceEvent, Formatting.Indented));
        }

        public async Task DeleteAccount(OmniTumblrAccount account)
        {
            Accounts.TryRemove(account.AccountId, out _);
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory), account.AccountId + ".json");
            await parent.GetDataHandler().DeleteFile(path);
        }

        public async Task DeletePost(OmniTumblrPostPlan post)
        {
            Posts.TryRemove(post.PostId, out _);
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory), post.PostId + ".json");
            await parent.GetDataHandler().DeleteFile(path);
        }

        public List<OmniTumblrServiceEvent> GetRecentEvents(int take = 500)
        {
            return Events.Values
                .OrderByDescending(x => x.TimestampUtc)
                .Take(take)
                .ToList();
        }
    }
}
