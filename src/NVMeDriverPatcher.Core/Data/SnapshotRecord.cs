using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NVMeDriverPatcher.Data;

[Table("Snapshots")]
public class SnapshotRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public DateTime Timestamp { get; set; }

    public string Description { get; set; } = string.Empty;

    public string RegistryStateJson { get; set; } = "{}";

    public string PatchStatusJson { get; set; } = "{}";

    public bool IsPrePatch { get; set; }
}
