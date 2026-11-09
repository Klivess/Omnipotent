namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>How a roster member relates to the evidence.</summary>
    public enum RosterRole { Subject, Participant, Mentioned }

    /// <summary>One person in a prompt: a stable handle, the names they went by in this period, neutral metadata only.</summary>
    public sealed record RosterEntry(string Handle, string PersonId, RosterRole Role, IReadOnlyList<string> Names, bool Tracked, bool IsKlives);

    /// <summary>
    /// Stable, unambiguous speaker handles (fixes F190–F193): a handle is derived from the person id ("p3fa2"), so the same
    /// person has the same handle in every slice, window and run (no letters reassigned per window, no "P26" overflow), and
    /// collisions within one prompt lengthen the hash instead of renaming anyone. The roster lists every author and every
    /// resolved mention with the names they used in the period (usernames, display names, nicknames), and only neutral
    /// metadata: no trust labels in the context (F213).
    /// </summary>
    public sealed class Roster
    {
        private readonly Dictionary<string, string> handleByPerson = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> personByHandle = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RosterEntry> entries = new(StringComparer.Ordinal);

        public string HandleFor(string personId)
        {
            if (handleByPerson.TryGetValue(personId, out var h)) return h;
            string hash = Store.NaturalKeys.Sha256Hex(personId);
            for (int len = 4; len <= 16; len += 2)
            {
                string candidate = "p" + hash[..len];
                if (!personByHandle.ContainsKey(candidate))
                {
                    handleByPerson[personId] = candidate;
                    personByHandle[candidate] = personId;
                    return candidate;
                }
            }
            throw new InvalidOperationException("Could not allocate a unique handle.");
        }

        public string? PersonFor(string handle) =>
            personByHandle.TryGetValue(handle.Trim().TrimStart('@').ToLowerInvariant(), out var p) ? p : null;

        public void Add(string personId, RosterRole role, IEnumerable<string> names, bool tracked, bool isKlives)
        {
            string handle = HandleFor(personId);
            var nameList = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (entries.TryGetValue(personId, out var existing))
            {
                var stronger = (RosterRole)Math.Min((int)existing.Role, (int)role);
                entries[personId] = existing with { Role = stronger, Names = existing.Names.Concat(nameList).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(), Tracked = existing.Tracked || tracked };
                return;
            }
            entries[personId] = new RosterEntry(handle, personId, role, nameList, tracked, isKlives);
        }

        public IReadOnlyList<RosterEntry> Entries => entries.Values.OrderBy(e => e.Role).ThenBy(e => e.Handle, StringComparer.Ordinal).ToList();

        public bool Contains(string personId) => entries.ContainsKey(personId);

        public RosterEntry? Entry(string personId) => entries.GetValueOrDefault(personId);

        /// <summary>The roster as prompt text (corpus-derived names are data, fenced by the caller).</summary>
        public string Render()
        {
            var lines = new List<string>();
            foreach (var e in Entries)
            {
                string role = e.Role switch { RosterRole.Subject => "subject of this extraction", RosterRole.Participant => "participant", _ => "mentioned" };
                string names = e.Names.Count == 0 ? "(no name recorded)" : string.Join(" / ", e.Names.Select(EvidenceRenderer.Sanitize));
                var tags = new List<string> { role };
                if (e.IsKlives) tags.Add("the operator, Klives");
                if (e.Tracked && e.Role != RosterRole.Subject) tags.Add("has a profile");
                lines.Add($"{e.Handle}: {names} [{string.Join("; ", tags)}]");
            }
            return string.Join("\n", lines);
        }
    }
}
