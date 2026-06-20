using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace HeliVMS.Models;

/// <summary>
/// Video segment index recording start/end time and path of each .ts file.
/// Composite index (CameraId, StartTime, EndTime) ensures timeline queries within 5ms.
/// </summary>
[Index(nameof(CameraId), nameof(StartTime), nameof(EndTime), Name = "IX_VideoSegments_CameraId_StartTime_EndTime")]
public class VideoSegment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Camera unique ID (maps to Camera.Id)</summary>
    [Required]
    [MaxLength(50)]
    public string CameraId { get; set; } = string.Empty;

    /// <summary>Segment start time</summary>
    public DateTime StartTime { get; set; }

    /// <summary>Segment end time (null means recording in progress)</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>Physical file path</summary>
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Record type: 0=Scheduled, 1=Motion, 2=AI Event</summary>
    public int RecordType { get; set; }

    /// <summary>File size (bytes) for UI display</summary>
    public long FileSize { get; set; }

    /// <summary>Creation time (index record creation time)</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the file is corrupted</summary>
    [JsonPropertyName("isCorrupted")]
    public bool IsCorrupted { get; set; }

    /// <summary>Integrity check time (null means not yet checked)</summary>
    public DateTime? IntegrityCheckedAt { get; set; }

    /// <summary>Motion score 0.0~1.0, 0=not detected, -1=analysis failed</summary>
    [JsonPropertyName("motionScore")]
    public double MotionScore { get; set; }

    /// <summary>Whether motion analysis is complete</summary>
    public bool MotionAnalysisDone { get; set; }
}
