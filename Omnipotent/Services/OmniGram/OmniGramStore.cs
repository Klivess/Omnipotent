using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramStore
    {
        private readonly OmniGram parent;

        public ConcurrentDictionary<string, OmniGramAccount> Accounts = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, OmniGramPostPlan> Posts = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, OmniGramCampaign> Campaigns = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, OmniGramServiceEvent> Events = new(StringComparer.OrdinalIgnoreCase);

        public OmniGramStore(OmniGram parent)
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
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramAccountsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniGramAccount>(content);
                    if (obj != null)
                    {
                        Accounts[obj.AccountId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniGram failed loading account file: " + file);
                }
            }
        }

        private async Task LoadPosts()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramPostsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniGramPostPlan>(content);
                    if (obj != null)
                    {
                        Posts[obj.PostId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniGram failed loading post file: " + file);
                }
            }
        }

        private async Task LoadEvents()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramEventsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniGramServiceEvent>(content);
                    if (obj != null)
                    {
                        Events[obj.EventId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniGram failed loading event file: " + file);
                }
            }
        }

        private async Task LoadCampaigns()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramCampaignsDirectory);
            Directory.CreateDirectory(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    var obj = JsonConvert.DeserializeObject<OmniGramCampaign>(content);
                    if (obj != null)
                    {
                        Campaigns[obj.CampaignId] = obj;
                    }
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "OmniGram failed loading campaign file: " + file);
                }
            }
        }

        public async Task SaveAccount(OmniGramAccount account)
        {
            Accounts[account.AccountId] = account;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramAccountsDirectory), account.AccountId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(account, Formatting.Indented));
        }

        public async Task SavePost(OmniGramPostPlan post)
        {
            Posts[post.PostId] = post;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramPostsDirectory), post.PostId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(post, Formatting.Indented));
        }

        public async Task SaveCampaign(OmniGramCampaign campaign)
        {
            Campaigns[campaign.CampaignId] = campaign;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramCampaignsDirectory), campaign.CampaignId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(campaign, Formatting.Indented));
        }

        public async Task SaveEvent(OmniGramServiceEvent serviceEvent)
        {
            Events[serviceEvent.EventId] = serviceEvent;
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramEventsDirectory), serviceEvent.EventId + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(serviceEvent, Formatting.Indented));
        }

        public List<OmniGramServiceEvent> GetRecentEvents(int take = 500)
        {
            return Events.Values
                .OrderByDescending(x => x.TimestampUtc)
                .Take(take)
                .ToList();
        }
    }
}
