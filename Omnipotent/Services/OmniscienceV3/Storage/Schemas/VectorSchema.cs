using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Schemas
{
    /// <summary>
    /// vectors.db — embeddings kept apart from intel.db because they are the most expensive derived data
    /// to recompute: rebuilding intel never forces re-embedding (plan R3). Every vector records the model
    /// that produced it and the source version it embeds (fixes F333/F388: no model/version column).
    /// Spaces: message, stimulus, image, face, person. Vectors are L2-normalised; quant "i8" stores
    /// int8 with a per-vector scale, "f32" stores raw floats.
    /// </summary>
    public static class VectorSchema
    {
        public const string ModuleId = "core.vectors";

        public static IReadOnlyList<Migration> Migrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "vector store", """
CREATE TABLE vectors(
  space TEXT NOT NULL,
  item_id TEXT NOT NULL,
  model TEXT NOT NULL,
  dims INTEGER NOT NULL,
  quant TEXT NOT NULL,
  scale REAL NOT NULL DEFAULT 1,
  vec BLOB NOT NULL,
  partition_key TEXT,
  source_version INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL,
  PRIMARY KEY(space, model, item_id)
);
CREATE INDEX ix_vectors_partition ON vectors(space, model, partition_key);
"""),
        };
    }
}
