using System.Text.Json.Serialization;

namespace Flow.Windows;

public sealed record MeetingSyncSegment(
    string SegmentId,
    string Speaker,
    long StartMs,
    long EndMs,
    string Text);

public sealed record MeetingGroqSegment(
    string Id,
    string Speaker,
    long StartMs,
    long EndMs,
    string Text);

public sealed record MeetingGroqTranscription(string Text, IReadOnlyList<MeetingGroqSegment> Segments);

public sealed record MeetingSyncPayload(
    string MeetingId,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long DurationMs,
    IReadOnlyList<string> Participants,
    string? Summary,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> Tasks,
    string? Transcript,
    IReadOnlyList<MeetingSyncSegment> Segments,
    string? AudioAssetId,
    string? AudioFileName,
    string? AudioSha256,
    bool ExportToKnowledge = true);
