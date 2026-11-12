using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;

namespace Omnipotent.Services.OmniscienceV3.Knowledge
{
    /// <summary>
    /// The modality and dialogue-act opinion of WS-C's classifier (plan A2), read from its packed per-message labels
    /// (<see cref="ClassificationReader"/>) under the active label set. The classifier's modality labels are the knowledge
    /// layer's own vocabulary (literal, joke, sarcasm, hypothetical, question, quoted, roleplay, lyrics_copypasta). Labels of
    /// another version of the message (it was edited since) say nothing about this one. Without classifier labels it falls
    /// back to the generic <c>labels</c> table, which is where a test or another producer may write them.
    /// </summary>
    public sealed class ClassifierModalityOracle : IModalityOracle
    {
        private readonly OmniscienceStores stores;
        private readonly IModalityOracle fallback;

        public ClassifierModalityOracle(OmniscienceStores stores, IModalityOracle fallback)
        {
            this.stores = stores;
            this.fallback = fallback;
        }

        public ModalityOpinion? Get(string messageId, int version)
        {
            var found = ClassificationReader.ForMessages(stores, new[] { messageId });
            if (!found.TryGetValue(messageId, out var labels) || labels.ItemVersion != version) return fallback.Get(messageId, version);
            string model = "classifier/ls" + labels.Labelset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Copypasta and the like are not the poster's own words (the classifier's gate): never a literal claim.
            if (labels.NonAuthored && labels.Gate == "copypasta") return new ModalityOpinion(Store.Modality.LyricsCopypasta, 1, 0, model);
            PackedLabel? Answered(string task) => labels.Labels.Where(l => l.Task == task && !l.Abstained).Select(l => (PackedLabel?)l).FirstOrDefault();
            var modality = Answered(LabelTableModalityOracle.ModalityTask);
            var act = Answered(LabelTableModalityOracle.DialogueActTask);
            if (modality == null && act == null) return fallback.Get(messageId, version);
            double nonLiteral = modality is not { } m ? 0 : m.Label == Store.Modality.Literal ? 1 - m.Probability : m.Probability;
            double question = act is not { } a ? 0 : a.Label == "question" ? a.Probability : 1 - a.Probability;
            return new ModalityOpinion(modality?.Label, nonLiteral, question, model);
        }
    }
}
