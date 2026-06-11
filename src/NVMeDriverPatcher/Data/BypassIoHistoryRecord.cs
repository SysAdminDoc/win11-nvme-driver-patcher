using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NVMeDriverPatcher.Data;

[Table("BypassIoHistory")]
public class BypassIoHistoryRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public DateTime Timestamp { get; set; }

    public string VolumeLetter { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Stack { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsPrePatch { get; set; }
}
