namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>A thread-safe set that forgets its oldest entries past a capacity (listing-id dedupe).</summary>
    public sealed class RecentIdSet
    {
        private readonly HashSet<string> set = new(StringComparer.Ordinal);
        private readonly Queue<string> order = new();
        private readonly int capacity;
        private readonly object gate = new();

        public RecentIdSet(int capacity)
        {
            this.capacity = Math.Max(1, capacity);
        }

        /// <summary>Adds the id; false when it was already present.</summary>
        public bool Add(string id)
        {
            lock (gate)
            {
                if (!set.Add(id)) return false;
                order.Enqueue(id);
                while (order.Count > capacity) set.Remove(order.Dequeue());
                return true;
            }
        }

        public bool Contains(string id)
        {
            lock (gate) return set.Contains(id);
        }

        public int Count
        {
            get { lock (gate) return set.Count; }
        }
    }
}
