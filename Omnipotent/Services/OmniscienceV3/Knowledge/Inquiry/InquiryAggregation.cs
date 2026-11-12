namespace Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry
{
    /// <summary>
    /// Aggregation of the K reasoning passes (plan X-inquiry step 7): the median probability, pulled towards 0.5 in proportion
    /// to how much the passes disagree, shrunk further when the devil's advocate made a verified strong case, and banded.
    /// Evidence strength counts independent, direct items (the person's own statements and facts count double, behaviour and
    /// hearsay once; one author on one day is one unit), never volume. Thin evidence yields "insufficient evidence", and the
    /// base-rate prior is reported separately, never as the deduction.
    /// </summary>
    public static class InquiryAggregation
    {
        public const int MinimumStrength = 2;

        public static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return double.NaN;
            var s = values.OrderBy(v => v).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        public static double StdDev(IReadOnlyList<double> values)
        {
            if (values.Count < 2) return 0;
            double mean = values.Average();
            return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        }

        /// <summary>(aggregated probability, disagreement) from the passes' probabilities of "yes".</summary>
        public static (double Probability, double Disagreement) Binary(IReadOnlyList<double> passProbabilities, string? devilStrength, int devilVerifiedCites)
        {
            if (passProbabilities.Count == 0) return (0.5, 0);
            double median = Median(passProbabilities.Select(p => Math.Clamp(p, 0, 1)).ToList());
            double sd = StdDev(passProbabilities);
            double p = 0.5 + (median - 0.5) * (1 - Math.Min(1, 2 * sd));
            double shrink = devilVerifiedCites >= 2 ? devilStrength switch { "strong" => 0.25, "moderate" => 0.1, _ => 0 } : 0;
            p = 0.5 + (p - 0.5) * (1 - shrink);
            return (Math.Clamp(p, 0.01, 0.99), sd);
        }

        /// <summary>An 80% band around <paramref name="p"/> from the passes' spread, widened when the curve is uncalibrated.</summary>
        public static (double Low, double High) Band(IReadOnlyList<double> passProbabilities, double p, bool calibrated)
        {
            double sd = StdDev(passProbabilities);
            double half = 1.2816 * sd + (calibrated ? 0.02 : 0.07);
            return (Math.Clamp(p - half, 0, 1), Math.Clamp(p + half, 0, 1));
        }

        public static InquiryVerdict VerdictFor(double p, int strength)
        {
            if (strength < MinimumStrength) return InquiryVerdict.InsufficientEvidence;
            return p >= 0.85 ? InquiryVerdict.Yes
                : p >= 0.62 ? InquiryVerdict.LeansYes
                : p <= 0.15 ? InquiryVerdict.No
                : p <= 0.38 ? InquiryVerdict.LeansNo
                : InquiryVerdict.Uncertain;
        }

        /// <summary>
        /// Independent direct evidence units among the cited items. What one author wrote on one day is one unit, however
        /// many messages it took (and a fact deduced from that statement is the same unit). A statistic a tactic computed
        /// from many observations (plans kept, replies given) is a unit of its own per kind of statistic.
        /// </summary>
        public static int Strength(IEnumerable<EvidenceItem> cited)
        {
            static string Unit(EvidenceItem i) => i.Reliability == Statistic
                ? "statistic|" + i.SubjectPersonId + "|" + i.Facet
                : (i.AuthorPersonId ?? i.AssertionId ?? i.Id) + "|" + (i.AtMs / 86_400_000L);
            int strength = 0;
            foreach (var unit in cited.GroupBy(Unit))
            {
                var best = unit.OrderByDescending(i => Weight(i.Reliability)).First();
                strength += Weight(best.Reliability);
            }
            return strength;
        }

        /// <summary>Reliability of an exact statistic over a person's past behaviour (computed, not said).</summary>
        public const string Statistic = "statistic";

        private static int Weight(string reliability) => reliability switch
        {
            "self" or "fact" or Statistic => 2,
            "behaviour" or "third_party" => 1,
            _ => 0,
        };

        /// <summary>Median per option across passes, renormalised; disagreement flattens it towards uniform.</summary>
        public static IReadOnlyList<ChoiceDto> Choice(IReadOnlyList<Dictionary<string, double>> distributions, IReadOnlyList<string> options)
        {
            if (options.Count == 0) options = distributions.SelectMany(d => d.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (options.Count == 0) return Array.Empty<ChoiceDto>();
            var medians = options.Select(o => (o, Values: distributions.Select(d => Lookup(d, o)).ToList())).ToList();
            double spread = medians.Count == 0 ? 0 : medians.Average(m => StdDev(m.Values));
            var raw = medians.Select(m => (m.o, P: Math.Max(0, Median(m.Values)))).ToList();
            double total = raw.Sum(r => r.P);
            double uniform = 1.0 / options.Count;
            double flatten = Math.Min(1, 2 * spread);
            return raw.Select(r => new ChoiceDto(r.o, Math.Round((total > 0 ? r.P / total : uniform) * (1 - flatten) + uniform * flatten, 4)))
                .OrderByDescending(c => c.Probability).ThenBy(c => c.Option, StringComparer.Ordinal).ToList();
        }

        private static double Lookup(Dictionary<string, double> d, string option) =>
            d.FirstOrDefault(kv => string.Equals(kv.Key.Trim(), option.Trim(), StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>Median scores per subject label across passes (ranking variants).</summary>
        public static IReadOnlyList<(string Label, double Score, double Spread)> Ranking(IReadOnlyList<Dictionary<string, double>> passes, IReadOnlyList<string> labels) =>
            labels.Select(l => (l, Values: passes.Select(p => Lookup(p, l)).ToList()))
                .Select(x => (x.l, Math.Round(Median(x.Values), 4), Math.Round(StdDev(x.Values), 4)))
                .OrderByDescending(x => x.Item2).ThenBy(x => x.l, StringComparer.Ordinal).ToList();

        public static (double Estimate, double Low, double High) Numeric(IReadOnlyList<(double Estimate, double? Low, double? High)> passes)
        {
            if (passes.Count == 0) return (double.NaN, double.NaN, double.NaN);
            double est = Median(passes.Select(p => p.Estimate).ToList());
            double low = passes.Select(p => p.Low ?? p.Estimate).Min();
            double high = passes.Select(p => p.High ?? p.Estimate).Max();
            return (est, Math.Min(low, est), Math.Max(high, est));
        }

        public static (long? From, long? To) Interval(IReadOnlyList<(long? From, long? To)> passes)
        {
            var froms = passes.Select(p => p.From).OfType<long>().Select(v => (double)v).ToList();
            var tos = passes.Select(p => p.To).OfType<long>().Select(v => (double)v).ToList();
            return (froms.Count == 0 ? null : (long)Median(froms), tos.Count == 0 ? null : (long)Median(tos));
        }
    }
}
